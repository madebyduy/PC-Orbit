using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Model;
using PcOrbit.Core.Outcomes;

namespace PcOrbit.Core.Transactions;

/// <summary>Why a changed step is not part of the automatic undo plan.</summary>
public enum UndoStepBlocker
{
    /// <summary>The action declares no automatic reverse route (spec 9.1, 21.7).</summary>
    NotAutomatic = 0,

    /// <summary>
    /// The user made this change by hand (guided firmware), so they change it back by hand.
    /// Spec 21.9: show the instructions, never hide the button.
    /// </summary>
    ByHand,

    /// <summary>
    /// The value before the change was never read. Restoring would mean inventing a value, and
    /// <c>Unknown</c> is never inferred into one (spec 6.6, 27.13).
    /// </summary>
    PreviousValueUnknown,
}

/// <summary>A change that happened but will not be undone automatically, and why.</summary>
public sealed record UndoExclusion(
    CapabilityId Capability,
    string ActionId,
    string ActionTitleKey,
    UndoStepBlocker Reason,
    string? GuideId);

/// <param name="Plan">The reverse plan, or null when nothing can be undone automatically.</param>
/// <param name="Excluded">
/// Changes that happened but are not in the plan. Never silently dropped: spec 9.4 requires the
/// user to see exactly which part of a transaction stays as it is.
/// </param>
public sealed record UndoPlan(Plan? Plan, IReadOnlyList<UndoExclusion> Excluded);

/// <summary>
/// Builds the reverse plan for a transaction: restore every changed capability to the value that
/// was read immediately before it was changed.
/// </summary>
/// <remarks>
/// <para>
/// Spec 21.9: "Undo là một transaction ngược, đi qua đúng preview → apply → verify." So this does
/// not execute anything — it produces an ordinary <see cref="Compiler.Plan"/> that the transaction
/// engine runs like any other, with the same checkpoints, restart boundaries, resume and
/// machine-read verification. The one difference is carried by <see cref="TransactionKind.Undo"/>:
/// the engine asks each executor to roll back instead of apply.
/// </para>
/// <para>
/// The plan is built from the manifests <em>inside</em> the source transaction, not from the
/// current catalog: spec 17.1 says a recipe update must not change what a reviewed transaction
/// meant, and that has to hold for its undo too.
/// </para>
/// </remarks>
public sealed class UndoPlanner(IClock? clock = null, IIdGenerator? ids = null)
{
    /// <summary>Step states that mean the machine was (or may have been) changed.</summary>
    private static readonly IReadOnlySet<StepState> ChangedStates = new HashSet<StepState>
    {
        StepState.Applied,
        StepState.Verified,
        StepState.AwaitingRestart,
        StepState.AwaitingUserAction,

        // Applied but the machine disagreed. Attempting the restore is still right: verification
        // will read what actually happened, exactly as it does for a forward step.
        StepState.VerifyFailed,
    };

    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly IIdGenerator _ids = ids ?? GuidIdGenerator.Instance;

    public UndoPlan Plan(Transaction source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Kind == TransactionKind.Undo)
        {
            // Undoing an undo would be re-applying the outcome with less information than the
            // compiler has. The honest route exists already: plan the outcome again.
            throw new InvalidOperationException(
                $"Transaction {source.Id} is itself an undo. To redo the change, plan the outcome again.");
        }

        if (source.State == TransactionState.RolledBack)
        {
            throw new InvalidOperationException($"Transaction {source.Id} has already been rolled back.");
        }

        List<UndoExclusion> excluded = [];
        List<PlanPhase> phases = [];
        List<VerificationCheck> finalChecks = [];
        int ordinal = 0;

        // Reverse order: the last thing applied is the first thing restored, so a dependency that
        // was created before its dependent is removed after it.
        foreach (PlanPhase sourcePhase in source.Plan.Phases.Reverse())
        {
            List<PlanStep> undoSteps = [];

            foreach (PlanStep planStep in sourcePhase.Steps.Reverse())
            {
                StepExecution execution = source.Steps.First(s => s.Ordinal == planStep.Ordinal);

                if (!ChangedStates.Contains(execution.State))
                {
                    // Nothing happened here (pending, skipped, failed before changing anything,
                    // or already rolled back), so there is nothing to undo and nothing to report.
                    continue;
                }

                UndoStepBlocker? blocker = ClassifyBlocker(planStep.Action, execution);

                if (blocker is { } reason)
                {
                    excluded.Add(new UndoExclusion(
                        planStep.Capability,
                        planStep.Action.Id,
                        planStep.Action.TitleKey,
                        reason,
                        planStep.Action.GuideId));
                    continue;
                }

                // The executor's rollback restores DesiredValue; verification reads the machine
                // and compares against the same value — never against the executor's opinion.
                ActionDefinition reverse = planStep.Action with
                {
                    SafeApply = SafeApplyPolicy.Off,
                    Verify =
                    [
                        new VerificationStep(
                            planStep.Capability,
                            execution.Before,
                            AfterRestart: planStep.Action.Restart != RestartKind.None),
                    ],
                };

                undoSteps.Add(new PlanStep(
                    Ordinal: ordinal++,
                    Capability: planStep.Capability,
                    CurrentValue: execution.Actual ?? execution.Requested,
                    DesiredValue: execution.Before,
                    Action: reverse,
                    Group: planStep.Group,
                    RequiredByOutcome: false,
                    RequiredBy: []));

                finalChecks.Add(new VerificationCheck(planStep.Capability, execution.Before));
            }

            if (undoSteps.Count == 0)
            {
                continue;
            }

            RestartKind restartAfter = undoSteps
                .Select(s => s.Action.Restart)
                .DefaultIfEmpty(RestartKind.None)
                .Max();

            phases.Add(new PlanPhase(phases.Count, undoSteps, restartAfter));
        }

        if (phases.Count == 0)
        {
            return new UndoPlan(null, excluded);
        }

        var plan = new Plan(
            Id: _ids.NewId("plan"),
            OutcomeId: "undo:" + source.Plan.OutcomeId,
            OutcomeVersion: source.Plan.OutcomeVersion,
            SnapshotId: source.Plan.SnapshotId,
            Machine: source.Plan.Machine,
            GraphVersion: source.Plan.GraphVersion,
            RuleVersion: source.Plan.RuleVersion,
            CatalogVersion: source.Plan.CatalogVersion,
            CompiledAt: _clock.Now,
            Phases: phases,
            Issues: [],
            FinalVerification: finalChecks,
            Cost: MeasureCost(phases),
            Outlook: PlanOutlook.Reachable,
            Hash: string.Empty);

        return new UndoPlan(plan with { Hash = PlanHasher.Hash(plan) }, excluded);
    }

    private static UndoStepBlocker? ClassifyBlocker(ActionDefinition action, StepExecution execution)
    {
        if (execution.State == StepState.AwaitingUserAction
            || action.Reversible.Mode == ReversibilityMode.ManualGuided)
        {
            return UndoStepBlocker.ByHand;
        }

        if (action.Reversible.Mode == ReversibilityMode.None)
        {
            return UndoStepBlocker.NotAutomatic;
        }

        if (!execution.Before.IsKnown)
        {
            return UndoStepBlocker.PreviousValueUnknown;
        }

        return null;
    }

    // Same arithmetic as the compiler's cost header, so the undo preview keeps spec 21.7's promise.
    private static PlanCost MeasureCost(IReadOnlyList<PlanPhase> phases)
    {
        List<PlanStep> steps = [.. phases.SelectMany(p => p.Steps)];
        int restarts = phases.Count(p => p.RestartAfter != RestartKind.None);

        return new PlanCost(
            Changes: steps.Count,
            Restarts: restarts,
            EstimatedSeconds: steps.Sum(s => s.Action.EstimatedSeconds) + (restarts * 90),
            ManualSteps: 0,
            IrreversibleChanges: 0);
    }
}

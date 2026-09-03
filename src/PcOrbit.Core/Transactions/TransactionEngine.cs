using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Events;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Transactions;

/// <summary>
/// Applies a plan as a multi-phase transaction that survives a restart and verifies its own work.
/// </summary>
/// <remarks>
/// <para>
/// Spec decision 6: Change Basket is the signature UX, but this is the signature architecture.
/// The guarantees it provides are the ones spec 9.3 asks for, and no more — no pretence of ACID,
/// because some firmware changes genuinely cannot be rolled back:
/// </para>
/// <list type="bullet">
///   <item>which steps are committed, staged, waiting for a restart or failed verification;</item>
///   <item>which parts can be rolled back independently;</item>
///   <item>a checkpoint durable enough to resume after a crash or a reboot;</item>
///   <item>never reporting the whole transaction as successful while a required outcome is unmet.</item>
/// </list>
/// <para>
/// Every state change is persisted before the next one starts. That is deliberately more writes
/// than necessary: the cost is microseconds, and the alternative is a machine that reboots
/// mid-plan and comes back with no idea what it was doing.
/// </para>
/// </remarks>
public sealed class TransactionEngine
{
    private readonly ExecutorRegistry _executors;
    private readonly ICapabilityReader _reader;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly IBootSession _boot;
    private readonly string _appVersion;

    public TransactionEngine(
        ExecutorRegistry executors,
        ICapabilityReader reader,
        IClock clock,
        IIdGenerator ids,
        IBootSession boot,
        string appVersion)
    {
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(boot);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);

        _executors = executors;
        _reader = reader;
        _clock = clock;
        _ids = ids;
        _boot = boot;
        _appVersion = appVersion;
    }

    /// <summary>
    /// Turns a reviewed plan into a transaction, ready to apply.
    /// </summary>
    /// <param name="reviewedPlanHash">
    /// The hash the user actually saw. If the plan has been recompiled since — because the machine
    /// changed, or the graph or catalog was updated — this refuses rather than applying something
    /// nobody approved (spec 9.1, 12.3, 19.1).
    /// </param>
    public Transaction Begin(Plan plan, ExecutionMode mode, string reviewedPlanHash)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewedPlanHash);

        if (!string.Equals(plan.Hash, reviewedPlanHash, StringComparison.Ordinal))
        {
            throw new PlanChangedException(reviewedPlanHash, plan.Hash);
        }

        if (plan.HasBlockers)
        {
            throw new InvalidOperationException(
                "This plan has blockers and cannot be applied. Resolve them or recompile: "
                + string.Join("; ", plan.Issues
                    .Where(i => i.Severity == PlanIssueSeverity.Blocker)
                    .Select(i => i.Code)));
        }

        IReadOnlyList<string> unresolvable = _executors.FindUnresolvable(
            new ActionCatalog(plan.Steps.Select(s => s.Action)));

        if (unresolvable.Count > 0)
        {
            // Spec 19.1: no shell fallback, no dynamic loading. A missing executor is a packaging
            // bug and must surface before anything is touched.
            throw new InvalidOperationException(
                "Plan references executors that are not in the allowlist: " + string.Join(", ", unresolvable));
        }

        DateTimeOffset now = _clock.Now;

        List<StepExecution> steps =
        [
            .. plan.Phases.SelectMany(phase => phase.Steps.Select(step => new StepExecution(
                Ordinal: step.Ordinal,
                PhaseIndex: phase.Index,
                ActionId: step.Action.Id,
                ActionVersion: step.Action.Version,
                Capability: step.Capability,
                Before: step.CurrentValue,
                Requested: step.DesiredValue,
                State: StepState.Pending))),
        ];

        return new Transaction(
            Id: _ids.NewId("tx"),
            Plan: plan,
            State: TransactionState.Ready,
            CurrentPhase: 0,
            Steps: steps,
            Provenance: Provenance.FromPlan(plan, _appVersion),
            Mode: mode,
            CreatedAt: now,
            UpdatedAt: now,
            StartBootId: _boot.CurrentBootId);
    }

    /// <summary>
    /// Runs phases until the plan is done or a restart is needed. Returns as soon as a restart
    /// boundary is reached; the caller decides when to restart (spec 21.8 — never automatically).
    /// </summary>
    public async Task<Transaction> RunAsync(
        Transaction transaction,
        ITransactionStore store,
        IEventLog events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);

        Transaction current = transaction;

        if (current.Mode == ExecutionMode.DryRun)
        {
            // A dry run must leave nothing behind: no checkpoint, no history entry.
            store = NullTransactionStore.Instance;
            events = NullEventLog.Instance;
        }

        current = await Checkpoint(current with { State = TransactionState.Applying }, store, cancellationToken)
            .ConfigureAwait(false);

        await LogTransactionEvent(current, "transaction.started", events, cancellationToken).ConfigureAwait(false);

        while (current.CurrentPhase < current.Plan.Phases.Count)
        {
            PlanPhase phase = current.Plan.Phases[current.CurrentPhase];

            current = await ApplyPhaseAsync(current, phase, store, events, cancellationToken).ConfigureAwait(false);
            current = await VerifyPhaseAsync(current, phase, afterRestart: false, store, events, cancellationToken)
                .ConfigureAwait(false);

            if (NeedsRestartAfter(current, phase))
            {
                current = await Checkpoint(
                    current with
                    {
                        State = TransactionState.AwaitingRestart,
                        PendingRestart = phase.RestartAfter,
                    },
                    store,
                    cancellationToken).ConfigureAwait(false);

                await LogTransactionEvent(current, "transaction.awaiting-restart", events, cancellationToken)
                    .ConfigureAwait(false);

                return current;
            }

            current = current with { CurrentPhase = current.CurrentPhase + 1 };
        }

        return await FinishAsync(current, store, events, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks a transaction back up after the machine has rebooted.
    /// </summary>
    /// <remarks>
    /// Spec 8.3.3: after boot, the app reads the real value from Windows. It never asks the user
    /// "did you turn it on?" — that question invites a wrong answer, and we can just look.
    /// </remarks>
    public async Task<Transaction> ResumeAsync(
        Transaction transaction,
        ITransactionStore store,
        IEventLog events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);

        if (transaction.State != TransactionState.AwaitingRestart)
        {
            throw new InvalidOperationException(
                $"Transaction {transaction.Id} is {transaction.State}, so there is nothing to resume.");
        }

        if (string.Equals(transaction.StartBootId, _boot.CurrentBootId, StringComparison.Ordinal))
        {
            // Same boot session: the restart has not happened yet. Do not verify — a firmware
            // change cannot possibly be visible, and reporting failure here would be a lie.
            return transaction;
        }

        Transaction current = await Checkpoint(
            transaction with
            {
                State = TransactionState.Resuming,
                ResumeBootId = _boot.CurrentBootId,
                PendingRestart = RestartKind.None,
            },
            store,
            cancellationToken).ConfigureAwait(false);

        await LogTransactionEvent(current, "transaction.resumed", events, cancellationToken).ConfigureAwait(false);

        PlanPhase restartedPhase = current.Plan.Phases[current.CurrentPhase];

        current = await VerifyPhaseAsync(current, restartedPhase, afterRestart: true, store, events, cancellationToken)
            .ConfigureAwait(false);

        current = current with { CurrentPhase = current.CurrentPhase + 1, State = TransactionState.Applying };

        while (current.CurrentPhase < current.Plan.Phases.Count)
        {
            PlanPhase phase = current.Plan.Phases[current.CurrentPhase];

            current = await ApplyPhaseAsync(current, phase, store, events, cancellationToken).ConfigureAwait(false);
            current = await VerifyPhaseAsync(current, phase, afterRestart: false, store, events, cancellationToken)
                .ConfigureAwait(false);

            if (NeedsRestartAfter(current, phase))
            {
                current = await Checkpoint(
                    current with { State = TransactionState.AwaitingRestart, PendingRestart = phase.RestartAfter },
                    store,
                    cancellationToken).ConfigureAwait(false);

                return current;
            }

            current = current with { CurrentPhase = current.CurrentPhase + 1 };
        }

        return await FinishAsync(current, store, events, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether this phase really has to end in a restart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two cases where the answer is no, even though the phase declares one:
    /// </para>
    /// <para>
    /// A dry run changed nothing, so there is nothing for a restart to complete. Stopping there
    /// would also mean <c>--dry-run</c> never simulates the second half of a plan, which is
    /// exactly the half a user wants to see before committing.
    /// </para>
    /// <para>
    /// And if every step in the phase failed or was skipped, the restart would achieve nothing.
    /// Asking someone to reboot to complete a change that did not happen is the kind of small
    /// dishonesty that costs trust (spec 6.6, 21.8).
    /// </para>
    /// </remarks>
    private static bool NeedsRestartAfter(Transaction transaction, PlanPhase phase)
    {
        if (phase.RestartAfter == RestartKind.None || transaction.Mode == ExecutionMode.DryRun)
        {
            return false;
        }

        HashSet<int> ordinals = [.. phase.Steps.Select(s => s.Ordinal)];

        return transaction.Steps.Any(s =>
            ordinals.Contains(s.Ordinal)
            && s.State is StepState.Applied or StepState.AwaitingRestart or StepState.AwaitingUserAction);
    }

    // ---------------------------------------------------------------- phases

    private async Task<Transaction> ApplyPhaseAsync(
        Transaction transaction,
        PlanPhase phase,
        ITransactionStore store,
        IEventLog events,
        CancellationToken cancellationToken)
    {
        Transaction current = transaction;

        foreach (PlanStep step in phase.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            StepExecution execution = current.Steps.First(s => s.Ordinal == step.Ordinal);

            if (execution.State != StepState.Pending)
            {
                continue;
            }

            current = await Checkpoint(
                Update(current, execution with { State = StepState.Applying, StartedAt = _clock.Now }),
                store,
                cancellationToken).ConfigureAwait(false);

            // Read the real value right before changing it, so history records what was actually
            // there rather than what the snapshot said minutes ago.
            CapabilityValue before = await ReadOrUnknownAsync(step.Capability, cancellationToken)
                .ConfigureAwait(false);

            IActionExecutor executor = _executors.Resolve(step.Action.Executor)
                ?? throw new InvalidOperationException($"Executor '{step.Action.Executor}' disappeared mid-apply.");

            var context = new ActionExecutionContext(
                step.Action,
                step.Capability,
                before,
                step.DesiredValue,
                current.Mode,
                current.Plan.Machine);

            ApplyOutcome outcome;

            try
            {
                outcome = await executor.ApplyAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // Spec 28: any failure must leave the workflow in a state the user can understand
                // and either continue or roll back. An unhandled exception here would not.
                outcome = ApplyOutcome.Failed("action.threw", $"{ex.GetType().Name}: {ex.Message}");
            }

            // The machine may have just changed, so nothing cached about it is trustworthy.
            // Without this, verification a moment later can read the value from before the change.
            _reader.Invalidate();

            StepState state = outcome.Status switch
            {
                ApplyStatus.Applied => StepState.Applied,
                ApplyStatus.AppliedPendingRestart => StepState.AwaitingRestart,
                ApplyStatus.AwaitingUserAction => StepState.AwaitingUserAction,
                ApplyStatus.Skipped => StepState.Skipped,
                _ => StepState.Failed,
            };

            current = await Checkpoint(
                Update(current, execution with
                {
                    State = state,
                    Before = before,
                    MessageKey = outcome.MessageKey,
                    Detail = outcome.Detail,
                    FinishedAt = _clock.Now,
                    Attempts = execution.Attempts + 1,
                }),
                store,
                cancellationToken).ConfigureAwait(false);

            await events.AppendAsync(
                new ChangeEvent(
                    Id: _ids.NewId("ev"),
                    Timestamp: _clock.Now,
                    Source: EventSource.PcOrbit,
                    Category: CategoryOf(step.Action),
                    Component: step.Capability.Value,
                    Before: before.Canonical,
                    After: step.DesiredValue.Canonical,
                    Initiator: Initiator.PcOrbit,
                    Confidence: outcome.Evidence?.Confidence ?? Confidence.Medium,
                    Evidence: outcome.Evidence,
                    RelatedTransaction: current.Id,
                    RelatedRestart: _boot.CurrentBootId,
                    MessageKey: outcome.MessageKey),
                cancellationToken).ConfigureAwait(false);
        }

        return current;
    }

    /// <summary>
    /// Re-reads the machine to decide whether each step in the phase actually worked.
    /// </summary>
    /// <remarks>
    /// Spec 8.3.2: Auto does not get to skip this. The executor's own opinion of its success is
    /// not evidence — the value the machine reports afterwards is.
    /// </remarks>
    private async Task<Transaction> VerifyPhaseAsync(
        Transaction transaction,
        PlanPhase phase,
        bool afterRestart,
        ITransactionStore store,
        IEventLog events,
        CancellationToken cancellationToken)
    {
        Transaction current = await Checkpoint(
            transaction with { State = TransactionState.Verifying },
            store,
            cancellationToken).ConfigureAwait(false);

        // Nothing was changed in a dry run, so verifying would report failures for changes that
        // never happened. The step results already say "would do this".
        if (current.Mode == ExecutionMode.DryRun)
        {
            return current;
        }

        foreach (PlanStep step in phase.Steps)
        {
            StepExecution execution = current.Steps.First(s => s.Ordinal == step.Ordinal);

            if (execution.State is StepState.Skipped or StepState.Failed or StepState.Verified)
            {
                continue;
            }

            foreach (VerificationStep check in step.Action.Verify.Where(v => v.AfterRestart == afterRestart))
            {
                CapabilityReading? reading = await _reader
                    .ReadAsync(check.Capability, cancellationToken)
                    .ConfigureAwait(false);

                CapabilityValue actual = reading?.Value ?? CapabilityValue.Unknown;
                bool passed = actual.Satisfies(check.Expected);

                current = await Checkpoint(
                    Update(current, execution with
                    {
                        State = passed ? StepState.Verified : StepState.VerifyFailed,
                        Actual = actual,
                        VerificationEvidence = reading?.Evidence
                            ?? Evidence.Missing($"no reader for '{check.Capability}'"),
                        // "It says something else" and "we could not read it" are different
                        // failures and the user gets told which one it was (spec 6.6, 27.13).
                        MessageKey = passed
                            ? execution.MessageKey
                            : actual.IsKnown ? "verify.mismatch" : "verify.unreadable",
                        FinishedAt = _clock.Now,
                    }),
                    store,
                    cancellationToken).ConfigureAwait(false);

                execution = current.Steps.First(s => s.Ordinal == step.Ordinal);

                if (!passed)
                {
                    break;
                }
            }
        }

        await LogTransactionEvent(current, "transaction.verified-phase", events, cancellationToken)
            .ConfigureAwait(false);

        return current;
    }

    private async Task<Transaction> FinishAsync(
        Transaction transaction,
        ITransactionStore store,
        IEventLog events,
        CancellationToken cancellationToken)
    {
        if (transaction.Mode == ExecutionMode.DryRun)
        {
            // Whether the outcome would be reached cannot be known without applying, and claiming
            // either answer would be a guess. The dry run reports what it would do, and stops.
            return await Checkpoint(
                transaction with
                {
                    State = TransactionState.Completed,
                    OutcomeVerdictKey = "action.dry-run",
                    PendingRestart = RestartKind.None,
                },
                store,
                cancellationToken).ConfigureAwait(false);
        }

        // The outcome's own verification, independent of the individual steps: an outcome can fail
        // even when every step passed, and spec 9.4 forbids calling that "Done".
        bool outcomeReached = true;
        bool outcomeUnknown = false;

        foreach (var check in transaction.Plan.FinalVerification)
        {
            CapabilityReading? reading = await _reader
                .ReadAsync(check.Check, cancellationToken)
                .ConfigureAwait(false);

            CapabilityValue actual = reading?.Value ?? CapabilityValue.Unknown;

            if (!actual.IsKnown)
            {
                outcomeUnknown = true;
                continue;
            }

            if (!actual.Satisfies(check.Expected))
            {
                outcomeReached = false;
            }
        }

        TransactionTally tally = transaction.Tally;

        TransactionState state = (tally.Failed, outcomeReached, outcomeUnknown) switch
        {
            (0, true, false) => TransactionState.Completed,
            (_, false, _) when tally.Completed == 0 => TransactionState.Failed,
            _ => TransactionState.PartiallyCompleted,
        };

        string verdictKey = outcomeUnknown
            ? "verify.unknown"
            : outcomeReached ? "verify.reached" : "verify.notReached";

        Transaction finished = await Checkpoint(
            transaction with
            {
                State = state,
                OutcomeVerdictKey = verdictKey,
                PendingRestart = RestartKind.None,
            },
            store,
            cancellationToken).ConfigureAwait(false);

        await LogTransactionEvent(finished, verdictKey, events, cancellationToken).ConfigureAwait(false);
        return finished;
    }

    // ---------------------------------------------------------------- helpers

    private async Task<CapabilityValue> ReadOrUnknownAsync(CapabilityId capability, CancellationToken cancellationToken)
    {
        CapabilityReading? reading = await _reader.ReadAsync(capability, cancellationToken).ConfigureAwait(false);
        return reading?.Value ?? CapabilityValue.Unknown;
    }

    private static Transaction Update(Transaction transaction, StepExecution step) =>
        transaction with
        {
            Steps = [.. transaction.Steps.Select(s => s.Ordinal == step.Ordinal ? step : s)],
        };

    private async Task<Transaction> Checkpoint(
        Transaction transaction,
        ITransactionStore store,
        CancellationToken cancellationToken)
    {
        Transaction stamped = transaction with { UpdatedAt = _clock.Now };
        await store.SaveAsync(stamped, cancellationToken).ConfigureAwait(false);
        return stamped;
    }

    private Task LogTransactionEvent(
        Transaction transaction,
        string messageKey,
        IEventLog events,
        CancellationToken cancellationToken) =>
        events.AppendAsync(
            new ChangeEvent(
                Id: _ids.NewId("ev"),
                Timestamp: _clock.Now,
                Source: EventSource.PcOrbit,
                Category: EventCategory.Transaction,
                Component: transaction.Plan.OutcomeId,
                Before: null,
                After: transaction.State.ToString(),
                Initiator: Initiator.PcOrbit,
                Confidence: Confidence.High,
                Evidence: null,
                RelatedTransaction: transaction.Id,
                RelatedRestart: _boot.CurrentBootId,
                MessageKey: messageKey),
            cancellationToken);

    private static EventCategory CategoryOf(ActionDefinition action) =>
        action.Restart == RestartKind.Firmware || action.Provides.Any(p => p.Capability.Value.StartsWith("firmware.", StringComparison.Ordinal))
            ? EventCategory.Firmware
            : action.Provides.Any(p => p.Capability.Value.StartsWith("windows.feature.", StringComparison.Ordinal))
                ? EventCategory.Feature
                : EventCategory.Setting;
}

/// <summary>
/// The plan changed between review and apply. Spec 12.3: that must force a fresh review, not a
/// best-effort apply of something the user never saw.
/// </summary>
public sealed class PlanChangedException(string reviewedHash, string currentHash)
    : Exception($"The plan changed since it was reviewed (reviewed {reviewedHash[..Math.Min(12, reviewedHash.Length)]}…, now {currentHash[..Math.Min(12, currentHash.Length)]}…). Review it again before applying.")
{
    public string ReviewedHash { get; } = reviewedHash;

    public string CurrentHash { get; } = currentHash;
}

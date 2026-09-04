using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;
using PcOrbit.Core.Outcomes;

namespace PcOrbit.Core.Compiler;

/// <param name="RuleVersion">
/// Version of the compiler rules themselves. Part of plan provenance: if we change how plans are
/// built, an old plan must not be silently re-used (spec 9.5, 12.3).
/// </param>
/// <param name="MaxRisk">
/// The compiler refuses to plan anything riskier than this. Spec 19.2 puts BIOS flashing in
/// Critical; nothing in v0.1 is allowed to reach for it.
/// </param>
/// <param name="AllowManualSteps">
/// False means "only plan what the app can do itself" — used by unattended paths, never the default.
/// Guided is the normal route for most machines (spec 8.3.2), so it is on by default.
/// </param>
/// <param name="MaxSnapshotAge">
/// How old a scan may be before the plan says so. Null switches the check off, which is only
/// appropriate for a caller replaying a stored snapshot on purpose.
/// </param>
public sealed record CompilerOptions(
    string RuleVersion = "0.1.0",
    RiskClass MaxRisk = RiskClass.High,
    bool AllowManualSteps = true,
    TimeSpan? MaxSnapshotAge = null)
{
    /// <summary>
    /// Fifteen minutes. Long enough to read a plan and think about it, short enough that the
    /// machine has probably not been changed by something else in between.
    /// </summary>
    public static TimeSpan DefaultMaxSnapshotAge { get; } = TimeSpan.FromMinutes(15);

    public static CompilerOptions Default { get; } = new(MaxSnapshotAge: DefaultMaxSnapshotAge);
}

/// <summary>
/// Turns "what the user wants" plus "what this machine actually is" into an ordered,
/// reviewable plan of only the changes this particular PC still needs (spec 12).
/// </summary>
/// <remarks>
/// Pure function of (snapshot, outcome, graph, catalog, options). No I/O, no clock reads beyond
/// the injected one, no randomness — that is what makes golden tests possible (spec 28).
/// </remarks>
public sealed class StateCompiler
{
    private readonly CapabilityGraph _graph;
    private readonly ActionCatalog _catalog;
    private readonly CompilerOptions _options;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public StateCompiler(
        CapabilityGraph graph,
        ActionCatalog catalog,
        CompilerOptions? options = null,
        IClock? clock = null,
        IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(catalog);

        _graph = graph;
        _catalog = catalog;
        _options = options ?? CompilerOptions.Default;
        _clock = clock ?? SystemClock.Instance;
        _ids = ids ?? GuidIdGenerator.Instance;
    }

    public Plan Compile(StateSnapshot snapshot, Outcome outcome)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(outcome);

        MachineIdentity machine = snapshot.Machine;
        List<PlanIssue> issues = [];

        NoteStaleSnapshot(snapshot, issues);

        Requirements requirements = ExpandRequirements(outcome, machine, issues);
        List<PlanStep> steps = SelectActions(snapshot, requirements, issues);
        List<PlanPhase> phases = BuildPhases(steps, requirements, issues);
        IReadOnlyList<VerificationCheck> finalVerification = ResolveVerification(outcome, snapshot, issues);

        PlanCost cost = MeasureCost(phases);
        PlanOutlook outlook = Decide(phases, issues);

        var plan = new Plan(
            Id: _ids.NewId("plan"),
            OutcomeId: outcome.Id,
            OutcomeVersion: outcome.Version,
            SnapshotId: snapshot.Id,
            Machine: machine,
            GraphVersion: _graph.Version,
            RuleVersion: _options.RuleVersion,
            CatalogVersion: _catalog.Version,
            CompiledAt: _clock.Now,
            Phases: phases,
            Issues: issues,
            FinalVerification: finalVerification,
            Cost: cost,
            Outlook: outlook,
            Hash: string.Empty);

        return plan with { Hash = PlanHasher.Hash(plan) };
    }

    /// <summary>
    /// Says so when the plan was compiled from a scan old enough to have gone out of date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A difference plan is a statement about a machine at a moment. Compile it from a scan taken
    /// hours ago and it can propose a change that has already happened, or omit one that has since
    /// become necessary — and the plan hash will happily lock in that stale picture for approval.
    /// </para>
    /// <para>
    /// A warning rather than a blocker, deliberately. The engine verifies by reading the machine
    /// again before and after every step, so a stale snapshot produces a misleading preview, not an
    /// unsafe apply. Blocking would also make an offline review of a stored plan impossible, which
    /// is a thing support engineers legitimately do (ADR 0004).
    /// </para>
    /// </remarks>
    private void NoteStaleSnapshot(StateSnapshot snapshot, List<PlanIssue> issues)
    {
        if (_options.MaxSnapshotAge is not { } limit)
        {
            return;
        }

        TimeSpan age = _clock.Now - snapshot.TakenAt;

        if (age <= limit)
        {
            return;
        }

        issues.Add(new PlanIssue(
            PlanIssueSeverity.Warning,
            "snapshot.stale",
            Capability: null,
            $"This plan was compiled from a scan taken {age.TotalMinutes:0} minutes ago, which is older "
            + $"than the {limit.TotalMinutes:0}-minute limit. Scan again before applying it: the machine "
            + "may have changed since."));
    }

    /// <summary>
    /// The outcome's own verification, with any <c>"max"</c> sentinel resolved to this machine's
    /// ceiling — the engine compares readings against values, and "max" is not a value.
    /// </summary>
    private IReadOnlyList<VerificationCheck> ResolveVerification(
        Outcome outcome,
        StateSnapshot snapshot,
        List<PlanIssue> issues)
    {
        List<VerificationCheck> checks = [];

        foreach (VerificationCheck check in outcome.Verify)
        {
            if (!LimitResolver.IsMax(check.Expected))
            {
                checks.Add(check);
                continue;
            }

            if (LimitResolver.TryResolveMax(_graph, snapshot, check.Check, out CapabilityValue resolved, out _))
            {
                checks.Add(check with { Expected = resolved });
                continue;
            }

            // SelectActions usually reported this capability already; do not say it twice.
            if (!issues.Any(i => i.Code == "capability.limit-unknown" && i.Capability is { } c && c.Equals(check.Check)))
            {
                issues.Add(new PlanIssue(
                    PlanIssueSeverity.Blocker,
                    "capability.limit-unknown",
                    check.Check,
                    $"The outcome verifies '{check.Check}' at its maximum, but the maximum could not be read "
                    + "on this machine, so the result could never be confirmed."));
            }

            checks.Add(check);
        }

        return checks;
    }

    // ---------------------------------------------------------------- requirements

    /// <param name="RequiredBy">Who needs this capability — the "why is this in my plan" answer.</param>
    /// <param name="DirectRequires">
    /// What this capability itself needs, scoped to this machine. Kept alongside the order so the
    /// phase builder can reason about dependencies without reaching back into the graph.
    /// </param>
    private sealed record Requirements(
        IReadOnlyList<CapabilityId> Order,
        IReadOnlyDictionary<CapabilityId, CapabilityValue> Expected,
        IReadOnlySet<CapabilityId> Optional,
        IReadOnlySet<CapabilityId> FromOutcome,
        IReadOnlyDictionary<CapabilityId, IReadOnlyList<CapabilityId>> RequiredBy,
        IReadOnlyDictionary<CapabilityId, IReadOnlyList<CapabilityId>> DirectRequires);

    /// <summary>
    /// Walks <see cref="EdgeKind.Requires"/> from the outcome to every capability it implies on
    /// <em>this</em> machine, recording what each one has to be and who needs it.
    /// </summary>
    private Requirements ExpandRequirements(Outcome outcome, MachineIdentity machine, List<PlanIssue> issues)
    {
        Dictionary<CapabilityId, CapabilityValue> expected = [];
        HashSet<CapabilityId> optional = [];
        HashSet<CapabilityId> fromOutcome = [];
        Dictionary<CapabilityId, List<CapabilityId>> requiredBy = [];
        Dictionary<CapabilityId, List<CapabilityId>> directRequires = [];
        List<CapabilityId> roots = [];

        foreach (CapabilityRequirement requirement in outcome.Requires)
        {
            if (!_graph.Contains(requirement.Capability))
            {
                issues.Add(new PlanIssue(
                    PlanIssueSeverity.Blocker,
                    "capability.not-in-graph",
                    requirement.Capability,
                    $"Outcome '{outcome.Id}' requires '{requirement.Capability}', which the capability graph does not declare."));
                continue;
            }

            roots.Add(requirement.Capability);
            fromOutcome.Add(requirement.Capability);
            Record(requirement.Capability, requirement.Expected, from: null);

            if (requirement.Optional)
            {
                optional.Add(requirement.Capability);
            }
        }

        // Breadth-first so the shallowest reason for a requirement is the one we report.
        Queue<CapabilityId> queue = new(roots);
        HashSet<CapabilityId> visited = [.. roots];

        while (queue.Count > 0)
        {
            CapabilityId current = queue.Dequeue();

            foreach (CapabilityEdge edge in _graph.RequirementsOf(current, machine).OrderBy(e => e.To))
            {
                Record(edge.To, edge.Expected, from: current);

                if (!directRequires.TryGetValue(current, out List<CapabilityId>? needs))
                {
                    needs = [];
                    directRequires[current] = needs;
                }

                if (!needs.Contains(edge.To))
                {
                    needs.Add(edge.To);
                }

                if (visited.Add(edge.To))
                {
                    queue.Enqueue(edge.To);
                }
            }
        }

        // Dependencies first — the order the transaction engine has to execute in.
        IReadOnlyList<CapabilityId> order = _graph.RequirementClosure(roots, machine);

        return new Requirements(
            order,
            expected,
            optional,
            fromOutcome,
            requiredBy.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<CapabilityId>)kv.Value),
            directRequires.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<CapabilityId>)kv.Value));

        void Record(CapabilityId capability, CapabilityValue value, CapabilityId? from)
        {
            if (from is { } parent)
            {
                if (!requiredBy.TryGetValue(capability, out List<CapabilityId>? parents))
                {
                    parents = [];
                    requiredBy[capability] = parents;
                }

                if (!parents.Contains(parent))
                {
                    parents.Add(parent);
                }
            }

            if (!expected.TryGetValue(capability, out CapabilityValue existing))
            {
                expected[capability] = value;
                return;
            }

            if (existing == value)
            {
                return;
            }

            // Two requirements disagree about the same capability. Never resolve this silently:
            // spec 12.3 wants conflicts between desired states detected, not averaged.
            issues.Add(new PlanIssue(
                PlanIssueSeverity.Blocker,
                "requirement.conflict",
                capability,
                $"'{capability}' is required to be both '{existing.Canonical}' and '{value.Canonical}'."));
        }
    }

    // ---------------------------------------------------------------- action selection

    private List<PlanStep> SelectActions(StateSnapshot snapshot, Requirements requirements, List<PlanIssue> issues)
    {
        List<PlanStep> steps = [];
        HashSet<string> chosenActionIds = [];
        HashSet<CapabilityId> satisfied = [];

        foreach (CapabilityId capability in requirements.Order)
        {
            if (!requirements.Expected.TryGetValue(capability, out CapabilityValue desired))
            {
                continue;
            }

            CapabilityValue current = snapshot.ValueOf(capability);
            bool isOptional = requirements.Optional.Contains(capability);

            // "max" means "as high as this machine allows" and has no value until the compiler
            // reads the ceiling from the graph's limits edge (spec 12.2). An unreadable ceiling
            // blocks rather than defaults: a step we cannot verify is a step we must not promise.
            CapabilityValue target = desired;

            if (LimitResolver.IsMax(desired))
            {
                if (LimitResolver.TryResolveMax(_graph, snapshot, capability, out CapabilityValue resolved, out CapabilityId limitedBy))
                {
                    target = resolved;

                    issues.Add(new PlanIssue(
                        PlanIssueSeverity.Info,
                        "requirement.max-resolved",
                        capability,
                        $"'{capability}' asked for 'max', which is '{resolved.Canonical}' on this machine "
                        + $"(ceiling read from '{limitedBy}')."));
                }
                else
                {
                    issues.Add(new PlanIssue(
                        isOptional ? PlanIssueSeverity.Warning : PlanIssueSeverity.Blocker,
                        "capability.limit-unknown",
                        capability,
                        $"'{capability}' is required to be at its maximum, but the maximum could not be read "
                        + "on this machine, so the change can neither be planned nor verified."));
                    continue;
                }
            }

            // Spec 12.3: no action when the machine is already there.
            if (current.Satisfies(target))
            {
                satisfied.Add(capability);
                continue;
            }

            if (IsImpliedBySatisfiedDependent(capability, snapshot, requirements, out CapabilityId dependent))
            {
                satisfied.Add(capability);

                issues.Add(new PlanIssue(
                    PlanIssueSeverity.Info,
                    "reading.implied-by-dependent",
                    capability,
                    $"'{capability}' reads '{current.Canonical}', but '{dependent}' is on and cannot work without it. "
                    + "Treating it as already satisfied instead of sending you into firmware setup. "
                    + "A running hypervisor is the usual reason this reads wrong (spec 8.3.4)."));
                continue;
            }

            if (!current.IsKnown)
            {
                issues.Add(new PlanIssue(
                    PlanIssueSeverity.Warning,
                    "reading.unknown",
                    capability,
                    $"We could not read '{capability}', so we cannot tell whether this step is needed. "
                    + "It is in the plan, and we verify the real value afterwards."));
            }

            IReadOnlyList<ActionDefinition> candidates = _catalog.CandidatesFor(capability, desired, snapshot.Machine);

            if (candidates.Count == 0)
            {
                ReportNoRoute(capability, desired, snapshot.Machine, current.IsKnown, isOptional, issues);
                continue;
            }

            ActionDefinition? chosen = null;

            foreach (ActionDefinition candidate in candidates)
            {
                if (candidate.Risk > _options.MaxRisk)
                {
                    issues.Add(new PlanIssue(
                        PlanIssueSeverity.Info,
                        "action.risk-above-policy",
                        capability,
                        $"Skipped '{candidate.Id}': risk {candidate.Risk} is above the configured maximum {_options.MaxRisk}."));
                    continue;
                }

                if (candidate.IsManualStep && !_options.AllowManualSteps)
                {
                    issues.Add(new PlanIssue(
                        PlanIssueSeverity.Info,
                        "action.manual-not-allowed",
                        capability,
                        $"Skipped '{candidate.Id}': it needs a step the user performs by hand and this plan disallows those."));
                    continue;
                }

                chosen = candidate;
                break;
            }

            if (chosen is null)
            {
                issues.Add(new PlanIssue(
                    isOptional ? PlanIssueSeverity.Warning : PlanIssueSeverity.Blocker,
                    "capability.no-usable-action",
                    capability,
                    $"'{capability}' has to change from '{current.Canonical}' to '{target.Canonical}', "
                    + "but every route is excluded by policy."));
                continue;
            }

            // One action can satisfy several requirements; it belongs in the plan once.
            if (!chosenActionIds.Add(chosen.Id))
            {
                continue;
            }

            // The manifest's own verify steps may carry the sentinel too. The plan travels with
            // its manifests, so the resolved value is baked in here and verification later
            // compares the machine against a number, never against "max".
            if (!target.Equals(desired))
            {
                chosen = chosen with
                {
                    Verify =
                    [
                        .. chosen.Verify.Select(v =>
                            v.Capability.Equals(capability) && LimitResolver.IsMax(v.Expected)
                                ? v with { Expected = target }
                                : v),
                    ],
                };
            }

            steps.Add(new PlanStep(
                Ordinal: steps.Count,
                Capability: capability,
                CurrentValue: current,
                DesiredValue: target,
                Action: chosen,
                Group: GroupOf(chosen),
                RequiredByOutcome: requirements.FromOutcome.Contains(capability),
                RequiredBy: requirements.RequiredBy.TryGetValue(capability, out IReadOnlyList<CapabilityId>? parents)
                    ? parents
                    : []));
        }

        VerifyActionPreconditions(steps, snapshot, satisfied, issues);
        return steps;
    }

    /// <summary>
    /// Spec 8.3.4: "I turned it on but the app still says off". If something that cannot run
    /// without this capability is running, the capability is on and our read is wrong. Believe the
    /// stronger evidence rather than marching the user into the firmware screen for nothing.
    /// </summary>
    private bool IsImpliedBySatisfiedDependent(
        CapabilityId capability,
        StateSnapshot snapshot,
        Requirements requirements,
        out CapabilityId dependent)
    {
        foreach (CapabilityEdge edge in _graph.To(capability, EdgeKind.Requires))
        {
            if (!requirements.Expected.TryGetValue(edge.From, out CapabilityValue dependentExpected))
            {
                continue;
            }

            if (snapshot.ValueOf(edge.From).Satisfies(dependentExpected))
            {
                dependent = edge.From;
                return true;
            }
        }

        dependent = default;
        return false;
    }

    /// <param name="currentIsKnown">
    /// False when the capability could not be read. It changes what this issue means, and therefore
    /// what it must say: "nothing can set your TPM version to 2.0" reads as "your machine is not
    /// eligible", when the truth may be that the scan was not elevated and the chip is fine. Those
    /// two sentences send a person to different places — one to a shop (spec 6.6, 27.13).
    /// </param>
    private void ReportNoRoute(
        CapabilityId capability,
        CapabilityValue desired,
        MachineIdentity machine,
        bool currentIsKnown,
        bool isOptional,
        List<PlanIssue> issues)
    {
        IReadOnlyList<(ActionDefinition Action, string Reason)> rejected =
            _catalog.RejectedFor(capability, desired, machine);

        if (rejected.Count == 0 && !currentIsKnown)
        {
            issues.Add(new PlanIssue(
                isOptional ? PlanIssueSeverity.Warning : PlanIssueSeverity.Blocker,
                "capability.unreadable-and-unwritable",
                capability,
                $"'{capability}' could not be read on this machine, and nothing in the action catalog can "
                + $"set it to '{desired.Canonical}' either. So this outcome cannot be confirmed here — which "
                + "is not the same as the machine being unable to meet it."));

            return;
        }

        string detail = rejected.Count == 0
            ? $"Nothing in the action catalog can set '{capability}' to '{desired.Canonical}'."
            : $"'{capability}' cannot be set to '{desired.Canonical}' on this machine: "
              + string.Join("; ", rejected.Select(r => $"{r.Action.Id} ({r.Reason})"));

        // Spec 27.13: "we cannot write it" is not the same as "nothing can be done". The code says
        // which one this is so the UI can offer the guided or read-only path instead of a dead end.
        issues.Add(new PlanIssue(
            isOptional ? PlanIssueSeverity.Warning : PlanIssueSeverity.Blocker,
            rejected.Count == 0 ? "capability.no-action-exists" : "capability.not-writable-here",
            capability,
            detail));
    }

    /// <summary>
    /// An action can declare its own preconditions beyond the graph. Anything neither already
    /// satisfied nor produced by another step in this plan is a blocker, not a runtime surprise.
    /// </summary>
    private static void VerifyActionPreconditions(
        List<PlanStep> steps,
        StateSnapshot snapshot,
        HashSet<CapabilityId> satisfied,
        List<PlanIssue> issues)
    {
        HashSet<CapabilityId> producedByPlan =
        [
            .. steps.SelectMany(s => s.Action.Provides).Select(p => p.Capability),
        ];

        foreach (PlanStep step in steps)
        {
            foreach (CapabilityAssertion precondition in step.Action.Requires)
            {
                if (satisfied.Contains(precondition.Capability)
                    || producedByPlan.Contains(precondition.Capability)
                    || snapshot.ValueOf(precondition.Capability).Satisfies(precondition.State))
                {
                    continue;
                }

                issues.Add(new PlanIssue(
                    PlanIssueSeverity.Blocker,
                    "action.unmet-precondition",
                    precondition.Capability,
                    $"Action '{step.Action.Id}' needs '{precondition.Capability}' to be "
                    + $"'{precondition.State.Canonical}', and nothing in this plan makes that true."));
            }
        }
    }

    private static StepGroup GroupOf(ActionDefinition action) => action.Restart switch
    {
        RestartKind.Firmware => StepGroup.Firmware,
        RestartKind.Windows => StepGroup.WindowsFeature,
        _ => action.IsManualStep ? StepGroup.Firmware : StepGroup.Online,
    };

    // ---------------------------------------------------------------- phases and restarts

    /// <summary>
    /// Splits the ordered steps at restart boundaries.
    /// </summary>
    /// <remarks>
    /// A step's tier is one more than its deepest dependency that only takes effect after a
    /// restart. Everything in a tier runs before the same restart, which is how spec 21.8's
    /// promise — one plan, one restart whenever dependencies allow — is actually kept, rather
    /// than being a UI claim.
    /// </remarks>
    private static List<PlanPhase> BuildPhases(
        List<PlanStep> steps,
        Requirements requirements,
        List<PlanIssue> issues)
    {
        if (steps.Count == 0)
        {
            return [];
        }

        Dictionary<CapabilityId, PlanStep> producer = [];

        foreach (PlanStep step in steps)
        {
            foreach (CapabilityAssertion provided in step.Action.Provides)
            {
                producer.TryAdd(provided.Capability, step);
            }
        }

        Dictionary<int, int> tierByOrdinal = [];

        foreach (PlanStep step in steps)
        {
            tierByOrdinal[step.Ordinal] = TierOf(step, []);
        }

        var phases = new List<PlanPhase>();
        int index = 0;

        foreach (IGrouping<int, PlanStep> tier in steps
            .GroupBy(s => tierByOrdinal[s.Ordinal])
            .OrderBy(g => g.Key))
        {
            // Online and reversible first, firmware last: the user sees working results before
            // being asked to restart, and the hand-done step sits next to the restart it needs.
            List<PlanStep> ordered =
            [
                .. tier
                    .OrderBy(s => s.Group)
                    .ThenBy(s => s.Action.Risk)
                    .ThenBy(s => s.Ordinal),
            ];

            RestartKind restartAfter = ordered
                .Select(s => s.Action.Restart)
                .DefaultIfEmpty(RestartKind.None)
                .Max();

            phases.Add(new PlanPhase(index++, ordered, restartAfter));
        }

        return phases;

        int TierOf(PlanStep step, HashSet<int> visiting)
        {
            if (tierByOrdinal.TryGetValue(step.Ordinal, out int cached))
            {
                return cached;
            }

            if (!visiting.Add(step.Ordinal))
            {
                issues.Add(new PlanIssue(
                    PlanIssueSeverity.Blocker,
                    "plan.dependency-cycle",
                    step.Capability,
                    $"Action '{step.Action.Id}' is part of a dependency cycle, so no safe order exists."));
                return 0;
            }

            int tier = 0;

            foreach (CapabilityId dependency in Dependencies(step))
            {
                if (!producer.TryGetValue(dependency, out PlanStep? source) || source.Ordinal == step.Ordinal)
                {
                    continue;
                }

                int sourceTier = TierOf(source, visiting);
                int needed = source.Action.Restart == RestartKind.None ? sourceTier : sourceTier + 1;
                tier = Math.Max(tier, needed);
            }

            visiting.Remove(step.Ordinal);
            tierByOrdinal[step.Ordinal] = tier;
            return tier;
        }

        IEnumerable<CapabilityId> Dependencies(PlanStep step)
        {
            // What the action itself declares it needs.
            foreach (CapabilityAssertion precondition in step.Action.Requires)
            {
                yield return precondition.Capability;
            }

            // Plus what the graph says the targeted capability needs, so an action manifest that
            // does not restate its dependencies still gets ordered correctly.
            if (requirements.DirectRequires.TryGetValue(step.Capability, out IReadOnlyList<CapabilityId>? needs))
            {
                foreach (CapabilityId need in needs)
                {
                    yield return need;
                }
            }
        }
    }

    // ---------------------------------------------------------------- cost and outlook

    private static PlanCost MeasureCost(IReadOnlyList<PlanPhase> phases)
    {
        if (phases.Count == 0)
        {
            return PlanCost.Empty;
        }

        List<PlanStep> steps = [.. phases.SelectMany(p => p.Steps)];

        int restarts = phases.Count(p => p.RestartAfter != RestartKind.None);
        int seconds = steps.Sum(s => s.Action.EstimatedSeconds);

        // A restart is not free and the header must not pretend otherwise.
        seconds += restarts * 90;

        return new PlanCost(
            Changes: steps.Count,
            Restarts: restarts,
            EstimatedSeconds: seconds,
            ManualSteps: steps.Count(s => s.IsManualStep),
            IrreversibleChanges: steps.Count(s => s.IsIrreversible));
    }

    private static PlanOutlook Decide(IReadOnlyList<PlanPhase> phases, List<PlanIssue> issues)
    {
        if (issues.Any(i => i.Severity == PlanIssueSeverity.Blocker))
        {
            return PlanOutlook.NotReachable;
        }

        if (phases.Count == 0)
        {
            return PlanOutlook.AlreadySatisfied;
        }

        return phases.SelectMany(p => p.Steps).Any(s => s.IsManualStep)
            ? PlanOutlook.ReachableWithManualSteps
            : PlanOutlook.Reachable;
    }
}

using System.Text.Json.Serialization;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Model;
using PcOrbit.Core.Outcomes;

namespace PcOrbit.Core.Compiler;

/// <summary>Display grouping inside a phase, mirroring the shape of spec 9.3.</summary>
public enum StepGroup
{
    /// <summary>Online and reversible. Applied and verified first so the user sees something work.</summary>
    Online = 0,

    /// <summary>Windows component changes that only take effect after a restart.</summary>
    WindowsFeature,

    /// <summary>Firmware. Either a vendor write or the one step the user does themselves.</summary>
    Firmware,
}

public enum PlanIssueSeverity
{
    Info = 0,
    Warning,
    Blocker,
}

/// <summary>
/// Something the user needs to know about the plan. Codes are stable ids, not sentences: the UI
/// resolves them through the string catalog, and a support report can be read in another language
/// (spec 21.11).
/// </summary>
public sealed record PlanIssue(
    PlanIssueSeverity Severity,
    string Code,
    CapabilityId? Capability,
    string Detail);

/// <param name="RequiredByOutcome">The outcome asked for this capability directly.</param>
/// <param name="RequiredBy">
/// Capabilities that need this one. This is the answer to "why is this step in my plan?" —
/// spec 21.6 and step 6 both insist every action can explain its own existence.
/// </param>
public sealed record PlanStep(
    int Ordinal,
    CapabilityId Capability,
    CapabilityValue CurrentValue,
    CapabilityValue DesiredValue,
    ActionDefinition Action,
    StepGroup Group,
    bool RequiredByOutcome,
    IReadOnlyList<CapabilityId> RequiredBy)
{
    [JsonIgnore]
    public bool NeedsElevation => Action.Privilege == PrivilegeLevel.Administrator;

    [JsonIgnore]
    public bool IsManualStep => Action.IsManualStep;

    [JsonIgnore]
    public bool IsIrreversible => Action.Reversible.Mode == ReversibilityMode.None;
}

/// <param name="RestartAfter">
/// The single restart that closes this phase. Spec 21.8: one plan never asks for two restarts
/// when the dependencies allow them to be merged, so this is the strongest restart any step in
/// the phase needs — a firmware restart also completes pending Windows component changes.
/// </param>
public sealed record PlanPhase(
    int Index,
    IReadOnlyList<PlanStep> Steps,
    RestartKind RestartAfter);

/// <summary>Spec 21.7 — the header the user reads before Apply.</summary>
public sealed record PlanCost(
    int Changes,
    int Restarts,
    int EstimatedSeconds,
    int ManualSteps,
    int IrreversibleChanges)
{
    [JsonIgnore]
    public int EstimatedMinutes => Math.Max(1, (int)Math.Ceiling(EstimatedSeconds / 60.0));

    public static PlanCost Empty { get; } = new(0, 0, 0, 0, 0);
}

public enum PlanOutlook
{
    /// <summary>Nothing to do. Spec 21.5 point 4: this is a good result and gets designed, not hidden.</summary>
    AlreadySatisfied = 0,

    Reachable,

    /// <summary>Reachable, but the user has to do at least one step by hand.</summary>
    ReachableWithManualSteps,

    /// <summary>Not reachable on this machine. The issues say why.</summary>
    NotReachable,
}

/// <summary>
/// A difference plan for one machine and one outcome — the output of the State Compiler.
/// </summary>
/// <remarks>
/// Everything needed to reproduce it is carried with it (spec 9.5, 12.3): the same snapshot plus
/// the same graph, rule and catalog versions must produce a byte-identical <see cref="Hash"/>.
/// The privileged side refuses work whose hash does not match what the user reviewed (spec 19.1).
/// </remarks>
public sealed record Plan(
    string Id,
    string OutcomeId,
    string OutcomeVersion,
    string SnapshotId,
    MachineIdentity Machine,
    string GraphVersion,
    string RuleVersion,
    string CatalogVersion,
    DateTimeOffset CompiledAt,
    IReadOnlyList<PlanPhase> Phases,
    IReadOnlyList<PlanIssue> Issues,
    IReadOnlyList<VerificationCheck> FinalVerification,
    PlanCost Cost,
    PlanOutlook Outlook,
    string Hash)
{
    [JsonIgnore]
    public IEnumerable<PlanStep> Steps => Phases.SelectMany(p => p.Steps);

    [JsonIgnore]
    public bool HasBlockers => Issues.Any(i => i.Severity == PlanIssueSeverity.Blocker);

    [JsonIgnore]
    public bool RequiresElevation => Steps.Any(s => s.NeedsElevation);

    [JsonIgnore]
    public IReadOnlyList<PreflightKind> RequiredPreflight =>
        [.. Steps.SelectMany(s => s.Action.Preflight).Distinct().Order()];
}

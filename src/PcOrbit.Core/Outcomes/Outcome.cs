using PcOrbit.Core.Model;

namespace PcOrbit.Core.Outcomes;

public enum OutcomeCategory
{
    General = 0,
    Developer,
    Gaming,
    Meeting,
    Power,
    Security,
}

/// <param name="Optional">
/// A miss degrades the outcome but does not make it "not reached". Keeps us honest about
/// nice-to-haves instead of failing a whole plan over one of them (spec 9.4).
/// </param>
public sealed record CapabilityRequirement(
    CapabilityId Capability,
    CapabilityValue Expected,
    bool Optional = false);

public sealed record VerificationCheck(CapabilityId Check, CapabilityValue Expected);

/// <summary>
/// A desired state the user can ask for: "get this PC ready for Docker".
/// </summary>
/// <remarks>
/// Spec 12.2: an outcome declares required capability <em>states</em> and never names actions.
/// Which action gets there — and whether it is Auto, Guided or impossible on this machine — is
/// the compiler's decision, made per machine. That is the whole difference from a preset script.
/// </remarks>
public sealed record Outcome(
    string Id,
    string Version,
    string TitleKey,
    string DescriptionKey,
    OutcomeCategory Category,
    IReadOnlyList<CapabilityRequirement> Requires,
    IReadOnlyList<VerificationCheck> Verify);

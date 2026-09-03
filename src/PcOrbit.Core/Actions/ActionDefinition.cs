using System.Text.Json.Serialization;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Actions;

/// <summary>Spec 8.3.2. Ordered by how much the app can do for the user.</summary>
public enum WriteMode
{
    /// <summary>We cannot read it and have no route to change it.</summary>
    Unsupported = 0,

    /// <summary>Readable, but there is no safe route to change it — not even a guided one.</summary>
    ReadOnly,

    /// <summary>The user changes it themselves, with vendor-level instructions.</summary>
    GuidedGeneric,

    /// <summary>The user changes it themselves, with instructions verified on this model.</summary>
    GuidedVerified,

    /// <summary>We change it through an official vendor or OS interface. Still verified after restart.</summary>
    Auto,
}

public enum PrivilegeLevel
{
    User = 0,
    Administrator,
}

/// <summary>Spec 19.2.</summary>
public enum RiskClass
{
    Low = 0,
    Medium,
    High,
    Critical,
}

public enum RestartKind
{
    None = 0,

    /// <summary>A normal Windows restart.</summary>
    Windows,

    /// <summary>A restart into the firmware setup screen, where the user does one step.</summary>
    Firmware,
}

public enum ReversibilityMode
{
    /// <summary>Must be shown as irreversible before Apply (spec 9.1, 21.7).</summary>
    None = 0,

    /// <summary>We can only tell the user how to undo it. The UI shows instructions, not a hidden button (spec 21.9).</summary>
    ManualGuided,

    /// <summary>An inverse transaction exists and goes through the same preview, apply and verify.</summary>
    Automatic,
}

public enum PreflightKind
{
    Elevation,
    Bitlocker,
    PowerSource,
    UnsavedWork,
    RecoveryAvailable,
    BiosPassword,
    DiskSpace,
}

public sealed record CapabilityAssertion(CapabilityId Capability, CapabilityValue State);

public sealed record Reversibility(ReversibilityMode Mode, string? Strategy = null, string? InverseActionId = null)
{
    public static Reversibility None { get; } = new(ReversibilityMode.None);
}

/// <summary>Spec 10.1 — apply, then ask the user whether the machine still works, then keep or revert.</summary>
public sealed record SafeApplyPolicy(bool Enabled, int ConfirmWithinSeconds, bool AutoRevert)
{
    public static SafeApplyPolicy Off { get; } = new(false, 0, false);
}

public sealed record VerificationStep(CapabilityId Capability, CapabilityValue Expected, bool AfterRestart);

public sealed record ActionCompatibility(
    int? OsMinBuild = null,
    int? OsMaxBuild = null,
    IReadOnlyList<string>? Editions = null,
    IReadOnlyList<string>? Vendors = null,
    IReadOnlyList<string>? Models = null,
    IReadOnlyList<CpuVendor>? CpuVendors = null)
{
    public static ActionCompatibility Any { get; } = new();

    /// <summary>
    /// Spec 12.3: the compiler must not put an action into a plan that cannot run on this
    /// machine. Unknown machine facts do not block — we would rather preflight tell the truth
    /// at apply time than silently drop the only route to the outcome.
    /// </summary>
    public bool Matches(MachineIdentity machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        if (OsMinBuild is { } min && machine.OsBuild != 0 && machine.OsBuild < min)
        {
            return false;
        }

        if (OsMaxBuild is { } max && machine.OsBuild != 0 && machine.OsBuild > max)
        {
            return false;
        }

        if (Editions is { Count: > 0 } editions
            && !editions.Any(e => machine.OsEdition.Contains(e, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (Vendors is { Count: > 0 } vendors
            && !VendorMatcher.MatchesAny(machine.SystemVendor, vendors)
            && !VendorMatcher.MatchesAny(machine.BaseBoardVendor, vendors))
        {
            return false;
        }

        if (Models is { Count: > 0 } models
            && !VendorMatcher.ModelMatchesAny(machine.SystemModel, models)
            && !VendorMatcher.ModelMatchesAny(machine.BaseBoardProduct, models))
        {
            return false;
        }

        if (CpuVendors is { Count: > 0 } cpus && !cpus.Contains(machine.CpuVendor))
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// A declared, versioned unit of change (spec 17).
/// </summary>
/// <remarks>
/// The manifest holds no script. <see cref="Executor"/> names a compiled adapter that was code
/// reviewed and allowlisted; an unknown executor id fails the plan rather than falling back to
/// a shell (spec 17.1, 19.1). Every action declares detect (via <see cref="Provides"/> and
/// <see cref="Requires"/>), preflight, apply, verify and rollback — spec 28 makes that mandatory,
/// so it is expressed in the type instead of in a review checklist.
/// </remarks>
public sealed record ActionDefinition(
    string Id,
    string Version,
    string TitleKey,
    string Executor,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<CapabilityAssertion> Provides,
    IReadOnlyList<CapabilityAssertion> Requires,
    WriteMode WriteMode,
    PrivilegeLevel Privilege,
    RiskClass Risk,
    RestartKind Restart,
    SafeApplyPolicy SafeApply,
    Reversibility Reversible,
    int EstimatedSeconds,
    IReadOnlyList<PreflightKind> Preflight,
    ActionCompatibility Compatibility,
    string? GuideId,
    IReadOnlyList<VerificationStep> Verify)
{
    /// <summary>True when the user has to do something by hand (spec 21.7 plan cost header).</summary>
    [JsonIgnore]
    public bool IsManualStep =>
        WriteMode is WriteMode.GuidedGeneric or WriteMode.GuidedVerified;

    /// <summary>True when running this action would satisfy the requirement.</summary>
    public bool CanProvide(CapabilityId capability, CapabilityValue desired) =>
        Provides.Any(p => p.Capability.Equals(capability) && p.State.Satisfies(desired));
}

using PcOrbit.Core.Model;

namespace PcOrbit.Core.Graph;

/// <summary>Spec 11.1.</summary>
public enum NodeKind
{
    HardwareDevice,
    HardwareCapability,
    FirmwareCapability,
    Driver,
    OsFeature,
    Setting,
    Application,
    Workload,
    ConnectionPath,
    Constraint,
    VerificationRule,
}

/// <summary>
/// Spec 11.2. <c>depends_on</c> alone is not expressive enough: the compiler must plan from
/// <see cref="Requires"/>, while <see cref="Affects"/> and <see cref="Limits"/> exist to explain
/// a reading without ever authorising a change.
/// </summary>
public enum EdgeKind
{
    /// <summary>The only edge the compiler plans from.</summary>
    Requires,

    Supports,
    SupportedBy,
    ConfiguredBy,
    ProvidedBy,
    ConflictsWith,
    Limits,
    ConnectedThrough,
    VerifiedBy,
    ChangedBy,
    Affects,
    CompatibleWith,
}

/// <summary>
/// Narrows a relation to the machines it was actually established on. An empty scope means
/// "everywhere", which is only honest for platform facts documented by Microsoft.
/// </summary>
public sealed record GraphScope(
    int? OsMinBuild = null,
    int? OsMaxBuild = null,
    IReadOnlyList<string>? Vendors = null,
    IReadOnlyList<string>? Models = null,
    IReadOnlyList<CpuVendor>? CpuVendors = null)
{
    public static GraphScope Everywhere { get; } = new();

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

/// <param name="Observable">
/// False means: on a typical machine this reads Unknown and that is expected. The checkup must
/// not raise a finding from it, and an outcome must not require it (spec 8.3.1).
/// </param>
public sealed record CapabilityNode(
    CapabilityId Id,
    NodeKind Kind,
    string DisplayKey,
    IReadOnlyList<string> Aliases,
    ValueKind ValueKind = ValueKind.State,
    string? Unit = null,
    bool Observable = true);

public enum ValueKind
{
    State,
    Scalar,
    Version,
}

/// <param name="Expected">
/// For <see cref="EdgeKind.Requires"/>: the value <c>To</c> must hold. Defaults to <c>enabled</c>.
/// </param>
public sealed record CapabilityEdge(
    CapabilityId From,
    EdgeKind Kind,
    CapabilityId To,
    CapabilityValue Expected,
    Evidence Evidence,
    GraphScope Scope);

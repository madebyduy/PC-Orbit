namespace PcOrbit.Core.Abstractions;

/// <param name="Detail">
/// The second line the UI shows: a resolution, a capacity, an interface. Null when the machine
/// reported only a name.
/// </param>
public sealed record HardwarePart(string Kind, string Name, string? Detail = null, string? Extra = null);

/// <param name="Letter">The drive letter, with its colon: <c>C:</c>.</param>
/// <param name="Label">The user's own name for it, or null when Windows has none.</param>
public sealed record VolumeInfo(string Letter, string? Label, string? FileSystem, double TotalGb, double FreeGb)
{
    public double UsedGb => Math.Max(0, TotalGb - FreeGb);

    public double UsedPercent => TotalGb <= 0 ? 0 : Math.Clamp(UsedGb / TotalGb * 100d, 0d, 100d);
}

/// <summary>
/// The inventory behind a Hardware Center (spec 8.2): what is physically in and attached to this
/// machine, as opposed to what is configured.
/// </summary>
/// <remarks>
/// Kept out of the capability graph on purpose. A GPU model or a monitor name is not something a
/// plan can target or verification can check — it is a fact to show, and mixing the two would let
/// an outcome "require" a monitor. Lists are empty, never fabricated, when the machine says
/// nothing.
/// </remarks>
public sealed record HardwareInventory(
    HardwarePart? Gpu = null,
    double? TotalRamGb = null,
    IReadOnlyList<HardwarePart>? MemoryModules = null,
    IReadOnlyList<HardwarePart>? Disks = null,
    IReadOnlyList<HardwarePart>? Monitors = null,
    IReadOnlyList<HardwarePart>? Audio = null,
    IReadOnlyList<HardwarePart>? Input = null,
    IReadOnlyList<HardwarePart>? Network = null,
    IReadOnlyList<VolumeInfo>? Volumes = null)
{
    public static HardwareInventory Empty { get; } = new();

    public IReadOnlyList<HardwarePart> All =>
    [
        .. Monitors ?? [],
        .. Audio ?? [],
        .. Input ?? [],
        .. Network ?? [],
    ];
}

public interface IHardwareInventory
{
    Task<HardwareInventory> ReadAsync(CancellationToken cancellationToken = default);
}

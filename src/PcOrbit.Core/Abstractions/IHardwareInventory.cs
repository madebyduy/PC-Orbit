namespace PcOrbit.Core.Abstractions;

/// <param name="Detail">
/// The second line the UI shows: a resolution, a capacity, an interface. Null when the machine
/// reported only a name.
/// </param>
public sealed record HardwarePart(string Kind, string Name, string? Detail = null, string? Extra = null);

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
    IReadOnlyList<HardwarePart>? Network = null)
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

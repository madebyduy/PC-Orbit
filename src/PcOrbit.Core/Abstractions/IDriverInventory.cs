using PcOrbit.Core.Model;

namespace PcOrbit.Core.Abstractions;

/// <param name="ProblemCode">
/// Windows' own device problem code, or null when the device reports none. Non-null is the only
/// thing here that is evidence of a fault — a driver being old is not.
/// </param>
/// <param name="IsSigned">
/// Null when the signature could not be checked, which is not the same as unsigned.
/// </param>
public sealed record DriverEntry(
    string DeviceName,
    string? Manufacturer,
    string? Version,
    DateOnly? Date,
    string? Provider,
    bool? IsSigned,
    int? ProblemCode,
    string? Class,
    Evidence Evidence)
{
    /// <summary>A device Windows is currently reporting a problem with.</summary>
    public bool HasProblem => ProblemCode is > 0;
}

/// <param name="Problem">Non-null when the list is incomplete, with the reason.</param>
public sealed record DriverInventoryResult(IReadOnlyList<DriverEntry> Drivers, string? Problem = null)
{
    public static DriverInventoryResult Empty { get; } = new([]);

    public IReadOnlyList<DriverEntry> Faulty => [.. Drivers.Where(d => d.HasProblem)];
}

/// <summary>
/// What is driving the hardware, and whether Windows is happy with it.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, and narrowed on purpose. The deep research proposes a "Driver Steward" that installs
/// and rolls back drivers; spec 23.2 excludes a driver updater, and ADR 0004 keeps that exclusion.
/// What survives the narrowing is the half that is both useful and safe: an inventory with
/// provider, version, date, signature and — the part that actually matters — Windows' own problem
/// code for each device.
/// </para>
/// <para>
/// The distinction this exists to preserve: a driver from 2019 is not a fault. A device with
/// problem code 28 is. Tools that sort by date and call the top of the list "outdated drivers" are
/// how people are talked into installing something worse than what they had.
/// </para>
/// </remarks>
public interface IDriverInventory
{
    Task<DriverInventoryResult> ReadAsync(CancellationToken cancellationToken = default);
}

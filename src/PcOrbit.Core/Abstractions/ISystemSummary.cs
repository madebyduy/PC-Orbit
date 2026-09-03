namespace PcOrbit.Core.Abstractions;

/// <param name="Label">Volume label, empty on an unnamed drive.</param>
public sealed record DriveSummary(string Name, string Label, double FreeGb, double TotalGb)
{
    public double UsedPercent => TotalGb > 0 ? (TotalGb - FreeGb) / TotalGb * 100d : 0d;
}

/// <param name="Source">Where Windows starts it from — the registry hive or the Startup folder.</param>
public sealed record StartupItem(string Name, string Source);

/// <param name="RealTimeProtection">
/// Null when no security product answered. Absent is not the same as off: a third-party antivirus
/// switches Defender off by design, and calling that "unprotected" would be wrong (spec 6.6).
/// </param>
public sealed record SecuritySummary(
    bool? AntivirusRunning = null,
    bool? RealTimeProtection = null,
    DateTimeOffset? SignaturesUpdated = null,
    DateTimeOffset? LastScan = null,
    string? Product = null);

public sealed record UpdateSummary(string? LatestPatch = null, DateTimeOffset? InstalledOn = null, int Count = 0);

public sealed record NetworkSummary(
    string? Adapter = null,
    string? IpAddress = null,
    string? Gateway = null,
    IReadOnlyList<string>? DnsServers = null,
    string? HostName = null);

/// <param name="Celsius">
/// Null when this machine exposes no temperature to Windows, which is the common case on desktops:
/// the ACPI thermal zone is optional and most consumer boards report only through a vendor driver.
/// </param>
/// <param name="Reason">Why there is no number. Shown to the user verbatim in Advanced detail.</param>
public sealed record ThermalReading(double? Celsius, string Source, string? Reason = null);

/// <summary>
/// The wider read-only picture behind the dashboard: storage, what starts with Windows, security,
/// patches, the network, and any temperature the machine is willing to report.
/// </summary>
/// <remarks>
/// None of this is capability state — nothing here is planned against or verified. It is the
/// context a person needs to judge their own machine (spec 8.1, 8.10, 8.17), so every field is
/// nullable and an unreadable one stays null rather than becoming a confident zero.
/// </remarks>
public sealed record SystemSummary(
    IReadOnlyList<DriveSummary>? Drives = null,
    IReadOnlyList<StartupItem>? Startup = null,
    SecuritySummary? Security = null,
    UpdateSummary? Updates = null,
    NetworkSummary? Network = null,
    ThermalReading? Thermal = null)
{
    public static SystemSummary Empty { get; } = new();
}

public interface ISystemSummary
{
    Task<SystemSummary> ReadAsync(CancellationToken cancellationToken = default);
}

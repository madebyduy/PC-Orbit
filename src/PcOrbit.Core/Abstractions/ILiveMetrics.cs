namespace PcOrbit.Core.Abstractions;

/// <summary>
/// Live gauges: what the machine is doing <em>right now</em>, as opposed to what it is configured
/// to be.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="Model.CapabilityReading"/>. A capability value is a
/// configuration fact that a plan can target and verification can check; a gauge is a moving
/// number nobody applies or verifies. Mixing them would let a plan "target" 40% CPU.
/// </para>
/// <para>
/// Every field is nullable, and null means exactly one thing: this machine did not tell us.
/// Callers must render that as unknown, never as zero (spec 6.6, 21.3).
/// </para>
/// </remarks>
public sealed record LiveMetrics(
    TimeSpan? Uptime = null,
    double? CpuPercent = null,
    double? RamUsedGb = null,
    double? RamTotalGb = null,
    double? DiskUsedGb = null,
    double? DiskTotalGb = null,
    string? NetworkAdapter = null,
    double? NetworkLinkMbps = null,
    double? NetworkDownMbps = null,
    double? NetworkUpMbps = null,
    int? BatteryPercent = null,
    bool? OnBattery = null)
{
    /// <summary>Nothing could be read. The honest default before the first sample.</summary>
    public static LiveMetrics None { get; } = new();

    public double? RamPercent => Ratio(RamUsedGb, RamTotalGb);

    public double? DiskPercent => Ratio(DiskUsedGb, DiskTotalGb);

    public double? DiskFreeGb => DiskTotalGb - DiskUsedGb;

    private static double? Ratio(double? used, double? total) =>
        used is { } u && total is { } t && t > 0 ? u / t * 100d : null;
}

/// <summary>
/// Samples the live gauges. Implementations must be cheap enough to call every second and must
/// never throw: an unreadable gauge is a null field, not an exception.
/// </summary>
public interface ILiveMetrics
{
    LiveMetrics Sample();
}

/// <summary>Reports nothing. Used where live gauges are not wanted, such as the CLI.</summary>
public sealed class NoLiveMetrics : ILiveMetrics
{
    public static NoLiveMetrics Instance { get; } = new();

    public LiveMetrics Sample() => LiveMetrics.None;
}

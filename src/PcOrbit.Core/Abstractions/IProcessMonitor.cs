namespace PcOrbit.Core.Abstractions;

/// <param name="CpuPercent">
/// Share of one wall-clock second spent on the CPU, across all cores, since the previous sample.
/// Null on the first sample, because a rate needs two readings (spec 6.6 — no invented zero).
/// </param>
/// <param name="Path">
/// The executable, when this process could be asked for it. Null for a process that belongs to
/// another user or runs elevated above this one — Windows will not say, and that is reported as
/// not knowing rather than guessed from the name.
/// </param>
public sealed record ProcessUsage(string Name, int Id, double? CpuPercent, double MemoryMb, string? Path = null);

/// <summary>
/// Which programs are using this machine right now — the "what is making my PC slow" answer
/// (spec 8.10, 8.14).
/// </summary>
/// <remarks>
/// Read-only and never a lever: nothing in v0.1 kills or throttles a process. A number that
/// changes twice a second is not a capability, so this is a live port like
/// <see cref="ILiveMetrics"/>, not a graph node.
/// </remarks>
public interface IProcessMonitor
{
    /// <summary>The heaviest processes, busiest first. Fewer than asked for is normal.</summary>
    IReadOnlyList<ProcessUsage> Top(int count);
}

/// <summary>Reports nothing, for surfaces that do not show processes.</summary>
public sealed class NoProcessMonitor : IProcessMonitor
{
    public static NoProcessMonitor Instance { get; } = new();

    public IReadOnlyList<ProcessUsage> Top(int count) => [];
}

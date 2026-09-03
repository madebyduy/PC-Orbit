using PcOrbit.Core.Model;

namespace PcOrbit.Core.Abstractions;

/// <summary>Time, injected so transaction and event timestamps are testable.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset Now => DateTimeOffset.Now;
}

/// <summary>Ids for snapshots, plans and transactions. Injected so golden tests are stable.</summary>
public interface IIdGenerator
{
    string NewId(string prefix);
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public static GuidIdGenerator Instance { get; } = new();

    public string NewId(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        // 12 hex chars is plenty to keep local ids unique and short enough to read out on a call.
        return $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];
    }
}

/// <summary>
/// Reads the machine. The one port every adapter implements (spec 18.3 observation adapters).
/// </summary>
public interface IStateScanner
{
    /// <summary>
    /// Reads what it can and records <see cref="CapabilityValue.Unknown"/> with a reason for what
    /// it cannot. Never throws for a single failed probe: a partial snapshot beats no snapshot.
    /// </summary>
    Task<StateSnapshot> ScanAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Re-reads one capability from the machine. Verification goes through this rather than through
/// the executor that just made the change, so "it worked" is always independent evidence
/// (spec 6.4, 8.3.2 — Auto does not get to skip verification).
/// </summary>
public interface ICapabilityReader
{
    /// <summary>Null when nothing in the adapter set knows how to read this capability.</summary>
    Task<CapabilityReading?> ReadAsync(CapabilityId capability, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards anything cached. The transaction engine calls this after every applied step.
    /// </summary>
    /// <remarks>
    /// Reading the machine is expensive enough that an implementation will want to cache, and a
    /// cache is exactly how you end up verifying a change against the value from before it. So
    /// invalidation is part of the contract rather than an implementation detail: the engine says
    /// "the machine just changed", and a reader that caches nothing ignores it.
    /// </remarks>
    void Invalidate()
    {
        // Nothing to do for a reader that always goes to the machine.
    }
}

/// <summary>Whether the current process can actually perform administrator actions.</summary>
public interface IElevationContext
{
    bool IsElevated { get; }
}

/// <summary>
/// Keeps scans so a plan can name the snapshot it was compiled from, and so a support report can
/// show what the machine looked like before a change (spec 9.5, 8.22).
/// </summary>
public interface ISnapshotStore
{
    Task SaveAsync(StateSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<StateSnapshot?> LoadAsync(string snapshotId, CancellationToken cancellationToken = default);

    Task<StateSnapshot?> LoadLatestAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Boot session identity, so the timeline can tie "this change" to "the restart it survived"
/// (spec 14.2 relatedRestart) and a transaction can tell that it has come back from a reboot.
/// </summary>
public interface IBootSession
{
    /// <summary>Stable for the life of one boot; different after every restart.</summary>
    string CurrentBootId { get; }

    DateTimeOffset BootedAt { get; }
}

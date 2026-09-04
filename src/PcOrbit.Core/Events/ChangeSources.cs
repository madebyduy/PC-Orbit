namespace PcOrbit.Core.Events;

/// <param name="Problem">
/// Non-null when this source could not be read. The reason travels with the empty list, because
/// "no driver installs" and "we are not allowed to see driver installs" are opposite answers to
/// the same question, and a timeline that shows the first when it means the second will send
/// someone hunting in the wrong place (spec 6.6; ADR 0004).
/// </param>
public sealed record ChangeSourceResult(string SourceId, IReadOnlyList<ChangeEvent> Events, string? Problem = null)
{
    public bool Failed => Problem is not null;

    public static ChangeSourceResult Unavailable(string sourceId, string problem) => new(sourceId, [], problem);
}

/// <summary>
/// A place changes come from that is not PC Orbit — Windows Update, the driver stack, restore
/// points, the system event log.
/// </summary>
/// <remarks>
/// <para>
/// Read live and never appended to <see cref="IEventLog"/>. The event log is append-only and holds
/// what this product did; Windows Update history is somebody else's record that is already
/// durable, and copying it in would duplicate on every run and give PC Orbit's own history a
/// second, unverifiable author.
/// </para>
/// <para>
/// This is the root-cause timeline of the deep research (§7.3) reduced to its one hard part: the
/// interesting question is never "what did PC Orbit change" but "what changed at all, in the hour
/// before this machine started misbehaving".
/// </para>
/// </remarks>
public interface IChangeSource
{
    /// <summary>Stable id, used to name the source when it cannot be read.</summary>
    string Id { get; }

    /// <summary>
    /// Changes at or after <paramref name="since"/>. Never throws for an unreadable source:
    /// it returns <see cref="ChangeSourceResult.Unavailable"/> with the reason.
    /// </summary>
    Task<ChangeSourceResult> ReadAsync(DateTimeOffset since, CancellationToken cancellationToken = default);
}

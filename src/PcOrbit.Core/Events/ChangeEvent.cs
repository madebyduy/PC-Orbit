using PcOrbit.Core.Model;

namespace PcOrbit.Core.Events;

public enum EventSource
{
    Unknown = 0,
    PcOrbit,
    WindowsUpdate,
    Driver,
    Firmware,
    App,
    Device,
    User,
    EventLog,
    Restore,
}

public enum EventCategory
{
    Setting = 0,
    Feature,
    Firmware,
    Driver,
    Update,
    Device,
    Profile,
    Transaction,
    Restart,
    Rollback,
    Crash,
    Preflight,
    Drift,

    /// <summary>
    /// A point the machine can be returned to — a Windows restore point, most often created by
    /// something else that was about to change the system.
    /// </summary>
    Checkpoint,
}

public enum Initiator
{
    Unknown = 0,
    User,
    PcOrbit,
    System,
    ExternalApp,
}

/// <summary>
/// The one history record shape for the whole product (spec 14.2).
/// </summary>
/// <remarks>
/// Spec step 7 is emphatic about this: every transaction writes normalised events from v0.1,
/// even though the Timeline UI comes later. If each module logged its own way, Drift and
/// Regression would later have to reconstruct history from inconsistent logs — and would get it
/// wrong. <see cref="RelatedRestart"/> is what makes "what changed before this problem?" answerable.
/// </remarks>
public sealed record ChangeEvent(
    string Id,
    DateTimeOffset Timestamp,
    EventSource Source,
    EventCategory Category,
    string Component,
    string? Before,
    string? After,
    Initiator Initiator,
    Confidence Confidence,
    Evidence? Evidence = null,
    string? RelatedTransaction = null,
    string? RelatedRestart = null,
    string? MessageKey = null);

/// <summary>Filters for reading history back. Kept small on purpose; Timeline will grow it.</summary>
public sealed record EventQuery(
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    string? Component = null,
    string? TransactionId = null,
    EventCategory? Category = null,
    int Limit = 200);

/// <summary>Append-only history. Nothing in the product edits or deletes an event.</summary>
public interface IEventLog
{
    Task AppendAsync(ChangeEvent changeEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChangeEvent>> QueryAsync(EventQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Swallows events. Used for dry runs, where nothing happened and nothing is recorded.</summary>
public sealed class NullEventLog : IEventLog
{
    public static NullEventLog Instance { get; } = new();

    public Task AppendAsync(ChangeEvent changeEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<ChangeEvent>> QueryAsync(EventQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChangeEvent>>([]);
}

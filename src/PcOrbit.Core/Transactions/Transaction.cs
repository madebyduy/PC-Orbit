using System.Text.Json.Serialization;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Transactions;

/// <summary>
/// Which direction a transaction moves the machine in.
/// </summary>
/// <remarks>
/// Spec 21.9: undo is not a special code path — it is a reverse transaction that goes through the
/// same preview, apply and verify as the change it undoes. This flag is the only difference the
/// engine sees: an <see cref="Undo"/> transaction asks each executor to roll back instead of apply.
/// </remarks>
public enum TransactionKind
{
    Apply = 0,

    /// <summary>A reverse transaction. <see cref="Transaction.UndoOf"/> names what it undoes.</summary>
    Undo,
}

/// <summary>Spec 9.2.</summary>
public enum TransactionState
{
    Draft = 0,
    Ready,
    Applying,

    /// <summary>Waiting for the user to restart. Survives days; nothing expires (spec 21.8).</summary>
    AwaitingRestart,

    Resuming,
    Verifying,
    Completed,

    /// <summary>Some of it worked. Spec 9.4 forbids reporting this as "Done".</summary>
    PartiallyCompleted,

    Failed,
    RolledBack,
}

public enum StepState
{
    Pending = 0,
    Applying,

    /// <summary>Applied, waiting for the restart that makes it effective.</summary>
    AwaitingRestart,

    /// <summary>Staged; the user performs this one themselves (guided firmware).</summary>
    AwaitingUserAction,

    Applied,
    Verified,

    /// <summary>Applied but the machine does not agree. Never reported as success (spec 6.6).</summary>
    VerifyFailed,

    Skipped,
    Failed,
    RolledBack,
}

/// <param name="Actual">
/// What the machine reads <em>after</em> the change. Distinct from <paramref name="Requested"/>
/// because "we asked for X" and "the machine now says X" are different facts (spec 20.1).
/// </param>
public sealed record StepExecution(
    int Ordinal,
    int PhaseIndex,
    string ActionId,
    string ActionVersion,
    CapabilityId Capability,
    CapabilityValue Before,
    CapabilityValue Requested,
    StepState State,
    CapabilityValue? Actual = null,
    string? MessageKey = null,
    string? Detail = null,
    Evidence? VerificationEvidence = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    int Attempts = 0)
{
    [JsonIgnore]
    public bool IsTerminalSuccess => State is StepState.Verified or StepState.Skipped;

    [JsonIgnore]
    public bool IsTerminalFailure => State is StepState.Failed or StepState.VerifyFailed;
}

/// <summary>
/// Spec 9.5 — everything needed to explain, audit or reproduce a transaction later.
/// </summary>
public sealed record Provenance(
    string OutcomeId,
    string OutcomeVersion,
    string GraphVersion,
    string RuleVersion,
    string CatalogVersion,
    string MachineFingerprint,
    int OsBuild,
    string PlanHash,
    string SnapshotId,
    string AppVersion)
{
    public static Provenance FromPlan(Plan plan, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new Provenance(
            plan.OutcomeId,
            plan.OutcomeVersion,
            plan.GraphVersion,
            plan.RuleVersion,
            plan.CatalogVersion,
            plan.Machine.Fingerprint,
            plan.Machine.OsBuild,
            plan.Hash,
            plan.SnapshotId,
            appVersion);
    }
}

/// <summary>
/// A change set being applied. Immutable: every state change produces a new value that the store
/// persists as a checkpoint, so a crash or a power cut leaves a readable, resumable record
/// rather than a half-written row (spec 9.3, 28 fault injection).
/// </summary>
public sealed record Transaction(
    string Id,
    Plan Plan,
    TransactionState State,
    int CurrentPhase,
    IReadOnlyList<StepExecution> Steps,
    Provenance Provenance,
    ExecutionMode Mode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string StartBootId,
    string? ResumeBootId = null,
    RestartKind PendingRestart = RestartKind.None,
    string? OutcomeVerdictKey = null,
    TransactionKind Kind = TransactionKind.Apply,
    string? UndoOf = null)
{
    /// <summary>
    /// The plan travels with the transaction rather than being looked up again.
    /// </summary>
    /// <remarks>
    /// Spec 17.1: a recipe update must not change a pending plan the user already approved. After
    /// a reboot, resume replays the manifests as they were when the plan was reviewed — even if
    /// the shipped catalog moved on in between.
    /// </remarks>
    [JsonIgnore]
    public string PlanHash => Plan.Hash;

    [JsonIgnore]
    public bool IsFinished => State is TransactionState.Completed
        or TransactionState.PartiallyCompleted
        or TransactionState.Failed
        or TransactionState.RolledBack;

    [JsonIgnore]
    public bool NeedsRestart => State == TransactionState.AwaitingRestart;

    /// <summary>Spec 9.4 — the exact counts the UI must show instead of a single "Done".</summary>
    [JsonIgnore]
    public TransactionTally Tally => new(
        Completed: Steps.Count(s => s.IsTerminalSuccess),
        Failed: Steps.Count(s => s.IsTerminalFailure),
        Pending: Steps.Count(s => !s.IsTerminalSuccess && !s.IsTerminalFailure),
        RollbackAvailable: Steps.Count(s => s.State is StepState.Verified or StepState.Applied),
        ManualRecoveryRequired: Steps.Count(s => s.IsTerminalFailure));
}

public sealed record TransactionTally(
    int Completed,
    int Failed,
    int Pending,
    int RollbackAvailable,
    int ManualRecoveryRequired);

/// <summary>Checkpoint storage. Writes must be durable before the engine moves on.</summary>
public interface ITransactionStore
{
    Task SaveAsync(Transaction transaction, CancellationToken cancellationToken = default);

    Task<Transaction?> LoadAsync(string transactionId, CancellationToken cancellationToken = default);

    /// <summary>Transactions that are waiting for a restart or mid-flight — what resume looks for.</summary>
    Task<IReadOnlyList<Transaction>> ListUnfinishedAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Transaction>> ListRecentAsync(int limit, CancellationToken cancellationToken = default);
}

/// <summary>Discards checkpoints. Used for dry runs, which must leave no trace.</summary>
public sealed class NullTransactionStore : ITransactionStore
{
    public static NullTransactionStore Instance { get; } = new();

    public Task SaveAsync(Transaction transaction, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<Transaction?> LoadAsync(string transactionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<Transaction?>(null);

    public Task<IReadOnlyList<Transaction>> ListUnfinishedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Transaction>>([]);

    public Task<IReadOnlyList<Transaction>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Transaction>>([]);
}

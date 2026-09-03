using PcOrbit.Core.Model;

namespace PcOrbit.Core.Actions;

public enum ExecutionMode
{
    /// <summary>Do everything except change the machine. Executors report what they would do.</summary>
    DryRun = 0,

    Apply,
}

public enum ApplyStatus
{
    /// <summary>Done and effective now.</summary>
    Applied = 0,

    /// <summary>Done, but only takes effect after the restart this phase ends with.</summary>
    AppliedPendingRestart,

    /// <summary>Staged. The user has to do the step themselves — the guided firmware path.</summary>
    AwaitingUserAction,

    /// <summary>Nothing needed doing.</summary>
    Skipped,

    Failed,
}

/// <param name="MessageKey">i18n key for what to tell the user. Never a raw sentence.</param>
/// <param name="Detail">Technical detail for Advanced mode, logs and support reports.</param>
public sealed record ApplyOutcome(
    ApplyStatus Status,
    string? MessageKey = null,
    string? Detail = null,
    Evidence? Evidence = null)
{
    public static ApplyOutcome Applied(string? detail = null) => new(ApplyStatus.Applied, Detail: detail);

    public static ApplyOutcome PendingRestart(string? detail = null) =>
        new(ApplyStatus.AppliedPendingRestart, Detail: detail);

    public static ApplyOutcome Failed(string messageKey, string? detail = null) =>
        new(ApplyStatus.Failed, messageKey, detail);
}

/// <param name="Before">
/// The value we read immediately before applying. Recorded so history has real before/after
/// rather than "what we assumed" (spec 20.1).
/// </param>
public sealed record ActionExecutionContext(
    ActionDefinition Action,
    CapabilityId Capability,
    CapabilityValue Before,
    CapabilityValue Requested,
    ExecutionMode Mode,
    MachineIdentity Machine);

/// <summary>
/// The compiled, reviewed code behind an action manifest.
/// </summary>
/// <remarks>
/// Spec 17.1 and 19.1: a manifest may only name an executor, never carry a script, and the
/// privileged side accepts an action id plus schema-valid parameters rather than a command line.
/// Verification is deliberately not on this interface — it happens through
/// <see cref="Abstractions.ICapabilityReader"/>, so "did it work?" is answered by reading the
/// machine again, not by the code that just claimed success.
/// </remarks>
public interface IActionExecutor
{
    /// <summary>Must match the <c>executor</c> field of the manifests that use it.</summary>
    string Id { get; }

    Task<ApplyOutcome> ApplyAsync(ActionExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores the previous state. Called by an undo transaction (spec 21.9).
    /// </summary>
    /// <remarks>
    /// The context reads exactly like a forward one: <see cref="ActionExecutionContext.Before"/> is
    /// what the machine says right now, and <see cref="ActionExecutionContext.Requested"/> is the
    /// value to end up at — the value recorded before the original change. An implementation that
    /// needs the old value must therefore use <c>Requested</c>, never <c>Before</c>.
    /// </remarks>
    Task<ApplyOutcome> RollbackAsync(ActionExecutionContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// The executor allowlist. An id that is not registered fails the step; there is no shell
/// fallback and no dynamic loading (spec 19.1).
/// </summary>
public sealed class ExecutorRegistry
{
    private readonly Dictionary<string, IActionExecutor> _executors;

    public ExecutorRegistry(IEnumerable<IActionExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);

        _executors = executors.ToDictionary(e => e.Id, e => e, StringComparer.Ordinal);
    }

    public static ExecutorRegistry Empty { get; } = new([]);

    public IActionExecutor? Resolve(string executorId) =>
        _executors.TryGetValue(executorId, out IActionExecutor? executor) ? executor : null;

    public IReadOnlyCollection<string> AllowedIds => _executors.Keys;

    /// <summary>
    /// Every manifest whose executor is not registered. Run at startup: a catalog that references
    /// an executor we do not ship is a packaging bug that must not wait to be discovered mid-apply.
    /// </summary>
    public IReadOnlyList<string> FindUnresolvable(ActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return
        [
            .. catalog.All
                .Where(a => !_executors.ContainsKey(a.Executor))
                .Select(a => $"{a.Id} -> executor '{a.Executor}'"),
        ];
    }
}

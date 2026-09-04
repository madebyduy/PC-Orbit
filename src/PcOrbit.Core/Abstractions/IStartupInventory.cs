using PcOrbit.Core.Model;

namespace PcOrbit.Core.Abstractions;

/// <summary>Where Windows starts something from. Each one is disabled a different way.</summary>
public enum StartupLocation
{
    Unknown = 0,

    /// <summary>A Run key under HKCU — this user only.</summary>
    UserRegistry,

    /// <summary>A Run key under HKLM — every user on the machine.</summary>
    MachineRegistry,

    /// <summary>A shortcut in a Startup folder.</summary>
    StartupFolder,

    /// <summary>A scheduled task with a logon trigger.</summary>
    ScheduledTask,

    /// <summary>
    /// A packaged application's own startup task.
    /// </summary>
    /// <remarks>
    /// Store and MSIX applications do not write to a Run key. They declare a startup task in their
    /// manifest, and Windows records whether it is on in per-package state of its own — which is
    /// why an inventory that only reads Run keys shows fewer entries than Task Manager does.
    /// </remarks>
    PackagedApp,
}

/// <param name="Enabled">
/// Null when we could read that the entry exists but not whether it is switched on. Task Manager
/// keeps that flag somewhere other than the Run key itself, and an entry we cannot classify is
/// reported as unclassified rather than assumed to be running (spec 6.6).
/// </param>
/// <param name="Publisher">Null when the file is unsigned or the signature could not be checked.</param>
/// <param name="Source">
/// Exactly where this came from — the registry key, the folder, or the scheduled-task path. Kept
/// because it is what the change has to address, and because a user deciding whether to switch
/// something off deserves to see where it lives.
/// </param>
public sealed record StartupEntry(
    string Name,
    string Command,
    StartupLocation Location,
    bool? Enabled,
    string? Publisher,
    Evidence Evidence,
    string Source = "");

/// <param name="Problem">
/// Non-null when the inventory is incomplete, with the reason. An empty list and a blocked read
/// look identical to a user otherwise, and one of them means "nothing starts with Windows".
/// </param>
public sealed record StartupInventoryResult(IReadOnlyList<StartupEntry> Entries, string? Problem = null);

/// <summary>
/// What starts when this PC does.
/// </summary>
/// <remarks>
/// <para>
/// The read-only half of the research's Startup &amp; Background Controller (§8.2). Inventory
/// ships; switching entries off does not, and that split is deliberate rather than unfinished:
/// <c>ActionParameters</c> validates every parameter against an allowlist before it reaches an
/// executor (spec 17.1), and "whichever entry the user picked" is not an allowlist. Turning these
/// into actions needs a parameter kind validated against the machine's own current inventory,
/// which is a design decision that gets its own ADR (ADR 0004).
/// </para>
/// <para>
/// Until then this answers the question on its own terms — people mostly want to know what is
/// there — and it does so without offering a button it cannot make safe.
/// </para>
/// </remarks>
public interface IStartupInventory
{
    Task<StartupInventoryResult> ReadAsync(CancellationToken cancellationToken = default);
}

/// <param name="Verified">
/// True only when the machine was read again afterwards and agreed. Never taken from the command's
/// own exit code (spec 6.4).
/// </param>
public sealed record StartupChangeResult(
    string Name,
    bool RequestedEnabled,
    bool Applied,
    bool Verified,
    string? Problem = null);

/// <summary>
/// Turns a startup entry on or off.
/// </summary>
/// <remarks>
/// <para>
/// This was held back because <c>ActionParameters</c> validates every parameter against an
/// allowlist before it reaches a command (spec 17.1), and "whichever entry the user picked" is not
/// an allowlist. The way through was to notice that an allowlist does not have to be shipped data:
/// it has to be a finite set the caller cannot extend. So the permitted set is
/// <em>the machine's own inventory, re-read at the moment of the change</em> — an entry that is not
/// in it right now is refused, which makes an invented or stale name unusable rather than merely
/// unlikely (ADR 0005).
/// </para>
/// <para>
/// On top of that sits a fixed refusal list. Some entries are the machine's own defences, and a
/// tool that offers to switch off Windows Security to make boot faster is a tool that has stopped
/// understanding what it is for.
/// </para>
/// </remarks>
public interface IStartupController
{
    /// <summary>
    /// Entries this product will never change, whatever the caller asks. Exposed so the UI can
    /// show them as fixed rather than pretending they are simply absent.
    /// </summary>
    IReadOnlySet<string> NeverChange { get; }

    /// <summary>
    /// Applies the change, then re-reads the machine to confirm it.
    /// </summary>
    /// <remarks>
    /// Reversible by construction: the inverse call restores the previous state, and the caller
    /// records which it was. No separate rollback path can therefore drift from the apply path.
    /// </remarks>
    Task<StartupChangeResult> SetEnabledAsync(
        string name,
        bool enabled,
        CancellationToken cancellationToken = default);
}

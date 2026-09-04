using PcOrbit.Core.Model;

namespace PcOrbit.Core.Setup;

/// <param name="Index">The image index inside the ISO, which is what setup asks for.</param>
public sealed record MediaEdition(int Index, string Name, string? Description);

/// <param name="IsWindowsMedia">
/// False when the file mounted but is not Windows installation media. Said plainly rather than
/// discovered by setup refusing it ten minutes later.
/// </param>
/// <param name="Architecture">x64 or arm64, from the image itself.</param>
/// <param name="Editions">What is inside, so the user can see it before committing to anything.</param>
public sealed record MediaContents(
    bool IsWindowsMedia,
    string? Architecture,
    string? Build,
    IReadOnlyList<MediaEdition> Editions,
    Evidence Evidence,
    string? Problem = null);

/// <summary>How much of the machine a reinstall keeps.</summary>
public enum ReinstallScope
{
    /// <summary>
    /// Files, settings and applications survive. Windows is replaced around them — the repair
    /// install, and the only mode this product offers.
    /// </summary>
    KeepEverything = 0,

    /// <summary>Personal files survive; applications and settings do not.</summary>
    KeepFilesOnly,
}

/// <param name="Blockers">
/// Things that must be resolved first. Non-empty means nothing is launched.
/// </param>
public sealed record ReinstallReadiness(
    ReinstallScope Scope,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings)
{
    public bool CanProceed => Blockers.Count == 0;
}

/// <param name="Launched">
/// True when Windows Setup started. It is then in charge — this product does not drive its wizard,
/// and cannot report what happens after.
/// </param>
public sealed record ReinstallStart(bool Launched, string? Problem = null);

/// <summary>
/// Reinstalling Windows over itself, from an ISO, without boot media.
/// </summary>
/// <remarks>
/// <para>
/// The supported half of what tools in this category call "install without USB". Running
/// <c>setup.exe</c> from mounted media against the running system is Microsoft's own repair-install
/// path: it replaces Windows and keeps files, settings and applications, and Windows Setup itself
/// shows exactly what it is about to keep before it starts.
/// </para>
/// <para>
/// The other half — choosing a target disk, an EFI partition and a boot partition, and laying an
/// image onto them — is not here and will not be. That writes a new operating system over the one
/// currently running, and nothing in this product's design survives it: there is no checkpoint to
/// resume from and no machine left to verify against if it goes wrong. It is the one operation in
/// this whole product where a mistake cannot be undone by anything we could build (ADR 0006).
/// </para>
/// <para>
/// So what this adds is not the launch, which is one command. It is everything before it: proving
/// the ISO is what it claims to be, showing what is inside, and refusing to start while the
/// machine is in a state where a reinstall would cost the user something.
/// </para>
/// </remarks>
public interface IWindowsMediaService
{
    /// <summary>Mounts the ISO, reads what is in it, and unmounts. Changes nothing.</summary>
    Task<MediaContents> InspectAsync(string isoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this machine is in a fit state to be reinstalled, and what has to happen first.
    /// </summary>
    ReinstallReadiness Assess(StateSnapshot snapshot, MediaContents media, ReinstallScope scope);

    /// <summary>
    /// Mounts the media and starts Windows Setup. Refuses while <see cref="Assess"/> reports a
    /// blocker.
    /// </summary>
    Task<ReinstallStart> StartAsync(
        string isoPath,
        ReinstallReadiness readiness,
        CancellationToken cancellationToken = default);
}

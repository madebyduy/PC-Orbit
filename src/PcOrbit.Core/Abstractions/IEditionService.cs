using PcOrbit.Core.Model;

namespace PcOrbit.Core.Abstractions;

/// <summary>What Windows says about this installation's licence.</summary>
public enum LicenceState
{
    Unknown = 0,

    /// <summary>Activated.</summary>
    Licensed,

    /// <summary>Inside the initial grace period.</summary>
    Grace,

    /// <summary>Not activated: the watermark and the nagging.</summary>
    Notification,

    /// <summary>No licence at all.</summary>
    Unlicensed,
}

/// <param name="Channel">RETAIL, OEM_DM or VOLUME_MAK — it decides whether a key can move machines.</param>
public sealed record LicenceStatus(
    LicenceState State,
    string? Channel,
    string? Description,
    Evidence Evidence)
{
    /// <summary>
    /// True when changing edition would leave the machine needing a key it does not have.
    /// </summary>
    /// <remarks>
    /// An edition change does not carry activation with it. Starting from an installation that is
    /// already unactivated means finishing unactivated as well, and the honest thing is to say so
    /// before rather than after.
    /// </remarks>
    public bool NeedsAttentionBeforeChanging => State is not LicenceState.Licensed;
}

/// <param name="Id">
/// The edition id DISM uses, e.g. <c>Professional</c>. Comes from Windows, never from the user.
/// </param>
public sealed record TargetEdition(string Id, string DisplayName);

/// <param name="Targets">
/// Editions this installation can be moved to, as Windows itself reports them. Empty is a real
/// answer — most machines can only go up a short, fixed list.
/// </param>
/// <param name="Problem">Non-null when the list could not be read, with the reason.</param>
public sealed record EditionOptions(
    string CurrentEdition,
    LicenceStatus Licence,
    IReadOnlyList<TargetEdition> Targets,
    string? Problem = null)
{
    public bool CanChange => Problem is null && Targets.Count > 0;
}

/// <param name="Applied">Whether the command ran to completion.</param>
/// <param name="RestartRequired">An edition change always needs one; it is stated, not assumed.</param>
public sealed record EditionChangeResult(
    string Target,
    bool Applied,
    bool RestartRequired,
    string? Problem = null);

/// <summary>
/// Moves this Windows installation from one edition to another without reinstalling.
/// </summary>
/// <remarks>
/// <para>
/// A supported Windows operation — <c>DISM /Set-Edition</c> — and a genuinely useful one: a Home
/// machine that needs BitLocker, Hyper-V or Remote Desktop does not have to be flattened and
/// rebuilt. It is also a one-way door: nothing moves an installation back down an edition, so the
/// action is declared irreversible and the UI shows that before Apply (spec 21.7).
/// </para>
/// <para>
/// <strong>This product never supplies a product key.</strong> The key is the user's, typed by the
/// user, and it is what makes this an administration tool rather than something else. A key that
/// does not belong to the person using it is not a technical problem this code should be solving,
/// and shipping a list of keys is the line between the two.
/// </para>
/// </remarks>
public interface IEditionService
{
    /// <summary>The current edition, its licence, and where Windows says it can go.</summary>
    Task<EditionOptions> ReadAsync(CancellationToken cancellationToken = default);

    /// <param name="productKey">
    /// The user's own key for the target edition. Never stored, never logged, never defaulted.
    /// </param>
    Task<EditionChangeResult> ChangeAsync(
        string targetEditionId,
        string productKey,
        CancellationToken cancellationToken = default);
}

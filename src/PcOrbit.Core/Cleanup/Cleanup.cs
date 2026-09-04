using PcOrbit.Core.Model;

namespace PcOrbit.Core.Cleanup;

/// <summary>A kind of reclaimable space, named so the UI never invents a label.</summary>
public enum CleanupCategory
{
    /// <summary>Files under the user's and Windows' temp folders, past an age threshold.</summary>
    TemporaryFiles = 0,

    /// <summary>Explorer's thumbnail and icon caches. Rebuilt on demand.</summary>
    ThumbnailCache,

    /// <summary>A web browser's on-disk cache. Never its cookies, history or saved passwords.</summary>
    BrowserCache,

    /// <summary>Compiled shader caches kept by DirectX and GPU drivers.</summary>
    ShaderCache,

    /// <summary>Installers Windows Update already applied.</summary>
    UpdateCache,

    /// <summary>Servicing and setup logs Windows keeps indefinitely.</summary>
    WindowsLogs,

    /// <summary>Package caches kept by developer tools.</summary>
    DeveloperCache,

    /// <summary>Crash dumps, which may still be worth reading before they go.</summary>
    CrashDumps,

    /// <summary>Queued Windows Error Reporting payloads that were never sent.</summary>
    ErrorReports,

    /// <summary>Delivery Optimization's peer-to-peer update cache.</summary>
    UpdateDeliveryCache,

    /// <summary>Installer payloads kept so applications can repair themselves.</summary>
    InstallerCache,

    /// <summary>The Recycle Bin. Reported, never emptied by this product.</summary>
    RecycleBin,
}

/// <summary>
/// How far this product is willing to go with a category.
/// </summary>
/// <remarks>
/// <para>
/// Three levels rather than two, because collapsing them costs the user something real. A browser
/// cache is rebuilt the next time a page loads: holding it in quarantine for thirty days means the
/// disk does not actually get smaller, which is the one thing the person pressing the button came
/// for. A temp file might be the only copy of something, and there the wait is the point.
/// </para>
/// <para>
/// So <see cref="Regenerable"/> is deleted and the space is free immediately, and
/// <see cref="Reclaimable"/> goes to quarantine and comes back if it was needed.
/// </para>
/// </remarks>
public enum CleanupTrust
{
    /// <summary>
    /// Windows or the application rebuilds this from scratch on demand. Deleted outright: there is
    /// nothing an undo could restore that will not simply reappear.
    /// </summary>
    Regenerable = 0,

    /// <summary>Safe to remove, but moved to quarantine first so it can come back.</summary>
    Reclaimable,

    /// <summary>Real space, but removing it costs the user something. Reported only.</summary>
    ReportOnly,

    /// <summary>
    /// Never touched by this product at any level: WinSxS, DriverStore, the recovery partition,
    /// restore points, <c>Windows.old</c>.
    /// </summary>
    Protected,
}

/// <param name="Bytes">Measured, not estimated. The number shown before is the number reclaimed after.</param>
/// <param name="NewestItem">
/// Null when the folder is empty. Used to keep the age threshold honest: a temp folder written to
/// a minute ago belongs to something that is still running.
/// </param>
public sealed record CleanupCandidate(
    string Id,
    CleanupCategory Category,
    string Path,
    long Bytes,
    int FileCount,
    DateTimeOffset? NewestItem,
    CleanupTrust Trust,
    Evidence Evidence)
{
    public double MegaBytes => Bytes / 1024d / 1024d;

    /// <summary>Only a measured candidate with something in it can be acted on.</summary>
    public bool CanReclaim =>
        Trust is CleanupTrust.Regenerable or CleanupTrust.Reclaimable && Bytes > 0 && FileCount > 0;

    /// <summary>
    /// True when removing this frees the disk at once, rather than when the retention window closes.
    /// </summary>
    public bool FreesSpaceNow => Trust == CleanupTrust.Regenerable;
}

/// <param name="Problems">
/// Folders that could not be measured, with the reason. A survey that silently skipped a
/// permission error would under-report the disk and then under-deliver on the reclaim figure.
/// </param>
public sealed record CleanupSurvey(
    IReadOnlyList<CleanupCandidate> Candidates,
    IReadOnlyList<string> Problems)
{
    public static CleanupSurvey Empty { get; } = new([], []);

    public long ReclaimableBytes => Candidates.Where(c => c.CanReclaim).Sum(c => c.Bytes);

    /// <summary>Of that, the part the disk gets back the moment the button is pressed.</summary>
    public long ImmediateBytes => Candidates.Where(c => c.CanReclaim && c.FreesSpaceNow).Sum(c => c.Bytes);

    public long ReportedBytes => Candidates.Where(c => !c.CanReclaim).Sum(c => c.Bytes);

    public bool IsComplete => Problems.Count == 0;
}

/// <summary>Finds reclaimable space. Read-only: surveying never deletes or moves anything.</summary>
public interface ICleanupScanner
{
    Task<CleanupSurvey> SurveyAsync(CancellationToken cancellationToken = default);
}

/// <param name="Freed">Bytes the disk actually got back.</param>
/// <param name="Locked">
/// Files something else had open. Expected — a browser holds its own cache — and reported rather
/// than retried, because the honest total is the one that excludes them.
/// </param>
public sealed record CleanupDeletion(long Freed, int Removed, int Locked, IReadOnlyList<string> Failures);

/// <param name="OriginalPath">Where the file came from, so restore puts it back exactly.</param>
public sealed record QuarantinedFile(string OriginalPath, string StoredName, long Bytes);

/// <param name="ExpiresAt">
/// When this batch may be deleted for real. Until then the space is not actually reclaimed, and
/// the product says so rather than claiming a number it has not yet delivered.
/// </param>
public sealed record QuarantineBatch(
    string Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<QuarantinedFile> Files,
    IReadOnlyList<string> Failures)
{
    public long Bytes => Files.Sum(f => f.Bytes);

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}

/// <param name="Restored">Files put back where they came from.</param>
/// <param name="Failures">
/// Files that could not be put back, with the reason — most often because something has since
/// created a file at the original path.
/// </param>
public sealed record QuarantineRestore(string BatchId, int Restored, IReadOnlyList<string> Failures)
{
    public bool IsComplete => Failures.Count == 0;
}

/// <summary>
/// The undo primitive that cleanup could not exist without.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in this product undoes a change by restoring the value it read before making
/// it. A deleted file has no before-value, which is exactly why cleanup was held back until this
/// existed (ADR 0004): nothing may be removed that cannot be put back.
/// </para>
/// <para>
/// So cleanup does not delete. It <em>moves</em>, into a store that keeps the original path and a
/// retention window, and the space is only truly released when that window closes. The honest
/// consequence — that the disk does not get smaller the instant you press the button — is stated
/// rather than hidden, because the alternative is an undo that quietly does not work.
/// </para>
/// </remarks>
public interface IQuarantineStore
{
    /// <summary>How long a batch is kept before the space is released for real.</summary>
    TimeSpan Retention { get; }

    /// <summary>Moves the files of these candidates into the store. Never throws for one bad file.</summary>
    /// <remarks>Refuses anything that is not <see cref="CleanupTrust.Reclaimable"/>.</remarks>
    Task<QuarantineBatch> QuarantineAsync(
        IReadOnlyList<CleanupCandidate> candidates,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes regenerable caches outright and returns the bytes the disk got back.
    /// </summary>
    /// <remarks>
    /// The second route, and the reason the disk actually shrinks when the button is pressed. There
    /// is nothing here an undo could usefully restore: a browser cache is rebuilt by the next page
    /// load, and holding it for thirty days would mean the space stayed spoken for while the user
    /// looked at a number claiming otherwise. Refuses anything not marked
    /// <see cref="CleanupTrust.Regenerable"/>.
    /// </remarks>
    Task<CleanupDeletion> DeleteRegenerableAsync(
        IReadOnlyList<CleanupCandidate> candidates,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Empties the whole quarantine now, whatever its retention says.
    /// </summary>
    /// <remarks>
    /// The user asking for their disk back before the window closes. It is theirs to ask, so long
    /// as what they are giving up is stated first.
    /// </remarks>
    Task<long> PurgeAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Puts a batch back where it came from.</summary>
    Task<QuarantineRestore> RestoreAsync(string batchId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QuarantineBatch>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes batches past their retention window, for real. Returns the bytes released.</summary>
    Task<long> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

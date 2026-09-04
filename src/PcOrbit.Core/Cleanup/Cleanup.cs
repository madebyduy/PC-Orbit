using PcOrbit.Core.Model;

namespace PcOrbit.Core.Cleanup;

/// <summary>A kind of reclaimable space, named so the UI never invents a label.</summary>
public enum CleanupCategory
{
    /// <summary>Files under the user's and Windows' temp folders, past an age threshold.</summary>
    TemporaryFiles = 0,

    /// <summary>Explorer's thumbnail and icon caches. Rebuilt on demand.</summary>
    ThumbnailCache,

    /// <summary>Queued Windows Error Reporting payloads that were never sent.</summary>
    ErrorReports,

    /// <summary>Delivery Optimization's peer-to-peer update cache.</summary>
    UpdateDeliveryCache,

    /// <summary>The Recycle Bin. Reported, never emptied by this product.</summary>
    RecycleBin,
}

/// <summary>
/// How far this product is willing to go with a category.
/// </summary>
/// <remarks>
/// The three-way split the research asks for (§8.1), and the middle one is the important one: a
/// category is only ever automated when every file in it is regenerable <em>and</em> restorable
/// from quarantine. Anything else is shown and explained, never actioned.
/// </remarks>
public enum CleanupTrust
{
    /// <summary>Regenerable by Windows, and putting the file back restores the previous state exactly.</summary>
    Reclaimable = 0,

    /// <summary>Real space, but removing it costs the user something they may want. Reported only.</summary>
    ReportOnly,

    /// <summary>
    /// Never touched by this product at any trust level: WinSxS, DriverStore, the recovery
    /// partition, restore points, <c>Windows.old</c>.
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

    /// <summary>Only a measured, restorable candidate with something in it can be acted on.</summary>
    public bool CanReclaim => Trust == CleanupTrust.Reclaimable && Bytes > 0 && FileCount > 0;
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

    public long ReportedBytes => Candidates.Where(c => !c.CanReclaim).Sum(c => c.Bytes);

    public bool IsComplete => Problems.Count == 0;
}

/// <summary>Finds reclaimable space. Read-only: surveying never deletes or moves anything.</summary>
public interface ICleanupScanner
{
    Task<CleanupSurvey> SurveyAsync(CancellationToken cancellationToken = default);
}

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
    Task<QuarantineBatch> QuarantineAsync(
        IReadOnlyList<CleanupCandidate> candidates,
        CancellationToken cancellationToken = default);

    /// <summary>Puts a batch back where it came from.</summary>
    Task<QuarantineRestore> RestoreAsync(string batchId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QuarantineBatch>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes batches past their retention window, for real. Returns the bytes released.</summary>
    Task<long> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

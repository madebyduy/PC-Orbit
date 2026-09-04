using System.Globalization;
using PcOrbit.Core.Cleanup;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// Measures reclaimable space on this machine.
/// </summary>
/// <remarks>
/// <para>
/// Every figure here is measured by walking the folder, not estimated from a rule of thumb. That
/// costs a second or two and buys the one thing that makes a cleanup feature trustworthy: the
/// number shown before is the number reclaimed after.
/// </para>
/// <para>
/// The list of locations is deliberately short. Each entry had to clear the same bar — Windows
/// regenerates it on demand, and putting the files back restores the previous state exactly — and
/// the well-known space hogs that do not clear it (WinSxS, DriverStore, <c>Windows.old</c>, the
/// recovery partition) are absent rather than present-and-guarded, so no future edit can promote
/// one by changing a flag.
/// </para>
/// </remarks>
public sealed class WindowsCleanupScanner(TimeSpan? minimumAge = null) : ICleanupScanner
{
    /// <summary>
    /// How old a temp file must be before we touch it.
    /// </summary>
    /// <remarks>
    /// Not caution for its own sake: installers, compilers and browsers keep live state in temp
    /// folders, and a file written an hour ago probably belongs to something still running. Seven
    /// days is past any plausible session.
    /// </remarks>
    private static readonly TimeSpan DefaultMinimumAge = TimeSpan.FromDays(7);

    private readonly TimeSpan _minimumAge = minimumAge ?? DefaultMinimumAge;

    public Task<CleanupSurvey> SurveyAsync(CancellationToken cancellationToken = default)
    {
        List<CleanupCandidate> candidates = [];
        List<string> problems = [];

        DateTime cutoff = DateTime.Now - _minimumAge;

        AddAgedFolder(
            candidates,
            problems,
            "temp.user",
            CleanupCategory.TemporaryFiles,
            Path.GetTempPath(),
            cutoff,
            cancellationToken);

        AddAgedFolder(
            candidates,
            problems,
            "temp.windows",
            CleanupCategory.TemporaryFiles,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
            cutoff,
            cancellationToken);

        AddThumbnailCache(candidates, problems, cancellationToken);

        AddAgedFolder(
            candidates,
            problems,
            "wer.queue",
            CleanupCategory.ErrorReports,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "Windows",
                "WER"),
            cutoff,
            cancellationToken);

        AddReportOnly(candidates, problems, cancellationToken);

        return Task.FromResult(new CleanupSurvey(
            [.. candidates.OrderByDescending(c => c.Bytes)],
            problems));
    }

    /// <summary>
    /// Everything in a folder older than the cutoff, measured.
    /// </summary>
    /// <remarks>
    /// Through <see cref="DirectoryInfo.EnumerateFiles(string, EnumerationOptions)"/>, whose
    /// <see cref="FileInfo"/> objects come back already carrying length and timestamps from the
    /// directory entry. Constructing a <c>FileInfo</c> from a path instead costs a separate stat
    /// per file, which on a real temp folder took this survey to eighty-four seconds — long enough
    /// that the page looked broken rather than busy.
    /// </remarks>
    private static void AddAgedFolder(
        List<CleanupCandidate> candidates,
        List<string> problems,
        string id,
        CleanupCategory category,
        string path,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        long bytes = 0;
        int files = 0;
        DateTimeOffset? newest = null;

        foreach (FileInfo info in Walk(path, problems))
        {
            cancellationToken.ThrowIfCancellationRequested();

            DateTime touched = info.LastWriteTime > info.CreationTime ? info.LastWriteTime : info.CreationTime;

            if (newest is null || touched > newest.Value.LocalDateTime)
            {
                newest = new DateTimeOffset(touched);
            }

            if (touched > cutoff)
            {
                continue;
            }

            bytes += info.Length;
            files++;
        }

        candidates.Add(new CleanupCandidate(
            id,
            category,
            path,
            bytes,
            files,
            newest,
            CleanupTrust.Reclaimable,
            new Evidence(
                EvidenceSourceKind.Win32Api,
                $"walked '{path}', counting files last written before {cutoff:yyyy-MM-dd}",
                Confidence.High,
                RawResult: string.Create(
                    CultureInfo.InvariantCulture,
                    $"{files} file(s), {bytes} bytes"))));
    }

    /// <summary>
    /// Explorer's thumbnail and icon caches. Named files rather than a folder walk, because the
    /// folder they live in also holds Explorer state we have no business moving.
    /// </summary>
    private static void AddThumbnailCache(
        List<CleanupCandidate> candidates,
        List<string> problems,
        CancellationToken cancellationToken)
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "Windows",
            "Explorer");

        if (!Directory.Exists(folder))
        {
            return;
        }

        long bytes = 0;
        int files = 0;

        foreach (string pattern in new[] { "thumbcache_*.db", "iconcache_*.db" })
        {
            string[] matches;

            try
            {
                matches = Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"The thumbnail cache in '{folder}' could not be listed: {ex.Message}");
                continue;
            }

            foreach (string file in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Explorer holds these open constantly; a locked one is expected.
                }
            }
        }

        candidates.Add(new CleanupCandidate(
            "cache.thumbnails",
            CleanupCategory.ThumbnailCache,
            folder,
            bytes,
            files,
            NewestItem: null,
            CleanupTrust.Reclaimable,
            new Evidence(
                EvidenceSourceKind.Win32Api,
                $"thumbcache_*.db and iconcache_*.db in '{folder}'",
                Confidence.High,
                RawResult: string.Create(CultureInfo.InvariantCulture, $"{files} file(s), {bytes} bytes"))));
    }

    /// <summary>
    /// Space worth knowing about that this product will not reclaim for you.
    /// </summary>
    /// <remarks>
    /// Reported because "where did my disk go?" is a real question and leaving these out makes the
    /// answer add up to less than the disk. Not actioned because emptying the Recycle Bin is
    /// irreversible by definition, and Delivery Optimization's cache is managed by Windows on its
    /// own schedule.
    /// </remarks>
    private static void AddReportOnly(
        List<CleanupCandidate> candidates,
        List<string> problems,
        CancellationToken cancellationToken)
    {
        (string Id, CleanupCategory Category, string Path)[] locations =
        [
            ("bin.recycle", CleanupCategory.RecycleBin, Path.Combine(
                Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\",
                "$Recycle.Bin")),
            ("cache.delivery", CleanupCategory.UpdateDeliveryCache, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "SoftwareDistribution",
                "DeliveryOptimization")),
        ];

        foreach ((string id, CleanupCategory category, string path) in locations)
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            long bytes = 0;
            int files = 0;

            foreach (FileInfo info in Walk(path, problems))
            {
                cancellationToken.ThrowIfCancellationRequested();

                bytes += info.Length;
                files++;
            }

            candidates.Add(new CleanupCandidate(
                id,
                category,
                path,
                bytes,
                files,
                NewestItem: null,
                CleanupTrust.ReportOnly,
                new Evidence(
                    EvidenceSourceKind.Win32Api,
                    $"walked '{path}' for size only — this product does not remove it",
                    Confidence.Medium,
                    RawResult: string.Create(CultureInfo.InvariantCulture, $"{files} file(s), {bytes} bytes"))));
        }
    }

    /// <summary>
    /// Every file under a folder, with its size and timestamps already filled in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IgnoreInaccessible</c> means a subtree we are not allowed into costs us that subtree
    /// rather than the whole walk — the previous hand-rolled version existed because
    /// <c>Directory.EnumerateFiles</c> throws on the first denied folder, which on a temp folder
    /// loses most of the answer.
    /// </para>
    /// <para>
    /// Reparse points are skipped. A junction under a temp folder would otherwise be followed into
    /// whatever it points at — counted, and worse, offered for removal.
    /// </para>
    /// </remarks>
    private static IEnumerable<FileInfo> Walk(string root, List<string> problems)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        IEnumerator<FileInfo> walker;

        try
        {
            walker = new DirectoryInfo(root).EnumerateFiles("*", options).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"'{root}' could not be opened, so nothing under it was counted: {ex.Message}");
            yield break;
        }

        using (walker)
        {
            while (true)
            {
                FileInfo current;

                try
                {
                    if (!walker.MoveNext())
                    {
                        break;
                    }

                    current = walker.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"part of '{root}' could not be read, so the total is a floor, not a ceiling.");
                    break;
                }

                yield return current;
            }
        }
    }
}

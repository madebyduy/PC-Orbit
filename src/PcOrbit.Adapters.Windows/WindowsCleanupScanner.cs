using System.Globalization;
using PcOrbit.Core.Cleanup;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows;

/// <param name="Id">Stable id. It is also the string-catalogue key suffix for this row's label.</param>
/// <param name="Paths">
/// Every folder this entry covers. Several, because "the browser cache" is one idea to a person and
/// four folders to Windows.
/// </param>
/// <param name="Pattern">Null walks the whole subtree; a pattern matches top-level files only.</param>
/// <param name="MinimumAge">Null takes everything, whatever its age.</param>
/// <remarks>
/// Public because it is the single place a path this product will remove is written down, and both
/// the scanner and the quarantine store read it. A test supplies its own table rather than being
/// pointed at the real one.
/// </remarks>
public sealed record CleanupLocation(
    string Id,
    CleanupCategory Category,
    CleanupTrust Trust,
    IReadOnlyList<string> Paths,
    string? Pattern = null,
    TimeSpan? MinimumAge = null);

/// <summary>
/// Measures reclaimable space on this machine.
/// </summary>
/// <remarks>
/// <para>
/// Every figure is measured by walking the folder, not estimated from a rule of thumb — the number
/// shown before has to be the number reclaimed after. The walk fills its sizes in from the
/// directory entry rather than stat-ing each path, which is the difference between five seconds and
/// eighty-four.
/// </para>
/// <para>
/// What is here and what is not is the whole design. Every location had to be one Windows or the
/// owning application rebuilds by itself, and the well-known space hogs that are not — WinSxS, the
/// DriverStore, <c>Windows.old</c>, the recovery partition, restore points — are absent rather than
/// present-and-guarded, so no future edit can promote one by changing a flag.
/// </para>
/// <para>
/// The line this draws that comparable tools do not: browser <em>caches</em> are cleared, browser
/// <em>cookies, history and saved passwords</em> are not. Clearing those is how someone loses every
/// session they have, and it gets offered as an optimisation because it makes the megabyte count
/// bigger.
/// </para>
/// </remarks>
public sealed class WindowsCleanupScanner(
    TimeSpan? minimumAge = null,
    IReadOnlyList<CleanupLocation>? locations = null) : ICleanupScanner
{
    /// <summary>
    /// How old a temp file must be before we touch it.
    /// </summary>
    /// <remarks>
    /// Not caution for its own sake: installers, compilers and browsers keep live state in temp
    /// folders, and a file written an hour ago probably belongs to something still running. Seven
    /// days is past any plausible session.
    /// </remarks>
    public static TimeSpan DefaultMinimumAge { get; } = TimeSpan.FromDays(7);

    private readonly TimeSpan _minimumAge = minimumAge ?? DefaultMinimumAge;
    private readonly IReadOnlyList<CleanupLocation>? _locations = locations;

    public Task<CleanupSurvey> SurveyAsync(CancellationToken cancellationToken = default)
    {
        List<CleanupCandidate> candidates = [];
        List<string> problems = [];

        foreach (CleanupLocation location in _locations ?? Locations(_minimumAge))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Measure(candidates, problems, location, cancellationToken);
        }

        return Task.FromResult(new CleanupSurvey(
            [.. candidates.OrderByDescending(c => c.Bytes)],
            problems));
    }

    // ---------------------------------------------------------------- where the space goes

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// The cache folders every Chromium browser keeps under its profile.
    /// </summary>
    /// <remarks>
    /// Named individually rather than taking the profile folder, which also holds Cookies, History,
    /// Login Data and Bookmarks. Clearing those is how someone loses every session they have.
    /// </remarks>
    private static readonly string[] ChromiumCacheFolders =
        ["Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache"];

    private static IEnumerable<string> ChromiumCaches(string profile) =>
        ChromiumCacheFolders.Select(leaf => Path.Combine(profile, leaf));

    public static IReadOnlyList<CleanupLocation> Locations(TimeSpan age) =>
    [
        // ---------------------------------------------------------------- rebuilt on demand
        new("cache.browser.chrome", CleanupCategory.BrowserCache, CleanupTrust.Regenerable,
            [.. ChromiumCaches(Path.Combine(Local, @"Google\Chrome\User Data\Default"))]),

        new("cache.browser.edge", CleanupCategory.BrowserCache, CleanupTrust.Regenerable,
            [.. ChromiumCaches(Path.Combine(Local, @"Microsoft\Edge\User Data\Default"))]),

        new("cache.browser.brave", CleanupCategory.BrowserCache, CleanupTrust.Regenerable,
            [.. ChromiumCaches(Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data\Default"))]),

        // Firefox names its profiles randomly, so the whole profile parent is walked. Only the
        // cache subtrees inside it are removed — see CacheSubfolders.
        new("cache.browser.firefox", CleanupCategory.BrowserCache, CleanupTrust.Regenerable,
            [Path.Combine(Local, @"Mozilla\Firefox\Profiles")]),

        new("cache.thumbnails", CleanupCategory.ThumbnailCache, CleanupTrust.Regenerable,
            [Path.Combine(Local, @"Microsoft\Windows\Explorer")],
            Pattern: "*cache_*.db"),

        new("cache.shaders", CleanupCategory.ShaderCache, CleanupTrust.Regenerable,
        [
            Path.Combine(Local, "D3DSCache"),
            Path.Combine(Local, @"NVIDIA\DXCache"),
            Path.Combine(Local, @"NVIDIA\GLCache"),
            Path.Combine(Local, @"AMD\DxCache"),
        ]),

        // ---------------------------------------------------------------- worth keeping a while
        new("temp.user", CleanupCategory.TemporaryFiles, CleanupTrust.Reclaimable,
            [Path.GetTempPath()], MinimumAge: age),

        new("temp.windows", CleanupCategory.TemporaryFiles, CleanupTrust.Reclaimable,
            [Path.Combine(WindowsDir, "Temp")], MinimumAge: age),

        new("cache.windows-update", CleanupCategory.UpdateCache, CleanupTrust.Reclaimable,
            [Path.Combine(WindowsDir, @"SoftwareDistribution\Download")], MinimumAge: age),

        new("logs.windows", CleanupCategory.WindowsLogs, CleanupTrust.Reclaimable,
        [
            Path.Combine(WindowsDir, @"Logs\CBS"),
            Path.Combine(WindowsDir, @"Logs\DISM"),
            Path.Combine(WindowsDir, "Panther"),
        ], MinimumAge: age),

        // No age threshold, unlike the temp folders. An entry here is a copy of something that is
        // still on the network; how old it is says nothing about whether it is needed. It stays
        // Reclaimable rather than Regenerable because rebuilding it costs a download, and on a
        // metered connection that is the user's to decide, not ours.
        new("cache.developer", CleanupCategory.DeveloperCache, CleanupTrust.Reclaimable,
        [
            Path.Combine(Local, @"NuGet\v3-cache"),
            Path.Combine(Local, @"Temp\NuGetScratch"),
            Path.Combine(Local, @"pip\cache"),
            Path.Combine(Roaming, "npm-cache"),
            Path.Combine(Local, @"Yarn\Cache"),
        ]),

        // Kept the full window: a dump is sometimes the only record of why a machine fell over, and
        // the timeline may be pointing straight at it.
        new("dumps.crash", CleanupCategory.CrashDumps, CleanupTrust.Reclaimable,
        [
            Path.Combine(Local, "CrashDumps"),
            Path.Combine(WindowsDir, "Minidump"),
        ]),

        new("wer.queue", CleanupCategory.ErrorReports, CleanupTrust.Reclaimable,
            [Path.Combine(Local, @"Microsoft\Windows\WER")], MinimumAge: age),

        // ---------------------------------------------------------------- shown, never removed
        new("bin.recycle", CleanupCategory.RecycleBin, CleanupTrust.ReportOnly,
            [Path.Combine(Path.GetPathRoot(WindowsDir) ?? @"C:\", "$Recycle.Bin")]),

        // Removing this is what makes "Repair" stop working in Add or Remove Programs months later.
        new("cache.installers", CleanupCategory.InstallerCache, CleanupTrust.ReportOnly,
            [Path.Combine(Local, "Package Cache")]),

        new("cache.delivery", CleanupCategory.UpdateDeliveryCache, CleanupTrust.ReportOnly,
            [Path.Combine(WindowsDir, @"SoftwareDistribution\DeliveryOptimization")]),
    ];

    /// <summary>
    /// Inside a Firefox profile, the only folders that are cache.
    /// </summary>
    /// <remarks>
    /// The profile parent has to be walked because the profile folder is named randomly, and the
    /// profile itself holds bookmarks, saved logins and history. So the walk is filtered to these
    /// names rather than taking the subtree — the difference between clearing a cache and clearing
    /// somebody's browser.
    /// </remarks>
    public static IReadOnlyList<string> FirefoxCacheFolders { get; } =
        ["cache2", "startupCache", "shader-cache", "thumbnails", "OfflineCache"];

    // ---------------------------------------------------------------- measuring

    private static void Measure(
        List<CleanupCandidate> candidates,
        List<string> problems,
        CleanupLocation location,
        CancellationToken cancellationToken)
    {
        string[] present = [.. location.Paths.Where(Directory.Exists)];

        if (present.Length == 0)
        {
            return;
        }

        DateTime cutoff = location.MinimumAge is { } age ? DateTime.Now - age : DateTime.MaxValue;

        long bytes = 0;
        int files = 0;
        DateTimeOffset? newest = null;

        // Report-only rows are measured for the total and never acted on, so a full walk of a
        // Recycle Bin with thousands of folders in it is time spent on a number nobody will use.
        int budget = location.Trust == CleanupTrust.ReportOnly ? 4000 : int.MaxValue;

        foreach (string path in present)
        {
            foreach (FileInfo info in Files(location, path, problems))
            {
                cancellationToken.ThrowIfCancellationRequested();

                DateTime touched = info.LastWriteTime > info.CreationTime ? info.LastWriteTime : info.CreationTime;

                if (newest is null || touched > newest.Value.LocalDateTime)
                {
                    newest = new DateTimeOffset(touched);
                }

                if (location.MinimumAge is not null && touched > cutoff)
                {
                    continue;
                }

                bytes += info.Length;
                files++;

                if (files >= budget)
                {
                    problems.Add($"'{path}' holds more than {budget} files; the figure counts the first {budget}.");
                    break;
                }
            }
        }

        candidates.Add(new CleanupCandidate(
            location.Id,
            location.Category,
            present[0],
            bytes,
            files,
            newest,
            location.Trust,
            new Evidence(
                EvidenceSourceKind.Win32Api,
                location.MinimumAge is null
                    ? $"walked {string.Join(", ", present)}"
                    : $"walked {string.Join(", ", present)}, counting files last written before {cutoff:yyyy-MM-dd}",
                Confidence.High,
                RawResult: string.Create(CultureInfo.InvariantCulture, $"{files} file(s), {bytes} bytes"))));
    }

    /// <summary>
    /// The files one location actually covers, including the Firefox filter.
    /// </summary>
    public static IEnumerable<FileInfo> Files(CleanupLocation location, string path, List<string> problems)
    {
        bool firefox = location.Id == "cache.browser.firefox";

        foreach (FileInfo info in Walk(path, location.Pattern, problems))
        {
            if (firefox && !InFirefoxCache(info))
            {
                continue;
            }

            yield return info;
        }
    }

    /// <summary>True only when some ancestor folder is one of Firefox's cache folders.</summary>
    private static bool InFirefoxCache(FileInfo file)
    {
        for (DirectoryInfo? folder = file.Directory; folder is not null; folder = folder.Parent)
        {
            if (FirefoxCacheFolders.Contains(folder.Name, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every file under a folder, with its size and timestamps already filled in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IgnoreInaccessible</c> means a subtree we are not allowed into costs us that subtree
    /// rather than the whole walk, which on a temp folder is most of the answer.
    /// </para>
    /// <para>
    /// Reparse points are skipped. A junction under a temp folder would otherwise be followed into
    /// whatever it points at — counted, and worse, offered for removal.
    /// </para>
    /// </remarks>
    public static IEnumerable<FileInfo> Walk(string root, string? pattern, List<string> problems)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = pattern is null,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        IEnumerator<FileInfo> walker;

        try
        {
            walker = new DirectoryInfo(root).EnumerateFiles(pattern ?? "*", options).GetEnumerator();
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

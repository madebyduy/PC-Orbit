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

    /// <summary>
    /// Measures every location. Read-only: surveying never deletes or moves anything.
    /// </summary>
    /// <remarks>
    /// The locations are walked in parallel because they are genuinely independent — different
    /// folders, no shared state — and the survey is almost entirely waiting on the file system.
    /// Sequentially it took fifteen seconds on the machine it was written against, which is fifteen
    /// seconds of a page sitting there with its headings and no numbers. Each location still
    /// collects its own results into its own list, and they are merged after, so parallelism cannot
    /// interleave two locations' problems into one.
    /// </remarks>
    public Task<CleanupSurvey> SurveyAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CleanupLocation> locations = _locations ?? Locations(_minimumAge);

        var found = new List<CleanupCandidate>[locations.Count];
        var trouble = new List<string>[locations.Count];

        Parallel.For(
            0,
            locations.Count,
            new ParallelOptions { CancellationToken = cancellationToken },
            i =>
            {
                found[i] = [];
                trouble[i] = [];

                Measure(found[i], trouble[i], locations[i], cancellationToken);
            });

        return Task.FromResult(new CleanupSurvey(
            [.. found.SelectMany(f => f).OrderByDescending(c => c.Bytes)],
            [.. trouble.SelectMany(t => t)]));
    }

    // ---------------------------------------------------------------- where the space goes

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

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

    /// <summary>
    /// Every profile under a Chromium browser's User Data, not just the first.
    /// </summary>
    /// <remarks>
    /// Only <c>Default</c> was measured, which on a machine with a work profile and a personal one
    /// reports half the cache and then reclaims half of what it promised. Profiles are named
    /// <c>Default</c> and <c>Profile 1</c>, <c>Profile 2</c>… plus two Chromium keeps for itself;
    /// they are listed by looking, because the count is the user's business and not something to
    /// hard-code.
    /// </remarks>
    private static IEnumerable<string> ChromiumProfileCaches(string userData)
    {
        if (!Directory.Exists(userData))
        {
            yield break;
        }

        string[] profiles;

        try
        {
            profiles =
            [
                .. Directory.EnumerateDirectories(userData)
                    .Where(d =>
                    {
                        string name = Path.GetFileName(d);

                        return name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("System Profile", StringComparison.OrdinalIgnoreCase);
                    }),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string profile in profiles)
        {
            foreach (string cache in ChromiumCaches(profile))
            {
                yield return cache;
            }
        }

        // Shared across profiles, and often the largest single folder of the three.
        yield return Path.Combine(userData, "ShaderCache");
        yield return Path.Combine(userData, "GrShaderCache");
        yield return Path.Combine(userData, "GraphiteDawnCache");
    }

    /// <summary>
    /// The per-package caches of Store applications.
    /// </summary>
    /// <remarks>
    /// <c>TempState</c> is what its name says and is rebuilt; <c>AC\INetCache</c> is the packaged
    /// equivalent of a browser cache. <c>LocalState</c> is deliberately absent — that is where a
    /// Store app keeps the things you would miss.
    /// </remarks>
    private static IEnumerable<string> PackagedCaches()
    {
        string packages = Path.Combine(Local, "Packages");

        if (!Directory.Exists(packages))
        {
            yield break;
        }

        string[] families;

        try
        {
            families = Directory.GetDirectories(packages);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string family in families)
        {
            yield return Path.Combine(family, "TempState");
            yield return Path.Combine(family, @"AC\INetCache");
        }
    }

    public static IReadOnlyList<CleanupLocation> Locations(TimeSpan age) =>
    [
        // ---------------------------------------------------------------- rebuilt on demand
        new("cache.browser.chrome", CleanupCategory.BrowserCache, CleanupTrust.Regenerable,
            [.. ChromiumProfileCaches(Path.Combine(Local, @"Google\Chrome\User Data"))]),

        new("cache.browser.edge", CleanupCategory.BrowserCache, CleanupTrust.Regenerable,
            [.. ChromiumProfileCaches(Path.Combine(Local, @"Microsoft\Edge\User Data"))]),

        new("cache.browser.brave", CleanupCategory.BrowserCache, CleanupTrust.Regenerable,
            [.. ChromiumProfileCaches(Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"))]),

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
            Path.Combine(Local, @"Intel\ShaderCache"),
            Path.Combine(Local, @"Steam\htmlcache"),
        ]),

        // Internet Explorer's cache is not gone; it is what WebView and anything hosting a browser
        // control still writes into.
        new("cache.inetcache", CleanupCategory.BrowserCache, CleanupTrust.Regenerable,
            [Path.Combine(Local, @"Microsoft\Windows\INetCache")]),

        new("cache.store-apps", CleanupCategory.StoreAppCache, CleanupTrust.Regenerable,
            [.. PackagedCaches()]),

        new("cache.remote-desktop", CleanupCategory.RemoteDesktopCache, CleanupTrust.Regenerable,
            [Path.Combine(Local, @"Microsoft\Terminal Server Client\Cache")]),

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
            Path.Combine(WindowsDir, @"Logs\WindowsUpdate"),
            Path.Combine(WindowsDir, @"Logs\MoSetup"),
            Path.Combine(WindowsDir, @"Logs\SIH"),
            Path.Combine(WindowsDir, "Panther"),
            Path.Combine(WindowsDir, @"System32\LogFiles"),
        ], MinimumAge: age),

        // Rebuilt the next time each program is launched, at the cost of one slower launch each.
        // That cost is why it waits in quarantine rather than going straight out.
        new("cache.prefetch", CleanupCategory.Prefetch, CleanupTrust.Reclaimable,
            [Path.Combine(WindowsDir, "Prefetch")], MinimumAge: age),

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
        [
            Path.Combine(Local, @"Microsoft\Windows\WER"),
            Path.Combine(ProgramData, @"Microsoft\Windows\WER"),
        ], MinimumAge: age),

        // ---------------------------------------------------------------- shown, never removed
        new("bin.recycle", CleanupCategory.RecycleBin, CleanupTrust.ReportOnly,
            [Path.Combine(Path.GetPathRoot(WindowsDir) ?? @"C:\", "$Recycle.Bin")]),

        // Removing this is what makes "Repair" stop working in Add or Remove Programs months later.
        new("cache.installers", CleanupCategory.InstallerCache, CleanupTrust.ReportOnly,
            [Path.Combine(Local, "Package Cache")]),

        new("cache.delivery", CleanupCategory.UpdateDeliveryCache, CleanupTrust.ReportOnly,
            [Path.Combine(WindowsDir, @"SoftwareDistribution\DeliveryOptimization")]),

        // Frequently the largest single thing on a disk after an upgrade, and the one this product
        // will not touch: it is the only way back to the Windows you had before. Windows removes it
        // itself after ten days, and Storage Sense will do it sooner if asked. Reported so the
        // number is not a mystery, never removed here.
        new("windows.old", CleanupCategory.PreviousWindows, CleanupTrust.Protected,
            [Path.Combine(Path.GetPathRoot(WindowsDir) ?? @"C:\", "Windows.old")]),
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

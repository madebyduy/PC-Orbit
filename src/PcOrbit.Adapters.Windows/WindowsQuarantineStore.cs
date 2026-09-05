using System.Globalization;
using System.Text.Json;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Cleanup;
using PcOrbit.Core.Serialization;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// Holds removed files where they can be put back.
/// </summary>
/// <remarks>
/// <para>
/// A folder per batch under <c>%LOCALAPPDATA%\PC Orbit\quarantine</c>, plus a manifest naming
/// every file's original path. Restore reads the manifest and moves each file home. Purge, after
/// the retention window, is the only place this product deletes anything for real.
/// </para>
/// <para>
/// Files are <em>moved</em>, not copied then deleted. On the same volume that is a rename: it
/// cannot half-succeed and leave a file existing in two places, and it needs no free space to
/// perform — which matters, because the machine we are doing this on is by definition short of
/// disk. A file on another volume falls back to copy-then-delete and is verified before the
/// original goes.
/// </para>
/// </remarks>
public sealed class WindowsQuarantineStore : IQuarantineStore
{
    private const string ManifestName = "batch.json";

    private readonly string _root;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    /// <summary>
    /// The same table the scanner measures from, so the two cannot disagree about which folder a
    /// candidate means. Injectable for the same reason it is on the scanner.
    /// </summary>
    private readonly IReadOnlyList<CleanupLocation> _locations;

    public WindowsQuarantineStore(
        string? root = null,
        IClock? clock = null,
        IIdGenerator? ids = null,
        TimeSpan? retention = null,
        IReadOnlyList<CleanupLocation>? locations = null)
    {
        _locations = locations ?? WindowsCleanupScanner.Locations(WindowsCleanupScanner.DefaultMinimumAge);

        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PC Orbit",
            "quarantine");

        _clock = clock ?? SystemClock.Instance;
        _ids = ids ?? GuidIdGenerator.Instance;
        Retention = retention ?? TimeSpan.FromDays(30);
    }

    /// <summary>
    /// Thirty days. Long enough that someone notices what broke and connects it to the cleanup,
    /// short enough that the space is genuinely released within a billing month of disk anxiety.
    /// </summary>
    public TimeSpan Retention { get; }

    public string Root => _root;

    public async Task<QuarantineBatch> QuarantineAsync(
        IReadOnlyList<CleanupCandidate> candidates,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        DateTimeOffset now = _clock.Now;
        string batchId = _ids.NewId("qtn");
        string batchPath = Path.Combine(_root, batchId);

        Directory.CreateDirectory(batchPath);

        List<QuarantinedFile> stored = [];
        List<string> failures = [];
        int index = 0;

        int total = candidates.Sum(c => c.FileCount);
        int done = 0;
        long bytesDone = 0;
        var stopped = false;

        foreach (CleanupCandidate candidate in candidates)
        {
            if (stopped)
            {
                break;
            }

            // Refused here as well as in the UI. Quarantine is for what can usefully come back;
            // a regenerable cache goes through DeleteRegenerableAsync, and anything else is a bug
            // in the caller that the store is the last place to catch.
            if (candidate.Trust != CleanupTrust.Reclaimable || !candidate.CanReclaim)
            {
                failures.Add($"'{candidate.Id}' is {candidate.Trust} and was not moved to quarantine.");
                continue;
            }

            foreach (FileInfo file in FilesOf(candidate, failures))
            {
                // Stopping is between files, never in the middle of one, and it is a result rather
                // than an exception: what moved is a complete, restorable batch.
                if (cancellationToken.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }

                long bytes;

                try
                {
                    bytes = file.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                string storedName = string.Create(CultureInfo.InvariantCulture, $"{index:D6}{file.Extension}");
                string destination = Path.Combine(batchPath, storedName);

                if (TryMove(file.FullName, destination, failures))
                {
                    stored.Add(new QuarantinedFile(file.FullName, storedName, bytes));
                    index++;
                    bytesDone += bytes;
                }

                if (++done % 40 == 0)
                {
                    progress?.Report(new CleanupProgress(done, total, bytesDone, candidate.Id));
                }
            }
        }

        if (stopped)
        {
            failures.Add("stopped by the user before every file was moved.");
        }

        progress?.Report(new CleanupProgress(done, total, bytesDone, string.Empty));

        var batch = new QuarantineBatch(batchId, now, now + Retention, stored, failures);

        await WriteManifestAsync(batchPath, batch, cancellationToken).ConfigureAwait(false);

        return batch;
    }

    public async Task<QuarantineRestore> RestoreAsync(string batchId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);

        string batchPath = Path.Combine(_root, batchId);
        QuarantineBatch? batch = await ReadManifestAsync(batchPath, cancellationToken).ConfigureAwait(false);

        if (batch is null)
        {
            return new QuarantineRestore(batchId, 0, [$"No quarantine batch '{batchId}' is stored."]);
        }

        List<string> failures = [];
        int restored = 0;

        foreach (QuarantinedFile file in batch.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string source = Path.Combine(batchPath, file.StoredName);

            if (!File.Exists(source))
            {
                failures.Add($"'{file.OriginalPath}' is no longer in the quarantine store.");
                continue;
            }

            // Something may have recreated the file since. Overwriting it would be a second,
            // unannounced data loss, so the restore reports it and leaves both copies alone.
            if (File.Exists(file.OriginalPath))
            {
                failures.Add($"'{file.OriginalPath}' exists again, so the quarantined copy was left in place.");
                continue;
            }

            try
            {
                string? parent = Path.GetDirectoryName(file.OriginalPath);

                if (parent is not null)
                {
                    Directory.CreateDirectory(parent);
                }

                File.Move(source, file.OriginalPath);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"'{file.OriginalPath}' could not be put back: {ex.Message}");
            }
        }

        if (failures.Count == 0)
        {
            TryDeleteDirectory(batchPath);
        }

        return new QuarantineRestore(batchId, restored, failures);
    }

    public async Task<IReadOnlyList<QuarantineBatch>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        List<QuarantineBatch> batches = [];

        foreach (string folder in Directory.EnumerateDirectories(_root))
        {
            QuarantineBatch? batch = await ReadManifestAsync(folder, cancellationToken).ConfigureAwait(false);

            if (batch is not null)
            {
                batches.Add(batch);
            }
        }

        return [.. batches.OrderByDescending(b => b.CreatedAt)];
    }

    public async Task<long> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        long released = 0;

        foreach (QuarantineBatch batch in await ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!batch.IsExpired(now))
            {
                continue;
            }

            if (TryDeleteDirectory(Path.Combine(_root, batch.Id)))
            {
                released += batch.Bytes;
            }
        }

        return released;
    }

    // ---------------------------------------------------------------- internals

    /// <summary>
    /// The files a candidate stands for, recomputed at the moment of the change.
    /// </summary>
    /// <remarks>
    /// Recomputed rather than carried on the candidate: the survey may be minutes old, and moving a
    /// file that appeared since it ran is not something the user previewed. The location table is
    /// the single place a path is written down, so the scanner and this cannot disagree.
    /// </remarks>
    private IEnumerable<FileInfo> FilesOf(CleanupCandidate candidate, List<string> failures)
    {
        CleanupLocation? location = _locations.FirstOrDefault(l => l.Id == candidate.Id);

        if (location is null)
        {
            failures.Add($"'{candidate.Id}' is not a location this build knows how to clean.");
            yield break;
        }

        DateTime cutoff = location.MinimumAge is { } age ? DateTime.Now - age : DateTime.MaxValue;

        foreach (string path in location.Paths.Where(Directory.Exists))
        {
            foreach (FileInfo info in WindowsCleanupScanner.Files(location, path, failures))
            {
                if (location.MinimumAge is not null)
                {
                    DateTime touched = info.LastWriteTime > info.CreationTime ? info.LastWriteTime : info.CreationTime;

                    if (touched > cutoff)
                    {
                        continue;
                    }
                }

                yield return info;
            }
        }
    }

    public Task<CleanupDeletion> DeleteRegenerableAsync(
        IReadOnlyList<CleanupCandidate> candidates,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        long freed = 0;
        int removed = 0;
        int locked = 0;
        List<string> failures = [];

        int total = candidates.Sum(c => c.FileCount);
        int done = 0;
        var stopped = false;

        foreach (CleanupCandidate candidate in candidates)
        {
            if (stopped)
            {
                break;
            }

            // The refusal is here as well as in the caller. This method deletes without a way back,
            // so it takes only the category that has nothing to come back to.
            if (candidate.Trust != CleanupTrust.Regenerable)
            {
                failures.Add($"'{candidate.Id}' is {candidate.Trust} and was not deleted.");
                continue;
            }

            foreach (FileInfo file in FilesOf(candidate, failures))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }

                long size;

                try
                {
                    size = file.Length;
                    file.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A running browser holds its own cache open. Normal, and counted rather than
                    // fought with: the honest total is the one that leaves these out.
                    locked++;
                    done++;
                    continue;
                }

                freed += size;
                removed++;

                if (++done % 40 == 0)
                {
                    progress?.Report(new CleanupProgress(done, total, freed, candidate.Id));
                }
            }
        }

        if (stopped)
        {
            failures.Add("stopped by the user before every file was deleted.");
        }

        progress?.Report(new CleanupProgress(done, total, freed, string.Empty));

        return Task.FromResult(new CleanupDeletion(freed, removed, locked, failures));
    }

    public async Task<long> PurgeAllAsync(CancellationToken cancellationToken = default)
    {
        long released = 0;

        foreach (QuarantineBatch batch in await ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (TryDeleteDirectory(Path.Combine(_root, batch.Id)))
            {
                released += batch.Bytes;
            }
        }

        return released;
    }

    private static bool TryMove(string source, string destination, List<string> failures)
    {
        try
        {
            File.Move(source, destination);
            return true;
        }
        catch (IOException)
        {
            // Different volume, or the file is open. Copy-verify-delete, so a failure at any point
            // leaves the original where it is rather than losing it.
            try
            {
                File.Copy(source, destination, overwrite: true);

                if (new FileInfo(destination).Length != new FileInfo(source).Length)
                {
                    File.Delete(destination);
                    failures.Add($"'{source}' did not copy completely and was left alone.");
                    return false;
                }

                File.Delete(source);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use. Extremely common in a temp folder and not worth alarming anyone about.
                return false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task WriteManifestAsync(string batchPath, QuarantineBatch batch, CancellationToken ct)
    {
        await File.WriteAllTextAsync(
            Path.Combine(batchPath, ManifestName),
            JsonSerializer.Serialize(batch, JsonDefaults.Readable),
            ct).ConfigureAwait(false);
    }

    private static async Task<QuarantineBatch?> ReadManifestAsync(string batchPath, CancellationToken ct)
    {
        string manifest = Path.Combine(batchPath, ManifestName);

        if (!File.Exists(manifest))
        {
            return null;
        }

        try
        {
            string json = await File.ReadAllTextAsync(manifest, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<QuarantineBatch>(json, JsonDefaults.Readable);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }
}

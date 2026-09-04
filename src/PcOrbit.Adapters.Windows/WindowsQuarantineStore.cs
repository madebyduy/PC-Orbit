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

    public WindowsQuarantineStore(
        string? root = null,
        IClock? clock = null,
        IIdGenerator? ids = null,
        TimeSpan? retention = null)
    {
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

        foreach (CleanupCandidate candidate in candidates)
        {
            if (!candidate.CanReclaim)
            {
                // Refused here as well as in the UI. A caller that hands us a Protected or
                // ReportOnly candidate has a bug, and the store is the last place to catch it.
                failures.Add($"'{candidate.Id}' is {candidate.Trust} and was not touched.");
                continue;
            }

            foreach (string file in FilesOf(candidate, failures))
            {
                cancellationToken.ThrowIfCancellationRequested();

                long bytes;

                try
                {
                    bytes = new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                string storedName = string.Create(CultureInfo.InvariantCulture, $"{index:D6}{Path.GetExtension(file)}");
                string destination = Path.Combine(batchPath, storedName);

                if (TryMove(file, destination, failures))
                {
                    stored.Add(new QuarantinedFile(file, storedName, bytes));
                    index++;
                }
            }
        }

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
    /// The files a candidate stands for. Recomputed here rather than carried on the candidate: the
    /// survey may be minutes old, and moving a file that appeared since it ran is not something the
    /// user previewed.
    /// </summary>
    private static IEnumerable<string> FilesOf(CleanupCandidate candidate, List<string> failures)
    {
        if (candidate.Category == CleanupCategory.ThumbnailCache)
        {
            foreach (string pattern in new[] { "thumbcache_*.db", "iconcache_*.db" })
            {
                string[] matches;

                try
                {
                    matches = Directory.GetFiles(candidate.Path, pattern, SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"'{candidate.Path}' could not be listed: {ex.Message}");
                    continue;
                }

                foreach (string file in matches)
                {
                    yield return file;
                }
            }

            yield break;
        }

        IEnumerable<string> all;

        try
        {
            all = Directory.EnumerateFiles(candidate.Path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add($"'{candidate.Path}' could not be listed: {ex.Message}");
            yield break;
        }

        foreach (string file in all)
        {
            yield return file;
        }
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

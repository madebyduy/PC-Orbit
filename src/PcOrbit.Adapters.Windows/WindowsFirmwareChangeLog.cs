using System.Text.Json;
using PcOrbit.Core.Firmware;
using PcOrbit.Core.Serialization;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// The firmware change log as a JSON file under the user's profile.
/// </summary>
/// <remarks>
/// A file rather than a table in the SQLite store, for one reason: this record has to survive the
/// store being reset, because the setting it describes is in the firmware and does not reset with
/// it. <c>%LOCALAPPDATA%\PC Orbit\firmware-changes.json</c>, whole file rewritten on each record —
/// a handful of entries, never more than a person would make by hand.
/// </remarks>
public sealed class WindowsFirmwareChangeLog(string? path = null) : IFirmwareChangeLog
{
    private readonly string _path = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PC Orbit",
        "firmware-changes.json");

    // A lock and synchronous I/O: the file is a few hundred bytes and a write is rarer than a
    // restart. A semaphore here would only buy the host a disposable it has no moment to dispose.
    private readonly Lock _gate = new();

    public Task RecordAsync(FirmwareChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            List<FirmwareChange> all = [.. Read(), change];

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(all, JsonDefaults.Readable));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FirmwareChange>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<FirmwareChange> all = [.. Read().OrderByDescending(c => c.When)];

            return Task.FromResult(all);
        }
    }

    private IReadOnlyList<FirmwareChange> Read()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<FirmwareChange>>(File.ReadAllText(_path), JsonDefaults.Readable) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable log is shown as empty rather than stopping the page. The file is not
            // overwritten until the next record, so a person can still recover it by hand.
            return [];
        }
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcOrbit.Adapters.Windows.Interop;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Model;
using PcOrbit.Core.Serialization;

namespace PcOrbit.Adapters.Windows;

/// <summary>Whether this process is running with administrator rights, asked once.</summary>
public sealed class WindowsElevationContext : IElevationContext
{
    private readonly Lazy<bool> _elevated = new(SystemInterop.IsProcessElevated);

    public static WindowsElevationContext Instance { get; } = new();

    public bool IsElevated => _elevated.Value;
}

/// <summary>
/// Identifies the current boot, which is how a transaction knows whether the restart it was
/// waiting for has actually happened (spec 14.2 relatedRestart, 8.3.3 resume-after-boot).
/// </summary>
/// <remarks>
/// Derived from <c>Win32_OperatingSystem.LastBootUpTime</c>, not from <c>GetTickCount64</c>: the
/// tick counter stops during sleep and hibernate, so a laptop that was merely closed and reopened
/// would look as though it had rebooted, and we would verify a firmware change that never had a
/// chance to take effect.
/// </remarks>
public sealed class WindowsBootSession : IBootSession
{
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        (Get-CimInstance -ClassName Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')
        """;

    private readonly Lazy<DateTimeOffset> _bootedAt;

    public WindowsBootSession(PowerShellRunner? powerShell = null)
    {
        PowerShellRunner runner = powerShell ?? PowerShellRunner.Default;

        _bootedAt = new Lazy<DateTimeOffset>(() =>
        {
            PowerShellResult result = runner.RunAsync(Script).GetAwaiter().GetResult();

            if (result.Succeeded
                && DateTimeOffset.TryParse(
                    result.StandardOutput,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsed))
            {
                return parsed;
            }

            // Fallback with a known weakness, recorded rather than hidden: this drifts across
            // sleep, so a resume decision made from it can be wrong. It only applies when WMI
            // itself is unavailable, at which point very little else works either.
            return DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        });
    }

    public static WindowsBootSession Instance { get; } = new();

    public DateTimeOffset BootedAt => _bootedAt.Value;

    public string CurrentBootId
    {
        get
        {
            string material = BootedAt.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            return "boot-" + Convert.ToHexStringLower(hash)[..12];
        }
    }
}

/// <summary>
/// Re-reads single capabilities so verification is independent of whatever made the change.
/// </summary>
/// <remarks>
/// <para>
/// Verification never asks the executor whether it worked — spec 8.3.2 is explicit that Auto does
/// not get to skip verification. It asks the machine.
/// </para>
/// <para>
/// A full re-scan per verified capability would be wasteful, so results are cached for a very
/// short window: long enough that verifying five capabilities in one phase costs one scan, short
/// enough that a value read after a restart is never a value read before it. Any change to the
/// machine calls <see cref="Invalidate"/>.
/// </para>
/// </remarks>
public sealed class WindowsCapabilityReader(WindowsStateScanner scanner, TimeSpan? cacheFor = null)
    : ICapabilityReader, IDisposable
{
    private readonly WindowsStateScanner _scanner = scanner
        ?? throw new ArgumentNullException(nameof(scanner));

    private readonly TimeSpan _cacheFor = cacheFor ?? TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private StateSnapshot? _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public async Task<CapabilityReading?> ReadAsync(
        CapabilityId capability,
        CancellationToken cancellationToken = default)
    {
        StateSnapshot snapshot = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.ReadingOf(capability);
    }

    /// <summary>Drops the cache. Called after anything that changes the machine.</summary>
    public void Invalidate() => _cachedAt = DateTimeOffset.MinValue;

    public void Dispose() => _gate.Dispose();

    private async Task<StateSnapshot> CurrentAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < _cacheFor)
            {
                return _cached;
            }

            _cached = await _scanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            _cachedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Finds the shipped data files, beside the binary or in the repo during development.</summary>
public static class DataLocator
{
    public static string FindDataDirectory(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Directory.Exists(explicitPath)
                ? explicitPath
                : throw new DirectoryNotFoundException($"No data directory at '{explicitPath}'.");
        }

        string beside = Path.Combine(AppContext.BaseDirectory, "data");

        if (Directory.Exists(Path.Combine(beside, "graph")))
        {
            return beside;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "data");

            if (Directory.Exists(Path.Combine(candidate, "graph")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the data directory (graph, outcomes, actions, guides, i18n). "
            + "Pass --data <path> to point at it.");
    }

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Readable);
}

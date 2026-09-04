using PcOrbit.Core.Abstractions;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// Switches a startup entry on or off, through the mechanisms Task Manager uses.
/// </summary>
/// <remarks>
/// <para>
/// This was blocked, and the block was real: <c>ActionParameters</c> validates every parameter
/// against an allowlist before it reaches a command (spec 17.1), and "whichever entry the user
/// picked" is not an allowlist. The way through was to notice that an allowlist need not be
/// shipped data — it needs to be a finite set the caller cannot extend. Here it is
/// <em>the machine's own startup inventory, re-read at the moment of the change</em>. A name that
/// is not in it right now is refused before any command is built, which makes an invented or stale
/// name unusable rather than merely unlikely (ADR 0005).
/// </para>
/// <para>
/// Two mechanisms, because Windows has two. A scheduled task goes through the documented
/// <c>Enable-ScheduledTask</c> / <c>Disable-ScheduledTask</c>. A Run-key or Startup-folder entry
/// goes through the <c>Explorer\StartupApproved</c> value whose low bit Task Manager flips — which
/// is undocumented, and is why we only ever write one of the two bytes Windows itself writes, and
/// why the result is confirmed by reading the machine back rather than by trusting an exit code.
/// </para>
/// <para>
/// The consequence of being wrong here is bounded — a program starts, or does not — and the
/// inverse call always exists. That is what puts this on the safe side of the line that firmware
/// writes are on the unsafe side of.
/// </para>
/// </remarks>
public sealed class WindowsStartupController(
    IStartupInventory? inventory = null,
    PowerShellRunner? powerShell = null) : IStartupController
{
    private const string EnableTaskScript = """
        $ErrorActionPreference = 'Stop'
        Enable-ScheduledTask -TaskName $env:PCORBIT_ARG0 -TaskPath $env:PCORBIT_ARG1 | Out-Null
        'done'
        """;

    private const string DisableTaskScript = """
        $ErrorActionPreference = 'Stop'
        Disable-ScheduledTask -TaskName $env:PCORBIT_ARG0 -TaskPath $env:PCORBIT_ARG1 | Out-Null
        'done'
        """;

    /// <summary>
    /// Writes the switch Task Manager writes, and only the two values it uses: 2 for on, 3 for off.
    /// </summary>
    /// <remarks>
    /// The whole byte array is preserved apart from its first element, because the rest of it is a
    /// timestamp Windows maintains and we have no business rewriting.
    /// </remarks>
    private const string ApprovalScript = """
        $ErrorActionPreference = 'Stop'

        $name   = $env:PCORBIT_ARG0
        $hive   = $env:PCORBIT_ARG1
        $enable = $env:PCORBIT_ARG2 -eq 'on'
        $written = $false

        foreach ($leaf in @('Run', 'Run32', 'StartupFolder')) {
            $path = "$hive\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\$leaf"
            if (-not (Test-Path -LiteralPath $path)) { continue }

            $key = Get-Item -LiteralPath $path
            if ($key.GetValueNames() -notcontains $name) { continue }

            $bytes = [byte[]] $key.GetValue($name)
            if ($bytes.Length -lt 1) { continue }

            $bytes[0] = if ($enable) { 2 } else { 3 }
            Set-ItemProperty -LiteralPath $path -Name $name -Value $bytes
            $written = $true
        }

        if (-not $written) {
            throw 'Windows keeps no on/off record for this entry, so there is no switch to flip. Remove it from where it is registered instead.'
        }

        'done'
        """;

    /// <summary>
    /// Entries this product refuses to change, whatever it is asked.
    /// </summary>
    /// <remarks>
    /// Windows Security's own agents and the BitLocker maintenance tasks. A tool that offers to
    /// switch off the antivirus to shave a second off sign-in has misunderstood its job — so the
    /// refusal is in code, where no setting can relax it.
    /// </remarks>
    public IReadOnlySet<string> NeverChange { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SecurityHealth",
        "SecurityHealthSystray",
        "WindowsDefender",
        "Windows Defender",
        "MsMpEng",
        "BitLocker",
        "BitLocker Encrypt All Drives",
        "BitLocker MDM policy Refresh",
    };

    private readonly IStartupInventory _inventory = inventory ?? new WindowsStartupInventory(powerShell);
    private readonly PowerShellRunner _powerShell = powerShell ?? PowerShellRunner.Default;

    public async Task<StartupChangeResult> SetEnabledAsync(
        string name,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (NeverChange.Contains(name))
        {
            return new StartupChangeResult(
                name,
                enabled,
                Applied: false,
                Verified: false,
                "This entry is part of the machine's own security, and this product will not change it.");
        }

        StartupInventoryResult before = await _inventory.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (before.Problem is { } problem)
        {
            return new StartupChangeResult(
                name,
                enabled,
                Applied: false,
                Verified: false,
                $"The startup list could not be read, so nothing was attempted: {problem}");
        }

        StartupEntry? entry = before.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, name, StringComparison.Ordinal));

        // The allowlist check. Everything below this line is working from a value the machine
        // itself just produced.
        if (entry is null)
        {
            return new StartupChangeResult(
                name,
                enabled,
                Applied: false,
                Verified: false,
                "No startup entry with that name exists on this machine right now.");
        }

        if (entry.Enabled == enabled)
        {
            // Already there. Verified, because we just read it — not applied, because nothing was.
            return new StartupChangeResult(name, enabled, Applied: false, Verified: true);
        }

        string? failure = entry.Location == StartupLocation.ScheduledTask
            ? await SetTaskAsync(entry, enabled, cancellationToken).ConfigureAwait(false)
            : await SetApprovalAsync(entry, enabled, cancellationToken).ConfigureAwait(false);

        if (failure is not null)
        {
            return new StartupChangeResult(name, enabled, Applied: false, Verified: false, failure);
        }

        // Spec 6.4: the command reporting success is not evidence that anything changed.
        StartupInventoryResult after = await _inventory.ReadAsync(cancellationToken).ConfigureAwait(false);

        StartupEntry? confirmed = after.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, name, StringComparison.Ordinal));

        bool verified = confirmed?.Enabled == enabled;

        return new StartupChangeResult(
            name,
            enabled,
            Applied: true,
            Verified: verified,
            verified ? null : "The command reported success, but reading the machine back does not agree.");
    }

    private async Task<string?> SetTaskAsync(StartupEntry entry, bool enabled, CancellationToken ct)
    {
        PowerShellResult result = await _powerShell
            .RunWithArgumentsAsync(
                enabled ? EnableTaskScript : DisableTaskScript,
                [entry.Name, TaskPathOf(entry)],
                ct)
            .ConfigureAwait(false);

        return result.Succeeded ? null : result.ErrorSummary;
    }

    private async Task<string?> SetApprovalAsync(StartupEntry entry, bool enabled, CancellationToken ct)
    {
        // The hive is chosen from the entry's own classification, not from anything the caller said.
        string hive = entry.Location == StartupLocation.MachineRegistry ? "HKLM:" : "HKCU:";

        PowerShellResult result = await _powerShell
            .RunWithArgumentsAsync(ApprovalScript, [entry.Name, hive, enabled ? "on" : "off"], ct)
            .ConfigureAwait(false);

        return result.Succeeded ? null : result.ErrorSummary;
    }

    /// <summary>
    /// The folder a scheduled task lives in. The inventory records it in
    /// <see cref="StartupEntry.Source"/>; a task at the root reports <c>\</c>.
    /// </summary>
    private static string TaskPathOf(StartupEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Source) ? "\\" : entry.Source;
}

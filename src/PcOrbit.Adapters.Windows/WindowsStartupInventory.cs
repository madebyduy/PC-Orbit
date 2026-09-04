using System.Text.Json;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// Reads what starts with Windows, from the same places Task Manager reads.
/// </summary>
/// <remarks>
/// <para>
/// <c>Win32_StartupCommand</c> covers the Run keys and the Startup folders and needs no
/// elevation, which matters: spec 21.10 wants a standard user to get the whole read-only picture.
/// Logon-triggered scheduled tasks are read separately because they are a different mechanism with
/// different rights, and folding them into one query would let one permission error hide both.
/// </para>
/// <para>
/// The enabled flag is the one piece of guesswork here, and it is labelled as such. Windows keeps
/// it under <c>Explorer\StartupApproved</c> as a binary blob whose first byte is the switch —
/// widely relied upon, not documented by Microsoft — so those readings carry Medium confidence and
/// the raw byte, and an entry with no approval record is reported as unclassified rather than
/// assumed to be on.
/// </para>
/// </remarks>
public sealed class WindowsStartupInventory(PowerShellRunner? powerShell = null) : IStartupInventory
{
    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        function Safe { param([scriptblock] $Block) try { & $Block } catch { $null } }

        # First byte of each StartupApproved value is the switch Task Manager writes. Undocumented,
        # so we carry the byte itself as evidence and let the C# side interpret it.
        $approved = @{}
        foreach ($root in 'HKCU:', 'HKLM:') {
            foreach ($leaf in 'Run', 'Run32', 'StartupFolder') {
                $path = "$root\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\$leaf"
                $key = Safe { Get-Item -LiteralPath $path -ErrorAction Stop }
                if ($key) {
                    foreach ($name in $key.GetValueNames()) {
                        $bytes = $key.GetValue($name)
                        if ($bytes -and $bytes.Length -gt 0) { $approved[$name] = [int] $bytes[0] }
                    }
                }
            }
        }

        $startup = Safe {
            Get-CimInstance -ClassName Win32_StartupCommand -ErrorAction Stop |
                ForEach-Object {
                    [pscustomobject]@{
                        Name     = [string] $_.Name
                        Command  = [string] $_.Command
                        Location = [string] $_.Location
                        Approval = if ($approved.ContainsKey($_.Name)) { $approved[$_.Name] } else { $null }
                        Kind     = 'startupCommand'
                    }
                }
        }

        # Only logon-triggered tasks. A task that runs weekly at 3am is not why a PC is slow to
        # sign in, and listing every task on the machine would bury the ones that are.
        $tasks = Safe {
            Get-ScheduledTask -ErrorAction Stop |
                Where-Object { $_.State -ne 'Disabled' -and ($_.Triggers | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' }) } |
                ForEach-Object {
                    [pscustomobject]@{
                        Name     = [string] $_.TaskName
                        Command  = [string] (@($_.Actions | ForEach-Object { $_.Execute }) -join '; ')
                        Location = [string] $_.TaskPath
                        Approval = $null
                        Kind     = 'scheduledTask'
                    }
                }
        }

        $all = @()
        if ($startup) { $all += @($startup) }
        if ($tasks)   { $all += @($tasks) }

        if ($all.Count -eq 0) { '[]' } else { $all | ConvertTo-Json -Depth 3 -Compress }
        """;

    private readonly PowerShellRunner _powerShell = powerShell ?? PowerShellRunner.Default;

    public async Task<StartupInventoryResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        (JsonDocument? document, string? problem) = await WindowsChangeSources
            .ReadJsonAsync(_powerShell, Script, cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null)
        {
            return new StartupInventoryResult(
                [],
                $"the list of startup items could not be read, so this is not evidence that nothing "
                + $"starts with Windows: {problem}");
        }

        if (document is null)
        {
            return new StartupInventoryResult([]);
        }

        using (document)
        {
            List<StartupEntry> entries = [];

            foreach (JsonElement row in WindowsChangeSources.Rows(document))
            {
                string? name = WindowsChangeSources.Text(row, "Name");

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                bool isTask = WindowsChangeSources.Text(row, "Kind") == "scheduledTask";
                string location = WindowsChangeSources.Text(row, "Location") ?? string.Empty;
                int? approval = WindowsChangeSources.Number(row, "Approval");

                entries.Add(new StartupEntry(
                    name,
                    WindowsChangeSources.Text(row, "Command") ?? string.Empty,
                    isTask ? StartupLocation.ScheduledTask : Classify(location),
                    isTask ? true : Enabled(approval),
                    Publisher: null,
                    Evidence: EvidenceFor(isTask, location, approval),
                    Source: location));
            }

            return new StartupInventoryResult(
                [.. entries.OrderBy(e => e.Location).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)]);
        }
    }

    /// <summary>
    /// The low bit of the approval byte is the switch: clear means enabled, set means disabled.
    /// </summary>
    /// <remarks>
    /// Microsoft documents none of this. The rule is derived from what the byte actually holds on
    /// real machines — 2 and 6 for entries Task Manager shows as enabled, 3 for disabled, and on
    /// this machine 4 (enabled) and 1 (disabled) — which the low bit explains and a fixed set of
    /// magic numbers does not. So it is applied at Medium confidence with the raw byte kept in the
    /// evidence, and no rule anywhere acts on it: this feeds a listing a person reads.
    /// <para>
    /// No approval record at all is a genuine "we do not know", not an assumed On. Windows only
    /// writes one once something has had a say in the entry.
    /// </para>
    /// </remarks>
    private static bool? Enabled(int? approval) => approval is { } value ? (value & 1) == 0 : null;

    /// <summary>
    /// <c>Win32_StartupCommand.Location</c> is a path-ish string: registry entries name the Run key,
    /// folder entries name the folder.
    /// </summary>
    private static StartupLocation Classify(string location)
    {
        if (location.Contains("Startup", StringComparison.OrdinalIgnoreCase)
            && !location.Contains("CurrentVersion\\Run", StringComparison.OrdinalIgnoreCase))
        {
            return StartupLocation.StartupFolder;
        }

        if (location.StartsWith("HKU", StringComparison.OrdinalIgnoreCase)
            || location.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase))
        {
            return StartupLocation.UserRegistry;
        }

        return location.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)
            ? StartupLocation.MachineRegistry
            : StartupLocation.Unknown;
    }

    private static Evidence EvidenceFor(bool isTask, string location, int? approval)
    {
        if (isTask)
        {
            return new Evidence(
                EvidenceSourceKind.PowerShell,
                "Get-ScheduledTask, tasks with an MSFT_TaskLogonTrigger",
                Confidence.High,
                Query: "Get-ScheduledTask",
                RawResult: location);
        }

        return approval is null
            ? new Evidence(
                EvidenceSourceKind.Wmi,
                "Win32_StartupCommand — the entry exists; no StartupApproved record says whether it is switched on",
                Confidence.Medium,
                Query: "Win32_StartupCommand",
                RawResult: location)
            : new Evidence(
                EvidenceSourceKind.Registry,
                @"Explorer\StartupApproved — the low bit of the value's first byte is the on/off switch "
                + "(clear = on). Not documented by Microsoft, so this is read with reservation",
                Confidence.Medium,
                Query: @"HKCU/HKLM\...\Explorer\StartupApproved",
                RawResult: $"location={location}, approvalByte={approval}");
    }
}

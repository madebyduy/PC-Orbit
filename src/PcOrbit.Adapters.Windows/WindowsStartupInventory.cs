using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// Reads what starts with Windows, from the places Windows actually keeps it.
/// </summary>
/// <remarks>
/// <para>
/// This used to ask <c>Win32_StartupCommand</c>, which is one query covering the Run keys and the
/// Startup folders and needing no elevation. It was replaced because it lies by omission: on the
/// machine this was found on, the HKCU Run key held five entries and the WMI class returned four.
/// The missing one was a packaged application whose command points into <c>WindowsApps</c>, which
/// the provider declines to resolve and then silently drops. A list of what starts with your PC
/// that quietly leaves things out is worse than no list, because it is believed.
/// </para>
/// <para>
/// So every location is read directly and named: both Run keys, both RunOnce keys, the 32-bit Run
/// key, both Startup folders, packaged applications' own startup tasks, and logon-triggered
/// scheduled tasks. Reading the registry itself also means an entry that cannot be interpreted is
/// still listed, because the value is right there whether or not we understand it.
/// </para>
/// <para>
/// The enabled flag is the one piece of guesswork, and it is labelled as such. Windows keeps it
/// under <c>Explorer\StartupApproved</c> as a binary blob whose first byte is the switch — widely
/// relied upon, not documented by Microsoft — so those readings carry Medium confidence and the
/// raw byte. An entry that appears nowhere in that list is on: StartupApproved records exceptions,
/// written when something is switched off, and Windows runs a Run-key entry that has no exception.
/// </para>
/// </remarks>
public sealed partial class WindowsStartupInventory(PowerShellRunner? powerShell = null) : IStartupInventory
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

        $rows = New-Object System.Collections.ArrayList

        function Add-Row {
            param($Name, $Command, $Location, $Kind, $Approval)
            if (-not $Name) { return }
            [void] $rows.Add([pscustomobject]@{
                Name     = [string] $Name
                Command  = [string] $Command
                Location = [string] $Location
                Kind     = [string] $Kind
                Approval = $Approval
            })
        }

        # ---- the Run keys, read as keys rather than through a provider that may skip a value.
        $runKeys = @(
            @{ Path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run';                  Kind = 'user'    },
            @{ Path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce';              Kind = 'once'    },
            @{ Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run';                  Kind = 'machine' },
            @{ Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce';              Kind = 'once'    },
            @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run';      Kind = 'machine' },
            @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce';  Kind = 'once'    }
        )

        foreach ($entry in $runKeys) {
            $key = Safe { Get-Item -LiteralPath $entry.Path -ErrorAction Stop }
            if (-not $key) { continue }
            foreach ($name in $key.GetValueNames()) {
                if (-not $name) { continue }
                $approval = if ($approved.ContainsKey($name)) { $approved[$name] } else { $null }
                Add-Row $name ([string] $key.GetValue($name)) $entry.Path $entry.Kind $approval
            }
        }

        # ---- the Startup folders. A shortcut here runs whether or not anything indexed it.
        $folders = @(
            @{ Path = [Environment]::GetFolderPath('Startup');       Scope = 'folder' },
            @{ Path = [Environment]::GetFolderPath('CommonStartup'); Scope = 'folder' }
        )

        foreach ($folder in $folders) {
            if (-not $folder.Path -or -not (Test-Path -LiteralPath $folder.Path)) { continue }
            Get-ChildItem -LiteralPath $folder.Path -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -ne 'desktop.ini' } |
                ForEach-Object {
                    $approval = if ($approved.ContainsKey($_.Name)) { $approved[$_.Name] } else { $null }
                    Add-Row $_.BaseName $_.FullName $folder.Path 'folder' $approval
                }
        }

        # ---- packaged applications register a startup task of their own. Task Manager lists these
        # ---- alongside the Run keys; nothing else on the machine records them.
        $appModel = 'HKCU:\SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData'
        Get-ChildItem -LiteralPath $appModel -ErrorAction SilentlyContinue | ForEach-Object {
            $family = $_.PSChildName
            $tasks = Join-Path $_.PSPath 'StartupTasks'
            if (-not (Test-Path -LiteralPath $tasks)) { return }
            Get-ChildItem -LiteralPath $tasks -ErrorAction SilentlyContinue | ForEach-Object {
                $state = (Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue).State
                # 1 and 2 are the enabled states Windows writes here; 0 and 3+ mean the user or
                # policy turned it off. Recorded raw either way.
                Add-Row $_.PSChildName $family 'packaged' 'packaged' $(if ($null -ne $state) { [int] $state } else { $null })
            }
        }

        # ---- only logon-triggered tasks. A task that runs weekly at 3am is not why a PC is slow to
        # ---- sign in, and listing every task on the machine would bury the ones that are.
        $tasks = Safe {
            Get-ScheduledTask -ErrorAction Stop |
                Where-Object { $_.State -ne 'Disabled' -and ($_.Triggers | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' }) } |
                ForEach-Object {
                    [pscustomobject]@{
                        Name     = [string] $_.TaskName
                        Command  = [string] (@($_.Actions | ForEach-Object { $_.Execute }) -join '; ')
                        Location = [string] $_.TaskPath
                        Kind     = 'scheduledTask'
                        Approval = $null
                    }
                }
        }

        if ($tasks) { foreach ($t in @($tasks)) { [void] $rows.Add($t) } }

        if ($rows.Count -eq 0) { '[]' } else { @($rows) | ConvertTo-Json -Depth 3 -Compress }
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

                string kind = WindowsChangeSources.Text(row, "Kind") ?? "unknown";
                string location = WindowsChangeSources.Text(row, "Location") ?? string.Empty;
                string command = WindowsChangeSources.Text(row, "Command") ?? string.Empty;
                int? approval = WindowsChangeSources.Number(row, "Approval");

                entries.Add(new StartupEntry(
                    name,
                    command,
                    Classify(kind),
                    EnabledFor(kind, approval),
                    Publisher: PublisherOf(kind, command),
                    Evidence: EvidenceFor(kind, location, approval),
                    Source: location));
            }

            return new StartupInventoryResult(
                [.. entries.OrderBy(e => e.Location).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)]);
        }
    }

    /// <summary>
    /// Who signed the program this entry runs.
    /// </summary>
    /// <remarks>
    /// Read from the executable's own version resource. It is the single most useful thing a
    /// non-technical person can be told about a row: "Docker Inc." answers "should this be here?"
    /// in a way that a name like <c>MicrosoftEdgeAutoLaunch_4211E6FA…</c> never will. Null when the
    /// command names nothing we can find, which is common enough not to be worth alarming about.
    /// </remarks>
    private static string? PublisherOf(string kind, string command)
    {
        if (kind is "scheduledTask" or "packaged" || string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string? path = ExecutablePath(command);

        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);

            string? company = info.CompanyName?.Trim();

            return string.IsNullOrWhiteSpace(company) ? null : company;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The executable out of a Run value, which is a command line and not a path.
    /// </summary>
    /// <remarks>
    /// Quoted first, because that is the only unambiguous form: everything inside the quotes is the
    /// path, arguments follow. Unquoted, the first token is taken, which is right for the common
    /// case and wrong for an unquoted path containing spaces — a shape Windows itself cannot
    /// resolve reliably either, and one we would rather show no publisher for than the wrong one.
    /// </remarks>
    internal static string? ExecutablePath(string command)
    {
        string trimmed = command.Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        Match quoted = QuotedPath().Match(trimmed);

        if (quoted.Success)
        {
            return Environment.ExpandEnvironmentVariables(quoted.Groups[1].Value);
        }

        int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        string head = space < 0 ? trimmed : trimmed[..space];

        return Environment.ExpandEnvironmentVariables(head);
    }

    /// <summary>
    /// The low bit of the approval byte is the switch: clear means enabled, set means disabled.
    /// </summary>
    /// <remarks>
    /// Microsoft documents none of this. The rule is derived from what the byte actually holds on
    /// real machines — 2 and 6 for entries Task Manager shows as enabled, 3 for disabled, and on
    /// one machine 4 (enabled) and 1 (disabled) — which the low bit explains and a fixed set of
    /// magic numbers does not. So it is applied at Medium confidence with the raw byte kept in the
    /// evidence, and no rule anywhere acts on it: this feeds a listing a person reads.
    /// <para>
    /// No approval record at all is a genuine "we do not know", not an assumed On. Windows only
    /// writes one once something has had a say in the entry.
    /// </para>
    /// </remarks>
    internal static bool? EnabledFor(string kind, int? approval) => kind switch
    {
        // A logon task was already filtered to State != Disabled, so its presence is the answer.
        "scheduledTask" => true,

        // Packaged applications keep a state number rather than a bit field: 1 and 2 are the
        // enabled values Windows writes, everything else means something switched it off.
        "packaged" => approval is { } state ? state is 1 or 2 : null,

        // A Run-key entry with no override record is one Windows runs. StartupApproved is a list of
        // exceptions that Task Manager and Settings write when something is switched off; an entry
        // absent from it has never been switched off. This is not inferring a value out of a failed
        // read — the read succeeded and found nothing, and finding nothing is the answer. Reporting
        // four of this machine's five sign-in entries as unreadable, while Windows plainly starts
        // them, was the version that misled.
        _ => approval is { } value ? (value & 1) == 0 : true,
    };

    private static StartupLocation Classify(string kind) => kind switch
    {
        "user" or "once" => StartupLocation.UserRegistry,
        "machine" => StartupLocation.MachineRegistry,
        "folder" => StartupLocation.StartupFolder,
        "packaged" => StartupLocation.PackagedApp,
        "scheduledTask" => StartupLocation.ScheduledTask,
        _ => StartupLocation.Unknown,
    };

    private static Evidence EvidenceFor(string kind, string location, int? approval) => kind switch
    {
        "scheduledTask" => new Evidence(
            EvidenceSourceKind.PowerShell,
            "Get-ScheduledTask, tasks with an MSFT_TaskLogonTrigger",
            Confidence.High,
            Query: "Get-ScheduledTask",
            RawResult: location),

        "folder" => new Evidence(
            EvidenceSourceKind.FileSystem,
            "a file in a Startup folder, which Windows runs at sign-in",
            Confidence.High,
            Query: location,
            RawResult: location),

        "packaged" => new Evidence(
            EvidenceSourceKind.Registry,
            "a packaged application's own startup task, where Windows records its on/off state",
            Confidence.Medium,
            Query: @"…\AppModel\SystemAppData\<package>\StartupTasks",
            RawResult: $"package={location}, state={approval?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}"),

        _ => approval is null
            ? new Evidence(
                EvidenceSourceKind.Registry,
                "the Run key itself. Nothing under StartupApproved overrides this entry, and Windows "
                + "runs a Run-key entry that has no override — so it is on",
                Confidence.Medium,
                Query: location,
                RawResult: $"location={location}, no StartupApproved record")
            : new Evidence(
                EvidenceSourceKind.Registry,
                @"the Run key, plus Explorer\StartupApproved, whose value's first byte carries the "
                + "on/off switch in its low bit (clear = on). Not documented by Microsoft, so the "
                + "switch is read with reservation while the entry itself is certain",
                Confidence.Medium,
                Query: location,
                RawResult: $"location={location}, approvalByte={approval}"),
    };

    [GeneratedRegex("^\"([^\"]+)\"")]
    private static partial Regex QuotedPath();
}

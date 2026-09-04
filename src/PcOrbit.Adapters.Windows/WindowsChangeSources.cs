using System.Globalization;
using System.Text.Json;
using PcOrbit.Core.Events;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// The changes Windows made to this machine that PC Orbit had nothing to do with.
/// </summary>
/// <remarks>
/// <para>
/// Three sources, each read on its own and each able to fail on its own: Windows Update history,
/// the system event log, and restore points. They are separate classes rather than one script
/// because their permissions differ — update history needs nothing, the event log usually needs
/// nothing, restore points need administrator — and a single script would turn one permission
/// error into three empty answers.
/// </para>
/// <para>
/// Every event here is an observation of somebody else's record. None of it is written to the
/// event log: it is already durable where it lives, and re-reading it is cheap.
/// </para>
/// </remarks>
public static class WindowsChangeSources
{
    public static IReadOnlyList<IChangeSource> All(PowerShellRunner? powerShell = null) =>
    [
        new WindowsUpdateHistorySource(powerShell),
        new SystemEventLogSource(powerShell),
        new RestorePointSource(powerShell),
    ];

    /// <summary>
    /// A stable id for an observation of somebody else's record.
    /// </summary>
    /// <remarks>
    /// FNV-1a rather than <see cref="string.GetHashCode()"/>, which .NET randomises per process:
    /// the same event would get a different id on every run, and the timeline's tie-break — two
    /// things stamped to the same second — would order them differently each time you asked. A
    /// timeline that reshuffles between runs is not one you can reason backwards from.
    /// </remarks>
    internal static string EventId(string source, DateTimeOffset at, string component)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        uint hash = offsetBasis;

        foreach (char c in $"{at.ToUniversalTime():O}|{component}")
        {
            hash = (hash ^ c) * prime;
        }

        return $"{source}-{hash:x8}";
    }

    internal static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset value)
            ? value
            : null;

    /// <summary>
    /// Runs a script that prints a JSON array, and turns a failure into a stated reason.
    /// </summary>
    internal static Task<(JsonDocument? Document, string? Problem)> ReadJsonAsync(
        PowerShellRunner runner,
        string script,
        CancellationToken cancellationToken) =>
        ReadJsonAsync(runner, script, null, cancellationToken);

    /// <summary>
    /// The same, for a script that takes values.
    /// </summary>
    /// <remarks>
    /// Values reach the child process through its environment rather than the command line:
    /// PowerShell refuses positional arguments after <c>-EncodedCommand</c>, and the refusal
    /// reaches the user as an error dialog rather than as anything useful.
    /// </remarks>
    internal static async Task<(JsonDocument? Document, string? Problem)> ReadJsonAsync(
        PowerShellRunner runner,
        string script,
        IReadOnlyList<string>? arguments,
        CancellationToken cancellationToken)
    {
        PowerShellResult result = arguments is { Count: > 0 }
            ? await runner.RunWithArgumentsAsync(script, arguments, cancellationToken).ConfigureAwait(false)
            : await runner.RunAsync(script, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // The exit code goes in when there is nothing else to say. "The query did not complete"
            // on its own tells a support engineer nothing, and this is the message that reaches the
            // user as the reason a reading is missing (spec 6.1).
            return (null, string.IsNullOrWhiteSpace(result.ErrorSummary)
                ? $"the query exited with {result.ExitCode} and produced no output"
                : result.ErrorSummary);
        }

        string output = result.StandardOutput.Trim();

        if (string.IsNullOrEmpty(output) || output == "null")
        {
            return (null, null);
        }

        try
        {
            return (JsonDocument.Parse(output), null);
        }
        catch (JsonException ex)
        {
            return (null, $"the query returned something that is not JSON: {ex.Message}");
        }
    }

    /// <summary>PowerShell renders a one-element array as the element. Accept both.</summary>
    internal static IEnumerable<JsonElement> Rows(JsonDocument document) =>
        document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
            : [document.RootElement];

    internal static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <remarks>
    /// The <c>ValueKind</c> test is not belt and braces: <c>TryGetInt32</c> throws rather than
    /// returning false when the element is a JSON null, and PowerShell writes null for every
    /// property it had no value for.
    /// </remarks>
    /// <remarks>
    /// PowerShell writes a <c>[bool]</c> as a JSON <c>true</c>/<c>false</c>, and writes null for a
    /// property it had no value for — which is a third answer, not a false.
    /// </remarks>
    internal static bool? Flag(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    internal static int? Number(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number)
            ? number
            : null;
}

/// <summary>
/// What Windows Update installed, and whether it worked.
/// </summary>
/// <remarks>
/// Through the Update Session COM object rather than the event log, because it is readable without
/// administrator rights and it records the outcome — the difference between an update that
/// installed and one that failed and retried is most of the value of this source.
/// </remarks>
public sealed class WindowsUpdateHistorySource(PowerShellRunner? powerShell = null) : IChangeSource
{
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $session  = New-Object -ComObject Microsoft.Update.Session
        $searcher = $session.CreateUpdateSearcher()
        $total    = $searcher.GetTotalHistoryCount()

        if ($total -le 0) { '[]'; exit 0 }

        $searcher.QueryHistory(0, [Math]::Min($total, 100)) | ForEach-Object {
            [pscustomobject]@{
                Date       = $_.Date.ToUniversalTime().ToString('o')
                Title      = $_.Title
                ResultCode = [int] $_.ResultCode
                Operation  = [int] $_.Operation
            }
        } | ConvertTo-Json -Depth 3 -Compress
        """;

    private readonly PowerShellRunner _powerShell = powerShell ?? PowerShellRunner.Default;

    public string Id => "windows-update";

    public async Task<ChangeSourceResult> ReadAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        (JsonDocument? document, string? problem) = await WindowsChangeSources
            .ReadJsonAsync(_powerShell, Script, cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null)
        {
            return ChangeSourceResult.Unavailable(
                Id,
                $"Windows Update history could not be read: {problem}");
        }

        if (document is null)
        {
            return new ChangeSourceResult(Id, []);
        }

        using (document)
        {
            List<ChangeEvent> events = [];

            foreach (JsonElement row in WindowsChangeSources.Rows(document))
            {
                DateTimeOffset? at = WindowsChangeSources.ParseTime(WindowsChangeSources.Text(row, "Date"));
                string? title = WindowsChangeSources.Text(row, "Title");

                if (at is not { } when || when < since || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                // Documented ResultCode values: 1 in progress, 2 succeeded, 3 succeeded with
                // errors, 4 failed, 5 aborted. Operation 1 is install, 2 is uninstall.
                int result = WindowsChangeSources.Number(row, "ResultCode") ?? 0;
                bool uninstall = WindowsChangeSources.Number(row, "Operation") == 2;

                events.Add(new ChangeEvent(
                    WindowsChangeSources.EventId(Id, when, title),
                    when,
                    EventSource.WindowsUpdate,
                    EventCategory.Update,
                    title,
                    Before: null,
                    After: Describe(result, uninstall),
                    Initiator.System,

                    // High: this is Windows' own record of its own action, not our reading of a
                    // side effect.
                    Confidence.High,
                    new Evidence(
                        EvidenceSourceKind.PowerShell,
                        "Microsoft.Update.Session history",
                        Confidence.High,
                        Query: "IUpdateSearcher::QueryHistory",
                        RawResult: $"resultCode={result}, operation={(uninstall ? 2 : 1)}")));
            }

            return new ChangeSourceResult(Id, events);
        }
    }

    private static string Describe(int resultCode, bool uninstall) => (resultCode, uninstall) switch
    {
        (2, false) => "installed",
        (2, true) => "uninstalled",
        (3, _) => "installed with errors",
        (4, _) => "failed",
        (5, _) => "cancelled",
        _ => "in progress",
    };
}

/// <summary>
/// Crashes, unexpected power loss, hardware errors and driver installs, from the System log.
/// </summary>
/// <remarks>
/// The four providers here are the ones that answer "did this machine actually fall over, or does
/// it just feel slow?" — and the answer changes what to investigate next. Reading the System log
/// normally needs no special rights; when it does, this source says so rather than reporting a
/// quiet machine.
/// </remarks>
public sealed class SystemEventLogSource(PowerShellRunner? powerShell = null) : IChangeSource
{
    private const string ScriptTemplate = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $since = [datetime]::Parse('{SINCE}', [Globalization.CultureInfo]::InvariantCulture,
                                   [Globalization.DateTimeStyles]::RoundtripKind)

        $filter = @{
            LogName      = 'System'
            StartTime    = $since
            ProviderName = @('Microsoft-Windows-Kernel-Power',
                             'Microsoft-Windows-WHEA-Logger',
                             'Microsoft-Windows-Kernel-PnP',
                             'Microsoft-Windows-WER-SystemErrorReporting')
        }

        $events = @(Get-WinEvent -FilterHashtable $filter -MaxEvents 200 -ErrorAction SilentlyContinue)

        if ($events.Count -eq 0) { '[]'; exit 0 }

        $events | ForEach-Object {
            [pscustomobject]@{
                Time     = $_.TimeCreated.ToUniversalTime().ToString('o')
                Id       = [int] $_.Id
                Provider = $_.ProviderName
                Level    = [int] $_.Level
            }
        } | ConvertTo-Json -Depth 3 -Compress
        """;

    private readonly PowerShellRunner _powerShell = powerShell ?? PowerShellRunner.Default;

    public string Id => "system-log";

    public async Task<ChangeSourceResult> ReadAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        string script = ScriptTemplate.Replace(
            "{SINCE}",
            since.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

        (JsonDocument? document, string? problem) = await WindowsChangeSources
            .ReadJsonAsync(_powerShell, script, cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null)
        {
            return ChangeSourceResult.Unavailable(
                Id,
                $"the Windows System log could not be read, so crashes and hardware errors are not "
                + $"shown here — this is not evidence that there were none: {problem}");
        }

        if (document is null)
        {
            return new ChangeSourceResult(Id, []);
        }

        using (document)
        {
            List<ChangeEvent> events = [];

            foreach (JsonElement row in WindowsChangeSources.Rows(document))
            {
                DateTimeOffset? at = WindowsChangeSources.ParseTime(WindowsChangeSources.Text(row, "Time"));
                string? provider = WindowsChangeSources.Text(row, "Provider");
                int id = WindowsChangeSources.Number(row, "Id") ?? 0;

                if (at is not { } when || provider is null)
                {
                    continue;
                }

                (EventCategory category, string what)? classified = Classify(provider, id);

                // Anything we cannot name is left out. A timeline of event ids nobody can read is
                // not a timeline, and guessing at the meaning of one is worse.
                if (classified is not { } entry)
                {
                    continue;
                }

                events.Add(new ChangeEvent(
                    WindowsChangeSources.EventId(Id, when, $"{provider}/{id}"),
                    when,
                    EventSource.EventLog,
                    entry.category,
                    entry.what,
                    Before: null,
                    After: null,
                    Initiator.System,
                    Confidence.High,
                    new Evidence(
                        EvidenceSourceKind.PowerShell,
                        $"System event log, {provider} event {id}",
                        Confidence.High,
                        Query: "Get-WinEvent -LogName System")));
            }

            return new ChangeSourceResult(Id, events);
        }
    }

    /// <summary>
    /// Only the handful of event ids whose meaning is documented and unambiguous.
    /// </summary>
    private static (EventCategory Category, string What)? Classify(string provider, int id) => (provider, id) switch
    {
        ("Microsoft-Windows-Kernel-Power", 41) =>
            (EventCategory.Crash, "The PC restarted without shutting down cleanly"),
        ("Microsoft-Windows-Kernel-Power", 42) =>
            (EventCategory.Restart, "The PC went to sleep"),
        ("Microsoft-Windows-Kernel-Power", 109) =>
            (EventCategory.Restart, "The PC was shut down or restarted"),
        ("Microsoft-Windows-WER-SystemErrorReporting", 1001) =>
            (EventCategory.Crash, "Windows stopped with a blue screen"),
        ("Microsoft-Windows-WHEA-Logger", 17 or 18 or 19 or 47) =>
            (EventCategory.Device, "The hardware reported an error the system corrected"),
        ("Microsoft-Windows-WHEA-Logger", 1 or 20) =>
            (EventCategory.Device, "The hardware reported an error the system could not correct"),
        ("Microsoft-Windows-Kernel-PnP", 400 or 410) =>
            (EventCategory.Driver, "A device driver was installed or updated"),
        _ => null,
    };
}

/// <summary>
/// Restore points, which double as a log of what Windows thought was worth protecting against.
/// </summary>
/// <remarks>
/// A restore point's description is usually the name of the thing that triggered it — an update,
/// an installer, a driver package. That makes this the cheapest source of "something significant
/// happened here" on the whole machine, and it is why the research pairs it with the timeline
/// rather than only with recovery.
/// </remarks>
public sealed class RestorePointSource(PowerShellRunner? powerShell = null) : IChangeSource
{
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $points = @(Get-ComputerRestorePoint)

        if ($points.Count -eq 0) { '[]'; exit 0 }

        $points | ForEach-Object {
            $when = $null
            try { $when = $_.ConvertToDateTime($_.CreationTime) }
            catch { $when = [System.Management.ManagementDateTimeConverter]::ToDateTime($_.CreationTime) }

            [pscustomobject]@{
                Time        = $when.ToUniversalTime().ToString('o')
                Description = [string] $_.Description
            }
        } | ConvertTo-Json -Depth 3 -Compress
        """;

    private readonly PowerShellRunner _powerShell = powerShell ?? PowerShellRunner.Default;

    public string Id => "restore-points";

    public async Task<ChangeSourceResult> ReadAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        (JsonDocument? document, string? problem) = await WindowsChangeSources
            .ReadJsonAsync(_powerShell, Script, cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null)
        {
            return ChangeSourceResult.Unavailable(
                Id,
                $"restore points could not be listed — Get-ComputerRestorePoint needs administrator "
                + $"rights, so this is not evidence that none were created: {problem}");
        }

        if (document is null)
        {
            return new ChangeSourceResult(Id, []);
        }

        using (document)
        {
            List<ChangeEvent> events = [];

            foreach (JsonElement row in WindowsChangeSources.Rows(document))
            {
                DateTimeOffset? at = WindowsChangeSources.ParseTime(WindowsChangeSources.Text(row, "Time"));
                string description = WindowsChangeSources.Text(row, "Description") ?? "Restore point";

                if (at is not { } when || when < since)
                {
                    continue;
                }

                events.Add(new ChangeEvent(
                    WindowsChangeSources.EventId(Id, when, description),
                    when,
                    EventSource.Restore,
                    EventCategory.Checkpoint,
                    description,
                    Before: null,
                    After: "restore point created",
                    Initiator.System,
                    Confidence.High,
                    new Evidence(
                        EvidenceSourceKind.PowerShell,
                        "Get-ComputerRestorePoint",
                        Confidence.High,
                        Query: "Get-ComputerRestorePoint")));
            }

            return new ChangeSourceResult(Id, events);
        }
    }
}

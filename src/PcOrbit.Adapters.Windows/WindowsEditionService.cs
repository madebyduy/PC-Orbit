using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// The pure parts of edition conversion: reading DISM's output, and handling the product key.
/// </summary>
/// <remarks>
/// Separated so they can be tested without an elevated DISM on real hardware. These are the two
/// places a mistake would be expensive — a mis-parsed target list offers an edition Windows never
/// mentioned, and a key that survives into a log outlives every other trace of itself.
/// </remarks>
public static partial class EditionOutput
{
    /// <summary>
    /// DISM prints its target editions one per line as "Target Edition : Professional".
    /// </summary>
    public static IReadOnlyList<TargetEdition> ParseTargets(string dism)
    {
        ArgumentNullException.ThrowIfNull(dism);

        return
        [
            .. TargetEditionLine()
                .Matches(dism)
                .Select(m => m.Groups[1].Value.Trim())
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(id => new TargetEdition(id, Humanise(id))),
        ];
    }

    /// <summary>True when DISM refused because it was not run elevated.</summary>
    public static bool NeedsElevation(string dism) =>
        dism.Contains("740", StringComparison.Ordinal)
        || dism.Contains("Elevated permissions", StringComparison.OrdinalIgnoreCase);

    /// <summary>Five groups of five alphanumerics. Anything else never reaches DISM.</summary>
    public static bool LooksLikeProductKey(string key) =>
        !string.IsNullOrWhiteSpace(key) && ProductKey().IsMatch(key.Trim());

    /// <summary>
    /// Removes the product key from anything on its way to a log or a screen.
    /// </summary>
    /// <remarks>
    /// DISM echoes its arguments into some failure messages, and this product writes failures into
    /// an append-only event log.
    /// </remarks>
    public static string Scrub(string text, string key) =>
        string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)
            ? text
            : text.Replace(key.Trim(), "•••••-•••••-•••••-•••••-•••••", StringComparison.OrdinalIgnoreCase);

    /// <summary>The documented SoftwareLicensingProduct values.</summary>
    public static LicenceState ToLicenceState(int? status) => status switch
    {
        1 => LicenceState.Licensed,
        2 or 3 or 6 => LicenceState.Grace,
        5 => LicenceState.Notification,
        0 => LicenceState.Unlicensed,
        _ => LicenceState.Unknown,
    };

    /// <summary>"ProfessionalWorkstation" reads as "Professional Workstation".</summary>
    private static string Humanise(string id) => CamelBoundary().Replace(id, "$1 $2");

    [GeneratedRegex(@"Target Edition\s*:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex TargetEditionLine();

    [GeneratedRegex("^[A-Za-z0-9]{5}-[A-Za-z0-9]{5}-[A-Za-z0-9]{5}-[A-Za-z0-9]{5}-[A-Za-z0-9]{5}$")]
    private static partial Regex ProductKey();

    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelBoundary();
}

/// <summary>
/// Edition conversion through DISM, the way Microsoft documents it.
/// </summary>
/// <remarks>
/// <para>
/// Two commands, both supported: <c>DISM /Online /Get-TargetEditions</c> to ask Windows what this
/// installation may become, and <c>DISM /Online /Set-Edition</c> to move it. Both need
/// administrator rights, and the first one failing with error 740 is how a standard user finds
/// that out — reported as such rather than as "no editions available".
/// </para>
/// <para>
/// The product key reaches DISM through the environment, never through the command line. A key on
/// a command line is visible in the process list to every account on the machine for as long as
/// the command runs, and DISM's own output is scrubbed of it before anything is stored.
/// </para>
/// </remarks>
public sealed partial class WindowsEditionService(PowerShellRunner? powerShell = null) : IEditionService
{
    private const string ReadScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $edition = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name EditionID).EditionID

        # The Windows operating system product, by its fixed application id. Filtering on a partial
        # product key skips the per-feature licences that would otherwise drown the real one.
        $licence = Get-CimInstance SoftwareLicensingProduct -Filter "ApplicationId='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL" |
            Select-Object -First 1 LicenseStatus, Description, Name

        $dism = & "$env:SystemRoot\System32\Dism.exe" /Online /English /Get-TargetEditions 2>&1 | Out-String

        [pscustomobject]@{
            Edition       = [string] $edition
            LicenseStatus = if ($licence) { [int] $licence.LicenseStatus } else { $null }
            Description   = [string] $licence.Description
            DismExit      = $LASTEXITCODE
            DismOutput    = [string] $dism
        } | ConvertTo-Json -Depth 3 -Compress
        """;

    /// <summary>
    /// The key arrives in the environment and DISM's output is scrubbed before it is returned.
    /// </summary>
    private const string ChangeScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $target = $env:PCORBIT_ARG0
        $key    = $env:PCORBIT_ARG1

        $output = & "$env:SystemRoot\System32\Dism.exe" /Online /English /Quiet /NoRestart `
            "/Set-Edition:$target" "/ProductKey:$key" /AcceptEula 2>&1 | Out-String

        [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output   = [string] $output
        } | ConvertTo-Json -Depth 3 -Compress
        """;

    private readonly PowerShellRunner _powerShell = powerShell ?? PowerShellRunner.Default;

    public async Task<EditionOptions> ReadAsync(CancellationToken cancellationToken = default)
    {
        (JsonDocument? document, string? problem) = await WindowsChangeSources
            .ReadJsonAsync(_powerShell, ReadScript, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return new EditionOptions(
                "Unknown",
                UnknownLicence(problem),
                [],
                problem ?? "the current edition could not be read");
        }

        using (document)
        {
            JsonElement row = WindowsChangeSources.Rows(document).First();

            string edition = WindowsChangeSources.Text(row, "Edition") ?? "Unknown";
            LicenceStatus licence = ReadLicence(row);

            string dism = WindowsChangeSources.Text(row, "DismOutput") ?? string.Empty;

            // 740 is DISM's own "elevated permissions are required". Saying that is the whole
            // difference between "you need to run this as administrator" and the user concluding
            // their machine cannot change edition at all.
            if (EditionOutput.NeedsElevation(dism))
            {
                return new EditionOptions(
                    edition,
                    licence,
                    [],
                    "Windows will only list the editions this machine can move to for an administrator.");
            }

            return new EditionOptions(edition, licence, EditionOutput.ParseTargets(dism));
        }
    }

    public async Task<EditionChangeResult> ChangeAsync(
        string targetEditionId,
        string productKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetEditionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(productKey);

        // The allowlist: Windows' own list of what this installation may become, read now. A target
        // the machine did not just offer never reaches DISM (ADR 0005).
        EditionOptions options = await ReadAsync(cancellationToken).ConfigureAwait(false);

        if (!options.Targets.Any(t => string.Equals(t.Id, targetEditionId, StringComparison.OrdinalIgnoreCase)))
        {
            return new EditionChangeResult(
                targetEditionId,
                Applied: false,
                RestartRequired: false,
                options.Problem
                    ?? $"Windows does not offer '{targetEditionId}' as a target for this installation.");
        }

        if (!EditionOutput.LooksLikeProductKey(productKey))
        {
            return new EditionChangeResult(
                targetEditionId,
                Applied: false,
                RestartRequired: false,
                "That does not look like a Windows product key. It is five groups of five characters.");
        }

        PowerShellResult result = await _powerShell
            .RunWithArgumentsAsync(ChangeScript, [targetEditionId, productKey.Trim()], cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return new EditionChangeResult(
                targetEditionId,
                Applied: false,
                RestartRequired: false,
                EditionOutput.Scrub(result.ErrorSummary, productKey));
        }

        using JsonDocument? document = TryParse(result.StandardOutput);
        JsonElement? row = document is null ? null : WindowsChangeSources.Rows(document).First();

        int exit = row is { } r ? WindowsChangeSources.Number(r, "ExitCode") ?? -1 : -1;
        string output = row is { } o ? WindowsChangeSources.Text(o, "Output") ?? string.Empty : string.Empty;

        // 0 is done, 3010 is done-and-wants-a-restart. Everything else is a failure with DISM's own
        // words, minus the key.
        return exit is 0 or 3010
            ? new EditionChangeResult(targetEditionId, Applied: true, RestartRequired: true)
            : new EditionChangeResult(
                targetEditionId,
                Applied: false,
                RestartRequired: false,
                EditionOutput.Scrub(output.Length == 0 ? $"DISM exited with {exit}." : output, productKey));
    }

    // ---------------------------------------------------------------- internals

    private static LicenceStatus ReadLicence(JsonElement row)
    {
        int? status = WindowsChangeSources.Number(row, "LicenseStatus");
        string? description = WindowsChangeSources.Text(row, "Description");

        LicenceState state = EditionOutput.ToLicenceState(status);

        // "Windows(R) Operating System, RETAIL channel" — the channel decides whether a key can
        // move to another machine, which is the question people actually have.
        string? channel = description is null ? null : Channel().Match(description) switch
        {
            { Success: true } m => m.Groups[1].Value,
            _ => null,
        };

        return new LicenceStatus(
            state,
            channel,
            description,
            status is null
                ? Evidence.Missing("SoftwareLicensingProduct reported no Windows licence with a product key")
                : new Evidence(
                    EvidenceSourceKind.Wmi,
                    "SoftwareLicensingProduct.LicenseStatus",
                    Confidence.High,
                    Query: "SoftwareLicensingProduct WHERE ApplicationId='55c92734-…'",
                    RawResult: status.Value.ToString(CultureInfo.InvariantCulture)));
    }

    private static LicenceStatus UnknownLicence(string? problem) => new(
        LicenceState.Unknown,
        null,
        null,
        Evidence.Missing(problem ?? "the licence could not be read"));

    private static JsonDocument? TryParse(string json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"Target Edition\s*:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex TargetEditionLine();

    [GeneratedRegex(@"([A-Z_]+) channel", RegexOptions.IgnoreCase)]
    private static partial Regex Channel();

    [GeneratedRegex("^[A-Za-z0-9]{5}-[A-Za-z0-9]{5}-[A-Za-z0-9]{5}-[A-Za-z0-9]{5}-[A-Za-z0-9]{5}$")]
    private static partial Regex ProductKey();

    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelBoundary();
}

using System.Text.Json;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows;

/// <param name="TamperProtected">
/// When true, Windows reverts writes to Defender's own settings. Any switch this product offered
/// would appear to work and then quietly undo itself.
/// </param>
public sealed record ProtectionState(
    bool? RealTimeProtection,
    bool? TamperProtected,
    bool? SmartScreenOn,
    string? AntivirusName,
    Evidence Evidence)
{
    /// <summary>
    /// Whether a request to weaken Defender can be honoured at all on this machine.
    /// </summary>
    /// <remarks>
    /// The honest gate. With Tamper Protection on, the registry write succeeds, the value appears,
    /// and Windows puts it straight back — so the only truthful thing to offer is the route the
    /// user takes themselves, in Windows Security.
    /// </remarks>
    public bool DefenderIsWritable => TamperProtected == false;
}

/// <summary>
/// What is currently protecting this machine.
/// </summary>
/// <remarks>
/// <para>
/// This exists because tools in this category ship "turn off Defender" and "turn off SmartScreen"
/// as optimisations, and users ask for them. Two of those three requests can be honoured honestly
/// and one cannot, and which is which is a fact about the machine rather than a matter of taste.
/// </para>
/// <para>
/// PC Orbit's position (ADR 0006) is that these appear in the checkup as findings when they are
/// <em>off</em>. Where the owner still wants one off — their machine, their decision — the change
/// must at least be real: applied, verified by reading back, and reversible. Where Windows will not
/// let it be real, we say so and point at the setting instead of writing a value that lies.
/// </para>
/// </remarks>
public sealed class WindowsProtectionState(PowerShellRunner? powerShell = null)
{
    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $status = Get-MpComputerStatus

        # SmartScreen for apps and files. Absent means Windows' default, which is on.
        $shell = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' `
                                   -Name SmartScreenEnabled -EA SilentlyContinue).SmartScreenEnabled
        $policy = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' `
                                    -Name EnableSmartScreen -EA SilentlyContinue).EnableSmartScreen

        [pscustomobject]@{
            RealTime  = if ($status) { [bool] $status.RealTimeProtectionEnabled } else { $null }
            Tamper    = if ($status) { [bool] $status.IsTamperProtected } else { $null }
            Product   = [string] $status.AMProductVersion
            Shell     = [string] $shell
            Policy    = if ($policy -ne $null) { [int] $policy } else { $null }
        } | ConvertTo-Json -Depth 3 -Compress
        """;

    private readonly PowerShellRunner _powerShell = powerShell ?? PowerShellRunner.Default;

    public async Task<ProtectionState> ReadAsync(CancellationToken cancellationToken = default)
    {
        (JsonDocument? document, string? problem) = await WindowsChangeSources
            .ReadJsonAsync(_powerShell, Script, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return new ProtectionState(
                null,
                null,
                null,
                null,
                Evidence.Missing(problem ?? "Get-MpComputerStatus returned nothing"));
        }

        using (document)
        {
            JsonElement row = WindowsChangeSources.Rows(document).First();

            bool? realTime = Bool(row, "RealTime");
            bool? tamper = Bool(row, "Tamper");

            string? shell = WindowsChangeSources.Text(row, "Shell");
            int? policy = WindowsChangeSources.Number(row, "Policy");

            // Policy wins where it exists; otherwise the shell value; otherwise the default, which
            // is on. "Off" here has to mean somebody turned it off, not that nobody set it.
            bool? smartScreen = policy switch
            {
                0 => false,
                1 => true,
                _ => shell switch
                {
                    null or "" => true,
                    "Off" => false,
                    _ => true,
                },
            };

            return new ProtectionState(
                realTime,
                tamper,
                smartScreen,
                WindowsChangeSources.Text(row, "Product"),
                new Evidence(
                    EvidenceSourceKind.PowerShell,
                    "Get-MpComputerStatus, plus the SmartScreen policy and shell values",
                    Confidence.High,
                    Query: "Get-MpComputerStatus",
                    RawResult: $"realTime={realTime}, tamper={tamper}, smartScreen={smartScreen}"));
        }
    }

    private static bool? Bool(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}

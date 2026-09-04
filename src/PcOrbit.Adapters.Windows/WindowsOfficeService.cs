using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PcOrbit.Core.Apps;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// Office through Microsoft's Office Deployment Tool.
/// </summary>
/// <remarks>
/// <para>
/// The tool itself comes from winget, not from a URL this product carries. A hard-coded download
/// link is a hostage to Microsoft's release cadence and a place for a typo to become a supply-chain
/// problem; <c>Microsoft.OfficeDeploymentTool</c> is in the same hashed repository as everything
/// else the catalogue installs.
/// </para>
/// <para>
/// The configuration is built with an XML writer rather than by pasting strings together, so a
/// language tag or an excluded app cannot close a tag early and turn the document into something
/// else.
/// </para>
/// </remarks>
public sealed class WindowsOfficeService(PowerShellRunner? powerShell = null) : IOfficeService
{
    private const string ReadScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        # Click-to-Run is where every supported Office since 2013 records itself.
        $c2r = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Office\ClickToRun\Configuration' -ErrorAction SilentlyContinue

        [pscustomobject]@{
            Product = [string] $c2r.ProductReleaseIds
            Version = [string] $c2r.VersionToReport
            Present = [bool] ($c2r -ne $null)
        } | ConvertTo-Json -Depth 3 -Compress
        """;

    /// <summary>
    /// Fetches the deployment tool, then runs it twice: download, then configure.
    /// </summary>
    /// <remarks>
    /// The tool is a self-extracting archive; <c>/extract</c> with <c>/quiet</c> unpacks
    /// <c>setup.exe</c> beside the configuration this product wrote.
    /// </remarks>
    private const string InstallScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $work = $env:PCORBIT_ARG0
        $xml  = $env:PCORBIT_ARG1

        $stage = 'tool'
        $problem = $null

        try {
            New-Item -ItemType Directory -Path $work -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $work 'configuration.xml') -Value $xml -Encoding UTF8

            # The tool from the same hashed repository the app catalogue uses.
            winget install --id Microsoft.OfficeDeploymentTool --exact --silent `
                --accept-package-agreements --accept-source-agreements 2>&1 | Out-Null

            $setup = Get-ChildItem -Path "$env:ProgramFiles\OfficeDeploymentTool", `
                                         "${env:ProgramFiles(x86)}\OfficeDeploymentTool", `
                                         "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" `
                                   -Filter 'setup.exe' -Recurse -ErrorAction SilentlyContinue |
                     Select-Object -First 1

            if (-not $setup) { throw 'The Office Deployment Tool was installed but setup.exe could not be found.' }

            $stage = 'download'
            & $setup.FullName /download (Join-Path $work 'configuration.xml') 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "The download stage exited with $LASTEXITCODE." }

            $stage = 'configure'
            & $setup.FullName /configure (Join-Path $work 'configuration.xml') 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "The install stage exited with $LASTEXITCODE." }

            $stage = 'done'
        }
        catch {
            $problem = $_.Exception.Message
        }

        [pscustomobject]@{ Stage = $stage; Problem = $problem } | ConvertTo-Json -Depth 3 -Compress
        exit 0
        """;

    private readonly PowerShellRunner _powerShell = powerShell
        ?? new PowerShellRunner(TimeSpan.FromMinutes(60));

    public async Task<OfficeState> ReadAsync(CancellationToken cancellationToken = default)
    {
        (JsonDocument? document, string? problem) = await WindowsChangeSources
            .ReadJsonAsync(_powerShell, ReadScript, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return new OfficeState(null, null, null, Evidence.Missing(problem ?? "the Office registry key could not be read"));
        }

        using (document)
        {
            JsonElement row = document.RootElement;

            bool present = row.TryGetProperty("Present", out JsonElement p) && p.ValueKind == JsonValueKind.True;
            string? product = WindowsChangeSources.Text(row, "Product");

            return new OfficeState(
                present,
                product,
                WindowsChangeSources.Text(row, "Version"),
                new Evidence(
                    EvidenceSourceKind.Registry,
                    @"HKLM\SOFTWARE\Microsoft\Office\ClickToRun\Configuration",
                    Confidence.High,
                    RawResult: present ? product : "(no Click-to-Run installation)"));
        }
    }

    /// <summary>
    /// Builds the deployment tool's configuration document.
    /// </summary>
    /// <remarks>
    /// Through <see cref="XElement"/>, so every value is escaped by the writer. Building this by
    /// string concatenation would let a language tag carrying a quote rewrite the document, and
    /// the document is the whole instruction the installer obeys.
    /// </remarks>
    public string DescribeConfiguration(OfficePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var product = new XElement(
            "Product",
            new XAttribute("ID", plan.ProductId),
            new XElement("Language", new XAttribute("ID", plan.Language)));

        foreach (string excluded in plan.ExcludedApps.Where(OfficePlan.ExcludableApps.Contains))
        {
            product.Add(new XElement("ExcludeApp", new XAttribute("ID", excluded)));
        }

        var configuration = new XElement(
            "Configuration",
            new XElement(
                "Add",
                new XAttribute("OfficeClientEdition", plan.SixtyFourBit ? "64" : "32"),
                new XAttribute("Channel", "Current"),
                product),

            // Silent, and it accepts the licence terms on the user's behalf only because the app
            // showed them this document and asked first.
            new XElement(
                "Display",
                new XAttribute("Level", "None"),
                new XAttribute("AcceptEULA", "TRUE")),

            // Off by default: this product does not send anyone's telemetry to a third party, and
            // Office's own reporting is Microsoft's business with the user, not ours to enable.
            new XElement("Property", new XAttribute("Name", "AUTOACTIVATE"), new XAttribute("Value", "0")));

        return configuration.ToString();
    }

    public async Task<OfficeInstallResult> InstallAsync(
        OfficePlan plan,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // The excluded list is filtered against the tool's own names, so a value from anywhere else
        // cannot become an element in the document.
        string xml = DescribeConfiguration(plan);

        string work = Path.Combine(
            Path.GetTempPath(),
            string.Create(CultureInfo.InvariantCulture, $"pcorbit-office-{Guid.NewGuid():N}"));

        progress?.Report("tool");

        PowerShellResult result = await _powerShell
            .RunWithArgumentsAsync(InstallScript, [work, xml], cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (!result.Succeeded)
            {
                return new OfficeInstallResult("tool", false, result.ErrorSummary);
            }

            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement row = document.RootElement;

            string stage = WindowsChangeSources.Text(row, "Stage") ?? "tool";
            string? problem = WindowsChangeSources.Text(row, "Problem");

            progress?.Report(stage);

            return new OfficeInstallResult(stage, problem is null && stage == "done", problem);
        }
        catch (JsonException ex)
        {
            return new OfficeInstallResult("tool", false, $"the installer returned something unreadable: {ex.Message}");
        }
        finally
        {
            TryRemove(work);
        }
    }

    /// <summary>The configuration carries no secrets, but a temp folder left behind is still litter.</summary>
    private static void TryRemove(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The installer may still hold it. It is under TEMP; Windows will clear it.
        }
    }
}

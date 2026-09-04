using System.Text.Json;
using PcOrbit.Core.Apps;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// Installs and removes applications through winget.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft's own package manager, present on Windows 11, downloading from a repository that
/// hashes every payload and refuses a mismatch. That is what makes this feature something the
/// product can stand behind: the alternative — fetching installers from vendor sites ourselves —
/// would mean re-implementing that verification and getting it wrong.
/// </para>
/// <para>
/// Two winget behaviours do the real work here. <c>winget export</c> writes the installed set as
/// JSON, which is how the catalogue knows what is already there. And
/// <c>winget list --id X --exact</c> exits 0 when the package is present and non-zero when it is
/// not, which is how a change is verified afterwards rather than taken on trust.
/// </para>
/// </remarks>
public sealed class WindowsAppService(AppCatalog catalog, PowerShellRunner? powerShell = null) : IAppService
{
    private const string AvailableScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        if (Get-Command winget -ErrorAction SilentlyContinue) { 'yes' } else { 'no' }
        """;

    /// <summary>
    /// What is installed, from two places.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>winget export</c> writes the installed set as JSON — to a file rather than to the pipe,
    /// so this writes a temporary one and reads it back. Its warnings about packages that came from
    /// outside the repository go to the error stream and are expected; every machine has some.
    /// </para>
    /// <para>
    /// Add or Remove Programs is read as well, and it is the half that fixes a real complaint. A
    /// machine with Chrome Beta on it exports <c>Google.Chrome.Beta.EXE</c>, so asking winget about
    /// <c>Google.Chrome</c> answers no — correct about package ids, wrong about the question the
    /// user is asking, which is whether they have Chrome. The uninstall keys say "Google Chrome"
    /// and that is the answer they can see for themselves.
    /// </para>
    /// </remarks>
    private const string InstalledScript = """
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $file = Join-Path $env:TEMP ("pcorbit-apps-" + [guid]::NewGuid().ToString('N') + ".json")
        $ids = @()
        $problem = $null

        try {
            # winget's own warnings about packages that came from outside the repository go to the
            # error stream, and every machine has some — so they are captured rather than allowed
            # to make the whole call look like a failure.
            $noise = winget export --output $file --accept-source-agreements --disable-interactivity 2>&1
            $code = $LASTEXITCODE

            if (Test-Path -LiteralPath $file) {
                $doc = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
                $ids = @($doc.Sources | ForEach-Object { $_.Packages } |
                         ForEach-Object { $_.PackageIdentifier } | Where-Object { $_ })
            }
            else {
                $problem = "winget export exited with $code and wrote nothing: " +
                           (($noise | Out-String).Trim() -split "`n" | Select-Object -First 3) -join ' '
            }
        }
        catch {
            $problem = $_.Exception.Message
        }
        finally {
            Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        }

        # Add or Remove Programs, from all three uninstall roots. SystemComponent entries are the
        # ones Windows hides from its own list, and hiding them here too keeps the two agreeing.
        $programs = @()
        try {
            $roots = @(
                'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
                'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')

            $programs = @(Get-ItemProperty $roots -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -and -not $_.SystemComponent } |
                ForEach-Object { [string] $_.DisplayName } |
                Sort-Object -Unique)
        }
        catch {
            # Left empty rather than failing the whole read: winget's answer is still worth having.
            $programs = @()
        }

        # Always JSON, always one shape. A script that sometimes prints nothing gives the caller
        # nothing to report except that it gave nothing.
        [pscustomobject]@{ Ids = @($ids); Programs = @($programs); Problem = $problem } |
            ConvertTo-Json -Depth 3 -Compress

        exit 0
        """;

    private const string InstallScript = """
        $ErrorActionPreference = 'Continue'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $id = $env:PCORBIT_ARG0

        $output = winget install --id $id --exact --silent `
            --accept-package-agreements --accept-source-agreements 2>&1 | Out-String

        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = [string] $output } |
            ConvertTo-Json -Depth 3 -Compress
        """;

    private const string UninstallScript = """
        $ErrorActionPreference = 'Continue'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $id = $env:PCORBIT_ARG0

        $output = winget uninstall --id $id --exact --silent 2>&1 | Out-String

        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = [string] $output } |
            ConvertTo-Json -Depth 3 -Compress
        """;

    /// <summary>Exit 0 means present. Anything else — 20 is the usual — means it is not.</summary>
    private const string PresentScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        winget list --id $env:PCORBIT_ARG0 --exact --accept-source-agreements 2>&1 | Out-Null

        if ($LASTEXITCODE -eq 0) { 'yes' } else { 'no' }
        """;

    private readonly AppCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly PowerShellRunner _powerShell = powerShell ?? PowerShellRunner.Default;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        PowerShellResult result = await _powerShell.RunAsync(AvailableScript, cancellationToken).ConfigureAwait(false);

        return result.Succeeded && result.StandardOutput.Trim() == "yes";
    }

    public async Task<InstalledApps> InstalledAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return InstalledApps.Unknown(
                "winget is not on this machine, so what is already installed could not be read. "
                + "It ships with Windows 11 as App Installer.");
        }

        (JsonDocument? document, string? problem) = await WindowsChangeSources
            .ReadJsonAsync(_powerShell, InstalledScript, cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null)
        {
            return InstalledApps.Unknown($"the installed package list could not be read: {problem}");
        }

        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> foundAs = new(StringComparer.OrdinalIgnoreCase);

        if (document is null)
        {
            return new InstalledApps(ids);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (WindowsChangeSources.Text(root, "Problem") is { Length: > 0 } reported)
            {
                return InstalledApps.Unknown($"the installed package list could not be read: {reported}");
            }

            foreach (string id in Strings(root, "Ids"))
            {
                ids.Add(id);

                if (_catalog.Find(id) is { } known)
                {
                    foundAs[known.Id] = known.Name;
                }
            }

            // The second source. A catalogue entry counts as installed when Windows' own list of
            // programs calls something by one of its names, whatever winget managed to correlate.
            foreach (string displayName in Strings(root, "Programs"))
            {
                foreach (CatalogApp app in _catalog.All)
                {
                    if (app.IsCalled(displayName))
                    {
                        ids.Add(app.Id);
                        foundAs[app.Id] = displayName;
                    }
                }
            }
        }

        return new InstalledApps(ids, null, foundAs);
    }

    /// <summary>
    /// A string array out of the payload, tolerating the shape PowerShell renders for one element.
    /// </summary>
    private static IEnumerable<string> Strings(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement list))
        {
            yield break;
        }

        // PowerShell renders a one-element array as the element, so both shapes arrive.
        IEnumerable<JsonElement> entries = list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
            : [list];

        foreach (JsonElement entry in entries)
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } value)
            {
                yield return value;
            }
        }
    }

    public Task<AppChangeResult> InstallAsync(string id, CancellationToken cancellationToken = default) =>
        ChangeAsync(id, install: true, cancellationToken);

    public Task<AppChangeResult> UninstallAsync(string id, CancellationToken cancellationToken = default) =>
        ChangeAsync(id, install: false, cancellationToken);

    private async Task<AppChangeResult> ChangeAsync(string id, bool install, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        // The allowlist. An id outside the shipped catalogue never reaches winget, whatever asked
        // for it (spec 17.1).
        CatalogApp? app = _catalog.Find(id);

        if (app is null)
        {
            return new AppChangeResult(id, install, false, false,
                "That application is not in this product's catalogue.");
        }

        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return new AppChangeResult(app.Id, install, false, false,
                "winget is not on this machine, so nothing was attempted.");
        }

        PowerShellResult result = await _powerShell
            .RunWithArgumentsAsync(install ? InstallScript : UninstallScript, [app.Id], cancellationToken)
            .ConfigureAwait(false);

        string? failure = result.Succeeded ? null : result.ErrorSummary;

        // Spec 6.4: winget's exit code says what winget thinks it did. Ask the machine.
        bool? present = await IsPresentAsync(app.Id, cancellationToken).ConfigureAwait(false);
        bool verified = present == install;

        return new AppChangeResult(
            app.Id,
            install,
            Applied: failure is null,
            Verified: verified,
            verified
                ? null
                : failure ?? (present is null
                    ? "The change ran, but whether it took effect could not be confirmed."
                    : install
                        ? "The installer finished, but the application is still not there."
                        : "The uninstaller finished, but the application is still installed."));
    }

    private async Task<bool?> IsPresentAsync(string id, CancellationToken cancellationToken)
    {
        PowerShellResult result = await _powerShell
            .RunWithArgumentsAsync(PresentScript, [id], cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded
            ? result.StandardOutput.Trim() == "yes"
            : null;
    }
}

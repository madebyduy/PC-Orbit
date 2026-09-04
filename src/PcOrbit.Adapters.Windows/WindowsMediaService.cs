using System.Globalization;
using System.Text.Json;
using PcOrbit.Core.Model;
using PcOrbit.Core.Preflight;
using PcOrbit.Core.Setup;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// Reinstalling Windows from a mounted ISO, and refusing to start while the machine is not ready.
/// </summary>
/// <remarks>
/// <para>
/// The launch is one command. Everything worth having here happens before it: proving the file is
/// Windows installation media rather than something that merely ends in .iso, reading the editions
/// out of the image so the user sees what they are about to install, and checking the four things
/// that turn a routine repair install into a bad afternoon — an encrypted drive that will demand a
/// recovery key, a laptop on battery, a disk with no room, and no way back if it goes wrong.
/// </para>
/// <para>
/// Windows Setup takes over from there and shows its own summary of what it will keep. This product
/// does not drive that wizard: a reinstall is the user reading Microsoft's own confirmation, not
/// this app clicking through it for them.
/// </para>
/// </remarks>
public sealed class WindowsMediaService(PowerShellRunner? powerShell = null) : IWindowsMediaService
{
    /// <summary>
    /// Mounts read-only, reads the image, unmounts. Nothing here writes.
    /// </summary>
    private const string InspectScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $iso = $env:PCORBIT_ARG0
        $mounted = $null
        $result = [pscustomobject]@{
            IsWindows = $false; Architecture = $null; Build = $null; Editions = @(); Problem = $null
        }

        try {
            if (-not (Test-Path -LiteralPath $iso)) { throw 'That file does not exist.' }

            $mounted = Mount-DiskImage -ImagePath $iso -Access ReadOnly -PassThru
            $letter = ($mounted | Get-Volume).DriveLetter

            if (-not $letter) { throw 'The image mounted but Windows gave it no drive letter.' }

            $root = "${letter}:"
            $setup = Join-Path $root 'setup.exe'
            $wim = Join-Path $root 'sources\install.wim'
            $esd = Join-Path $root 'sources\install.esd'
            $image = if (Test-Path -LiteralPath $wim) { $wim } elseif (Test-Path -LiteralPath $esd) { $esd } else { $null }

            # Both have to be there. An ISO with sources but no setup.exe is not something that can
            # upgrade a running Windows, whatever else it is.
            if (-not (Test-Path -LiteralPath $setup) -or -not $image) {
                $result.Problem = 'That ISO does not contain Windows installation media: setup.exe or the install image is missing.'
            }
            else {
                $info = Get-WindowsImage -ImagePath $image -ErrorAction Stop
                $first = Get-WindowsImage -ImagePath $image -Index $info[0].ImageIndex -ErrorAction SilentlyContinue

                $result.IsWindows = $true
                $result.Architecture = switch ($first.Architecture) { 9 { 'x64' } 12 { 'arm64' } 0 { 'x86' } default { $null } }
                $result.Build = [string] $first.Version
                $result.Editions = @($info | ForEach-Object {
                    [pscustomobject]@{
                        Index = [int] $_.ImageIndex
                        Name  = [string] $_.ImageName
                        Description = [string] $_.ImageDescription
                    }
                })
            }
        }
        catch {
            $result.Problem = $_.Exception.Message
        }
        finally {
            if ($mounted) { Dismount-DiskImage -ImagePath $iso -ErrorAction SilentlyContinue | Out-Null }
        }

        $result | ConvertTo-Json -Depth 4 -Compress
        exit 0
        """;

    /// <summary>
    /// Mounts and hands over to Windows Setup, which then shows its own summary and asks.
    /// </summary>
    /// <remarks>
    /// The media stays mounted deliberately: setup reads from it for the whole run, and unmounting
    /// underneath it is how an upgrade fails at sixty per cent.
    /// </remarks>
    private const string StartScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        $iso = $env:PCORBIT_ARG0
        $problem = $null

        try {
            $mounted = Mount-DiskImage -ImagePath $iso -Access ReadOnly -PassThru
            $letter = ($mounted | Get-Volume).DriveLetter
            if (-not $letter) { throw 'The image mounted but Windows gave it no drive letter.' }

            $setup = Join-Path "${letter}:" 'setup.exe'
            if (-not (Test-Path -LiteralPath $setup)) { throw 'setup.exe is not on the mounted media.' }

            # No /quiet and no /auto. Windows Setup shows what it will keep and asks; a reinstall is
            # the user reading Microsoft's own confirmation, not this app clicking through it.
            Start-Process -FilePath $setup -ArgumentList '/product server' -ErrorAction Stop | Out-Null
        }
        catch {
            $problem = $_.Exception.Message
        }

        [pscustomobject]@{ Problem = $problem } | ConvertTo-Json -Compress
        exit 0
        """;

    private readonly PowerShellRunner _powerShell = powerShell
        ?? new PowerShellRunner(TimeSpan.FromMinutes(10));

    public async Task<MediaContents> InspectAsync(string isoPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);

        PowerShellResult result = await _powerShell
            .RunWithArgumentsAsync(InspectScript, [isoPath], cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return Unreadable(isoPath, result.ErrorSummary);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement row = document.RootElement;

            if (WindowsChangeSources.Text(row, "Problem") is { Length: > 0 } problem)
            {
                return Unreadable(isoPath, problem);
            }

            List<MediaEdition> editions = [];

            if (row.TryGetProperty("Editions", out JsonElement list))
            {
                IEnumerable<JsonElement> entries = list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray()
                    : new[] { list };

                foreach (JsonElement entry in entries)
                {
                    if (WindowsChangeSources.Text(entry, "Name") is { Length: > 0 } name)
                    {
                        editions.Add(new MediaEdition(
                            WindowsChangeSources.Number(entry, "Index") ?? 1,
                            name,
                            WindowsChangeSources.Text(entry, "Description")));
                    }
                }
            }

            return new MediaContents(
                row.TryGetProperty("IsWindows", out JsonElement w) && w.ValueKind == JsonValueKind.True,
                WindowsChangeSources.Text(row, "Architecture"),
                WindowsChangeSources.Text(row, "Build"),
                editions,
                new Evidence(
                    EvidenceSourceKind.PowerShell,
                    "Get-WindowsImage against the install image on the mounted media",
                    Confidence.High,
                    Query: "Mount-DiskImage; Get-WindowsImage",
                    RawResult: string.Create(CultureInfo.InvariantCulture, $"{editions.Count} edition(s)")));
        }
        catch (JsonException ex)
        {
            return Unreadable(isoPath, $"the media could not be inspected: {ex.Message}");
        }
    }

    /// <summary>
    /// The four things that turn a routine repair install into a bad afternoon.
    /// </summary>
    /// <remarks>
    /// Blockers rather than warnings for the two that cost the user access to their own machine.
    /// An encrypted drive that has not been suspended will ask for a forty-eight digit key at the
    /// next boot, and a laptop that dies mid-upgrade is the textbook way to end up with neither the
    /// old Windows nor the new one.
    /// </remarks>
    public ReinstallReadiness Assess(StateSnapshot snapshot, MediaContents media, ReinstallScope scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(media);

        List<string> blockers = [];
        List<string> warnings = [];

        if (!media.IsWindowsMedia)
        {
            blockers.Add(media.Problem ?? "That file is not Windows installation media.");
        }

        // Architecture. A 32-bit or arm64 image against an x64 install fails part way through
        // rather than at the start, which is the worst possible moment to find out.
        if (media.Architecture is { Length: > 0 } arch
            && !string.Equals(arch, "x64", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(arch, "arm64", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add($"The media is {arch}, which cannot upgrade this installation.");
        }

        CapabilityValue encryption = snapshot.ValueOf(CoreCapabilities.BitLockerSystemDrive);

        // Unknown blocks too: reading it needs administrator rights, so a failed read is not
        // evidence the drive is unencrypted (spec 10.3).
        if (!encryption.IsKnown)
        {
            blockers.Add(
                "Whether this drive is encrypted could not be read, and that needs administrator "
                + "rights. If it is encrypted, Windows will ask for the recovery key after the "
                + "restart.");
        }
        else if (encryption.Canonical is "on" or "enabled")
        {
            blockers.Add(
                "This drive is encrypted. Suspend BitLocker for one restart first, or have the "
                + "48-digit recovery key to hand.");
        }

        if (snapshot.ValueOf(CoreCapabilities.OnBattery).Canonical == "yes")
        {
            blockers.Add("This laptop is on battery. Plug it in: losing power part way through leaves neither the old Windows nor the new one.");
        }

        // Setup wants room for the old installation as well as the new one.
        CapabilityValue free = snapshot.ValueOf(CoreCapabilities.SystemDriveFreeGb);

        if (double.TryParse(free.Raw, NumberStyles.Number, CultureInfo.InvariantCulture, out double gb) && gb < 20)
        {
            blockers.Add($"There is {gb:0.#} GB free on the Windows drive. A reinstall needs about 20 GB, because the old installation is kept until you are sure.");
        }

        if (snapshot.ValueOf(CoreCapabilities.RecoveryEnvironment).Status == CapabilityStatus.Disabled)
        {
            warnings.Add("This PC has no working recovery environment, so if the upgrade fails there is no built-in way back.");
        }

        if (scope == ReinstallScope.KeepFilesOnly)
        {
            warnings.Add("Applications and settings will not survive this. Only personal files will.");
        }

        return new ReinstallReadiness(scope, blockers, warnings);
    }

    public async Task<ReinstallStart> StartAsync(
        string isoPath,
        ReinstallReadiness readiness,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        ArgumentNullException.ThrowIfNull(readiness);

        // The gate, in code rather than in the UI. A caller that skipped the assessment does not
        // get to start a reinstall.
        if (!readiness.CanProceed)
        {
            return new ReinstallStart(false, string.Join(" ", readiness.Blockers));
        }

        PowerShellResult result = await _powerShell
            .RunWithArgumentsAsync(StartScript, [isoPath], cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return new ReinstallStart(false, result.ErrorSummary);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            string? problem = WindowsChangeSources.Text(document.RootElement, "Problem");

            return new ReinstallStart(problem is null, problem);
        }
        catch (JsonException ex)
        {
            return new ReinstallStart(false, $"Windows Setup could not be started: {ex.Message}");
        }
    }

    private static MediaContents Unreadable(string isoPath, string problem) => new(
        false,
        null,
        null,
        [],
        Evidence.Missing($"'{Path.GetFileName(isoPath)}' could not be inspected: {problem}"),
        problem);
}

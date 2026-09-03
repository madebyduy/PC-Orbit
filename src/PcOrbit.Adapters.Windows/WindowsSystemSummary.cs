using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using PcOrbit.Core.Abstractions;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// Storage, startup, security, patches, network and temperature — the context the dashboard shows
/// around the findings.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is readable by a standard user (spec 21.10). Drives and network come from the
/// BCL directly; Defender, the Run keys, installed updates and the ACPI thermal zone come from one
/// small read-only CIM batch.
/// </para>
/// <para>
/// The temperature probe deserves a note, because its usual answer is "no". Windows only exposes a
/// temperature when the firmware publishes an ACPI thermal zone, which most desktop boards do not:
/// their sensors are reachable only through a vendor driver or by reading MSRs from kernel mode,
/// and this app ships neither. So the probe is attempted honestly and its failure is reported with
/// the reason rather than filled in with a plausible 42 °C (spec 6.6, 8.3.1).
/// </para>
/// </remarks>
public sealed class WindowsSystemSummary(PowerShellRunner? powerShell = null) : ISystemSummary
{
    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        function Safe { param([scriptblock] $Block) try { & $Block } catch { $null } }
        function Stamp { param($Value) if ($Value) { ([datetime] $Value).ToString('yyyy-MM-ddTHH:mm:ss') } else { $null } }

        $defender = Safe {
            $d = Get-CimInstance -Namespace 'root/Microsoft/Windows/Defender' `
                                 -ClassName MSFT_MpComputerStatus | Select-Object -First 1
            [pscustomobject]@{
                AMServiceEnabled          = $d.AMServiceEnabled
                RealTimeProtectionEnabled = $d.RealTimeProtectionEnabled
                SignaturesUpdated         = Stamp $d.AntivirusSignatureLastUpdated
                LastScan                  = Stamp $d.QuickScanEndTime
            }
        }

        # The product Windows Security itself considers active. Present even when a third-party
        # antivirus has taken over, which is exactly the case Defender's own status misreports.
        $product = Safe {
            (Get-CimInstance -Namespace 'root/SecurityCenter2' -ClassName AntiVirusProduct |
                Select-Object -First 1).displayName
        }

        $runKeys = @(
            @{ Path = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'; Source = 'user' },
            @{ Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'; Source = 'machine' }
        )

        $startup = Safe {
            foreach ($key in $runKeys) {
                $entry = Get-ItemProperty -Path $key.Path
                if ($entry) {
                    $entry.PSObject.Properties |
                        Where-Object { $_.Name -notlike 'PS*' } |
                        ForEach-Object { [pscustomobject]@{ Name = $_.Name; Source = $key.Source } }
                }
            }
        }

        $updates = Safe {
            $hotfixes = @(Get-HotFix | Sort-Object InstalledOn -Descending)
            if ($hotfixes.Count -gt 0) {
                [pscustomobject]@{
                    Latest      = $hotfixes[0].HotFixID
                    InstalledOn = Stamp $hotfixes[0].InstalledOn
                    Count       = $hotfixes.Count
                }
            }
        }

        # Optional in ACPI and absent on most desktop boards. A failure here is expected, not a bug.
        $thermal = Safe {
            $zones = @(Get-CimInstance -Namespace 'root/wmi' -ClassName MSAcpi_ThermalZoneTemperature)
            if ($zones.Count -gt 0) {
                ($zones | Measure-Object -Property CurrentTemperature -Maximum).Maximum
            }
        }

        [pscustomobject]@{
            Defender = $defender
            Product  = $product
            Startup  = @($startup)
            Updates  = $updates
            Thermal  = $thermal
        } | ConvertTo-Json -Depth 4 -Compress
        """;

    private readonly PowerShellRunner _powerShell = powerShell ?? PowerShellRunner.Default;

    public async Task<SystemSummary> ReadAsync(CancellationToken cancellationToken = default)
    {
        PowerShellResult result = await _powerShell.RunAsync(Script, cancellationToken).ConfigureAwait(false);

        SecuritySummary? security = null;
        UpdateSummary? updates = null;
        List<StartupItem> startup = [];
        ThermalReading thermal = NoThermal("the inventory query did not run");

        if (result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
                JsonElement root = document.RootElement;

                security = ReadSecurity(root);
                updates = ReadUpdates(root);
                startup = ReadStartup(root);
                thermal = ReadThermal(root);
            }
            catch (JsonException)
            {
                thermal = NoThermal("the inventory query returned something unreadable");
            }
        }

        return new SystemSummary(
            Drives: ReadDrives(),
            Startup: startup,
            Security: security,
            Updates: updates,
            Network: ReadNetwork(),
            Thermal: thermal);
    }

    // ---------------------------------------------------------------- storage and network (BCL)

    private static List<DriveSummary> ReadDrives()
    {
        List<DriveSummary> drives = [];

        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                const double gb = 1024d * 1024d * 1024d;

                drives.Add(new DriveSummary(
                    drive.Name,
                    drive.VolumeLabel ?? string.Empty,
                    drive.AvailableFreeSpace / gb,
                    drive.TotalSize / gb));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A drive that disappears mid-enumeration is not worth failing the whole summary for.
        }

        return drives;
    }

    private static NetworkSummary ReadNetwork()
    {
        try
        {
            NetworkInterface? adapter = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .OrderByDescending(n => n.GetIPv4Statistics().BytesReceived)
                .FirstOrDefault();

            if (adapter is null)
            {
                return new NetworkSummary(HostName: Dns.GetHostName());
            }

            IPInterfaceProperties properties = adapter.GetIPProperties();

            string? ip = properties.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.Address.ToString();

            string? gateway = properties.GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.Address.ToString();

            List<string> dns =
            [
                .. properties.DnsAddresses
                    .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.ToString()),
            ];

            return new NetworkSummary(adapter.Name, ip, gateway, dns, Dns.GetHostName());
        }
        catch (NetworkInformationException)
        {
            return new NetworkSummary();
        }
    }

    // ---------------------------------------------------------------- script sections

    private static SecuritySummary? ReadSecurity(JsonElement root)
    {
        JsonElement? defender = Get(root, "Defender");
        string? product = Text(root, "Product");

        if (defender is null && product is null)
        {
            return null;
        }

        return new SecuritySummary(
            AntivirusRunning: Bool(defender, "AMServiceEnabled"),
            RealTimeProtection: Bool(defender, "RealTimeProtectionEnabled"),
            SignaturesUpdated: Stamp(defender, "SignaturesUpdated"),
            LastScan: Stamp(defender, "LastScan"),
            Product: product);
    }

    private static UpdateSummary? ReadUpdates(JsonElement root)
    {
        JsonElement? updates = Get(root, "Updates");

        if (updates is null)
        {
            return null;
        }

        return new UpdateSummary(
            LatestPatch: Text(updates.Value, "Latest"),
            InstalledOn: Stamp(updates, "InstalledOn"),
            Count: (int)(Number(updates, "Count") ?? 0));
    }

    private static List<StartupItem> ReadStartup(JsonElement root) =>
    [
        .. Array(root, "Startup")
            .Where(e => Text(e, "Name") is not null)
            .Select(e => new StartupItem(Text(e, "Name")!, Text(e, "Source") ?? "user")),
    ];

    /// <summary>
    /// The ACPI thermal zone reports tenths of a kelvin. Values outside a sane range mean the zone
    /// exists but is not wired to a real sensor, which is common enough to check for.
    /// </summary>
    private static ThermalReading ReadThermal(JsonElement root)
    {
        double? raw = Number(Get(root, "Thermal"), null);

        if (raw is not { } tenthsKelvin || tenthsKelvin <= 0)
        {
            return NoThermal(
                "this machine publishes no ACPI thermal zone. Most desktop boards report their sensors only "
                + "through a vendor driver, which this version does not install");
        }

        double celsius = (tenthsKelvin / 10d) - 273.15d;

        return celsius is > 0 and < 125
            ? new ThermalReading(celsius, "MSAcpi_ThermalZoneTemperature")
            : NoThermal(
                "the ACPI thermal zone answered with a value outside any believable range "
                + $"({celsius.ToString("0.#", CultureInfo.InvariantCulture)} °C), so it is not wired to a real sensor");
    }

    private static ThermalReading NoThermal(string reason) =>
        new(null, "MSAcpi_ThermalZoneTemperature", reason);

    // ---------------------------------------------------------------- json helpers

    private static JsonElement? Get(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) => Get(root, name) switch
    {
        { ValueKind: JsonValueKind.Array } array => array.EnumerateArray(),
        { ValueKind: JsonValueKind.Object } single => [single],
        _ => [],
    };

    private static string? Text(JsonElement element, string name)
    {
        string? value = element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool? Bool(JsonElement? element, string name) =>
        element is { } e && e.TryGetProperty(name, out JsonElement property)
            ? property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    /// <summary>A null <paramref name="name"/> reads the element itself, for bare scalars.</summary>
    private static double? Number(JsonElement? element, string? name)
    {
        if (element is not { } e)
        {
            return null;
        }

        JsonElement target = e;

        if (name is not null && (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out target)))
        {
            return null;
        }

        return target.ValueKind == JsonValueKind.Number && target.TryGetDouble(out double value) ? value : null;
    }

    private static DateTimeOffset? Stamp(JsonElement? element, string name) =>
        element is { } e && Text(e, name) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset value)
            ? value
            : null;
}

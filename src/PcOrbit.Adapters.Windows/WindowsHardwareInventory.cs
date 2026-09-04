using System.Globalization;
using System.Text.Json;
using PcOrbit.Core.Abstractions;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// What is in and attached to this machine, read through CIM in one read-only batch.
/// </summary>
/// <remarks>
/// <para>
/// A separate script from <see cref="WindowsInventory"/> on purpose: this one asks only for
/// hardware and skips the slow probes (restore points, encryption), so it finishes in a couple of
/// seconds and can run alongside the state scan instead of after it.
/// </para>
/// <para>
/// Every class here is readable by a standard user. A class the machine does not expose yields an
/// empty list, and the UI then shows nothing for that group rather than a placeholder device.
/// </para>
/// </remarks>
public sealed class WindowsHardwareInventory(PowerShellRunner? powerShell = null) : IHardwareInventory
{
    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        function Safe { param([scriptblock] $Block) try { & $Block } catch { $null } }

        $gpu = Safe {
            Get-CimInstance -ClassName Win32_VideoController |
                Where-Object { $_.Name } |
                Select-Object -First 1 Name, DriverVersion, AdapterRAM,
                                       CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate
        }

        $system = Safe {
            Get-CimInstance -ClassName Win32_ComputerSystem | Select-Object -First 1 TotalPhysicalMemory
        }

        $memory = Safe {
            Get-CimInstance -ClassName Win32_PhysicalMemory |
                Select-Object Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator
        }

        $disks = Safe {
            Get-CimInstance -ClassName Win32_DiskDrive |
                Select-Object Model, Size, InterfaceType, MediaType
        }

        # WmiMonitorID carries the real model name; Win32_DesktopMonitor usually says
        # "Generic PnP Monitor". The name arrives as a null-terminated UInt16 array.
        $monitors = Safe {
            Get-CimInstance -Namespace 'root\wmi' -ClassName WmiMonitorID | ForEach-Object {
                $name = if ($_.UserFriendlyName) {
                    (($_.UserFriendlyName | Where-Object { $_ -gt 0 }) | ForEach-Object { [char] $_ }) -join ''
                } else { $null }
                $vendor = if ($_.ManufacturerName) {
                    (($_.ManufacturerName | Where-Object { $_ -gt 0 }) | ForEach-Object { [char] $_ }) -join ''
                } else { $null }
                [pscustomobject]@{ Name = $name; Vendor = $vendor }
            }
        }

        $screens = Safe {
            Get-CimInstance -ClassName Win32_DesktopMonitor |
                Select-Object Name, ScreenWidth, ScreenHeight
        }

        $audio = Safe {
            Get-CimInstance -ClassName Win32_SoundDevice |
                Where-Object { $_.Status -eq 'OK' } | Select-Object Name, Status
        }

        $pointing = Safe { Get-CimInstance -ClassName Win32_PointingDevice | Select-Object Name }
        $keyboards = Safe { Get-CimInstance -ClassName Win32_Keyboard | Select-Object Name }

        # Every mounted volume, not just the Windows drive. DriveType 3 is a fixed disk; USB sticks
        # and card readers are 2 and are listed too, because "how many drives do I have" includes them.
        $volumes = Safe {
            Get-CimInstance -ClassName Win32_LogicalDisk |
                Where-Object { ($_.DriveType -eq 3 -or $_.DriveType -eq 2) -and $_.Size -gt 0 } |
                Select-Object DeviceID, VolumeName, FileSystem, Size, FreeSpace
        }

        $network = Safe {
            Get-CimInstance -ClassName Win32_NetworkAdapter |
                Where-Object { $_.PhysicalAdapter -eq $true -and $_.NetEnabled -eq $true } |
                Select-Object Name, Speed, MACAddress
        }

        [pscustomobject]@{
            Gpu       = $gpu
            System    = $system
            Memory    = @($memory)
            Disks     = @($disks)
            Monitors  = @($monitors)
            Screens   = @($screens)
            Audio     = @($audio)
            Pointing  = @($pointing)
            Keyboards = @($keyboards)
            Network   = @($network)
            Volumes   = @($volumes)
        } | ConvertTo-Json -Depth 5 -Compress
        """;

    private readonly PowerShellRunner _powerShell = powerShell ?? PowerShellRunner.Default;

    public async Task<HardwareInventory> ReadAsync(CancellationToken cancellationToken = default)
    {
        PowerShellResult result = await _powerShell.RunAsync(Script, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return HardwareInventory.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;

            return new HardwareInventory(
                Gpu: ReadGpu(root),
                TotalRamGb: Bytes(Get(root, "System"), "TotalPhysicalMemory"),
                MemoryModules: ReadMemory(root),
                Disks: ReadDisks(root),
                Monitors: ReadMonitors(root),
                Audio: ReadNamed(root, "Audio", "audio"),
                Input: [.. ReadNamed(root, "Pointing", "pointing"), .. ReadNamed(root, "Keyboards", "keyboard")],
                Network: ReadNetwork(root),
                Volumes: ReadVolumes(root));
        }
        catch (JsonException)
        {
            // Malformed output is the same as no output: show nothing rather than half a device.
            return HardwareInventory.Empty;
        }
    }

    // ---------------------------------------------------------------- sections

    private static HardwarePart? ReadGpu(JsonElement root)
    {
        JsonElement? gpu = Get(root, "Gpu");
        string? name = Text(gpu, "Name");

        if (name is null)
        {
            return null;
        }

        int? width = Int(gpu, "CurrentHorizontalResolution");
        int? height = Int(gpu, "CurrentVerticalResolution");
        int? hz = Int(gpu, "CurrentRefreshRate");

        string? detail = width is > 0 && height is > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{width} × {height}")
                + (hz is > 0 ? string.Create(CultureInfo.InvariantCulture, $" @ {hz} Hz") : string.Empty)
            : null;

        return new HardwarePart("gpu", name, detail, Text(gpu, "DriverVersion"));
    }

    private static List<HardwarePart> ReadMemory(JsonElement root) =>
    [
        .. Array(root, "Memory")
            .Select(m =>
            {
                double? gb = Bytes(m, "Capacity");
                int? configured = Int(m, "ConfiguredClockSpeed");
                string slot = Text(m, "DeviceLocator") ?? "?";

                return new HardwarePart(
                    "memory",
                    gb is { } size ? Gb(size) : "?",
                    configured is > 0
                        ? string.Create(CultureInfo.InvariantCulture, $"{configured} MT/s · {slot}")
                        : slot,
                    Text(m, "PartNumber"));
            }),
    ];

    private static List<VolumeInfo> ReadVolumes(JsonElement root) =>
    [
        .. Array(root, "Volumes")
            .Where(v => Text(v, "DeviceID") is not null && Bytes(v, "Size") is > 0)
            .Select(v => new VolumeInfo(
                Text(v, "DeviceID")!,
                Text(v, "VolumeName") is { Length: > 0 } label ? label : null,
                Text(v, "FileSystem"),
                Bytes(v, "Size") ?? 0,
                Bytes(v, "FreeSpace") ?? 0))
            .OrderBy(v => v.Letter, StringComparer.Ordinal),
    ];

    private static List<HardwarePart> ReadDisks(JsonElement root) =>
    [
        .. Array(root, "Disks")
            .Where(d => Text(d, "Model") is not null)
            .Select(d =>
            {
                double? gb = Bytes(d, "Size");

                return new HardwarePart(
                    "disk",
                    Text(d, "Model")!,
                    gb is { } size ? Gb(size) : null,
                    Text(d, "InterfaceType"));
            }),
    ];

    /// <summary>
    /// Monitor names from WmiMonitorID, falling back to Win32_DesktopMonitor when the firmware
    /// gives no friendly name — a real case on cheap panels and some laptop displays.
    /// </summary>
    private static List<HardwarePart> ReadMonitors(JsonElement root)
    {
        List<HardwarePart> monitors =
        [
            .. Array(root, "Monitors")
                .Where(m => !string.IsNullOrWhiteSpace(Text(m, "Name")))
                .Select(m => new HardwarePart("monitor", Text(m, "Name")!, Text(m, "Vendor"))),
        ];

        if (monitors.Count > 0)
        {
            return monitors;
        }

        return
        [
            .. Array(root, "Screens")
                .Where(s => Text(s, "Name") is not null)
                .Select(s =>
                {
                    int? width = Int(s, "ScreenWidth");
                    int? height = Int(s, "ScreenHeight");

                    return new HardwarePart(
                        "monitor",
                        Text(s, "Name")!,
                        width is > 0 && height is > 0
                            ? string.Create(CultureInfo.InvariantCulture, $"{width} × {height}")
                            : null);
                }),
        ];
    }

    private static List<HardwarePart> ReadNetwork(JsonElement root) =>
    [
        .. Array(root, "Network")
            .Where(n => Text(n, "Name") is not null)
            .Select(n =>
            {
                double? bits = Number(n, "Speed");

                return new HardwarePart(
                    "network",
                    Text(n, "Name")!,
                    bits is > 0
                        ? (bits.Value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + " Mbps"
                        : null);
            }),
    ];

    private static List<HardwarePart> ReadNamed(JsonElement root, string section, string kind) =>
    [
        .. Array(root, section)
            .Where(e => Text(e, "Name") is not null)
            .Select(e => new HardwarePart(kind, Text(e, "Name")!))
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
    ];

    // ---------------------------------------------------------------- json helpers

    private static JsonElement? Get(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    /// <summary>
    /// PowerShell's <c>ConvertTo-Json</c> writes a one-element array as a bare object, so a caller
    /// that expects a list has to accept both shapes.
    /// </summary>
    private static IEnumerable<JsonElement> Array(JsonElement root, string name) => Get(root, name) switch
    {
        { ValueKind: JsonValueKind.Array } array => array.EnumerateArray(),
        { ValueKind: JsonValueKind.Object } single => [single],
        _ => [],
    };

    private static string? Text(JsonElement? element, string name)
    {
        string? value = element is { } e && e.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static double? Number(JsonElement? element, string name) =>
        element is { } e && e.TryGetProperty(name, out JsonElement property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetDouble(out double value)
            ? value
            : null;

    private static int? Int(JsonElement? element, string name) =>
        Number(element, name) is { } value ? (int)value : null;

    private static double? Bytes(JsonElement? element, string name) =>
        Number(element, name) is { } value && value > 0 ? value / (1024d * 1024d * 1024d) : null;

    /// <summary>Sizes are technical values, so they read the same in every locale (spec 21.11).</summary>
    private static string Gb(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + " GB";
}

using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace PcOrbit.Adapters.Windows.Interop;

/// <param name="OnBattery">Null when the machine has no battery or the call failed.</param>
internal sealed record PowerState(bool? OnBattery, int? BatteryPercent);

internal static partial class SystemInterop
{
    private const byte AcLineOnline = 1;
    private const byte AcLineUnknown = 255;
    private const byte BatteryPercentUnknown = 255;

    /// <summary>
    /// Mains or battery, for the preflight that stops a laptop applying a restart-needing plan on
    /// 8% battery (spec 21.8).
    /// </summary>
    internal static PowerState ReadPowerState()
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus status))
        {
            return new PowerState(null, null);
        }

        bool? onBattery = status.ACLineStatus switch
        {
            AcLineOnline => false,
            AcLineUnknown => null,
            _ => true,
        };

        int? percent = status.BatteryLifePercent == BatteryPercentUnknown
            ? null
            : status.BatteryLifePercent;

        return new PowerState(onBattery, percent);
    }

    /// <summary>Free space on the drive Windows is installed on, in GB.</summary>
    internal static double? SystemDriveFreeGb()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);

            return drive.IsReady ? drive.AvailableFreeSpace / (1024d * 1024d * 1024d) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Windows build and edition, read from the registry rather than <c>Environment.OSVersion</c>,
    /// which does not carry the UBR or the edition and is subject to app compatibility shims.
    /// </summary>
    internal static (int Build, string Edition, string DisplayVersion) ReadWindowsVersion()
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path);

        if (key is null)
        {
            return (0, "Unknown", "Unknown");
        }

        int build = int.TryParse(
            key.GetValue("CurrentBuildNumber") as string,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int parsed)
            ? parsed
            : 0;

        string edition = key.GetValue("EditionID") as string
            ?? key.GetValue("ProductName") as string
            ?? "Unknown";

        string displayVersion = key.GetValue("DisplayVersion") as string
            ?? key.GetValue("ReleaseId") as string
            ?? "Unknown";

        return (build, edition, displayVersion);
    }

    /// <summary>
    /// UEFI or legacy BIOS boot. Uses <c>GetFirmwareType</c> rather than the <c>firmware_type</c>
    /// environment variable, which is a shell convenience and not guaranteed to reach a process.
    /// </summary>
    internal static string? ReadFirmwareType() =>
        GetFirmwareType(out int type)
            ? type switch
            {
                1 => "legacy",
                2 => "uefi",
                _ => null,
            }
            : null;

    [LibraryImport("kernel32.dll", EntryPoint = "GetFirmwareType")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFirmwareType(out int firmwareType);

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemPowerStatus")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}

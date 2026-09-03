using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace PcOrbit.Adapters.Windows.Interop;

/// <summary>
/// Reads and changes the display mode through the Win32 display configuration API.
/// </summary>
/// <remarks>
/// <para>
/// Spec 23.1.H puts the refresh-rate fix in v0.1 precisely because it is technically simple, needs
/// no restart, is the most common finding on gaming machines and new laptops, and is the demo a
/// non-developer feels immediately. It is also the first place Safe Apply is exercised: this API
/// supports applying a mode and putting the old one back, which is what makes a 15-second
/// "can you still see this?" countdown honest rather than a promise we cannot keep.
/// </para>
/// <para>
/// v0.1 handles the primary display only. Multi-monitor means per-path enumeration and a UI that
/// can talk about "which screen", and that belongs with the Display Center (spec 8.6), not here.
/// </para>
/// </remarks>
internal static class DisplayInterop
{
    private const int EnumCurrentSettings = -1;
    private const int CdsUpdateRegistry = 0x00000001;
    private const int CdsTest = 0x00000002;
    private const int DispChangeSuccessful = 0;
    private const int DmBitsPerPixel = 0x00040000;
    private const int DmPelsWidth = 0x00080000;
    private const int DmPelsHeight = 0x00100000;
    private const int DmDisplayFrequency = 0x00400000;

    /// <summary>The mode the primary display is running right now, or null if it cannot be read.</summary>
    internal static DisplayMode? CurrentMode()
    {
        DevMode devMode = NewDevMode();

        return EnumDisplaySettingsEx(null, EnumCurrentSettings, ref devMode, 0)
            ? new DisplayMode(devMode.dmPelsWidth, devMode.dmPelsHeight, devMode.dmDisplayFrequency, devMode.dmBitsPerPel)
            : null;
    }

    /// <summary>
    /// Every mode the primary display advertises at its current resolution and colour depth.
    /// </summary>
    /// <remarks>
    /// Filtered to the current resolution on purpose: telling someone their 1080p panel supports
    /// 240 Hz when that only holds at a lower resolution would be a false finding.
    /// </remarks>
    internal static IReadOnlyList<DisplayMode> SupportedModesAtCurrentResolution()
    {
        DisplayMode? current = CurrentMode();

        if (current is null)
        {
            return [];
        }

        List<DisplayMode> modes = [];
        DevMode devMode = NewDevMode();

        for (int index = 0; EnumDisplaySettingsEx(null, index, ref devMode, 0); index++)
        {
            if (devMode.dmPelsWidth == current.Width
                && devMode.dmPelsHeight == current.Height
                && devMode.dmBitsPerPel == current.BitsPerPixel
                && devMode.dmDisplayFrequency > 1)
            {
                modes.Add(new DisplayMode(
                    devMode.dmPelsWidth,
                    devMode.dmPelsHeight,
                    devMode.dmDisplayFrequency,
                    devMode.dmBitsPerPel));
            }

            devMode = NewDevMode();
        }

        return [.. modes.DistinctBy(m => m.RefreshHz).OrderBy(m => m.RefreshHz)];
    }

    /// <summary>
    /// Switches the primary display to <paramref name="refreshHz"/> at the current resolution.
    /// </summary>
    /// <remarks>
    /// Tests the mode first. If the driver says it will not work we stop there rather than
    /// applying it and hoping — a black screen is the exact failure Safe Apply exists to avoid.
    /// </remarks>
    internal static DisplayChangeResult TrySetRefreshRate(int refreshHz)
    {
        DisplayMode? current = CurrentMode();

        if (current is null)
        {
            return new DisplayChangeResult(false, "Could not read the current display mode.");
        }

        DevMode devMode = NewDevMode();

        if (!EnumDisplaySettingsEx(null, EnumCurrentSettings, ref devMode, 0))
        {
            return new DisplayChangeResult(false, "Could not read the current display mode.");
        }

        devMode.dmDisplayFrequency = refreshHz;
        devMode.dmFields = DmPelsWidth | DmPelsHeight | DmBitsPerPixel | DmDisplayFrequency;

        int test = ChangeDisplaySettingsEx(null, ref devMode, IntPtr.Zero, CdsTest, IntPtr.Zero);

        if (test != DispChangeSuccessful)
        {
            return new DisplayChangeResult(false, $"The display driver rejected {refreshHz} Hz (test result {test}).");
        }

        int result = ChangeDisplaySettingsEx(null, ref devMode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);

        return result == DispChangeSuccessful
            ? new DisplayChangeResult(true, $"Switched to {refreshHz} Hz.")
            : new DisplayChangeResult(false, $"ChangeDisplaySettingsEx returned {result}.");
    }

    private static DevMode NewDevMode() => new()
    {
        dmDeviceName = new string('\0', 32),
        dmFormName = new string('\0', 32),
        dmSize = (short)Marshal.SizeOf<DevMode>(),
    };

    // DllImport rather than LibraryImport for these two: DEVMODE contains ByValTStr strings, which
    // the P/Invoke source generator cannot marshal (SYSLIB1051). Rewriting DEVMODE with fixed char
    // buffers would mean hand-written unsafe pointer code in exchange for a few nanoseconds on a
    // call we make a handful of times per scan. Runtime marshalling is the right trade here.
    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute",
        Justification = "DEVMODE uses ByValTStr fields, which source-generated interop does not support.")]
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsEx(string? deviceName, int modeNumber, ref DevMode devMode, int flags);

    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute",
        Justification = "DEVMODE uses ByValTStr fields, which source-generated interop does not support.")]
    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int ChangeDisplaySettingsEx(
        string? deviceName,
        ref DevMode devMode,
        IntPtr hwnd,
        int flags,
        IntPtr parameters);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;

        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}

internal sealed record DisplayMode(int Width, int Height, int RefreshHz, int BitsPerPixel);

internal sealed record DisplayChangeResult(bool Succeeded, string Detail);

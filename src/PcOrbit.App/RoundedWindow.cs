using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PcOrbit.App;

/// <summary>
/// Asks the desktop compositor to round the window's corners.
/// </summary>
/// <remarks>
/// <para>
/// The window is <c>WindowStyle="None"</c> so that it can draw its own caption, and a window with
/// no style is one Windows leaves square. Rounding it in WPF instead — clipping the root to a
/// rounded rectangle — gets the shape but not the rest of it: the drop shadow stays square, the
/// clipped edge is aliased against whatever is behind, and the corners stop responding to a resize
/// drag. Handing the corner to DWM gets the system's own radius, its own antialiasing and its own
/// shadow, which is what makes it look like part of Windows rather than like a shape drawn on top
/// of it.
/// </para>
/// <para>
/// Windows 10 has no such attribute and returns an error, which is ignored: a square window there
/// is the correct look for that version, and refusing to start over a corner would be absurd.
/// </para>
/// </remarks>
internal static partial class RoundedWindow
{
    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE. Windows 11 build 22000 and later.</summary>
    private const int WindowCornerPreference = 33;

    /// <summary>DWMWCP_ROUND — the radius Windows 11 gives its own top-level windows.</summary>
    private const int Round = 2;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    /// <summary>
    /// Rounds the window. Safe to call on any Windows version, and before or after it is shown.
    /// </summary>
    public static void Apply(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;

        if (handle == 0)
        {
            return;
        }

        int preference = Round;

        // The HRESULT is deliberately dropped. The only failure worth distinguishing would be "this
        // Windows does not do rounded corners", and the answer to that is the same as doing nothing.
        _ = DwmSetWindowAttribute(handle, WindowCornerPreference, ref preference, sizeof(int));
    }
}

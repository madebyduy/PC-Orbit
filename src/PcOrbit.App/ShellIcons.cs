using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcOrbit.App;

/// <summary>
/// The icon Windows shows for an executable, the way Task Manager and Explorer show it.
/// </summary>
/// <remarks>
/// <para>
/// <c>SHGetFileInfo</c> rather than parsing the resource section ourselves: it is the same call
/// the shell uses, so the result is what the user already recognises from their taskbar. Small
/// size (16 px), because the rows these go in are 20 px tall and a 32 px icon downscaled reads as
/// a blur.
/// </para>
/// <para>
/// Cached by path, forever, for the life of the process. The process list refreshes every three
/// seconds and the same dozen executables appear in it every time; extracting an icon is a shell
/// call plus a bitmap conversion, and doing that forty times a minute for the same file would be
/// the app making itself the heaviest thing in its own list. The cache is bounded by how many
/// distinct executables run on the machine, which is not many.
/// </para>
/// <para>
/// Null when there is no path or the shell has nothing for it — an elevated process seen from a
/// standard-user app has no readable path, and the caller falls back to a letter for those.
/// </para>
/// </remarks>
internal static partial class ShellIcons
{
    private const uint FileInfoIcon = 0x000000100;

    private const uint FileInfoSmallIcon = 0x000000001;

    private const uint FileInfoUseFileAttributes = 0x000000010;

    private const uint FileAttributeNormal = 0x00000080;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lock Gate = new();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShFileInfo info,
        uint sizeOfInfo,
        uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    /// <summary>The small shell icon for a file, or null when there is none to be had.</summary>
    public static ImageSource? For(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out ImageSource? cached))
            {
                return cached;
            }
        }

        ImageSource? icon = Extract(path);

        lock (Gate)
        {
            Cache[path] = icon;
        }

        return icon;
    }

    private static ImageSource? Extract(string path)
    {
        var info = default(ShFileInfo);

        // UseFileAttributes lets the shell answer from the extension when the file itself cannot be
        // opened — which is the case for anything under WindowsApps or another user's profile. The
        // generic executable icon is a worse answer than the real one and a better one than a letter.
        nint result = SHGetFileInfo(
            path,
            FileAttributeNormal,
            ref info,
            (uint)Marshal.SizeOf<ShFileInfo>(),
            FileInfoIcon | FileInfoSmallIcon | FileInfoUseFileAttributes);

        if (result == 0 || info.Icon == 0)
        {
            return null;
        }

        try
        {
            BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(
                info.Icon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            // Frozen so it can be handed to any thread and so WPF does not keep change-tracking
            // machinery alive for an image that will never change.
            bitmap.Freeze();

            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            // The shell hands us an HICON we own. Not destroying it leaks a GDI handle per call, and
            // a process list that refreshes every three seconds would exhaust the per-process GDI
            // budget within an afternoon.
            _ = DestroyIcon(info.Icon);
        }
    }
}

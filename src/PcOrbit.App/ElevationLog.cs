using System.Globalization;
using System.IO;
using System.Security.Principal;

namespace PcOrbit.App;

/// <summary>
/// A plain-text record of every attempt to start this app with administrator rights.
/// </summary>
/// <remarks>
/// <para>
/// The first two versions of the elevation button failed silently: the elevated copy died before
/// a window appeared and nothing anywhere said so, so the button "did nothing" and the only trace
/// was an Event Log entry nobody looks at. A relaunch through Windows' elevation service runs in a
/// process we do not own and cannot see the output of, so the one place evidence can live is a
/// file both copies can write: what the launching copy tried, and whether the launched copy ever
/// got as far as saying hello.
/// </para>
/// <para>
/// Under <c>%LOCALAPPDATA%\PC Orbit\elevate.log</c>, appended, never rotated by this app — it is
/// a few lines per launch. It records paths and exit codes; never a reading, never a password.
/// </para>
/// </remarks>
internal static class ElevationLog
{
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PC Orbit",
        "elevate.log");

    public static bool IsElevated
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();

            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static void Note(string text)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            File.AppendAllText(
                Path,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} elevated={IsElevated} {text}{Environment.NewLine}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written is not a reason to stop.
        }
    }
}

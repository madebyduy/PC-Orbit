using System.Runtime.InteropServices;

namespace PcOrbit.App;

/// <summary>
/// Gives back the physical memory the app has finished with.
/// </summary>
/// <remarks>
/// <para>
/// The background warm-up reads every page's data in the first minute, and most of what that
/// touches — parsed JSON, PowerShell output, the bitmaps behind a hundred rows — is garbage by the
/// time the page is on screen. The GC reclaims it in due course, but reclaimed pages stay resident
/// until Windows is short of memory, and what the person sees in Task Manager is the resident
/// figure. On an 8 GB laptop that figure is the difference between an app that feels light and one
/// that looks like it is hoarding.
/// </para>
/// <para>
/// So after the heavy phases the app collects, then asks Windows to trim its working set. The
/// trim is not free — pages touched again are faulted back in, softly — which is why it runs only
/// at two moments: once when the warm-up is over and the app is settling into idle, and when the
/// window is minimised, at which point nobody is looking and nothing needs to be fast.
/// </para>
/// <para>
/// This is a real reduction in resident memory, not an accounting trick: the pages go back to the
/// system's pool and other programs can have them. It is also not a substitute for allocating
/// less, which the rest of the app still has to do.
/// </para>
/// </remarks>
internal static partial class WorkingSet
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    /// <summary>Collects, then returns idle pages to Windows. Safe to call at any time.</summary>
    public static void Trim()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        // -1 for both bounds is the documented "trim as much as you can" request. The return value
        // is ignored: a refusal here changes nothing the user can see and there is nothing to retry.
        _ = SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }
}

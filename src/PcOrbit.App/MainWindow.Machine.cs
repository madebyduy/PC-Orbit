using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Transactions;

namespace PcOrbit.App;

/// <summary>
/// The device map, the volume list, elevation, and the restart that finishes a firmware change.
/// </summary>
public partial class MainWindow
{
    // ---------------------------------------------------------------- volumes

    /// <summary>Every mounted drive, with how full it is. The question "how many drives do I have".</summary>
    private void RenderVolumes()
    {
        if (!Ready || VolumeList is null)
        {
            return;
        }

        IReadOnlyList<VolumeInfo> volumes = _inventory.Volumes ?? [];

        VolumesTitle.Text = T("app.volumes.title");
        VolumesHint.Text = T("app.volumes.count", Args(("count", N(volumes.Count))));

        VolumeList.ItemsSource = volumes.Select((v, i) => new MeterRow(
            Name: v.Label is null ? v.Letter : $"{v.Letter}  {v.Label}",
            Value: $"{v.UsedPercent:0}%",
            Note: T("app.volumes.detail", Args(
                ("free", v.FreeGb.ToString("0.0", CultureInfo.CurrentCulture)),
                ("total", v.TotalGb.ToString("0.0", CultureInfo.CurrentCulture)),
                ("fs", v.FileSystem ?? "—"))),
            Percent: v.UsedPercent,

            // Amber past 85%, red past 95%: the two points where Windows itself starts failing
            // updates and restore points, not a taste.
            Accent: v.UsedPercent >= 95 ? B("Bad") : v.UsedPercent >= 85 ? B("Warn") : B("Accent"),
            RowBg: Stripe(i))).ToList();
    }

    // ---------------------------------------------------------------- elevation

    /// <summary>
    /// Offers to run again as administrator, which is the honest fix for every "could not read"
    /// on a standard-user scan.
    /// </summary>
    private void RenderElevation()
    {
        if (!Ready || _host is null || ElevateBtn is null)
        {
            return;
        }

        bool elevated = _host.Elevation.IsElevated;

        ElevateBtn.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
        ElevateBtn.Content = T("app.elevate.button");
        ElevatedBadge.Visibility = elevated ? Visibility.Visible : Visibility.Collapsed;
        ElevatedBadge.Text = T("app.elevate.running");
    }

    /// <summary>
    /// Starts this app again with administrator rights.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Environment.ProcessPath"/> is not the answer on its own. Under
    /// <c>dotnet run</c> — and any framework-dependent launch — it is <c>dotnet.exe</c>, so
    /// elevating it started an empty runtime with no arguments and the user watched a UAC prompt
    /// buy them nothing at all.
    /// </para>
    /// <para>
    /// So: the apphost beside our own assembly if there is one, and otherwise the runtime with the
    /// assembly path handed back to it.
    /// </para>
    /// </remarks>
    private void OnElevate(object sender, RoutedEventArgs e) => TryElevate(fromButton: true);

    /// <summary>
    /// Starts the administrator copy and closes this one — or says exactly why it could not.
    /// </summary>
    /// <returns>True when the elevated copy was started and this window is closing.</returns>
    /// <remarks>
    /// Windows asks the user in its own prompt. Declining it comes back as error 1223 and is not a
    /// failure — nothing happens and we stay. Anything else is a failure and is shown, with the path
    /// to the log, because a button that silently does nothing is the thing that cost a day here.
    /// </remarks>
    private bool TryElevate(bool fromButton)
    {
        ElevationLog.Note(fromButton ? "button: elevation requested" : "startup: AlwaysElevate is on");

        if (ElevatedRelaunch() is not { } start)
        {
            ElevationLog.Note("no launch could be built");
            ShowElevationFailure(T("app.elevate.noHost"));
            return false;
        }

        ElevationLog.Note($"launching \"{start.FileName}\" {string.Join(' ', start.ArgumentList)} in \"{start.WorkingDirectory}\"");

        try
        {
            using Process? started = Process.Start(start);
            ElevationLog.Note($"started, new pid={started?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ElevationLog.Note("declined at the UAC prompt");
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ElevationLog.Note($"FAILED: {ex.NativeErrorCode} {ex.Message}");
            ShowElevationFailure(ex.Message);
            return false;
        }

        Close();
        return true;
    }

    private void ShowElevationFailure(string reason) =>
        MessageBox.Show(
            this,
            T("app.elevate.failed", Args(("reason", reason), ("log", ElevationLog.Path))),
            T("app.title"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

    /// <summary>
    /// This app, asked for again with administrator rights, in a way that will actually start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three versions of this have failed on the machine it was written on, and each failure was
    /// silent until the log existed. Repeating <see cref="Environment.ProcessPath"/> relaunched the
    /// apphost, which died with "You must install .NET": under <c>dotnet run</c> it finds the runtime
    /// through a <c>DOTNET_ROOT</c> the SDK puts in the environment, and a <c>runas</c> launch goes
    /// through Windows' elevation service, which builds the new environment from the elevated token
    /// and drops it. Naming the user-local <c>dotnet.exe</c> directly failed with
    /// <c>ERROR_PATH_NOT_FOUND</c> before the prompt ever appeared — for the full path, for the 8.3
    /// path, with and without a working directory — while <c>powershell.exe</c> from System32
    /// elevated every time. The cause of that refusal was not established; what is established is
    /// that it does not apply to System32.
    /// </para>
    /// <para>
    /// So the elevated process is <c>powershell.exe</c>, hidden, and its one job is to set
    /// <c>DOTNET_ROOT</c> to the runtime this very process is running on and start the apphost, which
    /// is a windowed executable and so shows no console. Inside an already-elevated process that is
    /// an ordinary launch, and ordinary launches from a user's own folder work. The script travels as
    /// <c>-EncodedCommand</c>, the same way every other script in this product does, so a path with
    /// a space or an apostrophe in it cannot break the quoting. If there is no apphost beside the
    /// assembly, the runtime host is started with the assembly instead, console hidden.
    /// </para>
    /// </remarks>
    private static ProcessStartInfo? ElevatedRelaunch()
    {
        string assembly = Assembly.GetEntryAssembly()?.Location ?? string.Empty;

        if (string.IsNullOrEmpty(assembly) || !File.Exists(assembly))
        {
            ElevationLog.Note("no entry assembly on disk");
            return null;
        }

        // shared\Microsoft.NETCore.App\<version>\ -> up three is the dotnet root.
        string runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        string? root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtime.TrimEnd(Path.DirectorySeparatorChar))));
        string? host = root is null ? null : Path.Combine(root, "dotnet.exe");
        string apphost = Path.ChangeExtension(assembly, ".exe");
        string directory = Path.GetDirectoryName(assembly) ?? string.Empty;

        string trampoline = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        ElevationLog.Note(
            $"runtime={runtime} root={root ?? "(none)"} host={host ?? "(none)"} hostExists={host is not null && File.Exists(host)} "
            + $"apphost={apphost} apphostExists={File.Exists(apphost)} trampoline={trampoline} trampolineExists={File.Exists(trampoline)}");

        if (root is null || !File.Exists(trampoline))
        {
            return null;
        }

        static string Q(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

        string script = File.Exists(apphost)
            ? $"$env:DOTNET_ROOT = {Q(root)}; $env:DOTNET_ROOT_X64 = {Q(root)}; "
              + $"Start-Process -FilePath {Q(apphost)} -WorkingDirectory {Q(directory)}"
            : host is not null && File.Exists(host)
                ? $"Start-Process -FilePath {Q(host)} -ArgumentList ('\"' + {Q(assembly)} + '\"') -WorkingDirectory {Q(directory)} -WindowStyle Hidden"
                : string.Empty;

        if (script.Length == 0)
        {
            return null;
        }

        var start = new ProcessStartInfo(trampoline)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = directory,
        };

        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-WindowStyle");
        start.ArgumentList.Add("Hidden");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));

        ElevationLog.Note($"trampoline script: {script}");

        return start;
    }

    // ---------------------------------------------------------------- restart

    /// <summary>
    /// Shows the banner that finishes a staged firmware or Windows change.
    /// </summary>
    /// <remarks>
    /// A firmware step is only ever half done when Apply returns: the user still has to boot into
    /// setup and flip the switch. This is where that is said, next to the button that gets them
    /// there — a restart straight into firmware setup — rather than in a sentence at the bottom.
    /// </remarks>
    private void RenderRestartBanner(Transaction result)
    {
        bool needs = result.NeedsRestart && result.Mode == ExecutionMode.Apply;

        RestartBanner.Visibility = needs ? Visibility.Visible : Visibility.Collapsed;

        if (!needs)
        {
            return;
        }

        bool firmware = result.PendingRestart == RestartKind.Firmware;

        RestartTitle.Text = T(firmware ? "app.restart.firmwareTitle" : "app.restart.windowsTitle");
        RestartNowBtn.Content = T(firmware ? "app.restart.intoFirmware" : "app.restart.now");
        RestartNowBtn.Tag = firmware ? "fw" : "os";
    }

    private void OnRestartNow(object sender, RoutedEventArgs e)
    {
        bool firmware = RestartNowBtn.Tag is "fw";

        MessageBoxResult answer = MessageBox.Show(
            this,
            T(firmware ? "app.restart.confirmFirmware" : "app.restart.confirmWindows"),
            T("app.title"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        // shutdown /r /fw boots straight into firmware setup, and needs administrator rights;
        // a plain restart does not. Both give a 15-second window to save work.
        var info = new ProcessStartInfo("shutdown.exe")
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            Arguments = firmware ? "/r /fw /t 15" : "/r /t 15",
        };

        if (firmware && _host?.Elevation.IsElevated == false)
        {
            info.Verb = "runas";
        }

        try
        {
            Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, T("app.restart.declined"), T("app.title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

}

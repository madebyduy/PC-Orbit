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
    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevatedRelaunch() is not { } start)
        {
            return;
        }

        // Windows asks the user in its own prompt. If they decline, nothing happens and we stay.
        try
        {
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;
        }

        Close();
    }

    /// <summary>
    /// The same launch that produced this process, asked for again with administrator rights.
    /// </summary>
    /// <remarks>
    /// Reusing <see cref="Environment.ProcessPath"/> rather than looking for the apphost beside the
    /// assembly, because the apphost is not always usable: on a machine whose .NET lives in a
    /// user-local folder that nothing has registered, <c>PcOrbit.exe</c> exits with
    /// "You must install .NET" before a window appears. However this process was started, that way
    /// demonstrably works — so repeat it, and hand the runtime our assembly back when the runtime
    /// is what started us.
    /// </remarks>
    private static ProcessStartInfo? ElevatedRelaunch()
    {
        if (Environment.ProcessPath is not { } host)
        {
            return null;
        }

        var start = new ProcessStartInfo(host) { UseShellExecute = true, Verb = "runas" };

        if (!Path.GetFileName(host).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return start;
        }

        string assembly = Assembly.GetEntryAssembly()?.Location ?? string.Empty;

        if (string.IsNullOrEmpty(assembly))
        {
            return null;
        }

        start.ArgumentList.Add(assembly);

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

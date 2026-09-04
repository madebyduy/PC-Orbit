using System.Diagnostics;
using System.Globalization;
using System.Windows;
using PcOrbit.Core.Checkup;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Preflight;
using PcOrbit.Core.Model;

namespace PcOrbit.App;

/// <summary>
/// The firmware page: what the BIOS is, what it is set to, and the two things that can be done
/// about it from here.
/// </summary>
/// <remarks>
/// <para>
/// The honest scope of this page is narrower than the request that produced it, and saying so is
/// the point. Writing BIOS settings from Windows needs a vendor interface, and the machine this was
/// built against registers Lenovo's WMI classes but returns no instances from them — that provider
/// only populates on the commercial ThinkPad and ThinkCentre lines. A page that offered switches
/// which silently did nothing would be worse than one that says what it can and cannot reach.
/// </para>
/// <para>
/// What it can do is real. Restarting into the firmware's own settings screen is a documented UEFI
/// facility, and it removes the actual difficulty for a non-technical owner — that entering BIOS
/// means pressing an unnamed key inside a window of about one second. And it says whether Windows
/// is able to deliver firmware updates for this machine at all, which decides whether the answer
/// to "is my BIOS old" is Windows Update or the manufacturer's site.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// The capabilities this page owns.
    /// </summary>
    /// <remarks>
    /// Also the filter the Readings page applies in reverse. One list, used by both, so a firmware
    /// capability cannot end up on both pages or on neither.
    /// </remarks>
    internal static bool IsFirmware(CapabilityId capability) =>
        capability.Value.StartsWith("firmware.", StringComparison.Ordinal);

    private void ApplyBiosStrings()
    {
        BiosTitle.Text = T("app.bios.title");
        BiosActionsTitle.Text = T("app.bios.actions");
        BiosActionsHint.Text = T("app.bios.actionsHint");
        BiosEnterName.Text = T("app.bios.enter");
        BiosEnterNote.Text = T("app.bios.enterNote");
        BiosEnter.Content = T("app.bios.enterGo");
        BiosVendorNote.Text = T("app.bios.vendorNote");
        BiosVendor.Content = T("app.bios.vendorGo");
        BiosWriteNote.Text = T("app.bios.writeNote");
        BiosStateTitle.Text = T("app.bios.state");
        BiosStateHint.Text = T("app.status.readingsHint");
    }

    private void RenderBios()
    {
        if (_snapshot is null || _host is null)
        {
            return;
        }

        MachineIdentity machine = _snapshot.Machine;

        CapabilityValue version = _snapshot.ValueOf(CoreCapabilities.BiosVersion);

        BiosVersion.Text = version.IsKnown ? version.Canonical : T("status.unknown");

        BiosAge.Text = BiosAgeSentence();

        // The vendor's front door, never a deep link built from a guessed model path: a constructed
        // URL that 404s sends the user to a search engine, which is the journey this prevents.
        string? support = VendorSupport.SupportUrlFor(machine);

        BiosVendorName.Text = string.IsNullOrWhiteSpace(machine.SystemVendor)
            ? T("app.bios.vendorUnknown")
            : machine.SystemVendor;

        BiosVendor.IsEnabled = support is not null;
        BiosVendor.Tag = support;

        RenderFirmwareReadings();
    }

    /// <summary>
    /// How old the firmware is, in a sentence rather than a number of days.
    /// </summary>
    /// <remarks>
    /// The threshold and the reasoning belong to <see cref="FirmwareAdvisoryRule"/>, which is
    /// Info-severity and deliberately offers no action — this product does not flash firmware. This
    /// only phrases what that rule already decides, so the page and the checkup cannot disagree
    /// about whether a BIOS is old.
    /// </remarks>
    private string BiosAgeSentence()
    {
        CapabilityReading? age = _snapshot?.ReadingOf(CoreCapabilities.BiosAgeDays);

        // Parsed here rather than through the checkup's own helper, which is internal to Core. The
        // scalar's raw text is what that helper reads too, so the two cannot disagree about a value.
        if (age?.Value.Raw is not { } raw
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double days))
        {
            return T("app.bios.ageUnknown");
        }

        int years = (int)(days / 365);

        return T(years >= 3 ? "app.bios.ageOld" : "app.bios.ageFine", Args(
            ("days", N((int)days)),
            ("years", N(Math.Max(1, years)))));
    }

    private void RenderFirmwareReadings()
    {
        if (_snapshot is null || _host is null)
        {
            return;
        }

        BiosList.ItemsSource = _snapshot.Readings
            .Where(r => IsFirmware(r.Capability))
            .Select((r, i) => CapabilityRow(r.Capability, i))
            .ToList();
    }

    // ---------------------------------------------------------------- actions

    /// <summary>
    /// Restarts into the firmware's own settings screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>shutdown /r /fw</c> asks UEFI to come up in its setup screen on the next boot. It is the
    /// documented way, it needs administrator rights, and it only exists on a UEFI machine — all
    /// three of which are checked and said out loud rather than discovered as a silent failure.
    /// </para>
    /// <para>
    /// This is the one thing on this page that changes what the machine does, and what it does is
    /// restart. So it confirms first, in the words of the consequence: unsaved work will be lost,
    /// and Windows will not come back until you leave the firmware screen.
    /// </para>
    /// </remarks>
    private void OnEnterFirmware(object sender, RoutedEventArgs e)
    {
        if (_host is null)
        {
            return;
        }

        if (!_host.Elevation.IsElevated)
        {
            MessageBox.Show(
                this,
                T("app.bios.needsAdmin"),
                T("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        if (MessageBox.Show(
                this,
                T("app.bios.enterConfirm"),
                T("app.title"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            using Process? restart = Process.Start(new ProcessStartInfo("shutdown")
            {
                // Ten seconds, not zero. A restart that begins the instant the button is released
                // leaves no room to have changed your mind, and `shutdown /a` is the way back.
                Arguments = "/r /fw /t 10",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            MessageBox.Show(
                this,
                T("app.bios.enterStarted"),
                T("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(
                this,
                T("app.bios.enterFailed", Args(("problem", ex.Message))),
                T("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnOpenVendorSupport(object sender, RoutedEventArgs e)
    {
        if (BiosVendor.Tag is not string url)
        {
            return;
        }

        try
        {
            using Process? browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(
                this,
                ex.Message,
                T("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}

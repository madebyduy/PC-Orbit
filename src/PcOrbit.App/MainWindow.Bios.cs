using System.Diagnostics;
using System.Globalization;
using System.Windows;
using PcOrbit.Core.Checkup;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Guides;
using PcOrbit.Core.Preflight;
using PcOrbit.Core.Model;

namespace PcOrbit.App;

/// <param name="SettingName">The vendor's English label, never translated.</param>
public sealed record GuideRow(
    string Goal,
    string Tier,
    string SettingLabel,
    string SettingName,
    string AlsoCalled,
    Visibility AlsoCalledVisible,
    string Steps,
    string Note,
    Visibility NoteVisible,
    System.Windows.Media.Brush RowBg);

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

    /// <summary>
    /// The four firmware readings the Protection page owns.
    /// </summary>
    /// <remarks>
    /// Secure Boot, the boot mode and the TPM are firmware settings, and they are also the answer
    /// to "is this machine protected" — which is where somebody actually looks for them. So they
    /// live on Protection and this page does not repeat them. Every firmware capability has exactly
    /// one home: these four there, the rest here, and none of them on Readings.
    /// </remarks>
    private static readonly CapabilityId[] OwnedByProtection =
    [
        CoreCapabilities.SecureBoot,
        CoreCapabilities.BootMode,
        CoreCapabilities.TpmVersion,
        CoreCapabilities.TpmReady,
    ];

    private static bool BelongsHere(CapabilityId capability) =>
        IsFirmware(capability) && !OwnedByProtection.Contains(capability);

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
        GuideTitle.Text = T("app.guide.title");
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
        RenderGuides();
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
            .Where(r => BelongsHere(r.Capability))
            .Select((r, i) => CapabilityRow(r.Capability, i))
            .ToList();
    }

    /// <summary>
    /// The manufacturer's own menu path for each firmware setting this product can guide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guide packs have carried this since they were written — which key opens setup on this
    /// vendor, where the setting sits, what the firmware calls it in English — and nothing rendered
    /// them outside a plan step. So a user told "your model cannot be driven from Windows" got a
    /// restart button and no answer to the obvious next question.
    /// </para>
    /// <para>
    /// The setting's name is shown untranslated and set apart, because it is the one thing on the
    /// card that has to match a screen this product does not control (spec 21.11). An expired pack
    /// shows nothing: a menu path the user trusts and that has since moved is worse than none.
    /// </para>
    /// </remarks>
    private void RenderGuides()
    {
        if (_snapshot is null || _host is null)
        {
            return;
        }

        MachineIdentity machine = _snapshot.Machine;
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);

        List<GuideRow> rows = [];

        foreach (GuideData guide in _host.Guides.Values.OrderBy(g => g.Capability.Value, StringComparer.Ordinal))
        {
            if (guide.SelectFor(machine, today) is { } entry)
            {
                rows.Add(BuildGuideRow(guide, entry, rows.Count));
                continue;
            }

            // Expired, rather than absent. Dropping the row silently would leave the user with two
            // guides where yesterday there were three and nothing to explain the difference — and
            // "we stopped trusting this" is a better answer than a gap.
            if (guide.IsExpired(today))
            {
                rows.Add(new GuideRow(
                    T($"cap.{guide.Capability.Value}"),
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    Visibility.Collapsed,
                    T("app.guide.expired"),
                    string.Empty,
                    Visibility.Collapsed,
                    Stripe(rows.Count)));
            }
        }

        GuideList.ItemsSource = rows;
        GuideHint.Text = rows.Count == 0 ? T("app.guide.none") : T("app.guide.hint");
    }

    private GuideRow BuildGuideRow(GuideData guide, GuideEntry entry, int index)
    {
        string goalKey = guide.TargetState.Canonical switch
        {
            "enabled" or "on" => "app.guide.goal.enable",
            "disabled" or "off" => "app.guide.goal.disable",
            _ => "app.guide.goal.set",
        };

        // A menu path reads as a path. Chevrons rather than commas, because that is what the user
        // is about to do: go in, then in again.
        string path = string.Join("  ›  ", entry.MenuPath);

        string enter = entry.EnterKeys is { Count: > 0 } keys
            ? string.Join(" / ", keys)
            : "F2 / Del";

        string steps = entry.SaveKeys is { Count: > 0 } save
            ? T("app.guide.steps", Args(("enter", enter), ("path", path), ("save", string.Join(" / ", save))))
            : T("app.guide.stepsNoSave", Args(("enter", enter), ("path", path)));

        if (entry.SearchKey is { Length: > 0 } search)
        {
            steps += T("app.guide.search", Args(("key", search)));
        }

        bool alsoCalled = entry.AlternateNames is { Count: > 0 };

        return new GuideRow(
            T(goalKey, Args(("setting", T($"cap.{guide.Capability.Value}")))),
            T($"tier.{Camel(entry.Tier.ToString())}"),
            T("app.guide.settingLabel"),
            entry.SettingName,
            alsoCalled ? T("app.guide.alsoCalled", Args(("names", string.Join(", ", entry.AlternateNames!)))) : string.Empty,
            alsoCalled ? Visibility.Visible : Visibility.Collapsed,
            steps,
            entry.NoteKey is { Length: > 0 } note ? T(note) : string.Empty,
            entry.NoteKey is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed,
            Stripe(index));
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

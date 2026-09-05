using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcOrbit.Core.Firmware;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Preflight;

namespace PcOrbit.App;

/// <param name="Wanted">
/// The value chosen in the row's list. Two-way bound, so the list is the state rather than a
/// picture of it, and the button reads what is actually selected.
/// </param>
public sealed class FirmwareRow(
    string name,
    string detail,
    string current,
    IReadOnlyList<string> options,
    string wanted,
    bool writable,
    string riskLabel,
    string applyLabel,
    Brush riskTint,
    Brush riskInk,
    Brush rowBg)
{
    public string Name { get; } = name;

    public string Detail { get; } = detail;

    public string Current { get; } = current;

    public IReadOnlyList<string> Options { get; } = options;

    public string Wanted { get; set; } = wanted;

    public bool Writable { get; } = writable;

    public string RiskLabel { get; } = riskLabel;

    public string ApplyLabel { get; } = applyLabel;

    public Brush RiskTint { get; } = riskTint;

    public Brush RiskInk { get; } = riskInk;

    public Brush RowBg { get; } = rowBg;

    /// <summary>List-valued: offers Reorder instead of the one-value picker.</summary>
    public bool Reorderable { get; init; }

    public string ReorderLabel { get; init; } = string.Empty;

    public Visibility ReorderVisible => Reorderable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ApplyVisible => Reorderable ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>One firmware write this app made, ready for the history card.</summary>
public sealed record FirmwareHistoryRow(
    string Id,
    string When,
    string Setting,
    string Change,
    string Verdict,
    Brush Tint,
    Brush Ink,
    string RevertLabel,
    Visibility RevertVisible,
    Brush RowBg);

public sealed record FirmwareGroup(string Key, string Name, string Count, IReadOnlyList<FirmwareRow> Rows);

/// <summary>
/// Changing a firmware setting from Windows.
/// </summary>
/// <remarks>
/// <para>
/// The three parts that make this safe are all somewhere else, on purpose.
/// <see cref="FirmwareRiskTable"/> decides what a setting costs if it is wrong,
/// <see cref="FirmwareChangePlan"/> decides what has to be true and what has to be said before the
/// write, and <see cref="FirmwareConfirmDialog"/> makes saying yes as hard as the risk deserves.
/// All three are testable without hardware, which matters because the write itself is not.
/// </para>
/// <para>
/// This file renders and dispatches. Ninety settings arrived from the first machine that answered,
/// so rendering means grouping, searching and filtering — a person who wants virtualization for
/// Docker should see one row, not scroll for it. The grouping is by keyword in the vendor's own
/// name, which is the only handle there is, and a name no keyword claims goes to Other rather than
/// being hidden.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private FirmwareInterface _firmware = FirmwareInterface.None();

    /// <summary>The rows as built, before any search or filter. Filtering never re-reads the machine.</summary>
    private IReadOnlyList<(FirmwareSetting Setting, FirmwareRow Row)> _firmwareRows = [];

    /// <summary>
    /// Which group a setting belongs to, from what its name contains.
    /// </summary>
    /// <remarks>
    /// First match wins, and the order is the priority: "WakeOnLAN" is network before it is power,
    /// "BIOSPasswordAtBoot" is security before it is boot. Anything unclaimed is Other.
    /// </remarks>
    private static readonly (string Key, string[] Tokens)[] FirmwareGroupTable =
    [
        ("alarm", ["alarm", "wakeuponalarm"]),
        ("security", ["password", "secure", "security", "tpm", "chip", "dma", "txt", "absolute", "wipe", "certificate", "biometric", "fingerprint", "lock", "executionprevention", "sid", "rollback", "uefica", "unlock"]),
        ("network", ["lan", "wifi", "wireless", "ethernet", "network", "pxe", "ipv", "tftp", "proxy", "url", "cloud", "macaddress", "http", "bluetooth"]),
        ("boot", ["boot", "startup", "f12", "displaydevice", "deployment"]),
        ("keyboard", ["fn", "keyboard", "touchpad", "trackpoint", "beep", "key"]),
        ("power", ["power", "battery", "charge", "thermal", "turbo", "speedstep", "energy", "onbyac", "alwayson", "flip", "coolquiet", "smp", "management"]),
        ("devices", ["camera", "microphone", "audio", "thunderbolt", "tunneling", "usb", "port", "access", "panel", "display", "reader"]),
    ];

    private static readonly string[] FirmwareGroupOrder =
        ["security", "boot", "power", "devices", "keyboard", "network", "alarm", "other"];

    /// <summary>What the quick chips type into the search box.</summary>
    private static readonly (string Key, string Search)[] FirmwareQuickSearches =
    [
        ("virtualization", "virtual"),
        ("privacy", "camera micro"),
        ("fn", "fn"),
        ("boot", "boot"),
        ("power", "charge power battery"),
        ("network", "lan wake"),
    ];

    private static string FirmwareGroupOf(string name)
    {
        string n = string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

        foreach ((string key, string[] tokens) in FirmwareGroupTable)
        {
            if (tokens.Any(t => n.Contains(t, StringComparison.Ordinal)))
            {
                return key;
            }
        }

        return "other";
    }

    private void ApplyFirmwareStrings()
    {
        FirmwareHistoryTitle.Text = T("app.firmware.history.title");
        FirmwareHistoryHint.Text = T("app.firmware.history.hint");
        FirmwareHistoryEmpty.Text = T("app.firmware.history.empty");
        FirmwareTitle.Text = T("app.firmware.title");
        FirmwareHint.Text = T("app.firmware.hint");
        FirmwareSearch.Tag = T("app.firmware.search");
        FirmwareQuickLabel.Text = T("app.firmware.quick");
        FirmwareNoMatch.Text = T("app.firmware.noMatch");

        FwAll.Content = T("app.firmware.filter.all");
        FwRoutine.Content = T("app.firmware.filter.routine");
        FwCareful.Content = T("app.firmware.filter.careful");
        FwSerious.Content = T("app.firmware.filter.serious");
        FwRefused.Content = T("app.firmware.filter.refused");

        FirmwareQuick.Children.Clear();

        foreach ((string key, string search) in FirmwareQuickSearches)
        {
            var chip = new Button
            {
                Style = (Style)FindResource("QuickChip"),
                Content = T($"app.firmware.quick.{key}"),
                Tag = search,
            };

            chip.Click += (_, _) => FirmwareSearch.Text = (string)chip.Tag;
            FirmwareQuick.Children.Add(chip);
        }
    }

    private async Task RenderFirmwareAsync()
    {
        if (_host is null)
        {
            return;
        }

        _firmware = await _host.Firmware.ReadAsync();

        FirmwareVendor.Text = _firmware.Vendor ?? string.Empty;

        if (!_firmware.IsUsable)
        {
            _firmwareRows = [];
            FirmwareGroups.ItemsSource = null;
            FirmwareTools.Visibility = Visibility.Collapsed;
            FirmwareUnavailable.Visibility = Visibility.Visible;
            FirmwareUnavailableText.Text = UnavailableReason();
            RenderFirmwareStatus();
            await RenderFirmwareHistoryAsync();

            return;
        }

        FirmwareUnavailable.Visibility = Visibility.Collapsed;
        FirmwareTools.Visibility = Visibility.Visible;

        _firmwareRows = [.. _firmware.Settings.Select((setting, i) =>
        {
            (Brush tint, Brush ink) = RiskTone(setting.Risk);

            return (setting, new FirmwareRow(
                setting.Name,
                Detail(setting),
                setting.Current ?? T("status.unknown"),
                setting.Options,
                setting.Current ?? (setting.Options.Count > 0 ? setting.Options[0] : string.Empty),
                setting.Writable,
                T($"app.firmware.risk.{Camel(setting.Risk.ToString())}"),
                T("app.firmware.apply"),
                tint,
                ink,
                Stripe(i))
            {
                Reorderable = setting.Reorderable,
                ReorderLabel = T("app.firmware.reorder"),
            });
        })];

        ApplyFirmwareFilter();
        RenderFirmwareStatus();
        await RenderFirmwareHistoryAsync();
    }

    /// <summary>
    /// The four facts a person opens a BIOS page to check, as chips above the list.
    /// </summary>
    /// <remarks>
    /// Three of them also live on Protection. That is a deliberate second home — the one exception
    /// to the no-repeats rule — because a BIOS page with no Secure Boot on it reads as broken
    /// however tidy the rule behind it. The fourth, the supervisor password, comes from the vendor
    /// interface and is shown only when that interface answered.
    /// </remarks>
    private void RenderFirmwareStatus()
    {
        List<StateRow> chips =
        [
            CapabilityRow(CoreCapabilities.SecureBoot, 0),
            CapabilityRow(CoreCapabilities.BootMode, 1),
            CapabilityRow(CoreCapabilities.TpmReady, 2),
        ];

        if (_firmware.Availability == FirmwareAvailability.Available)
        {
            chips.Add(new StateRow(
                T(_firmware.PasswordRequired ? "app.firmware.password.set" : "app.firmware.password.none"),
                string.Empty,
                string.Empty,
                GlyphOf("GlyphSecurity"),
                _firmware.PasswordRequired ? B("GoodSoft") : B("Hair"),
                _firmware.PasswordRequired ? B("Good") : B("Muted"),
                Brushes.Transparent));
        }

        FirmwareStatus.ItemsSource = chips;
    }

    private async Task RenderFirmwareHistoryAsync()
    {
        if (_host is null)
        {
            return;
        }

        IReadOnlyList<FirmwareChange> changes = await _host.FirmwareChanges.ListAsync();

        FirmwareHistoryEmpty.Visibility = changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        FirmwareHistory.ItemsSource = changes.Select((c, i) => new FirmwareHistoryRow(
            c.Id,
            c.When.LocalDateTime.ToString("dd/MM HH:mm", System.Globalization.CultureInfo.CurrentCulture),
            c.Setting,
            T("app.firmware.history.change", Args(("from", c.From ?? T("status.unknown")), ("to", c.To))),
            T(c.Verified ? "app.firmware.history.verified" : "app.firmware.history.unverified"),
            c.Verified ? B("GoodSoft") : B("WarnSoft"),
            c.Verified ? B("Good") : B("Warn"),
            T("app.firmware.revert"),
            c.CanRevert ? Visibility.Visible : Visibility.Collapsed,
            Stripe(i))).ToList();
    }

    private void OnFirmwareFilterChanged(object sender, RoutedEventArgs e)
    {
        if (Ready && FirmwareGroups is not null)
        {
            ApplyFirmwareFilter();
        }
    }

    /// <summary>
    /// Search and risk filter over the rows already built, grouped for display.
    /// </summary>
    /// <remarks>
    /// Every search word has to appear somewhere in the name or the current value, in any order —
    /// "camera micro" finds both privacy switches at once, which is what the quick chip relies on.
    /// Grouping happens after filtering so an empty group simply is not shown.
    /// </remarks>
    private void ApplyFirmwareFilter()
    {
        string[] words = FirmwareSearch.Text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        FirmwareRisk? risk =
            FwRoutine.IsChecked == true ? FirmwareRisk.Routine
            : FwCareful.IsChecked == true ? FirmwareRisk.Careful
            : FwSerious.IsChecked == true ? FirmwareRisk.Serious
            : FwRefused.IsChecked == true ? FirmwareRisk.Refused
            : null;

        List<(FirmwareSetting Setting, FirmwareRow Row)> matching = [.. _firmwareRows
            .Where(r => risk is null || r.Setting.Risk == risk)
            .Where(r => words.All(w =>
                r.Setting.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                || (r.Setting.Current?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))];

        List<FirmwareGroup> groups = [];

        foreach (string key in FirmwareGroupOrder)
        {
            List<FirmwareRow> rows = [.. matching
                .Where(r => FirmwareGroupOf(r.Setting.Name) == key)
                .Select(r => r.Row)];

            if (rows.Count > 0)
            {
                groups.Add(new FirmwareGroup(
                    key,
                    T($"app.firmware.group.{key}"),
                    T("app.firmware.groupHint", Args(("count", N(rows.Count)))),
                    rows));
            }
        }

        FirmwareGroups.ItemsSource = groups;
        FirmwareCount.Text = T("app.firmware.count", Args(("count", N(matching.Count))));
        FirmwareNoMatch.Visibility = matching.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Why there is nothing to show, in the words of the actual reason.
    /// </summary>
    /// <remarks>
    /// Four different answers, and collapsing them into "not available" would throw away the one
    /// piece of information the reader can act on. Needing administrator rights is a fact about
    /// this process and is fixed by a button. A model that answered and had nothing is a fact about
    /// the hardware. Dell needs a download. No interface at all is most self-built and older
    /// hardware. Only the second of those is a reason to stop looking.
    /// </remarks>
    private string UnavailableReason() => _firmware switch
    {
        { Problem: { } problem } => T("app.firmware.problem", Args(("problem", problem))),
        { Availability: FirmwareAvailability.NeedsElevation } => T("app.firmware.needsElevation"),
        { Availability: FirmwareAvailability.NeedsVendorTool } => T("app.firmware.dellNeedsTool"),
        { Availability: FirmwareAvailability.ModelDoesNotImplement, Vendor: { Length: > 0 } vendor } =>
            T("app.firmware.modelHasNone", Args(("vendor", vendor))),
        _ => T("app.firmware.noInterface"),
    };

    private string Detail(FirmwareSetting setting) => setting.Risk == FirmwareRisk.Refused
        ? T("app.firmware.refusedDetail")
        : T("app.firmware.currentDetail", Args(("value", setting.Current ?? T("status.unknown"))));

    private static (Brush Tint, Brush Ink) RiskTone(FirmwareRisk risk) => risk switch
    {
        FirmwareRisk.Routine => (Freeze("#DCFCE7"), Freeze("#15803D")),
        FirmwareRisk.Careful => (Freeze("#FEF3C7"), Freeze("#B45309")),
        FirmwareRisk.Serious => (Freeze("#FEE2E2"), Freeze("#B91C1C")),
        _ => (Freeze("#E2E8F0"), Freeze("#475569")),
    };

    private async void OnFirmwareApply(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name })
        {
            return;
        }

        (FirmwareSetting Setting, FirmwareRow Row) hit = _firmwareRows
            .FirstOrDefault(r => string.Equals(r.Setting.Name, name, StringComparison.Ordinal));

        if (hit.Setting is not null && !string.IsNullOrWhiteSpace(hit.Row.Wanted))
        {
            await ChangeFirmwareAsync(hit.Setting, hit.Row.Wanted);
        }
    }

    private async void OnFirmwareReorder(object sender, RoutedEventArgs e)
    {
        if (_strings is null || sender is not Button { Tag: string name })
        {
            return;
        }

        FirmwareSetting? setting = _firmware.Settings
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

        if (setting?.Current is null)
        {
            return;
        }

        string? reordered = BootOrderDialog.Show(this, _strings, setting.Name, setting.Current);

        if (reordered is not null)
        {
            await ChangeFirmwareAsync(setting, reordered);
        }
    }

    /// <summary>
    /// Put back: the previous value, written through exactly the same confirmation.
    /// </summary>
    /// <remarks>
    /// Not a shortcut. The consequences of restoring Secure Boot are the consequences of changing
    /// Secure Boot, and the typed confirmation applies both ways. The only thing the history adds is
    /// the value, which is the one thing a person cannot be expected to remember.
    /// </remarks>
    private async void OnFirmwareRevert(object sender, RoutedEventArgs e)
    {
        if (_host is null || sender is not Button { Tag: string id })
        {
            return;
        }

        FirmwareChange? change = (await _host.FirmwareChanges.ListAsync())
            .FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

        if (change is null || change.From is null)
        {
            return;
        }

        FirmwareSetting? setting = _firmware.Settings
            .FirstOrDefault(s => string.Equals(s.Name, change.Setting, StringComparison.Ordinal));

        if (setting is null)
        {
            MessageBox.Show(this, T("app.firmware.revert.gone"), T("app.title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ChangeFirmwareAsync(setting, change.From);
    }

    /// <summary>
    /// The one path every firmware write takes: plan, confirm to the risk, write, read back, record.
    /// </summary>
    private async Task ChangeFirmwareAsync(FirmwareSetting setting, string wanted)
    {
        if (_host is null || _strings is null)
        {
            return;
        }

        FirmwareChangePlan plan = FirmwareChangePlan.For(setting, wanted, _snapshot, _firmware.PasswordRequired);

        if (!plan.CanProceed)
        {
            MessageBox.Show(
                this,
                string.Join("\n\n", plan.Blockers.Select(b => T(b))),
                T("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        FirmwareConfirmation answer = FirmwareConfirmDialog.Show(this, _strings, plan);

        if (!answer.Confirmed)
        {
            return;
        }

        FirmwareWriteResult result = await BusyResultAsync(
            T("app.firmware.working"),
            () => _host.Firmware.SetAsync(setting, wanted, answer.Password));

        // Recorded whenever the firmware accepted the write, verified or not: an unverified change
        // is exactly the one whose previous value the person will want later.
        if (result.Applied)
        {
            await _host.FirmwareChanges.RecordAsync(new FirmwareChange(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.Now,
                _firmware.Vendor ?? string.Empty,
                setting.Name,
                setting.Current,
                wanted,
                result.Verified));
        }

        MessageBox.Show(
            this,
            Outcome(result),
            T("app.title"),
            MessageBoxButton.OK,
            result is { Applied: true, Verified: true } ? MessageBoxImage.Information : MessageBoxImage.Warning);

        await RenderFirmwareAsync();
    }

    /// <summary>
    /// What to say afterwards, decided by what the firmware said when it was asked again.
    /// </summary>
    /// <remarks>
    /// The interesting case is the third one: the vendor call returned Success and reading the
    /// firmware back does not agree. Some firmware holds a change as pending until the restart and
    /// reads back the old value until then — which is exactly why this says "we could not confirm
    /// it" rather than either claiming success or claiming failure.
    /// </remarks>
    private string Outcome(FirmwareWriteResult result) => result switch
    {
        { Problem: { } problem } when !result.Applied =>
            T("app.firmware.failed", Args(("name", result.Name), ("problem", problem))),
        { Applied: true, Verified: true } =>
            T("app.firmware.done", Args(("name", result.Name), ("value", result.Wanted))),
        { Applied: true } =>
            T("app.firmware.unverified", Args(("name", result.Name), ("value", result.Wanted))),
        _ => T("app.firmware.failed", Args(("name", result.Name), ("problem", T("status.unknown")))),
    };

    /// <summary>The busy overlay, for work that returns something.</summary>
    private async Task<T> BusyResultAsync<T>(string message, Func<Task<T>> work)
    {
        BusyText.Text = message;
        BusyOverlay.Visibility = Visibility.Visible;

        try
        {
            return await work();
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }
}

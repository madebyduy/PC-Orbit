using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcOrbit.Core.Firmware;

namespace PcOrbit.App;

/// <param name="Wanted">
/// The value chosen in the row's list. Two-way bound, so the list is the state rather than a
/// picture of it, and the button reads what is actually selected.
/// </param>
public sealed class FirmwareRow(
    string name,
    string detail,
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

    public IReadOnlyList<string> Options { get; } = options;

    public string Wanted { get; set; } = wanted;

    public bool Writable { get; } = writable;

    public string RiskLabel { get; } = riskLabel;

    public string ApplyLabel { get; } = applyLabel;

    public Brush RiskTint { get; } = riskTint;

    public Brush RiskInk { get; } = riskInk;

    public Brush RowBg { get; } = rowBg;
}

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
/// This file only renders and dispatches. A decision made inside a click handler is a decision
/// nothing can test, and on this page the cost of an untested decision is a PC that does not start.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private FirmwareInterface _firmware = FirmwareInterface.None();

    private void ApplyFirmwareStrings()
    {
        FirmwareTitle.Text = T("app.firmware.title");
        FirmwareHint.Text = T("app.firmware.hint");
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
            FirmwareList.ItemsSource = null;
            FirmwareUnavailable.Visibility = Visibility.Visible;
            FirmwareUnavailableText.Text = UnavailableReason();

            return;
        }

        FirmwareUnavailable.Visibility = Visibility.Collapsed;

        FirmwareList.ItemsSource = _firmware.Settings.Select((setting, i) =>
        {
            (Brush tint, Brush ink) = RiskTone(setting.Risk);

            return new FirmwareRow(
                setting.Name,
                Detail(setting),
                setting.Options,
                setting.Current ?? (setting.Options.Count > 0 ? setting.Options[0] : string.Empty),
                setting.Writable,
                T($"app.firmware.risk.{Camel(setting.Risk.ToString())}"),
                T("app.firmware.apply"),
                tint,
                ink,
                Stripe(i));
        }).ToList();
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

        // First, and it is the one that was missing. Without it a standard user was told their
        // hardware lacked a feature that nobody had been allowed to ask about.
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
        if (_host is null || _strings is null || sender is not Button { Tag: string name })
        {
            return;
        }

        if (FirmwareList.ItemsSource is not IEnumerable<FirmwareRow> rows)
        {
            return;
        }

        FirmwareRow? row = rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

        FirmwareSetting? setting = _firmware.Settings
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

        if (row is null || setting is null || string.IsNullOrWhiteSpace(row.Wanted))
        {
            return;
        }

        FirmwareChangePlan plan = FirmwareChangePlan.For(
            setting, row.Wanted, _snapshot, _firmware.PasswordRequired);

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
            () => _host.Firmware.SetAsync(setting, row.Wanted, answer.Password));

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
    /// firmware back does not agree. That is a failure, and it is reported as one. Some firmware
    /// holds a change as pending until the restart and will read back the old value until then —
    /// which is exactly why this says "we could not confirm it" rather than either claiming success
    /// or claiming failure.
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

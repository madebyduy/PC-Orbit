using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PcOrbit.Adapters.Windows;
using PcOrbit.Cli;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Checkup;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Events;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Guides;
using PcOrbit.Core.Localization;
using PcOrbit.Core.Model;
using PcOrbit.Core.Outcomes;
using PcOrbit.Core.Preflight;
using PcOrbit.Core.Transactions;

namespace PcOrbit.App;

// ---------------------------------------------------------------- row models

public sealed record ReadingRow(string Name, string Value, string Source, Brush Accent, Brush RowBg);

public sealed record FindingRow(
    string Title,
    string Benefit,
    string Safety,
    string Cost,
    string Evidence,
    string FixLabel,
    Visibility FixVisible,
    string? OutcomeId,
    Brush Tone,
    Brush Accent);

public sealed record OutcomeRow(Outcome Outcome, string Title, string Description, string Id, string OpenLabel)
{
    /// <summary>
    /// What a screen reader announces for this row in the outcome list. Without it the automation
    /// name falls back to the record's own text, which is a dump of the whole outcome (spec 21.12).
    /// </summary>
    public override string ToString() => Title;
}

public sealed record StepRow(string Title, string Change, string Why, string Meta);

public sealed record PhaseRow(string Header, IReadOnlyList<StepRow> Steps);

public sealed record ResultRow(string Symbol, string Title, string Detail, Brush Tone, Brush Accent);

public sealed record TxRow(string Id, string Title, string Detail, string UndoLabel, Visibility UndoVisible, Brush Tone, Brush Accent);

public sealed record EventRow(string When, string Category, string Change, Brush RowBg);

public sealed record TileRow(string Name, string Value, string Note, Brush Accent);

public sealed record CheckRow(string Name, string State, Geometry Glyph, Brush Tone);

public sealed record StatusRow(string Name, string Value, Brush Tone, Brush Accent);

/// <summary>
/// The desktop surface. Six views, one engine.
/// </summary>
/// <remarks>
/// <para>
/// No product logic lives here (spec 18.1): every view reads the machine, asks the compiler, the
/// checkup or the transaction engine, and renders the answer. The engine is the same
/// <see cref="PcOrbitHost"/> the CLI composes, so the executor allowlist exists once.
/// </para>
/// <para>
/// Two rules shape all the rendering below. Every string comes from the catalog, so a screenshot
/// in Vietnamese is the product and not a translation of it (spec 21.11). And a value the machine
/// did not give us is drawn as "we could not read this", never as a zero or a plausible guess
/// (spec 6.6, 21.3) — which is why the live tiles have no temperature or fan speed: Windows has no
/// vendor-independent way to read them.
/// </para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The window owns the host for the lifetime of the process and disposes it in OnClosed.")]
public partial class MainWindow : Window
{
    private const int SparkSamples = 60;

    private readonly ILiveMetrics _liveMetrics = new WindowsLiveMetrics();
    private readonly List<double> _cpuHistory = [];
    private readonly List<double> _ramHistory = [];
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private PcOrbitHost? _host;

    /// <summary>
    /// Null until <see cref="OnLoaded"/> has loaded the catalog. WPF raises <c>Checked</c> and
    /// <c>SelectionChanged</c> while <c>InitializeComponent</c> is still running — before any of
    /// this exists — so every handler that formats a string checks <see cref="Ready"/> first.
    /// </summary>
    private IStringCatalog? _strings;
    private StateSnapshot? _snapshot;
    private IReadOnlyList<Finding> _findings = [];
    private LiveMetrics _live = LiveMetrics.None;
    private Plan? _plan;

    public MainWindow()
    {
        InitializeComponent();
        _liveTimer.Tick += OnLiveTick;
    }

    // ---------------------------------------------------------------- helpers

    [MemberNotNullWhen(true, nameof(_strings))]
    private bool Ready => _strings is not null;

    private string T(string key, IReadOnlyDictionary<string, string>? args = null) =>
        _strings is null ? string.Empty : _strings.Format(key, args);

    private static Dictionary<string, string> Args(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);

        foreach ((string key, string value) in pairs)
        {
            result[key] = value;
        }

        return result;
    }

    private static Brush B(string key) => (Brush)Application.Current.Resources[key];

    private static Geometry G(string key) => (Geometry)Application.Current.Resources[key];

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private string Pct(double? value) => value is { } v
        ? v.ToString("0", CultureInfo.CurrentCulture) + "%"
        : T("status.unknown");

    private string Gb(double? value) => value is { } v
        ? v.ToString("0.0", CultureInfo.CurrentCulture) + " GB"
        : T("status.unknown");

    private string Mbps(double? value) => value is { } v
        ? v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture) + " Mbps"
        : T("status.unknown");

    // ---------------------------------------------------------------- window chrome

    private void OnCaptionDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        DragMove();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- startup

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var options = CliOptions.Parse([]);

        IStringCatalog catalog = JsonStringCatalog.LoadForLocale(
            System.IO.Path.Combine(DataLocator.FindDataDirectory(null), "i18n"),
            options.Locale);

        _strings = catalog;
        ApplyStrings();

        _host = await Task.Run(() => PcOrbitHost.Create(options, new WpfSafeApplyConfirmation(catalog)));

        PopulateOutcomes();
        await RescanAsync();
        await RefreshHistoryAsync();

        _liveTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _liveTimer.Stop();
        _host?.Dispose();
    }

    private async void OnLangVi(object sender, RoutedEventArgs e) => await SwitchLanguageAsync("vi");

    private async void OnLangEn(object sender, RoutedEventArgs e) => await SwitchLanguageAsync("en");

    private async Task SwitchLanguageAsync(string locale)
    {
        if (_host is null)
        {
            return;
        }

        _strings = JsonStringCatalog.LoadForLocale(System.IO.Path.Combine(_host.DataDirectory, "i18n"), locale);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(locale);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(locale);

        ApplyStrings();
        PopulateOutcomes();
        RenderMachineCard();
        RenderDashboard();
        RenderStatusView();
        RenderStatusStrip();
        RenderPerf();

        _plan = null;
        PlanResultArea.Visibility = Visibility.Collapsed;
        RunResultCard.Visibility = Visibility.Collapsed;

        await RefreshHistoryAsync();
    }

    private void ApplyStrings()
    {
        Title = T("app.title");
        AppTitle.Text = T("app.title");
        AppTagline.Text = T("app.tagline");
        SearchHint.Text = T("app.search");
        BusyText.Text = T("app.status.scanning");

        NavDashboard.Content = T("app.nav.dashboard");
        NavStatus.Content = T("app.nav.status");
        NavPerf.Content = T("app.nav.perf");
        NavPlan.Content = T("app.nav.plan");
        NavHistory.Content = T("app.nav.history");
        NavOptimize.Content = T("app.nav.optimize");
        NavRepair.Content = T("app.nav.repair");
        NavUpdates.Content = T("app.nav.updates");
        NavHardware.Content = T("app.nav.hardware");
        NavDrivers.Content = T("app.nav.drivers");
        NavApps.Content = T("app.nav.apps");
        NavAutomation.Content = T("app.nav.automation");
        NavBackup.Content = T("app.nav.backup");
        NavTools.Content = T("app.nav.tools");

        DashTitle.Text = T("app.nav.dashboard");
        DashSub.Text = T("app.dash.sub");
        RescanBtn.Content = T("app.rescan");
        HeroLabel.Text = T("app.hero.label");
        HealthTitle.Text = T("app.health.title");
        FindingsSect.Text = T("app.dash.findings");
        OutcomesSect.Text = T("cli.outcomes.heading");

        StatusTitle.Text = T("app.nav.status");
        StatusSub.Text = T("app.status.sub");
        UptimeLabel.Text = T("app.uptime.label");
        OsLabel.Text = T("app.os.label");
        EncryptionLabel.Text = T("cap.security.bitlocker.system-drive");
        HardwareSect.Text = T("app.status.hardware");
        ActivitySect.Text = T("app.status.activity");
        ReadingsTitle.Text = T("cli.scan.heading");
        ReadingsHint.Text = T("app.status.readingsHint");
        DiskTitle.Text = T("app.disk.title");
        DisplayTitle.Text = T("cap.display.current-refresh-rate");

        PerfTitle.Text = T("app.nav.perf");
        PerfSub.Text = T("app.perf.sub");
        LiveChip.Text = T("app.perf.live");
        CpuChartTitle.Text = T("app.perf.cpu");
        RamChartTitle.Text = T("app.perf.ram");
        CpuChartHint.Text = T("app.perf.window");
        RamChartHint.Text = T("app.perf.window");
        PerfHonestyTitle.Text = T("app.perf.honesty.title");
        PerfHonestyBody.Text = T("app.perf.honesty.body");

        PlanTitle.Text = T("app.nav.plan");
        PlanSub.Text = T("app.plan.sub");
        CompileBtn.Content = T("app.plan.compile");
        DryRunBox.Content = T("app.plan.dryRun");
        RecoveryKeyBox.Content = T("app.plan.recoveryKey");
        PlanNotesTitle.Text = T("cli.plan.beforeApply");
        UpdateApplyButtonText();

        HistoryTitle.Text = T("app.nav.history");
        HistorySub.Text = T("app.history.sub");
        HistoryRefreshBtn.Content = T("app.history.refresh");
        TxSect.Text = T("app.history.transactions");
        EventsTitle.Text = T("app.history.events");
        TxEmpty.Text = T("cli.resume.nothing");
        EventsEmpty.Text = T("cli.history.empty");

        SoonBackBtn.Content = T("app.soon.back");
    }

    private void UpdateApplyButtonText()
    {
        if (!Ready)
        {
            return;
        }

        ApplyBtn.Content = DryRunBox.IsChecked == true ? T("app.plan.simulate") : T("app.plan.apply");
    }

    // ---------------------------------------------------------------- navigation

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (!Ready || ViewDashboard is null || sender is not RadioButton nav)
        {
            return;
        }

        ViewDashboard.Visibility = Visibility.Collapsed;
        ViewStatus.Visibility = Visibility.Collapsed;
        ViewPerf.Visibility = Visibility.Collapsed;
        ViewPlan.Visibility = Visibility.Collapsed;
        ViewHistory.Visibility = Visibility.Collapsed;
        ViewSoon.Visibility = Visibility.Collapsed;

        if (ReferenceEquals(nav, NavDashboard))
        {
            ViewDashboard.Visibility = Visibility.Visible;
        }
        else if (ReferenceEquals(nav, NavStatus))
        {
            ViewStatus.Visibility = Visibility.Visible;
        }
        else if (ReferenceEquals(nav, NavPerf))
        {
            ViewPerf.Visibility = Visibility.Visible;
        }
        else if (ReferenceEquals(nav, NavPlan))
        {
            ViewPlan.Visibility = Visibility.Visible;
        }
        else if (ReferenceEquals(nav, NavHistory))
        {
            ViewHistory.Visibility = Visibility.Visible;
        }
        else
        {
            // A module the spec describes but v0.1 does not implement. Saying so plainly beats a
            // screen that looks finished and does nothing (spec 6.6).
            SoonModule.Text = nav.Content as string ?? string.Empty;
            SoonBody.Text = T("app.soon.body");
            SoonPhase.Text = T("app.soon.phase");
            ViewSoon.Visibility = Visibility.Visible;
        }
    }

    private void OnSoonBack(object sender, RoutedEventArgs e) => NavDashboard.IsChecked = true;

    private async Task<bool> BusyAsync(string messageKey, Func<Task> work)
    {
        BusyText.Text = T(messageKey);
        BusyOverlay.Visibility = Visibility.Visible;

        try
        {
            await work();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, T("app.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (PlanChangedException ex)
        {
            MessageBox.Show(this, ex.Message, T("app.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------------------------------------------------------- scan

    private async void OnRescan(object sender, RoutedEventArgs e) => await RescanAsync();

    private async Task RescanAsync()
    {
        if (_host is null)
        {
            return;
        }

        await BusyAsync("app.status.scanning", async () =>
        {
            StateSnapshot snapshot = await Task.Run(() => _host.Scanner.ScanAsync(CancellationToken.None));
            await _host.Snapshots.SaveAsync(snapshot);

            _snapshot = snapshot;
            _findings = PcOrbitHost.Checkup.Run(new CheckupContext(snapshot, _host.Graph, _host.Catalog));
        });

        RenderMachineCard();
        RenderDashboard();
        RenderStatusView();
        RenderStatusStrip();
    }

    // ---------------------------------------------------------------- live gauges

    private void OnLiveTick(object? sender, EventArgs e)
    {
        _live = _liveMetrics.Sample();

        if (_live.CpuPercent is { } cpu)
        {
            Push(_cpuHistory, cpu);
        }

        if (_live.RamPercent is { } ram)
        {
            Push(_ramHistory, ram);
        }

        RenderMachineCard();
        RenderLiveCards();
        RenderPerf();
    }

    private static void Push(List<double> history, double value)
    {
        history.Add(value);

        while (history.Count > SparkSamples)
        {
            history.RemoveAt(0);
        }
    }

    private static void DrawSpark(Polyline line, IReadOnlyList<double> history, double max = 100d)
    {
        double width = line.ActualWidth;
        double height = line.ActualHeight;

        if (history.Count < 2 || width < 4 || height < 4)
        {
            line.Points = [];
            return;
        }

        var points = new PointCollection();
        double step = width / (SparkSamples - 1);
        int offset = SparkSamples - history.Count;

        for (int i = 0; i < history.Count; i++)
        {
            double y = height - Math.Clamp(history[i] / max, 0d, 1d) * (height - 2) - 1;
            points.Add(new Point((offset + i) * step, y));
        }

        line.Points = points;
    }

    // ---------------------------------------------------------------- machine card and strip

    private void RenderMachineCard()
    {
        if (_snapshot is null)
        {
            return;
        }

        MachineIdentity machine = _snapshot.Machine;

        MachineName.Text = machine.DisplayName;
        MachineOs.Text = $"Windows {machine.OsEdition} · {machine.OsBuild}";
        MachineUptime.Text = UptimeText();
        MachineTier.Text = T("cli.header.support", Args(("tier", T(SupportTierResolver.DisplayKey(Tier())))));
    }

    private SupportTier Tier()
    {
        GuideData? guide = _host!.Guides.TryGetValue("guide.firmware.virtualization", out GuideData? g) ? g : null;
        return SupportTierResolver.Resolve(_snapshot!.Machine, _host.Catalog, guide);
    }

    private string UptimeText() => _live.Uptime is { } up
        ? T("app.uptime.value", Args(
            ("days", N(up.Days)),
            ("hours", N(up.Hours)),
            ("minutes", N(up.Minutes))))
        : T("status.unknown");

    private void RenderStatusStrip()
    {
        if (_snapshot is null || _host is null)
        {
            return;
        }

        CapabilityValue encryption = _snapshot.ValueOf(CoreCapabilities.BitLockerSystemDrive);

        StatusStrip.ItemsSource = new List<StatusRow>
        {
            new(T("app.strip.engine"), $"PC Orbit {PcOrbitHost.AppVersion}", B("GoodSoft"), B("Good")),
            new($"Windows {_snapshot.Machine.OsEdition}", T("app.strip.build", Args(("build", N(_snapshot.Machine.OsBuild)))), B("AccentSoft"), B("Accent")),
            new(T("cap.security.bitlocker.system-drive"), EncryptionText(encryption), B("VioletSoft"), B("Violet")),
            new(
                T("app.strip.rights"),
                _host.Elevation.IsElevated ? T("app.strip.admin") : T("app.strip.standard"),
                _host.Elevation.IsElevated ? B("GoodSoft") : B("WarnSoft"),
                _host.Elevation.IsElevated ? B("Good") : B("Warn")),
            new(T("app.strip.checked"), _snapshot.TakenAt.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture), B("CyanSoft"), B("Cyan")),
        };
    }

    private string EncryptionText(CapabilityValue value) => value.Canonical switch
    {
        "on" or "enabled" => T("status.enabled"),
        "off" or "disabled" => T("status.disabled"),
        "suspended" => T("app.encryption.suspended"),
        _ => T("status.unknown"),
    };

    // ---------------------------------------------------------------- dashboard

    private void RenderDashboard()
    {
        if (_snapshot is null || _host is null)
        {
            return;
        }

        HealthVerdict verdict = HealthScore.Evaluate(_findings);

        HealthScoreText.Text = N(verdict.Score);
        HealthLabel.Text = T(verdict.LabelKey);
        HealthNote.Text = _findings.Count == 0 ? T("app.health.clean") : T("app.health.some");
        DrawRing(verdict.Score);

        bool clean = _findings.Count == 0;

        HeroState.Text = clean ? T("app.hero.stable") : T("app.hero.attention");
        HeroBadge.Background = clean ? B("Good") : B("Warn");
        HeroDetail.Text = clean ? T("cli.checkup.ok.body") : T("app.hero.attentionBody");
        HeroFixBtn.Content = clean ? T("app.rescan") : T("app.hero.fix");
        HeroDetailBtn.Content = T("app.hero.details");

        AlertBadge.Visibility = clean ? Visibility.Collapsed : Visibility.Visible;
        AlertCount.Text = N(_findings.Count);

        FindingsSect.Text = clean
            ? T("cli.checkup.ok.title")
            : T("cli.checkup.heading", Args(("count", N(_findings.Count))));

        FindingsList.ItemsSource = _findings.Select(f =>
        {
            bool warn = f.Severity == FindingSeverity.Warning;

            return new FindingRow(
                Title: T(f.TitleKey, f.Arguments),
                Benefit: T(f.BenefitKey, f.Arguments),
                Safety: T(f.SafetyKey, f.Arguments),
                Cost: T("cli.checkup.cost", Args(
                    ("restart", Restart(f.Restart)),
                    ("seconds", N(Math.Max(1, f.EstimatedSeconds))))),
                Evidence: f.Evidence.Source,
                FixLabel: T("app.fix"),
                FixVisible: f.SuggestedOutcomeId is null ? Visibility.Collapsed : Visibility.Visible,
                OutcomeId: f.SuggestedOutcomeId,
                Tone: warn ? B("WarnSoft") : B("AccentSoft"),
                Accent: warn ? B("Warn") : B("Accent"));
        }).ToList();

        HealthChecks.ItemsSource = BuildHealthChecks();
    }

    /// <summary>
    /// The checklist beside the score. Each row is a real capability this build reads, in one of
    /// three states — fine, worth a look, or could not be read. Nothing here is asserted from a
    /// value we do not have.
    /// </summary>
    private List<CheckRow> BuildHealthChecks()
    {
        (string Key, CapabilityId Capability)[] rows =
        [
            ("cap.firmware.cpu.virtualization", CoreCapabilities.FirmwareVirtualization),
            ("cap.display.current-refresh-rate", CoreCapabilities.DisplayCurrentRefreshRate),
            ("cap.memory.current-speed", CoreCapabilities.MemoryCurrentSpeed),
            ("cap.windows.system-restore", CoreCapabilities.SystemRestore),
            ("cap.security.bitlocker.system-drive", CoreCapabilities.BitLockerSystemDrive),
        ];

        List<CheckRow> checks = [];

        foreach ((string key, CapabilityId capability) in rows)
        {
            CapabilityValue value = _snapshot!.ValueOf(capability);
            bool flagged = _findings.Any(f => f.Capability is { } c && c.Equals(capability));

            (string state, Geometry glyph, Brush tone) = !value.IsKnown
                ? (T("status.unknown"), G("IconSearch"), (Brush)B("Faint"))
                : flagged
                    ? (T("status.needsAttention"), G("IconSpark"), (Brush)B("Warn"))
                    : (T("app.health.ok"), G("IconCheck"), (Brush)B("Good"));

            checks.Add(new CheckRow(T(key), state, glyph, tone));
        }

        return checks;
    }

    private void DrawRing(int score)
    {
        const double radius = 48d;
        const double thickness = 10d;
        double circumference = 2 * Math.PI * radius;
        double filled = circumference * score / 100d;

        HealthRing.StrokeDashArray = [filled / thickness, circumference / thickness];
    }

    private void OnHeroFix(object sender, RoutedEventArgs e)
    {
        if (_findings.Count == 0)
        {
            _ = RescanAsync();
            return;
        }

        string? outcomeId = _findings.Select(f => f.SuggestedOutcomeId).FirstOrDefault(id => id is not null);
        SelectOutcome(outcomeId);
        NavPlan.IsChecked = true;
    }

    private void OnHeroDetails(object sender, RoutedEventArgs e) => NavStatus.IsChecked = true;

    // ---------------------------------------------------------------- status view

    private void RenderStatusView()
    {
        if (_snapshot is null)
        {
            return;
        }

        StateSnapshot snapshot = _snapshot;

        OsValue.Text = $"Windows {snapshot.Machine.OsEdition}";
        OsBuild.Text = T("app.strip.build", Args(("build", N(snapshot.Machine.OsBuild))));

        CapabilityValue encryption = snapshot.ValueOf(CoreCapabilities.BitLockerSystemDrive);
        EncryptionValue.Text = EncryptionText(encryption);
        EncryptionNote.Text = encryption.IsKnown
            ? snapshot.ReadingOf(CoreCapabilities.BitLockerSystemDrive)?.Evidence.Source ?? string.Empty
            : T("app.status.needsAdmin");

        CpuName.Text = snapshot.Machine.CpuName;

        CapabilityValue ramSpeed = snapshot.ValueOf(CoreCapabilities.MemoryCurrentSpeed);
        RamSpeed.Text = ramSpeed.Status == CapabilityStatus.Value ? $"{ramSpeed.Raw} MT/s" : T("status.unknown");

        DiskName.Text = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

        CapabilityValue currentHz = snapshot.ValueOf(CoreCapabilities.DisplayCurrentRefreshRate);
        CapabilityValue maxHz = snapshot.ValueOf(CoreCapabilities.DisplayMaxRefreshRate);

        DisplayValue.Text = currentHz.Status == CapabilityStatus.Value ? $"{currentHz.Raw} Hz" : T("status.unknown");
        DisplayMax.Text = maxHz.Status == CapabilityStatus.Value
            ? T("app.display.max", Args(("max", maxHz.Raw ?? string.Empty)))
            : T("status.unknown");
        DisplayNote.Text = currentHz.Satisfies(maxHz) ? T("app.display.atBest") : T("app.display.below");

        ReadingsList.ItemsSource = snapshot.Readings
            .Select((r, index) => new ReadingRow(
                Name: CapabilityName(r.Capability),
                Value: Status(r.Value) + Unit(r.Capability),
                Source: r.Evidence.Source,
                Accent: r.Value.IsKnown ? B("Ink") : B("Faint"),
                RowBg: index % 2 == 0 ? B("RowBg") : Brushes.Transparent))
            .ToList();

        RenderLiveCards();
    }

    private void RenderLiveCards()
    {
        if (_snapshot is null)
        {
            return;
        }

        UptimeValue.Text = UptimeText();

        CpuValue.Text = Pct(_live.CpuPercent);
        DrawSpark(CpuSpark, _cpuHistory);

        RamValue.Text = Pct(_live.RamPercent);
        RamBar.Value = _live.RamPercent ?? 0d;
        RamDetail.Text = _live.RamTotalGb is null
            ? T("status.unknown")
            : T("app.ram.detail", Args(("used", Gb(_live.RamUsedGb)), ("total", Gb(_live.RamTotalGb))));

        DiskValue.Text = Pct(_live.DiskPercent);
        DiskBar.Value = _live.DiskPercent ?? 0d;
        DiskDetail.Text = _live.DiskTotalGb is null
            ? T("status.unknown")
            : T("app.disk.detail", Args(("free", Gb(_live.DiskFreeGb)), ("total", Gb(_live.DiskTotalGb))));

        ActivityTiles.ItemsSource = BuildActivityTiles();
    }

    private List<TileRow> BuildActivityTiles()
    {
        List<TileRow> tiles =
        [
            new(
                T("app.tile.network"),
                _live.NetworkLinkMbps is null ? T("status.unknown") : Mbps(_live.NetworkLinkMbps),
                _live.NetworkAdapter ?? T("status.unknown"),
                B("Cyan")),
            new(
                T("app.tile.throughput"),
                _live.NetworkDownMbps is null ? T("status.unknown") : Mbps(_live.NetworkDownMbps),
                T("app.tile.up", Args(("up", Mbps(_live.NetworkUpMbps)))),
                B("Accent")),
            new(
                T("app.tile.power"),
                _live.OnBattery switch
                {
                    true => T("cap.power.on-battery"),
                    false => T("app.tile.mains"),
                    null => T("status.unknown"),
                },
                _live.BatteryPercent is { } percent ? $"{percent}%" : T("app.tile.noBattery"),
                B("Good")),
            new(
                T("app.tile.thermal"),
                T("status.unknown"),
                T("app.tile.thermalWhy"),
                B("Faint")),
            new(
                T("app.tile.fan"),
                T("status.unknown"),
                T("app.tile.fanWhy"),
                B("Faint")),
        ];

        return tiles;
    }

    // ---------------------------------------------------------------- performance view

    private void RenderPerf()
    {
        CpuChartValue.Text = Pct(_live.CpuPercent);
        RamChartValue.Text = Pct(_live.RamPercent);

        DrawSpark(CpuChart, _cpuHistory);
        DrawSpark(RamChart, _ramHistory);

        PerfTiles.ItemsSource = new List<TileRow>
        {
            new(T("app.perf.uptime"), UptimeText(), T("app.perf.sinceBoot"), B("Accent")),
            new(T("app.tile.network"), Mbps(_live.NetworkLinkMbps), _live.NetworkAdapter ?? T("status.unknown"), B("Cyan")),
            new(T("app.perf.down"), Mbps(_live.NetworkDownMbps), T("app.perf.live"), B("Good")),
            new(T("app.perf.up"), Mbps(_live.NetworkUpMbps), T("app.perf.live"), B("Violet")),
            new(T("app.disk.title"), Pct(_live.DiskPercent), Gb(_live.DiskFreeGb), B("Orange")),
        };
    }

    // ---------------------------------------------------------------- plan view

    private void PopulateOutcomes()
    {
        if (_host is null)
        {
            return;
        }

        List<OutcomeRow> rows = _host.Outcomes
            .Select(o => new OutcomeRow(o, T(o.TitleKey), T(o.DescriptionKey), o.Id, T("app.fix")))
            .ToList();

        OutcomeBox.ItemsSource = rows;
        OutcomeBox.SelectedIndex = 0;
        OutcomeCards.ItemsSource = rows;
    }

    private void SelectOutcome(string? outcomeId)
    {
        if (outcomeId is null)
        {
            return;
        }

        OutcomeRow? row = OutcomeBox.Items.OfType<OutcomeRow>().FirstOrDefault(o => o.Outcome.Id == outcomeId);

        if (row is not null)
        {
            OutcomeBox.SelectedItem = row;
        }
    }

    private void OnFindingFix(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string outcomeId })
        {
            SelectOutcome(outcomeId);
        }

        NavPlan.IsChecked = true;
    }

    private void OnOutcomeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!Ready)
        {
            return;
        }

        if (OutcomeBox.SelectedItem is OutcomeRow row)
        {
            OutcomeDescription.Text = row.Description;
        }

        // The plan belongs to the outcome it was compiled for; keeping it on screen after the
        // selection changes would invite applying the wrong one.
        _plan = null;
        PlanResultArea.Visibility = Visibility.Collapsed;
        RunResultCard.Visibility = Visibility.Collapsed;
    }

    private void OnApplyModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateApplyButtonText();

        if (Ready && _plan is not null)
        {
            RenderPlan();
        }
    }

    private void OnRecoveryAck(object sender, RoutedEventArgs e)
    {
        if (Ready && _plan is not null)
        {
            RenderPlan();
        }
    }

    private async void OnCompile(object sender, RoutedEventArgs e)
    {
        if (_host is null || OutcomeBox.SelectedItem is not OutcomeRow row)
        {
            return;
        }

        RunResultCard.Visibility = Visibility.Collapsed;
        OutcomeDescription.Text = row.Description;

        // Reading this machine takes seconds, not milliseconds — a full WMI inventory. Rescanning
        // on every compile would mean a ten-second wait to look at a plan, so a snapshot this
        // recent is reused and the plan says which read it was built from. "Kiểm tra lại" forces a
        // fresh one, and apply verifies against the machine regardless (spec 6.1, 8.3.2).
        bool fresh = _snapshot is { } existing
            && _host.Clock.Now - existing.TakenAt < TimeSpan.FromMinutes(2);

        await BusyAsync(fresh ? "app.plan.compilingFast" : "app.plan.compiling", async () =>
        {
            if (!fresh)
            {
                StateSnapshot scanned = await Task.Run(() => _host.Scanner.ScanAsync(CancellationToken.None));
                await _host.Snapshots.SaveAsync(scanned);

                _snapshot = scanned;
                _findings = PcOrbitHost.Checkup.Run(new CheckupContext(scanned, _host.Graph, _host.Catalog));
            }

            StateSnapshot snapshot = _snapshot!;
            _plan = await Task.Run(() => _host.CreateCompiler().Compile(snapshot, row.Outcome));
        });

        RenderDashboard();
        RenderStatusView();
        RenderPlan();
    }

    private PreflightReport? RunPreflight()
    {
        if (_host is null || _plan is null || _snapshot is null)
        {
            return null;
        }

        HashSet<string> acknowledgements = [];

        if (RecoveryKeyBox.IsChecked == true)
        {
            acknowledgements.Add(PreflightContext.Ack.BitLockerKeyConfirmed);
        }

        return PreflightRunner.Default.Run(
            new PreflightContext(_plan, _snapshot, _host.Elevation.IsElevated, acknowledgements));
    }

    private void RenderPlan()
    {
        if (_host is null || _plan is null)
        {
            return;
        }

        Plan plan = _plan;
        PlanResultArea.Visibility = Visibility.Visible;

        PlanBasedOn.Text = _snapshot is { } basis
            ? T("app.plan.basedOn", Args(("time", basis.TakenAt.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture))))
            : string.Empty;

        if (plan.Outlook == PlanOutlook.AlreadySatisfied)
        {
            PlanCost.Text = T("plan.outcome.alreadyReached");
            PlanIrreversible.Text = string.Empty;
            PlanHash.Text = string.Empty;
            PlanNotesCard.Visibility = Visibility.Collapsed;
            PhasesList.ItemsSource = null;
            ApplyBtn.IsEnabled = false;
            return;
        }

        PlanCost.Text = T("plan.cost.header", Args(
            ("changes", N(plan.Cost.Changes)),
            ("restarts", N(plan.Cost.Restarts)),
            ("minutes", N(plan.Cost.EstimatedMinutes)),
            ("manual", N(plan.Cost.ManualSteps))));

        PlanIrreversible.Text = plan.Cost.IrreversibleChanges > 0
            ? T("plan.cost.irreversible", Args(("count", N(plan.Cost.IrreversibleChanges))))
            : string.Empty;

        PlanHash.Text = T("cli.plan.hash", Args(
            ("hash", plan.Hash[..16]),
            ("graph", plan.GraphVersion),
            ("rules", plan.RuleVersion)));

        PreflightReport? preflight = RunPreflight();
        List<string> notes = [];

        if (plan.Outlook == PlanOutlook.NotReachable)
        {
            notes.Add(T("plan.outcome.notReachable"));
        }

        notes.AddRange(plan.Issues
            .Where(i => i.Severity != PlanIssueSeverity.Info)
            .Select(i => i.Detail));

        if (preflight is not null)
        {
            notes.AddRange(preflight.Findings
                .OrderByDescending(f => f.Verdict)
                .Select(f => T(f.MessageKey, f.Arguments)));
        }

        PlanNotes.Text = string.Join(Environment.NewLine + Environment.NewLine, notes);
        PlanNotesCard.Visibility = notes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        PhasesList.ItemsSource = plan.Phases.Select(phase => new PhaseRow(
            Header: T("cli.plan.stage", Args(("number", N(phase.Index + 1))))
                + (phase.RestartAfter == RestartKind.None
                    ? string.Empty
                    : "  ·  " + T("cli.plan.thenRestart", Args(("restart", Restart(phase.RestartAfter))))),
            Steps: phase.Steps.Select(step => new StepRow(
                Title: $"{step.Ordinal + 1}. {T(step.Action.TitleKey)}",
                Change: $"{CapabilityName(step.Capability)}:  {Status(step.CurrentValue)}  →  {Status(step.DesiredValue)}",
                Why: T("cli.plan.why", Args(("reason", WhyOf(step)))),
                Meta: T("cli.plan.stepMeta", Args(
                    ("mode", T($"cli.writeMode.{Camel(step.Action.WriteMode.ToString())}")),
                    ("risk", T($"cli.risk.{Camel(step.Action.Risk.ToString())}")),
                    ("reversible", T($"cli.reversible.{Camel(step.Action.Reversible.Mode.ToString())}")),
                    ("restart", Restart(step.Action.Restart)))))).ToList())).ToList();

        bool blockedForReal = plan.HasBlockers || (preflight?.IsBlocked ?? false);
        ApplyBtn.IsEnabled = !plan.HasBlockers && (DryRunBox.IsChecked == true || !blockedForReal);
    }

    private string WhyOf(PlanStep step) => step.RequiredByOutcome
        ? T("cli.plan.why.asked")
        : step.RequiredBy.Count > 0
            ? T("cli.plan.why.neededBy", Args(("capabilities", string.Join(", ", step.RequiredBy.Select(CapabilityName)))))
            : T("cli.plan.why.part");

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_host is null || _plan is null)
        {
            return;
        }

        Plan plan = _plan;
        ExecutionMode mode = DryRunBox.IsChecked == true ? ExecutionMode.DryRun : ExecutionMode.Apply;
        PreflightReport? preflight = RunPreflight();

        if (plan.HasBlockers || (mode == ExecutionMode.Apply && (preflight?.IsBlocked ?? false)))
        {
            MessageBox.Show(this, T("cli.apply.blocked"), T("app.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (mode == ExecutionMode.Apply
            && MessageBox.Show(
                this,
                T("cli.apply.confirm", Args(("count", N(plan.Cost.Changes)))),
                T("app.title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        Transaction? result = null;

        bool ok = await BusyAsync(mode == ExecutionMode.DryRun ? "app.plan.simulating" : "app.plan.applying", async () =>
        {
            Transaction transaction = _host.Engine.Begin(plan, mode, plan.Hash);
            result = await Task.Run(() => _host.Engine.RunAsync(transaction, _host.Transactions, _host.Events));
        });

        if (ok && result is not null)
        {
            RenderRunResult(result);
            await RescanAsync();
            await RefreshHistoryAsync();
        }
    }

    private void RenderRunResult(Transaction result)
    {
        RunResultCard.Visibility = Visibility.Visible;

        TransactionTally tally = result.Tally;

        ResultHeading.Text = T("cli.result.heading", Args(("state", T($"transaction.state.{Camel(result.State.ToString())}"))));

        ResultCounts.Text =
            $"{T("cli.result.completed")}: {tally.Completed}      {T("cli.result.failed")}: {tally.Failed}      " +
            $"{T("cli.result.pending")}: {tally.Pending}" +
            (result.OutcomeVerdictKey is { } verdict ? $"      {T("cli.result.outcome")}: {T(verdict)}" : string.Empty);

        ResultList.ItemsSource = result.Steps.Select(step =>
        {
            (Brush tone, Brush accent) = step.State switch
            {
                StepState.Verified or StepState.Applied => (B("GoodSoft"), B("Good")),
                StepState.Failed or StepState.VerifyFailed => ((Brush)new SolidColorBrush(Color.FromRgb(0xFD, 0xEA, 0xE7)), B("Bad")),
                StepState.AwaitingRestart or StepState.AwaitingUserAction => (B("WarnSoft"), B("Warn")),
                _ => (B("Hair"), B("Muted")),
            };

            return new ResultRow(
                Symbol: StepSymbol(step.State),
                Title: ActionTitle(step.ActionId) + (step.MessageKey is { } key ? $" — {T(key)}" : string.Empty),
                Detail: step.Actual is { } actual
                    ? T("cli.result.stepActual", Args(
                        ("capability", CapabilityName(step.Capability)),
                        ("before", Status(step.Before)),
                        ("requested", Status(step.Requested)),
                        ("actual", Status(actual))))
                    : T("cli.result.step", Args(
                        ("capability", CapabilityName(step.Capability)),
                        ("before", Status(step.Before)),
                        ("requested", Status(step.Requested)))),
                Tone: tone,
                Accent: accent);
        }).ToList();

        ResultNext.Text = result.NeedsRestart
            ? T("cli.result.next", Args(("restart", Restart(result.PendingRestart)))) + "  " + T("cli.result.noExpiry")
            : result.Mode == ExecutionMode.DryRun ? T("cli.result.dryRun") : string.Empty;
    }

    // ---------------------------------------------------------------- history view

    private async void OnHistoryRefresh(object sender, RoutedEventArgs e) => await RefreshHistoryAsync();

    private async Task RefreshHistoryAsync()
    {
        if (_host is null)
        {
            return;
        }

        IReadOnlyList<Transaction> transactions = await _host.Transactions.ListRecentAsync(15);
        IReadOnlyList<ChangeEvent> events = await _host.Events.QueryAsync(new EventQuery(Limit: 60));

        TxList.ItemsSource = transactions.Select(tx =>
        {
            bool undoable = CanUndo(tx);

            return new TxRow(
                Id: tx.Id,
                Title: $"{OutcomeTitle(tx.Plan.OutcomeId)}  ·  {T($"transaction.state.{Camel(tx.State.ToString())}")}",
                Detail: $"{tx.CreatedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture)}      " +
                    $"{T("cli.result.completed")}: {tx.Tally.Completed}   {T("cli.result.failed")}: {tx.Tally.Failed}   " +
                    $"{T("cli.result.pending")}: {tx.Tally.Pending}" +
                    (tx.Mode == ExecutionMode.DryRun ? $"      {T("action.dry-run")}" : string.Empty),
                UndoLabel: T("app.history.undo"),
                UndoVisible: undoable ? Visibility.Visible : Visibility.Collapsed,
                Tone: tx.Kind == TransactionKind.Undo ? B("VioletSoft") : B("AccentSoft"),
                Accent: tx.Kind == TransactionKind.Undo ? B("Violet") : B("Accent"));
        }).ToList();

        TxEmpty.Visibility = transactions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        EventsList.ItemsSource = events.Select((ev, index) => new EventRow(
            When: ev.Timestamp.LocalDateTime.ToString("dd/MM HH:mm", CultureInfo.CurrentCulture),
            Category: ev.Category.ToString(),
            Change: $"{CapabilityName(CapabilityId.Parse(ev.Component))}: {ev.Before ?? "·"} → {ev.After ?? "·"}",
            RowBg: index % 2 == 0 ? B("RowBg") : Brushes.Transparent)).ToList();

        EventsEmpty.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool CanUndo(Transaction tx) =>
        tx.Kind == TransactionKind.Apply
        && tx.State != TransactionState.RolledBack
        && tx.Mode == ExecutionMode.Apply
        && tx.Steps.Any(s => s.State is StepState.Applied
            or StepState.Verified
            or StepState.AwaitingRestart
            or StepState.AwaitingUserAction
            or StepState.VerifyFailed);

    private async void OnUndoTransaction(object sender, RoutedEventArgs e)
    {
        if (_host is null || sender is not Button { Tag: string id })
        {
            return;
        }

        Transaction? source = await _host.Transactions.LoadAsync(id);

        if (source is null)
        {
            return;
        }

        UndoPlan undo = new UndoPlanner(_host.Clock).Plan(source);

        if (undo.Plan is null)
        {
            MessageBox.Show(
                this,
                T(undo.Excluded.Count > 0 ? "cli.undo.noAutomatic" : "cli.undo.nothingChanged"),
                T("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string question = T("cli.undo.confirm", Args(("count", N(undo.Plan.Cost.Changes))));

        if (MessageBox.Show(this, question, T("app.title"), MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            return;
        }

        Transaction? result = null;

        bool ok = await BusyAsync("app.history.undoing", async () =>
        {
            Transaction transaction = _host.Engine.BeginUndo(undo.Plan, source, ExecutionMode.Apply, undo.Plan.Hash);
            result = await Task.Run(() => _host.Engine.RunAsync(transaction, _host.Transactions, _host.Events));

            if (result.IsFinished)
            {
                await _host.Engine.ReconcileUndoAsync(result, _host.Transactions, _host.Events);
            }
        });

        if (ok && result is not null)
        {
            if (result.NeedsRestart)
            {
                MessageBox.Show(
                    this,
                    T("cli.result.next", Args(("restart", Restart(result.PendingRestart)))),
                    T("app.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            await RescanAsync();
            await RefreshHistoryAsync();
        }
    }

    // ---------------------------------------------------------------- shared formatting

    private string CapabilityName(CapabilityId id)
    {
        CapabilityNode? node = _host?.Graph.Node(id);
        return node is null ? id.Value : T(node.DisplayKey);
    }

    private string Unit(CapabilityId id)
    {
        CapabilityNode? node = _host?.Graph.Node(id);
        return node?.Unit is { } unit ? " " + unit : string.Empty;
    }

    private string OutcomeTitle(string outcomeId)
    {
        const string undoPrefix = "undo:";

        if (outcomeId.StartsWith(undoPrefix, StringComparison.Ordinal))
        {
            return $"{T("app.history.undo")} · {OutcomeTitle(outcomeId[undoPrefix.Length..])}";
        }

        Outcome? outcome = _host?.Outcomes.FirstOrDefault(o => o.Id == outcomeId);
        return outcome is null ? outcomeId : T(outcome.TitleKey);
    }

    private string ActionTitle(string actionId)
    {
        ActionDefinition? action = _host?.Catalog.ById(actionId);
        return action is null ? actionId : T(action.TitleKey);
    }

    private string Status(CapabilityValue value) => value.Status switch
    {
        CapabilityStatus.Value => value.Raw ?? T("status.unknown"),
        CapabilityStatus.Supported => T("status.supported"),
        CapabilityStatus.NotSupported => T("status.notSupported"),
        CapabilityStatus.Enabled => T("status.enabled"),
        CapabilityStatus.Disabled => T("status.disabled"),
        CapabilityStatus.Present => T("status.present"),
        CapabilityStatus.Absent => T("status.absent"),
        _ => T("status.unknown"),
    };

    private string Restart(RestartKind kind) => T($"cli.restart.{Camel(kind.ToString())}");

    private static string Camel(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string StepSymbol(StepState state) => state switch
    {
        StepState.Verified => "✓",
        StepState.Applied => "●",
        StepState.Skipped => "–",
        StepState.AwaitingRestart => "⏱",
        StepState.AwaitingUserAction => "☝",
        StepState.VerifyFailed => "?",
        StepState.Failed => "✕",
        StepState.RolledBack => "↩",
        _ => "·",
    };
}

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
using PcOrbit.Core.Navigation;
using PcOrbit.Core.Outcomes;
using PcOrbit.Core.Preflight;
using PcOrbit.Core.Transactions;

namespace PcOrbit.App;

// ---------------------------------------------------------------- row models

public sealed record ReadingRow(string Name, string Detail, string Value, string Glyph, Brush Tone, Brush Accent);

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
    Brush Accent,
    string Code = "",
    string RouteLabel = "",
    Visibility RouteVisible = Visibility.Collapsed);

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

public sealed record GaugeRow(
    string Name,
    string Value,
    string Note,
    double Percent,
    string Glyph,
    Brush Tone,
    Brush Accent);

/// <param name="Icon">The executable's own icon, or null — in which case the letter tile shows.</param>
public sealed record ProcRow(
    string Name,
    string Pid,
    string Initial,
    ImageSource? Icon,
    Visibility LetterVisible,
    string Cpu,
    double CpuPercent,
    string Ram,
    Brush Tone,
    Brush Accent,
    Brush RowBg);

public sealed record DeviceRow(string Name, string Detail, string Glyph, Brush Tone, Brush Accent);

public sealed record DeviceGroupRow(string Group, IReadOnlyList<DeviceRow> Items);

public sealed record DriveRow(string Name, string Free, double Percent, Brush Accent);

public sealed record StartupRow(string Name, string Detail, string Initial, Brush Tone, Brush Accent);

public sealed record LabelRow(string Name, string Value);

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
    private readonly IProcessMonitor _processes = new WindowsProcessMonitor();
    private readonly IHardwareInventory _hardware = new WindowsHardwareInventory();
    private readonly ISystemSummary _summaryReader = new WindowsSystemSummary();
    private readonly List<double> _cpuHistory = [];
    private readonly List<double> _ramHistory = [];
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private HardwareInventory _inventory = HardwareInventory.Empty;
    private SystemSummary _summary = SystemSummary.Empty;

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
    private int _tick;
    private IReadOnlyList<Section>? _sections;

    /// <summary>The page on screen, and the section it belongs to.</summary>
    private Section? _section;
    private Page? _page;

    /// <summary>
    /// Which page each section was last left on, keyed by section.
    /// </summary>
    /// <remarks>
    /// Coming back to Tune-up and landing on Drivers because that is where you were is the
    /// difference between navigation and a reset button. Deliberately not persisted across runs:
    /// on a fresh launch the first tab of a section is the one that introduces it.
    /// </remarks>
    private readonly Dictionary<string, string> _lastPage = new(StringComparer.Ordinal);

    /// <summary>
    /// Set while navigation is moving the controls itself, so a programmatic
    /// <c>IsChecked</c> does not come back round as a user's click.
    /// </summary>
    private bool _switching;

    /// <summary>What the app remembers between runs: language, window, whether to elevate.</summary>
    private readonly AppSettings _settings = AppSettings.Load();

    private string _locale = "vi";

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

    /// <summary>A Segoe Fluent glyph from the resources, by key.</summary>
    private static string Gl(string key) => (string)Application.Current.Resources[key];

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

    /// <summary>
    /// The first moment the window has a handle, which is the earliest DWM can be told anything
    /// about it. Done here rather than in <c>OnLoaded</c> so the corners are already round in the
    /// frame the window first appears in, instead of squaring off for one frame and then snapping.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RoundedWindow.Apply(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var options = CliOptions.Parse(_settings.Locale is { } remembered ? ["--lang", remembered] : []);

        _locale = options.Locale;

        RestoreWindow();

        IStringCatalog catalog = JsonStringCatalog.LoadForLocale(
            System.IO.Path.Combine(DataLocator.FindDataDirectory(null), "i18n"),
            options.Locale);

        _strings = catalog;
        ApplyStrings();

        _host = await Task.Run(() => PcOrbitHost.Create(options, new WpfSafeApplyConfirmation(catalog)));

        ElevationLog.Note($"window up, host={Environment.ProcessPath} assembly={System.Reflection.Assembly.GetEntryAssembly()?.Location}");

        // Someone who lives in the BIOS page asked to skip the button. Windows still asks them, and
        // a decline means carrying on as a standard user, which is a complete app too.
        if (_settings.AlwaysElevate && !_host.Elevation.IsElevated && TryElevate(fromButton: false))
        {
            return;
        }

        AlwaysElevateBox.IsChecked = _settings.AlwaysElevate;

        PopulateOutcomes();
        RenderMissions();

        // Two more read-only batches. Started alongside the state scan rather than after it, so
        // the wait stays the length of the slowest one instead of their sum.
        Task<HardwareInventory> hardware = _hardware.ReadAsync();
        Task<SystemSummary> summary = _summaryReader.ReadAsync();

        await LoadOrScanAsync();

        _inventory = await hardware;
        _summary = await summary;

        RenderInventory();

        await RefreshHistoryAsync();

        _liveTimer.Start();

        // Now that the window is interactive and the first scan is in, fill the rest of the pages
        // quietly, so that changing tab is not the moment the work starts.
        _ = WarmPagesAsync();
    }

    /// <summary>
    /// Minimised: the cheapest moment to give memory back. Maximised: keep the edges on the screen.
    /// </summary>
    /// <remarks>
    /// A <c>WindowStyle="None"</c> window maximises to the monitor <em>plus</em> its invisible resize
    /// border on every side, so the bottom seven pixels of the sidebar — where the language chips
    /// live — were painted below the screen. The border's thickness is padded back in while
    /// maximised, so the content is where the window appears to be.
    /// </remarks>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Minimized)
        {
            WorkingSet.Trim();
        }

        Root.Margin = WindowState == WindowState.Maximized
            ? new Thickness(
                SystemParameters.WindowResizeBorderThickness.Left + SystemParameters.WindowNonClientFrameThickness.Left - 1,
                SystemParameters.WindowResizeBorderThickness.Top + SystemParameters.WindowNonClientFrameThickness.Top - 1,
                SystemParameters.WindowResizeBorderThickness.Right + SystemParameters.WindowNonClientFrameThickness.Right - 1,
                SystemParameters.WindowResizeBorderThickness.Bottom + SystemParameters.WindowNonClientFrameThickness.Bottom - 1)
            : new Thickness(0);
    }

    /// <summary>Puts the window back where it was closed, as long as that is still on a screen.</summary>
    private void RestoreWindow()
    {
        if (_settings.WindowWidth is { } w && _settings.WindowHeight is { } h && w >= MinWidth && h >= MinHeight)
        {
            Width = w;
            Height = h;
        }

        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top
            && left >= SystemParameters.VirtualScreenLeft
            && top >= SystemParameters.VirtualScreenTop
            && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 200
            && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (_settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// The two shortcuts every desktop app is expected to have: Ctrl+F finds, F5 refreshes.
    /// </summary>
    /// <remarks>
    /// Ctrl+F goes to whichever search box the current page has, so it does the right thing on
    /// BIOS and on the store and nothing elsewhere. F5 is the same as the button in the header.
    /// </remarks>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == System.Windows.Input.Key.K
            && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            FocusMissionSearch();
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.F5)
        {
            OnRescan(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.F
            && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0
            && _page is not null)
        {
            TextBox? search = ReferenceEquals(_page.View, ViewBios) ? FirmwareSearch
                : ReferenceEquals(_page.View, ViewApps) ? AppsSearch
                : null;

            if (search is not null)
            {
                search.Focus();
                search.SelectAll();
                e.Handled = true;
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _settings.Locale = _locale;
        _settings.LastSection = _section?.TitleKey;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;

        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
        }

        _settings.Save();

        _liveTimer.Stop();
        _host?.Dispose();
    }

    private async void OnLangVi(object sender, RoutedEventArgs e) => await SwitchLanguageAsync("vi");

    private async void OnLangEn(object sender, RoutedEventArgs e) => await SwitchLanguageAsync("en");

    private void OnAlwaysElevateChanged(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysElevate = AlwaysElevateBox.IsChecked == true;
        _settings.Save();
    }

    /// <summary>The chip for the language in use is the lit one. Set from the fact, not from the click.</summary>
    private void ShowLanguage()
    {
        LangVi.IsChecked = _locale == "vi";
        LangEn.IsChecked = _locale == "en";
    }

    private async Task SwitchLanguageAsync(string locale)
    {
        if (_host is null || locale == _locale)
        {
            ShowLanguage();
            return;
        }

        _locale = locale;
        _settings.Locale = locale;
        _settings.Save();

        _strings = JsonStringCatalog.LoadForLocale(System.IO.Path.Combine(_host.DataDirectory, "i18n"), locale);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(locale);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(locale);

        ApplyStrings();
        PopulateOutcomes();
        RenderMissions();
        RenderMachineCard();
        RenderDashboard();
        RenderStatusView();
        RenderInventory();
        RenderDashGauges();
        RenderProcesses();
        RenderPerf();

        InvalidatePages();
        await ReloadCurrentPageAsync();

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
        BusyText.Text = T("app.status.scanning");



        foreach (Section section in Sections)
        {
            section.Nav.Content = T(section.TitleKey);
        }

        // First run lands on the section the rail already has checked, or on the one the window was
        // closed on last time. The page inside it is the first — the one that introduces it.
        if (_section is null)
        {
            _section = Sections.FirstOrDefault(s => s.TitleKey == _settings.LastSection) ?? Sections[0];
            _page = _section.Pages[0];

            _switching = true;

            try
            {
                _section.Nav.IsChecked = true;
            }
            finally
            {
                _switching = false;
            }

            foreach (Page other in AllPages)
            {
                other.View.Visibility = ReferenceEquals(other, _page) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // The pivot's labels are built, not bound, so a language switch has to rebuild them.
        BuildSubNav(_section, _page);

        AppVersionLabel.Text = "v" + PcOrbitHost.AppVersion;
        TopRescan.Content = T("app.rescan");
        LangVi.Content = T("app.lang.vi");
        LangEn.Content = T("app.lang.en");
        AlwaysElevateBox.Content = T("app.alwaysElevate");
        AlwaysElevateBox.ToolTip = T("app.alwaysElevate.hint");
        ShowLanguage();
        ApplyPageChrome();

        HeroLabel.Text = T("app.hero.label");
        HealthTitle.Text = T("app.health.title");
        FindingsSect.Text = T("app.dash.findings");
        OutcomesSect.Text = T("cli.outcomes.heading");

        ReadingsTitle.Text = T("cli.scan.heading");

        LiveChip.Text = T("app.perf.live");
        CpuChartTitle.Text = T("app.perf.cpu");
        RamChartTitle.Text = T("app.perf.ram");
        CpuChartHint.Text = T("app.perf.window");
        RamChartHint.Text = T("app.perf.window");
        PerfHonestyTitle.Text = T("app.perf.honesty.title");
        PerfHonestyBody.Text = T("app.perf.honesty.body");

        CompileBtn.Content = T("app.plan.compile");
        DryRunBox.Content = T("app.plan.dryRun");
        RecoveryKeyBox.Content = T("app.plan.recoveryKey");
        PlanNotesTitle.Text = T("cli.plan.beforeApply");
        UpdateApplyButtonText();

        TxSect.Text = T("app.history.transactions");
        TxEmpty.Text = T("cli.resume.nothing");

        ApplyPageStrings();

        ProcTitle.Text = T("app.processes.title");
        ProcHint.Text = T("app.processes.hint");
        ProcColName.Text = T("app.processes.name");
        ProcColCpu.Text = T("app.processes.cpu");
        ProcColRam.Text = T("app.processes.ram");
        ProcEmpty.Text = T("app.processes.none");
        PerfProcTitle.Text = T("app.processes.title");
        PerfProcHint.Text = T("app.processes.hint");

        DevTitle.Text = T("app.devices.title");
        DevHint.Text = T("app.status.readingsHint");
        DevEmpty.Text = T("app.devices.none");
        InventoryTitle.Text = T("app.status.hardware");
        InventoryHint.Text = T("app.hw.reading");

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

    /// <summary>
    /// The rail, and the pages inside each of its destinations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Twelve flat destinations was one per page, which is not navigation so much as a list of
    /// everything the app can do. Five sections, each holding the pages that answer the same
    /// question, is: the rail says <em>which part of my PC</em>, and the pivot inside the page says
    /// <em>which view of it</em>. Nothing was removed - every page that was in the rail is still
    /// reachable, one level in.
    /// </para>
    /// <para>
    /// A table rather than a chain of <c>if</c>s, for the reason the flat version was one: with the
    /// list in a single place, a page with no view cannot be added to the rail by accident, and the
    /// pivot cannot disagree with what the rail leads to.
    /// </para>
    /// </remarks>
    private IReadOnlyList<Section> Sections => _sections ??=
    [
        new(NavOverview, "app.section.overview",
        [
            new(ViewDashboard, "app.section.overview", "app.dash.sub"),
        ]),

        new(NavMachine, "app.section.machine",
        [
            new(ViewStatus, "app.tab.status", "app.status.sub"),
            new(ViewHardware, "app.tab.hardware", "app.hardware.sub"),
            new(ViewPerf, "app.tab.perf", "app.perf.sub"),
            new(ViewBios, "app.tab.bios", "app.bios.sub"),
        ]),

        new(NavProtect, "app.section.protect",
        [
            new(ViewSecurity, "app.tab.security", "app.security.sub"),
            new(ViewRecovery, "app.tab.recovery", "app.recovery.sub"),
        ]),

        new(NavTune, "app.section.tune",
        [
            new(ViewCleanup, "app.tab.cleanup", "app.cleanup.sub"),
            new(ViewStartup, "app.tab.startup", "app.startupPage.sub"),
            new(ViewDrivers, "app.tab.drivers", "app.drivers.sub"),
        ]),

        new(NavApps, "app.section.apps",
        [
            new(ViewApps, "app.tab.apps", "app.section.apps.sub"),
            new(ViewOffice, "app.tab.office", "app.office.planHint"),
            new(ViewWindows, "app.tab.windows", "app.edition.hint"),
        ]),

        new(NavChanges, "app.section.changes",
        [
            new(ViewPlan, "app.tab.plan", "app.plan.sub"),
            new(ViewTimeline, "app.tab.timeline", "app.timeline.sub"),
            new(ViewCompare, "app.tab.compare", "app.compare.sub"),
        ]),
    ];

    private IEnumerable<Page> AllPages => Sections.SelectMany(s => s.Pages);

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (_switching || !Ready || ViewDashboard is null || sender is not RadioButton nav)
        {
            return;
        }

        foreach (Section section in Sections)
        {
            if (ReferenceEquals(section.Nav, nav))
            {
                ShowSection(section);
                return;
            }
        }
    }

    /// <summary>Opens a section, on the page it was last left on.</summary>
    private void ShowSection(Section section, Page? want = null)
    {
        Page page = want
            ?? section.Pages.FirstOrDefault(p => p.Key == _lastPage.GetValueOrDefault(section.TitleKey))
            ?? section.Pages[0];

        BuildSubNav(section, page);
        ShowPage(section, page);
    }

    /// <summary>
    /// Rebuilds the pivot strip for a section.
    /// </summary>
    /// <remarks>
    /// Built from <see cref="Sections"/> rather than written out in XAML: which pages a section
    /// holds is one fact, and a hand-written strip beside the table would be a second place for it
    /// to go stale. A section with one page shows no strip at all - a single tab is furniture.
    /// </remarks>
    private void BuildSubNav(Section section, Page? selected)
    {
        SubNav.Children.Clear();

        if (section.Pages.Count < 2)
        {
            SubNavHost.Visibility = Visibility.Collapsed;
            return;
        }

        SubNavHost.Visibility = Visibility.Visible;

        _switching = true;

        try
        {
            foreach (Page page in section.Pages)
            {
                var tab = new RadioButton
                {
                    Style = (Style)FindResource("PivotTab"),
                    GroupName = "PcOrbitSubNav",
                    Content = T(page.TabKey),
                    Tag = page,
                    IsChecked = ReferenceEquals(page, selected),
                };

                tab.Checked += OnSubNavChanged;
                SubNav.Children.Add(tab);
            }
        }
        finally
        {
            _switching = false;
        }
    }

    private void OnSubNavChanged(object sender, RoutedEventArgs e)
    {
        if (_switching || _section is null || sender is not RadioButton { Tag: Page page })
        {
            return;
        }

        ShowPage(_section, page);
    }

    private void ShowPage(Section section, Page page)
    {
        foreach (Page other in AllPages)
        {
            other.View.Visibility = ReferenceEquals(other, page) ? Visibility.Visible : Visibility.Collapsed;
        }

        _section = section;
        _page = page;
        _lastPage[section.TitleKey] = page.Key;

        Reveal(page.View);
        ApplyPageChrome();

        // Usually already filled by the background warm-up, in which case this returns at once and
        // the page is simply there. When it is not, this joins the load in progress and shows the
        // bar until it finishes.
        _ = ShowAndLoadAsync(page);
    }

    /// <summary>
    /// The page arrives rather than appears: a fade and a few pixels of rise.
    /// </summary>
    /// <remarks>
    /// Short enough that nobody waits for it, long enough that the eye follows the content instead
    /// of re-finding it. Kept to opacity and a transform, both of which the compositor handles off
    /// the UI thread, so a page that is still filling itself in does not stutter its own entrance.
    /// </remarks>
    private static void Reveal(FrameworkElement view)
    {
        var slide = new TranslateTransform();
        view.RenderTransform = slide;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        view.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });

        slide.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    /// <summary>Navigates to a page from somewhere other than the rail - a link on another page.</summary>
    private void GoTo(FrameworkElement view)
    {
        foreach (Section section in Sections)
        {
            foreach (Page page in section.Pages)
            {
                if (!ReferenceEquals(page.View, view))
                {
                    continue;
                }

                _switching = true;

                try
                {
                    section.Nav.IsChecked = true;
                }
                finally
                {
                    _switching = false;
                }

                ShowSection(section, page);
                return;
            }
        }
    }

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

    private async void OnRescan(object sender, RoutedEventArgs e)
    {
        Task<HardwareInventory> hardware = _hardware.ReadAsync();
        Task<SystemSummary> summary = _summaryReader.ReadAsync();

        await RescanAsync();

        _inventory = await hardware;
        _summary = await summary;

        RenderInventory();
    }

    /// <summary>
    /// Opens on the last scan when it is recent, and only reads the machine when there is none.
    /// </summary>
    /// <remarks>
    /// Ten seconds of "reading your PC" on every launch is a tax on people who opened the app to
    /// look at something they saw yesterday. A snapshot under six hours old is what they would have
    /// got anyway; the title bar says when it was taken, and the button beside it takes a new one.
    /// </remarks>
    private async Task LoadOrScanAsync()
    {
        if (_host is null)
        {
            return;
        }

        StateSnapshot? stored = await _host.Snapshots.LoadLatestAsync();

        // An elevated run must not open on a snapshot taken without those rights: the values the
        // user elevated *for* — TPM, drive encryption, restore points — would still read Unknown,
        // and the UAC prompt would have bought them a stale answer.
        bool storedIsBlind = stored is not null
            && _host.Elevation.IsElevated
            && HealthScore.CountUnreadable(stored, _host.Graph) > 0;

        if (stored is not null && !storedIsBlind && _host.Clock.Now - stored.TakenAt < TimeSpan.FromHours(6))
        {
            _snapshot = stored;
            _findings = PcOrbitHost.Checkup.Run(new CheckupContext(stored, _host.Graph, _host.Catalog));

            RenderMachineCard();
            RenderDashboard();
            RenderStatusView();
                InvalidatePages();
            await ReloadCurrentPageAsync();

            LastScanLabel.Text = T("app.scan.cached", Args(
                ("time", stored.TakenAt.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture))));
            return;
        }

        await RescanAsync();
    }

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

        // Everything the other pages showed was derived from the previous scan. Dropping the cache
        // is what stops a page that is now wrong from staying on screen until the app restarts.
        InvalidatePages();
        await ReloadCurrentPageAsync();

        LastScanLabel.Text = _snapshot is null
            ? T("status.unknown")
            : _snapshot.TakenAt.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>Re-fills whichever page is open, so a rescan is visible without navigating away.</summary>
    private async Task ReloadCurrentPageAsync()
    {
        if (_page is not null)
        {
            await ShowAndLoadAsync(_page);
        }
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
        RenderDashGauges();
        RenderPerf();

        // Enumerating every process allocates a few hundred objects a pass. Gauges are worth
        // refreshing every second; the top-process list does not change fast enough to justify it.
        if (++_tick % 3 == 0)
        {
            RenderProcesses();
        }
    }

    /// <summary>The four gauges across the dashboard: what the machine is doing this second.</summary>
    private void RenderDashGauges()
    {
        if (!Ready)
        {
            return;
        }

        DashGauges.ItemsSource = new List<GaugeRow>
        {
            new(
                "CPU",
                Pct(_live.CpuPercent),
                _snapshot?.Machine.CpuName ?? string.Empty,
                _live.CpuPercent ?? 0d,
                Gl("GlyphCpu"),
                B("AccentSoft"),
                B("Accent")),
            new(
                "RAM",
                Pct(_live.RamPercent),
                _live.RamTotalGb is null
                    ? T("status.unknown")
                    : T("app.ram.detail", Args(("used", Gb(_live.RamUsedGb)), ("total", Gb(_live.RamTotalGb)))),
                _live.RamPercent ?? 0d,
                Gl("GlyphRam"),
                B("VioletSoft"),
                B("Violet")),
            new(
                T("app.disk.title"),
                Pct(_live.DiskPercent),
                _live.DiskTotalGb is null
                    ? T("status.unknown")
                    : T("app.disk.detail", Args(("free", Gb(_live.DiskFreeGb)), ("total", Gb(_live.DiskTotalGb)))),
                _live.DiskPercent ?? 0d,
                Gl("GlyphDisk"),
                B("OrangeSoft"),
                B("Orange")),
            new(
                T("app.tile.network"),
                _live.NetworkDownMbps is null && _live.NetworkUpMbps is null
                    ? T("status.unknown")
                    : $"↓ {Mbps(_live.NetworkDownMbps)}   ↑ {Mbps(_live.NetworkUpMbps)}",
                _live.NetworkLinkMbps is { } linkMbps
                    ? T("app.net.linkDetail", Args(("adapter", _live.NetworkAdapter ?? "—"), ("link", Mbps(linkMbps))))
                    : _live.NetworkAdapter ?? T("status.unknown"),

                // A throughput bar needs a ceiling, and the link speed is the honest one.
                _live.NetworkDownMbps is { } down && _live.NetworkLinkMbps is { } link && link > 0
                    ? Math.Clamp(down / link * 100d, 0d, 100d)
                    : 0d,
                Gl("GlyphNet"),
                B("CyanSoft"),
                B("Cyan")),
        };
    }

    private void RenderProcesses()
    {
        if (!Ready)
        {
            return;
        }

        IReadOnlyList<ProcessUsage> top = _processes.Top(12);

        List<ProcRow> rows = [.. top.Select((u, index) =>
        {
            // The executable's own icon, the way Task Manager shows it. Falls back to the initial
            // when Windows will not say where the process lives, which it will not for an elevated
            // process seen from a standard-user app.
            ImageSource? icon = ShellIcons.For(u.Path);

            return new ProcRow(
            Name: u.Name,
            Pid: string.Create(CultureInfo.InvariantCulture, $"#{u.Id}"),
            Initial: u.Name.Length > 0 ? u.Name[..1].ToUpperInvariant() : "?",
            Icon: icon,
            LetterVisible: icon is null ? Visibility.Visible : Visibility.Collapsed,
            // A process rarely reaches whole percentages, and rounding every one of them to "0%"
            // makes the whole column useless — so small shares keep a decimal.
            Cpu: u.CpuPercent is { } cpu
                ? cpu.ToString(cpu < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + "%"
                : T("status.unknown"),
            CpuPercent: u.CpuPercent ?? 0d,
            Ram: u.MemoryMb >= 1024
                ? (u.MemoryMb / 1024d).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
                : u.MemoryMb.ToString("0", CultureInfo.CurrentCulture) + " MB",
            Tone: index < 3 ? B("AccentSoft") : B("Hair"),
            Accent: index < 3 ? B("Accent") : B("Muted"),
            RowBg: index % 2 == 0 ? B("RowBg") : Brushes.Transparent);
        })];

        ProcList.ItemsSource = rows.Take(6).ToList();
        PerfProcList.ItemsSource = rows;

        ProcEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Storage, security, startup and network: the context cards under the findings.
    /// </summary>
    private void RenderInventory()
    {
        if (!Ready)
        {
            return;
        }

        List<DeviceGroupRow> Groups((string Key, IReadOnlyList<HardwarePart>? Parts, string Icon, string Tone, string Accent)[] source) =>
        [
            .. source
                .Where(g => g.Parts is { Count: > 0 })
                .Select(g => new DeviceGroupRow(
                    T(g.Key),
                    [.. g.Parts!.Select(p => new DeviceRow(
                        p.Name,
                        p.Detail ?? p.Extra ?? string.Empty,
                        Gl(g.Icon),
                        B(g.Tone),
                        B(g.Accent)))])),
        ];

        IReadOnlyList<HardwarePart>? gpu = _inventory.Gpu is { } card ? [card] : null;

        // The hint only describes the wait; once the read is in, the cards speak for themselves.
        InventoryHint.Text = string.Empty;

        InventoryGroups.ItemsSource = Groups(
        [
            ("app.hw.gpu", gpu, "GlyphGpu", "GoodSoft", "Good"),
            ("app.hw.disks", _inventory.Disks, "GlyphDisk", "OrangeSoft", "Orange"),
            ("app.hw.memory", _inventory.MemoryModules, "GlyphRam", "VioletSoft", "Violet"),
            ("app.hw.monitors", _inventory.Monitors, "GlyphMonitor", "AccentSoft", "Accent"),
            ("app.hw.network", _inventory.Network, "GlyphNet", "CyanSoft", "Cyan"),
            ("app.hw.audio", _inventory.Audio, "GlyphAudio", "VioletSoft", "Violet"),
            ("app.hw.input", _inventory.Input, "GlyphKeyboard", "RowHover", "Muted"),
        ]);

        List<DeviceGroupRow> attached = Groups(
        [
            ("app.hw.monitors", _inventory.Monitors, "GlyphMonitor", "AccentSoft", "Accent"),
            ("app.hw.audio", _inventory.Audio, "GlyphAudio", "VioletSoft", "Violet"),
            ("app.hw.input", _inventory.Input, "GlyphKeyboard", "GoodSoft", "Good"),
            ("app.hw.network", _inventory.Network, "GlyphNet", "CyanSoft", "Cyan"),
        ]);

        DeviceGroups.ItemsSource = attached;
        DevEmpty.Visibility = attached.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderVolumes();
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
        // Uptime and support tier moved off the rail into the tooltip of the card: they are
        // context, and context was crowding out the navigation.
        MachineName.ToolTip = UptimeText() + Environment.NewLine
            + T("cli.header.support", Args(("tier", T(SupportTierResolver.DisplayKey(Tier())))));
        RenderElevation();
    }

    private SupportTier Tier()
    {
        GuideData? guide = _host!.Guides.TryGetValue("guide.firmware.virtualization", out GuideData? g) ? g : null;
        return SupportTierResolver.Resolve(_snapshot!.Machine, _host.Catalog, guide, _host.Today);
    }

    private string UptimeText() => _live.Uptime is { } up
        ? T("app.uptime.value", Args(
            ("days", N(up.Days)),
            ("hours", N(up.Hours)),
            ("minutes", N(up.Minutes))))
        : T("status.unknown");

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

        HealthVerdict verdict = HealthScore.Evaluate(
            new CheckupContext(_snapshot, _host.Graph, _host.Catalog),
            _findings);

        HealthScoreText.Text = N(verdict.Score);
        HealthLabel.Text = T(verdict.LabelKey);

        // The number can only speak for what we managed to read, so when something was unreadable
        // it says so instead of implying the whole machine was checked (ADR 0004).
        HealthNote.Text = verdict.IsPartial
            ? T("health.partial", Args(("count", N(verdict.UnreadableCount))))
            : _findings.Count == 0 ? T("app.health.clean") : T("app.health.some");
        DrawRing(verdict.Score);

        bool clean = _findings.Count == 0;

        HeroState.Text = clean ? T("app.hero.stable") : T("app.hero.attention");
        HeroBadge.Background = clean ? B("Good") : B("Warn");

        // A tick beside "needs a look" contradicts the words next to it.
        HeroBadgeGlyph.Data = clean ? G("IconCheck") : G("IconSpark");
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
                Code: f.Code,
                RouteLabel: f.NextRoute is { } route ? RouteLabel(route) : string.Empty,
                // Fix already leads to the outcome; the route button is for everything else.
                RouteVisible: f.NextRoute is { Kind: not RouteKind.Outcome } ? Visibility.Visible : Visibility.Collapsed,
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
        GoTo(ViewPlan);
    }

    private void OnHeroDetails(object sender, RoutedEventArgs e) => GoTo(ViewStatus);

    // ---------------------------------------------------------------- status view

    private void RenderStatusView()
    {
        if (_snapshot is null)
        {
            return;
        }

        StateSnapshot snapshot = _snapshot;



        RenderReadings();

    }

    /// <summary>
    /// Every reading as a Settings row.
    /// </summary>
    /// <remarks>
    /// The second line is a sentence for a person, not the WMI class the value came from. The
    /// source is still one switch away — spec 6.1 wants "how do you know?" to be answerable — but
    /// a registry path on every line is the app talking to itself in front of the user.
    /// </remarks>
    private void RenderReadings()
    {
        if (_snapshot is null || _host is null)
        {
            return;
        }

        bool technical = DetailToggle.IsChecked == true;

        DetailToggleLabel.Text = T("app.status.detailToggle");
        ReadingsTitle.Text = T("app.status.readingsPlain");

        ReadingsList.ItemsSource = _snapshot.Readings
            // The firmware readings live on the BIOS page. Filtered here rather than duplicated
            // there, from the same predicate that page selects with, so a capability cannot land on
            // both pages or on neither.
            .Where(r => !IsFirmware(r.Capability))
            .Select(r =>
            {
                bool known = r.Value.IsKnown;
                bool needsAdmin = !known && r.Evidence.Source.Contains("administrator", StringComparison.OrdinalIgnoreCase);

                string detail = technical
                    ? r.Evidence.Source
                    : known
                        ? T("app.status.readFine")
                        : needsAdmin ? T("app.status.needsAdmin") : T("app.status.notReadable");

                return new ReadingRow(
                    Name: CapabilityName(r.Capability),
                    Detail: detail,
                    Value: Status(r.Value) + Unit(r.Capability),
                    Glyph: known ? Gl("GlyphCheck") : Gl("GlyphSearch"),
                    Tone: known ? B("GoodSoft") : B("RowHover"),
                    Accent: known ? B("Ink") : B("Faint"));
            })
            .ToList();
    }

    private void OnDetailToggle(object sender, RoutedEventArgs e) => RenderReadings();


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
            // Attempted for real: some firmware does publish an ACPI thermal zone. When this one
            // does not, the tile carries the machine's own reason instead of a made-up number.
            _summary.Thermal is { Celsius: { } celsius }
                ? new TileRow(
                    T("app.tile.thermal"),
                    celsius.ToString("0.#", CultureInfo.CurrentCulture) + " °C",
                    T("app.tile.thermalSource"),
                    celsius >= 85 ? B("Bad") : celsius >= 70 ? B("Orange") : B("Good"))
                : new TileRow(
                    T("app.tile.thermal"),
                    T("status.unknown"),
                    _summary.Thermal?.Reason ?? T("app.tile.thermalWhy"),
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

        GoTo(ViewPlan);
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
            ? T("cli.result.noExpiry")
            : result.Mode == ExecutionMode.DryRun ? T("cli.result.dryRun") : string.Empty;

        RenderRestartBanner(result);
    }

    // ---------------------------------------------------------------- history view

    /// <summary>
    /// The transaction list, which now lives on Plan &amp; Apply.
    /// </summary>
    /// <remarks>
    /// It used to share a History page with the event log. The event log is the Timeline now — the
    /// same rows plus everything Windows did — so keeping both put identical entries on two pages.
    /// Transactions belong here anyway: this is where a change is made, and undoing one is the
    /// same conversation.
    /// </remarks>
    private async Task RefreshHistoryAsync()
    {
        if (_host is null)
        {
            return;
        }

        IReadOnlyList<Transaction> transactions = await _host.Transactions.ListRecentAsync(15);

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

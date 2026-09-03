using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PcOrbit.Adapters.Windows;
using PcOrbit.Cli;
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

public sealed record ReadingRow(string Name, string Value, string Source);

public sealed record FindingRow(
    string Title,
    string Benefit,
    string Safety,
    string Cost,
    string FixLabel,
    Visibility FixVisible,
    string? OutcomeId);

public sealed record OutcomeRow(Outcome Outcome, string Title);

public sealed record StepRow(string Title, string Change, string Why, string Meta);

public sealed record PhaseRow(string Header, IReadOnlyList<StepRow> Steps);

public sealed record ResultRow(string Symbol, string Title, string Detail);

public sealed record TxRow(string Id, string Title, string Detail, string UndoLabel, Visibility UndoVisible);

public sealed record EventRow(string When, string Category, string Change);

/// <summary>
/// One window, three views — the same seams the CLI sits on. No product logic here: every view
/// reads the machine, asks the compiler or the engine, and renders (spec step 1, 18.2).
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "A WPF window's lifetime ends at OnClosed, where the host is disposed.")]
public partial class MainWindow : Window
{
    private PcOrbitHost? _host;
    private IStringCatalog _strings = null!;
    private StateSnapshot? _snapshot;
    private Plan? _plan;

    public MainWindow()
    {
        InitializeComponent();
    }

    private string T(string key, IReadOnlyDictionary<string, string>? args = null) => _strings.Format(key, args);

    private static Dictionary<string, string> Args(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);

        foreach ((string key, string value) in pairs)
        {
            result[key] = value;
        }

        return result;
    }

    // ---------------------------------------------------------------- startup and language

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var options = CliOptions.Parse([]);

        _strings = JsonStringCatalog.LoadForLocale(
            System.IO.Path.Combine(DataLocator.FindDataDirectory(null), "i18n"),
            options.Locale);

        _host = await Task.Run(() => PcOrbitHost.Create(options, new WpfSafeApplyConfirmation(_strings)));

        ApplyStrings();
        PopulateOutcomes();
        await RescanAsync();
        await RefreshHistoryAsync();
    }

    private void OnClosed(object? sender, EventArgs e) => _host?.Dispose();

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

        ApplyStrings();
        PopulateOutcomes();
        RenderStatus();
        _plan = null;
        PlanResultArea.Visibility = Visibility.Collapsed;
        RunResultCard.Visibility = Visibility.Collapsed;
        await RefreshHistoryAsync();
    }

    /// <summary>Every user-visible string comes from the catalog — none live in code (spec 21.11).</summary>
    private void ApplyStrings()
    {
        AppTitle.Text = T("app.title");
        AppTagline.Text = T("app.tagline");
        Title = T("app.title");

        NavStatus.Content = T("app.nav.status");
        NavPlan.Content = T("app.nav.plan");
        NavHistory.Content = T("app.nav.history");

        StatusTitle.Text = T("app.nav.status");
        StatusSub.Text = T("app.status.sub");
        RescanBtn.Content = T("app.rescan");
        ReadingsTitle.Text = T("cli.scan.heading");

        PlanTitle.Text = T("app.nav.plan");
        PlanSub.Text = T("app.plan.sub");
        CompileBtn.Content = T("app.plan.compile");
        ApplyBtn.Content = T("app.plan.apply");
        DryRunBox.Content = T("app.plan.dryRun");
        RecoveryKeyBox.Content = T("app.plan.recoveryKey");

        HistoryTitle.Text = T("app.nav.history");
        HistorySub.Text = T("app.history.sub");
        HistoryRefreshBtn.Content = T("app.history.refresh");
        EventsTitle.Text = T("app.history.events");

        BusyText.Text = T("app.status.scanning");
    }

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (PanelStatus is null)
        {
            return;
        }

        PanelStatus.Visibility = NavStatus.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelPlan.Visibility = NavPlan.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelHistory.Visibility = NavHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task<bool> BusyAsync(Func<Task> work)
    {
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

    // ---------------------------------------------------------------- status view

    private async void OnRescan(object sender, RoutedEventArgs e) => await RescanAsync();

    private async Task RescanAsync()
    {
        if (_host is null)
        {
            return;
        }

        await BusyAsync(async () =>
        {
            StateSnapshot snapshot = await Task.Run(() => _host.Scanner.ScanAsync(CancellationToken.None));
            await _host.Snapshots.SaveAsync(snapshot);
            _snapshot = snapshot;
        });

        RenderStatus();
    }

    private void RenderStatus()
    {
        if (_host is null || _snapshot is null)
        {
            return;
        }

        StateSnapshot snapshot = _snapshot;
        MachineIdentity machine = snapshot.Machine;

        GuideData? guide = _host.Guides.TryGetValue("guide.firmware.virtualization", out GuideData? g) ? g : null;
        SupportTier tier = SupportTierResolver.Resolve(machine, _host.Catalog, guide);

        MachineName.Text = machine.DisplayName;
        MachineOs.Text = T("cli.header.os", Args(
            ("edition", machine.OsEdition),
            ("build", machine.OsBuild.ToString(CultureInfo.InvariantCulture)),
            ("cpu", machine.CpuName)));
        MachineTier.Text = T("cli.header.support", Args(("tier", T(SupportTierResolver.DisplayKey(tier)))));

        IReadOnlyList<Finding> findings = PcOrbitHost.Checkup.Run(new CheckupContext(snapshot, _host.Graph, _host.Catalog));

        HeroState.Text = findings.Count == 0
            ? T("cli.checkup.ok.title")
            : T("cli.checkup.heading", Args(("count", findings.Count.ToString(CultureInfo.InvariantCulture))));

        HeroDetail.Text = findings.Count == 0 ? T("cli.checkup.ok.body") : T("app.status.attention");

        FindingsList.ItemsSource = findings.Select(f => new FindingRow(
            Title: T(f.TitleKey, f.Arguments),
            Benefit: T(f.BenefitKey, f.Arguments),
            Safety: T(f.SafetyKey, f.Arguments),
            Cost: T("cli.checkup.cost", Args(
                ("restart", Restart(f.Restart)),
                ("seconds", Math.Max(1, f.EstimatedSeconds).ToString(CultureInfo.InvariantCulture)))),
            FixLabel: T("app.fix"),
            FixVisible: f.SuggestedOutcomeId is null ? Visibility.Collapsed : Visibility.Visible,
            OutcomeId: f.SuggestedOutcomeId)).ToList();

        ReadingsList.ItemsSource = snapshot.Readings.Select(r => new ReadingRow(
            Name: CapabilityName(r.Capability),
            Value: Status(r.Value) + Unit(r.Capability),
            Source: r.Value.IsKnown ? r.Evidence.Source : r.Evidence.Source)).ToList();
    }

    private void OnFindingFix(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string outcomeId })
        {
            return;
        }

        OutcomeRow? row = OutcomeBox.Items.OfType<OutcomeRow>().FirstOrDefault(o => o.Outcome.Id == outcomeId);

        if (row is not null)
        {
            OutcomeBox.SelectedItem = row;
        }

        NavPlan.IsChecked = true;
    }

    // ---------------------------------------------------------------- plan view

    private void PopulateOutcomes()
    {
        if (_host is null)
        {
            return;
        }

        OutcomeBox.ItemsSource = _host.Outcomes
            .Select(o => new OutcomeRow(o, T(o.TitleKey)))
            .ToList();

        OutcomeBox.SelectedIndex = 0;
    }

    private async void OnCompile(object sender, RoutedEventArgs e) => await CompileAsync();

    private async Task CompileAsync()
    {
        if (_host is null || OutcomeBox.SelectedItem is not OutcomeRow row)
        {
            return;
        }

        RunResultCard.Visibility = Visibility.Collapsed;
        OutcomeDescription.Text = T(row.Outcome.DescriptionKey);

        await BusyAsync(async () =>
        {
            StateSnapshot snapshot = await Task.Run(() => _host.Scanner.ScanAsync(CancellationToken.None));
            await _host.Snapshots.SaveAsync(snapshot);
            _snapshot = snapshot;
            _plan = _host.CreateCompiler().Compile(snapshot, row.Outcome);
        });

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

    private void OnRecoveryAck(object sender, RoutedEventArgs e)
    {
        if (_plan is not null)
        {
            RenderPlan();
        }
    }

    private void RenderPlan()
    {
        if (_host is null || _plan is null)
        {
            return;
        }

        Plan plan = _plan;
        PlanResultArea.Visibility = Visibility.Visible;

        if (plan.Outlook == PlanOutlook.AlreadySatisfied)
        {
            PlanCost.Text = T("plan.outcome.alreadyReached");
            PlanNotes.Text = string.Empty;
            PlanHash.Text = string.Empty;
            PhasesList.ItemsSource = null;
            ApplyBtn.IsEnabled = false;
            return;
        }

        PlanCost.Text = T("plan.cost.header", Args(
            ("changes", plan.Cost.Changes.ToString(CultureInfo.InvariantCulture)),
            ("restarts", plan.Cost.Restarts.ToString(CultureInfo.InvariantCulture)),
            ("minutes", plan.Cost.EstimatedMinutes.ToString(CultureInfo.InvariantCulture)),
            ("manual", plan.Cost.ManualSteps.ToString(CultureInfo.InvariantCulture))));

        PreflightReport? preflight = RunPreflight();
        List<string> notes = [];

        if (plan.Outlook == PlanOutlook.NotReachable)
        {
            notes.Add(T("plan.outcome.notReachable"));
        }

        notes.AddRange(plan.Issues
            .Where(i => i.Severity != PlanIssueSeverity.Info)
            .Select(i => $"[{i.Code}] {i.Detail}"));

        if (preflight is not null)
        {
            notes.AddRange(preflight.Findings.Select(f => T(f.MessageKey, f.Arguments)));
        }

        PlanNotes.Text = string.Join(Environment.NewLine, notes);
        PlanHash.Text = T("cli.plan.hash", Args(
            ("hash", plan.Hash[..16]),
            ("graph", plan.GraphVersion),
            ("rules", plan.RuleVersion)));

        PhasesList.ItemsSource = plan.Phases.Select(phase => new PhaseRow(
            Header: T("cli.plan.stage", Args(("number", (phase.Index + 1).ToString(CultureInfo.InvariantCulture))))
                + (phase.RestartAfter == RestartKind.None
                    ? string.Empty
                    : " — " + T("cli.plan.thenRestart", Args(("restart", Restart(phase.RestartAfter))))),
            Steps: phase.Steps.Select(step => new StepRow(
                Title: $"{step.Ordinal + 1}. {T(step.Action.TitleKey)}",
                Change: $"{CapabilityName(step.Capability)}: {Status(step.CurrentValue)} → {Status(step.DesiredValue)}",
                Why: T("cli.plan.why", Args(("reason", WhyOf(step)))),
                Meta: T("cli.plan.stepMeta", Args(
                    ("mode", T($"cli.writeMode.{Camel(step.Action.WriteMode.ToString())}")),
                    ("risk", T($"cli.risk.{Camel(step.Action.Risk.ToString())}")),
                    ("reversible", T($"cli.reversible.{Camel(step.Action.Reversible.Mode.ToString())}")),
                    ("restart", Restart(step.Action.Restart)))))).ToList())).ToList();

        bool blocked = plan.HasBlockers || (preflight?.IsBlocked ?? false);
        ApplyBtn.IsEnabled = !plan.HasBlockers && (!blocked || DryRunBox.IsChecked == true);
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

        if (mode == ExecutionMode.Apply)
        {
            string question = T("cli.apply.confirm", Args(
                ("count", plan.Cost.Changes.ToString(CultureInfo.InvariantCulture))));

            if (MessageBox.Show(this, question, T("app.title"), MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes)
            {
                return;
            }
        }

        Transaction? result = null;

        bool ok = await BusyAsync(async () =>
        {
            Transaction transaction = _host.Engine.Begin(plan, mode, plan.Hash);
            result = await Task.Run(() => _host.Engine.RunAsync(transaction, _host.Transactions, _host.Events));
        });

        if (ok && result is not null)
        {
            RenderRunResult(result);
            await RefreshHistoryAsync();
        }
    }

    private void RenderRunResult(Transaction result)
    {
        RunResultCard.Visibility = Visibility.Visible;

        TransactionTally tally = result.Tally;

        ResultHeading.Text = T("cli.result.heading", Args(("state", T($"transaction.state.{Camel(result.State.ToString())}"))));

        ResultCounts.Text =
            $"{T("cli.result.completed")}: {tally.Completed}   ·   " +
            $"{T("cli.result.failed")}: {tally.Failed}   ·   " +
            $"{T("cli.result.pending")}: {tally.Pending}" +
            (result.OutcomeVerdictKey is { } verdict ? $"   ·   {T("cli.result.outcome")}: {T(verdict)}" : string.Empty);

        ResultList.ItemsSource = result.Steps.Select(step => new ResultRow(
            Symbol: StepSymbol(step.State),
            Title: ActionTitle(step.ActionId),
            Detail: step.Actual is { } actual
                ? T("cli.result.stepActual", Args(
                    ("capability", CapabilityName(step.Capability)),
                    ("before", Status(step.Before)),
                    ("requested", Status(step.Requested)),
                    ("actual", Status(actual))))
                : T("cli.result.step", Args(
                    ("capability", CapabilityName(step.Capability)),
                    ("before", Status(step.Before)),
                    ("requested", Status(step.Requested)))))).ToList();

        ResultNext.Text = result.NeedsRestart
            ? T("cli.result.next", Args(("restart", Restart(result.PendingRestart))))
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

        TxList.ItemsSource = transactions.Select(tx => new TxRow(
            Id: tx.Id,
            Title: $"{OutcomeTitle(tx.Plan.OutcomeId)} — {T($"transaction.state.{Camel(tx.State.ToString())}")}",
            Detail: $"{tx.CreatedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture)}   ·   " +
                $"{T("cli.result.completed")}: {tx.Tally.Completed} · {T("cli.result.failed")}: {tx.Tally.Failed} · {T("cli.result.pending")}: {tx.Tally.Pending}",
            UndoLabel: T("app.history.undo"),
            UndoVisible: CanUndo(tx) ? Visibility.Visible : Visibility.Collapsed)).ToList();

        EventsList.ItemsSource = events.Select(ev => new EventRow(
            When: ev.Timestamp.LocalDateTime.ToString("dd/MM HH:mm", CultureInfo.CurrentCulture),
            Category: ev.Category.ToString(),
            Change: $"{ev.Component}: {ev.Before ?? "·"} → {ev.After ?? "·"}")).ToList();
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

        string question = T("cli.undo.confirm", Args(
            ("count", undo.Plan.Cost.Changes.ToString(CultureInfo.InvariantCulture))));

        if (MessageBox.Show(this, question, T("app.title"), MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            return;
        }

        Transaction? result = null;

        bool ok = await BusyAsync(async () =>
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
        StepState.Failed => "✗",
        StepState.RolledBack => "↩",
        _ => "·",
    };
}

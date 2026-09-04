using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Cleanup;
using PcOrbit.Core.Compare;
using PcOrbit.Core.Events;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;
using PcOrbit.Core.Preflight;

namespace PcOrbit.App;

// ---------------------------------------------------------------- row models

/// <param name="Value">The reading, in the reader's language. Never a raw enum name.</param>
public sealed record StateRow(
    string Name,
    string Detail,
    string Value,
    string Glyph,
    Brush Tone,
    Brush Accent,
    Brush RowBg);

/// <param name="Percent">0-100, for the bar. A share of the page's own total, not of the disk.</param>
public sealed record MeterRow(
    string Name,
    string Value,
    string Note,
    double Percent,
    Brush Accent,
    Brush RowBg);

/// <param name="CanToggle">
/// False for an entry this product refuses to change, and for one whose on/off state Windows keeps
/// no record of. The switch is shown disabled rather than hidden, so the row still says what it is.
/// </param>
public sealed record SwitchRow(
    string Name,
    string Detail,
    string State,
    string Hint,
    bool IsOn,
    bool CanToggle,
    Brush Accent,
    Brush RowBg);

public sealed record TimelineRow(
    string When,
    string Category,
    string Change,
    Brush Tone,
    Brush Accent,
    Brush RowBg);

/// <param name="TabKey">The label on the pivot tab. Also the section name for a single-page section.</param>
/// <param name="SubtitleKey">The line under the section name: what this particular view is for.</param>
public sealed record Page(FrameworkElement View, string TabKey, string SubtitleKey)
{
    /// <summary>
    /// The page's identity for bookkeeping - its XAML name, which is unique by construction.
    /// </summary>
    /// <remarks>
    /// Used as the key for "has this page been filled in" and "which tab was this section left on".
    /// The <see cref="Page"/> object itself is rebuilt whenever the table is, so identity has to
    /// come from the view it wraps rather than from the record.
    /// </remarks>
    public string Key => View.Name;
}

/// <param name="Nav">The rail button. One per section, not one per page.</param>
/// <param name="TitleKey">The section name, shown in the rail and as the page heading.</param>
public sealed record Section(RadioButton Nav, string TitleKey, IReadOnlyList<Page> Pages);

/// <summary>
/// The pages added from the deep research, and the chrome that keeps them consistent.
/// </summary>
/// <remarks>
/// <para>
/// One rule shapes all of them: <em>no page repeats another page's content</em>. Live meters exist
/// only on Performance, the hardware list only on Hardware, capability readings with their
/// evidence only on Status. Dashboard summarises and links; it holds no table of its own. The
/// four context cards that used to sit on Dashboard — storage, security, startup, network — are
/// gone, because each is now a page that can say more than a card could.
/// </para>
/// <para>
/// Each page loads when it is first opened rather than at startup. Every one of these runs a
/// PowerShell batch, and paying for all of them before the first frame would make the app slow to
/// open in order to fill pages nobody has asked for.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);

    private CleanupSurvey _cleanup = CleanupSurvey.Empty;
    private StartupInventoryResult _startup = new([]);

    // ---------------------------------------------------------------- chrome

    /// <summary>Puts the current page's name and one-line purpose in the title bar.</summary>
    private void ApplyPageChrome()
    {
        if (!Ready || PageTitle is null)
        {
            return;
        }

        if (_section is null || _page is null)
        {
            return;
        }

        // The heading names the section, not the page: the pivot right below it already says which
        // page, and a title that repeated the tab under it would say the same word twice. The line
        // beneath is the page's own, so the header still changes when the tab does.
        PageTitle.Text = T(_section.TitleKey);
        PageSubtitle.Text = T(_page.SubtitleKey);
    }

    /// <summary>Static labels for the pages in this file. Live text is set by each renderer.</summary>
    private void ApplyPageStrings()
    {

        SecurityStateTitle.Text = T("app.security.state");
        SecurityStateHint.Text = T("app.status.readingsHint");
        Win11Title.Text = T("cap.workload.windows11-ready");
        Win11Plan.Content = T("app.security.openPlan");

        RecoveryStateTitle.Text = T("app.recovery.state");
        RecoveryStateHint.Text = T("app.status.readingsHint");
        QuarantineTitle.Text = T("app.recovery.quarantine");

        CleanupTotalLabel.Text = T("app.cleanup.total");
        CleanupApply.Content = T("app.cleanup.apply");
        CleanupListTitle.Text = T("app.cleanup.list");

        StartupListTitle.Text = T("app.startup.list");

        DriverProblemTitle.Text = T("app.drivers.problems");
        DriverAllTitle.Text = T("app.drivers.all");

        TimelineListTitle.Text = T("app.timeline.list");
        TimelineGapTitle.Text = T("app.timeline.gap");

        ApplyAppsPageStrings();

        CompareRun.Content = T("app.compare.run");
        CompareChangedTitle.Text = T("app.compare.changed");
        CompareVisibilityTitle.Text = T("app.compare.visibility");
    }

    /// <summary>
    /// Fills a page the first time it is opened.
    /// </summary>
    /// <remarks>
    /// Cleared by <see cref="InvalidatePages"/> after anything that could change what the pages
    /// show — a rescan, or a language switch — so a stale page is never left on screen.
    /// </remarks>
    private async Task EnsurePageLoadedAsync(Page page)
    {
        if (_host is null || !_loaded.Add(page.Key))
        {
            return;
        }

        // Several of these read the machine and take seconds doing it. Without this the page sits
        // there with its headings and no content, which reads as broken rather than as busy — and
        // did: a cleanup survey walks every temp folder on the disk before it can name a figure.
        PageBusy.Visibility = Visibility.Visible;

        try
        {
            await LoadPageAsync(page);
        }
        finally
        {
            PageBusy.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadPageAsync(Page page)
    {
        if (ReferenceEquals(page.View, ViewSecurity))
        {
            RenderSecurity();
        }
        else if (ReferenceEquals(page.View, ViewRecovery))
        {
            await RenderRecoveryAsync();
        }
        else if (ReferenceEquals(page.View, ViewCleanup))
        {
            await RenderCleanupAsync();
        }
        else if (ReferenceEquals(page.View, ViewStartup))
        {
            await RenderStartupAsync();
        }
        else if (ReferenceEquals(page.View, ViewDrivers))
        {
            await RenderDriversAsync();
        }
        else if (ReferenceEquals(page.View, ViewTimeline))
        {
            await RenderTimelineAsync();
        }
        else if (ReferenceEquals(page.View, ViewCompare))
        {
            await RenderCompareAsync();
        }
        else if (ReferenceEquals(page.View, ViewApps))
        {
            await RenderAppsAsync();
        }
        else if (ReferenceEquals(page.View, ViewOffice))
        {
            await RenderOfficeAsync();
        }
        else if (ReferenceEquals(page.View, ViewWindows))
        {
            await RenderWindowsAsync();
        }
    }

    private void InvalidatePages() => _loaded.Clear();

    // ---------------------------------------------------------------- shared helpers

    private static Brush Stripe(int index) => index % 2 == 0 ? B("RowBg") : Brushes.Transparent;

    /// <summary>
    /// A row for one capability, coloured by what it actually says.
    /// </summary>
    /// <remarks>
    /// Unknown is neutral, never red. "We could not read this" is not a fault, and painting it as
    /// one would alarm every standard user about the four values that need administrator rights
    /// (spec 6.6).
    /// </remarks>
    /// <param name="expected">
    /// What this row is being judged against, when there is such a thing. Without it a scalar can
    /// only be shown as neutral: "2.0" is not intrinsically good, it is good because the
    /// requirement is ">=2.0" — and judging it by a hardcoded list of positive-sounding words put a
    /// warning triangle next to a TPM that was perfectly fine.
    /// </param>
    private StateRow CapabilityRow(
        CapabilityId capability,
        int index,
        string? detail = null,
        CapabilityValue? expected = null)
    {
        CapabilityReading? reading = _snapshot?.ReadingOf(capability);
        CapabilityValue value = reading?.Value ?? CapabilityValue.Unknown;
        CapabilityNode? node = _host?.Graph.Node(capability);

        bool known = value.IsKnown;
        bool positive = expected is { } want
            ? value.Satisfies(want)
            : value.Status is CapabilityStatus.Enabled or CapabilityStatus.Supported or CapabilityStatus.Present
                || value.Canonical is "on" or "uefi" or "ready";

        (Brush tone, Brush accent, string glyph) = !known
            ? (B("RowHover"), (Brush)B("Faint"), Gl("GlyphSearch"))
            : positive
                ? (B("GoodSoft"), (Brush)B("Good"), Gl("GlyphCheck"))
                : (B("WarnSoft"), (Brush)B("Warn"), Gl("GlyphWarn"));

        return new StateRow(
            Name: node is null ? capability.Value : T(node.DisplayKey),
            Detail: detail ?? PlainDetail(reading),
            Value: StatusText(value),
            Glyph: glyph,
            Tone: tone,
            Accent: accent,
            RowBg: Stripe(index));
    }

    /// <summary>
    /// Why a row reads the way it does, in a sentence rather than in a registry path.
    /// </summary>
    /// <remarks>
    /// The evidence has not gone anywhere — Status carries a "technical detail" switch that shows
    /// every source. It is simply not what someone opening a security page came to read
    /// (spec 6.1 is satisfied by the answer being reachable, not by it being unavoidable).
    /// </remarks>
    private string PlainDetail(CapabilityReading? reading)
    {
        if (reading is null)
        {
            return T("app.status.notReadable");
        }

        if (reading.Value.IsKnown)
        {
            return T("app.status.readFine");
        }

        return reading.Evidence.Source.Contains("administrator", StringComparison.OrdinalIgnoreCase)
            ? T("app.status.needsAdmin")
            : T("app.status.notReadable");
    }

    /// <summary>
    /// A startup entry's name, as something a person would recognise.
    /// </summary>
    /// <remarks>
    /// Two things are hidden here, both deliberately. The full command line — nobody deciding
    /// whether OneDrive should start at sign-in needs its <c>/background</c> switch, and a column
    /// of paths made the page unreadable. And the identifier some installers append to their
    /// registry key: <c>MicrosoftEdgeAutoLaunch_4211E6FAE8020E9D430E9D2A0C95613B</c> is Edge, and
    /// showing it in full says nothing except that we did not look.
    /// </remarks>
    private static string FriendlyStartupName(StartupEntry entry)
    {
        string name = entry.Name;

        // A trailing run of hex long enough to be a machine-generated id, never a real word.
        int underscore = name.LastIndexOf('_');

        if (underscore > 0
            && name.Length - underscore > 16
            && name[(underscore + 1)..].All(Uri.IsHexDigit))
        {
            name = name[..underscore];
        }

        return name;
    }

    private string StatusText(CapabilityValue value) => value.Status switch
    {
        CapabilityStatus.Value => value.Raw ?? T("status.unknown"),
        CapabilityStatus.Unknown => T("status.unknown"),
        CapabilityStatus.Enabled => T("status.enabled"),
        CapabilityStatus.Disabled => T("status.disabled"),
        CapabilityStatus.Supported => T("status.supported"),
        CapabilityStatus.NotSupported => T("status.notSupported"),
        CapabilityStatus.Present => T("status.present"),
        _ => T("status.unknown"),
    };

    // ---------------------------------------------------------------- security

    private void RenderSecurity()
    {
        if (_host is null || _snapshot is null)
        {
            return;
        }

        CapabilityId[] state =
        [
            CoreCapabilities.SecureBoot,
            CoreCapabilities.BootMode,
            CoreCapabilities.TpmVersion,
            CoreCapabilities.TpmReady,
            CoreCapabilities.BitLockerSystemDrive,
        ];

        SecurityList.ItemsSource = state.Select((c, i) => CapabilityRow(c, i)).ToList();

        // Windows 11 readiness, as the requirements rather than as a verdict. A machine that fails
        // on a firmware switch and one that fails on the chip itself get different sentences,
        // because they lead to completely different decisions.
        List<CapabilityEdge> requirements =
            [.. _host.Graph.RequirementsOf(CoreCapabilities.Windows11Ready, _snapshot.Machine)];

        Win11List.ItemsSource = requirements
            .Select((edge, i) => CapabilityRow(
                edge.To,
                i,
                T("app.security.needs", Args(("expected", edge.Expected.Canonical))),
                edge.Expected))
            .ToList();

        CapabilityValue readiness = _snapshot.ValueOf(CoreCapabilities.Windows11Ready);

        bool fixableHere = requirements.Any(edge =>
        {
            CapabilityValue actual = _snapshot.ValueOf(edge.To);

            return actual.IsKnown
                && !actual.Satisfies(edge.Expected)
                && (edge.To == CoreCapabilities.SecureBoot || edge.To == CoreCapabilities.TpmReady);
        });

        Win11Body.Text = !readiness.IsKnown
            ? T("app.security.win11Unknown")
            : readiness.Canonical == WorkloadEvaluator.Ready
                ? T("app.security.win11Ready")
                : fixableHere ? T("app.security.win11Firmware") : T("app.security.win11Hardware");

        Win11Plan.Visibility = fixableHere ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenWin11Plan(object sender, RoutedEventArgs e)
    {
        GoTo(ViewPlan);
        SelectOutcome("outcome.windows11-ready");
    }

    // ---------------------------------------------------------------- recovery

    private async Task RenderRecoveryAsync()
    {
        if (_host is null)
        {
            return;
        }

        CapabilityId[] state =
        [
            CoreCapabilities.RecoveryEnvironment,
            CoreCapabilities.RecoveryPartition,
            CoreCapabilities.SystemRestore,
            CoreCapabilities.RestorePointAgeDays,
        ];

        RecoveryList.ItemsSource = state.Select((c, i) => CapabilityRow(c, i)).ToList();

        IReadOnlyList<QuarantineBatch> batches = await _host.Quarantine.ListAsync();

        QuarantineHint.Text = T("app.recovery.retention", Args(
            ("days", N((int)_host.Quarantine.Retention.TotalDays))));

        QuarantineList.ItemsSource = batches.Count == 0
            ? [EmptyRow(T("app.recovery.noQuarantine"))]
            : batches.Select((batch, i) => new StateRow(
                Name: T("app.recovery.batch", Args(
                    ("files", N(batch.Files.Count)),
                    ("mb", N((int)Math.Round(batch.Bytes / 1024d / 1024d))))),
                Detail: T("app.recovery.batchDetail", Args(
                    ("created", batch.CreatedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture)),
                    ("expires", batch.ExpiresAt.LocalDateTime.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture)))),
                Value: batch.Id,
                Glyph: Gl("GlyphFolder"),
                Tone: B("AccentSoft"),
                Accent: B("Accent"),
                RowBg: Stripe(i))).ToList();
    }

    private static StateRow EmptyRow(string message) => new(
        Name: message,
        Detail: string.Empty,
        Value: string.Empty,
        Glyph: Gl("GlyphCheck"),
        Tone: B("GoodSoft"),
        Accent: B("Good"),
        RowBg: Brushes.Transparent);

    // ---------------------------------------------------------------- cleanup

    private async Task RenderCleanupAsync()
    {
        if (_host is null)
        {
            return;
        }

        _cleanup = await Task.Run(() => _host.CleanupScanner.SurveyAsync());

        double reclaimableMb = _cleanup.ReclaimableBytes / 1024d / 1024d;

        CleanupTotal.Text = $"{reclaimableMb:N0} MB";

        // The honest sentence, on the page rather than buried in a confirmation: this moves files
        // into quarantine, and the disk gets smaller when the window closes, not when you click.
        CleanupNote.Text = T("app.cleanup.note", Args(
            ("days", N((int)_host.Quarantine.Retention.TotalDays)),
            ("reported", N((int)Math.Round(_cleanup.ReportedBytes / 1024d / 1024d)))));

        CleanupApply.IsEnabled = _cleanup.Candidates.Any(c => c.CanReclaim);

        CleanupListHint.Text = _cleanup.IsComplete
            ? string.Empty
            : T("app.cleanup.partial", Args(("count", N(_cleanup.Problems.Count))));

        // The bar is each entry's share of the largest entry, so the page reads as "where the space
        // is" rather than as a set of unrelated numbers.
        double largest = _cleanup.Candidates.Count == 0 ? 1 : Math.Max(1, _cleanup.Candidates.Max(c => c.Bytes));

        CleanupList.ItemsSource = _cleanup.Candidates.Select((candidate, i) => new MeterRow(
            Name: T($"cleanup.item.{candidate.Id}"),
            Value: $"{candidate.MegaBytes:N0} MB",
            Note: candidate.Trust is CleanupTrust.ReportOnly or CleanupTrust.Protected
                ? T("cli.clean.reportOnly")
                : candidate.CanReclaim
                    ? T("app.cleanup.willMove", Args(("count", N(candidate.FileCount))))
                    : T("cli.clean.alreadyClear"),
            Percent: candidate.Bytes / largest * 100d,
            Accent: candidate.CanReclaim ? B("Accent") : B("Faint"),
            RowBg: Stripe(i))).ToList();
    }

    private async void OnCleanupApply(object sender, RoutedEventArgs e)
    {
        if (_host is null)
        {
            return;
        }

        List<CleanupCandidate> actionable = [.. _cleanup.Candidates.Where(c => c.CanReclaim)];

        if (actionable.Count == 0)
        {
            return;
        }

        // Spec 21.7: the cost is stated before the button does anything, and the cost here includes
        // the part people do not expect — that the space comes back in thirty days, not now.
        MessageBoxResult answer = MessageBox.Show(
            this,
            T("app.cleanup.confirm", Args(
                ("mb", N((int)Math.Round(_cleanup.ReclaimableBytes / 1024d / 1024d))),
                ("days", N((int)_host.Quarantine.Retention.TotalDays)))),
            T("app.title"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        await BusyAsync("app.cleanup.working", async () =>
        {
            QuarantineBatch batch = await _host.Quarantine.QuarantineAsync(actionable);

            MessageBox.Show(
                this,
                T("cli.clean.done", Args(
                    ("files", N(batch.Files.Count)),
                    ("mb", N((int)Math.Round(batch.Bytes / 1024d / 1024d))),
                    ("id", batch.Id),
                    ("expires", batch.ExpiresAt.LocalDateTime.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture)))),
                T("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        });

        await RenderCleanupAsync();

        // The quarantine list on Recovery is now out of date.
        _loaded.Remove(nameof(ViewRecovery));
    }

    // ---------------------------------------------------------------- startup

    private async Task RenderStartupAsync()
    {
        if (_host is null)
        {
            return;
        }

        _startup = await _host.Startup.ReadAsync();

        StartupListHint.Text = _startup.Problem ?? T("app.startup.count", Args(
            ("count", N(_startup.Entries.Count))));

        StartupList.ItemsSource = _startup.Entries.Select((entry, i) =>
        {
            bool refused = _host.StartupControl.NeverChange.Contains(entry.Name);
            bool unknown = entry.Enabled is null;

            return new SwitchRow(
                Name: FriendlyStartupName(entry),
                Detail: T($"startup.location.{Camel(entry.Location.ToString())}"),
                State: entry.Enabled switch
                {
                    true => T("status.enabled"),
                    false => T("status.disabled"),
                    null => T("status.unknown"),
                },
                Hint: refused
                    ? T("app.startup.refused")
                    : unknown ? T("app.startup.noSwitch") : T("app.startup.toggleHint"),
                IsOn: entry.Enabled ?? false,
                CanToggle: !refused && !unknown,
                Accent: entry.Enabled switch
                {
                    true => B("Good"),
                    false => B("Faint"),
                    null => B("Warn"),
                },
                RowBg: Stripe(i));
        }).ToList();
    }

    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_host is null || sender is not ToggleButton { Tag: string name } toggle)
        {
            return;
        }

        bool wanted = toggle.IsChecked == true;

        StartupChangeResult result = await _host.StartupControl.SetEnabledAsync(name, wanted);

        if (result.Problem is { } problem)
        {
            MessageBox.Show(this, problem, T("app.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (result.Applied && !result.Verified)
        {
            // The command said it worked and the machine disagrees. That is a failure, and it is
            // reported as one rather than left as a switch in a position nothing verified.
            MessageBox.Show(
                this,
                T("app.startup.notVerified"),
                T("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        // Re-read either way: the row must show what the machine says, not what was asked for.
        await RenderStartupAsync();
    }

    // ---------------------------------------------------------------- drivers

    private async Task RenderDriversAsync()
    {
        if (_host is null)
        {
            return;
        }

        DriverInventoryResult inventory = await _host.Drivers.ReadAsync();

        if (inventory.Problem is { } problem)
        {
            DriverProblemHint.Text = problem;
            DriverProblemList.ItemsSource = Array.Empty<StateRow>();
            DriverList.ItemsSource = Array.Empty<StateRow>();
            return;
        }

        DriverProblemHint.Text = string.Empty;

        DriverProblemList.ItemsSource = inventory.Faulty.Count == 0
            ? [EmptyRow(T("cli.drivers.allWorking"))]
            : inventory.Faulty.Select((driver, i) => new StateRow(
                Name: driver.DeviceName,
                Detail: $"{driver.Provider}  ·  {driver.Version}",
                Value: T("cli.drivers.problemCode", Args(("code", N(driver.ProblemCode ?? 0)))),
                Glyph: Gl("GlyphWarn"),
                Tone: B("BadSoft"),
                Accent: B("Bad"),
                RowBg: Stripe(i))).ToList();

        DriverAllHint.Text = T("app.drivers.notByAge");

        // Not sorted by date, here or anywhere. An old driver is not a fault, and a list that
        // implies otherwise is how people are talked into installing something worse.
        DriverList.ItemsSource = inventory.Drivers.Select((driver, i) => new StateRow(
            Name: driver.DeviceName,
            Detail: $"{driver.Provider ?? "—"}  ·  {driver.Class ?? "—"}"
                + (driver.IsSigned == false ? "  ·  " + T("app.drivers.unsigned") : string.Empty),
            Value: driver.Version ?? "—",
            Glyph: Gl("GlyphCpu"),
            Tone: driver.HasProblem ? B("BadSoft") : B("RowHover"),
            Accent: driver.HasProblem ? B("Bad") : B("Muted"),
            RowBg: Stripe(i))).ToList();
    }

    // ---------------------------------------------------------------- timeline

    private async Task RenderTimelineAsync()
    {
        if (_host is null)
        {
            return;
        }

        DateTimeOffset since = _host.Clock.Now.AddDays(-14);

        IReadOnlyList<ChangeEvent> own = await _host.Events.QueryAsync(new EventQuery(Since: since, Limit: 200));

        List<ChangeSourceResult> external = [];

        foreach (IChangeSource source in _host.ChangeSources)
        {
            external.Add(await source.ReadAsync(since));
        }

        Timeline timeline = TimelineBuilder.Build(own, external, since, limit: 120);

        // Stated above the list, not below it: a reader who has already scrolled a quiet timeline
        // has already concluded the machine was quiet.
        TimelineGapCard.Visibility = timeline.IsComplete ? Visibility.Collapsed : Visibility.Visible;
        TimelineGapList.ItemsSource = timeline.Unavailable.Select(u => u.Problem).ToList();

        TimelineListHint.Text = T("app.timeline.count", Args(("count", N(timeline.Events.Count))));

        TimelineList.ItemsSource = timeline.Events.Select((ev, i) =>
        {
            (Brush tone, Brush accent) = ev.Category switch
            {
                EventCategory.Crash => (B("BadSoft"), (Brush)B("Bad")),
                EventCategory.Device => (B("WarnSoft"), (Brush)B("Warn")),
                EventCategory.Update => (B("AccentSoft"), (Brush)B("Accent")),
                EventCategory.Checkpoint => (B("GoodSoft"), (Brush)B("Good")),
                EventCategory.Driver => (B("VioletSoft"), (Brush)B("Violet")),
                _ => (B("RowHover"), (Brush)B("Muted")),
            };

            return new TimelineRow(
                When: ev.Timestamp.LocalDateTime.ToString("dd/MM HH:mm", CultureInfo.CurrentCulture),
                Category: T($"event.category.{Camel(ev.Category.ToString())}"),
                Change: ev.After is { } after ? $"{ev.Component} → {after}" : ev.Component,
                Tone: tone,
                Accent: accent,
                RowBg: Stripe(i));
        }).ToList();
    }

    // ---------------------------------------------------------------- compare

    private async Task RenderCompareAsync()
    {
        if (_host is null || _snapshot is null)
        {
            return;
        }

        IReadOnlyList<SnapshotSummary> recent = await _host.Snapshots.ListRecentAsync(50);

        SnapshotSummary? previous = recent.FirstOrDefault(s =>
            s.Id != _snapshot.Id
            && string.Equals(s.MachineFingerprint, _snapshot.Machine.Fingerprint, StringComparison.Ordinal));

        if (previous is null)
        {
            CompareWindow.Text = T("cli.diff.noBaseline");
            CompareChangedList.ItemsSource = Array.Empty<StateRow>();
            CompareVisibilityList.ItemsSource = Array.Empty<StateRow>();
            return;
        }

        StateSnapshot? before = await _host.Snapshots.LoadAsync(previous.Id);

        if (before is null)
        {
            return;
        }

        SnapshotComparison comparison = SnapshotDiff.Compare(before, _snapshot);

        CompareWindow.Text = T("app.compare.window", Args(
            ("before", before.TakenAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture)),
            ("after", _snapshot.TakenAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture))));

        CompareChangedHint.Text = N(comparison.RealChanges.Count);
        CompareVisibilityHint.Text = N(comparison.VisibilityChanges.Count);

        CompareChangedList.ItemsSource = comparison.RealChanges.Count == 0
            ? [EmptyRow(T("cli.diff.identical"))]
            : comparison.RealChanges.Select(ChangeRow).ToList();

        // Kept apart from the real changes, which is the entire point of the page: an unelevated
        // scan loses sight of the TPM and drive encryption, and listing that beside a genuine
        // change would report a firmware update as having switched the security chip off.
        CompareVisibilityList.ItemsSource = comparison.VisibilityChanges.Count == 0
            ? [EmptyRow(T("app.compare.noVisibility"))]
            : comparison.VisibilityChanges.Select(ChangeRow).ToList();
    }

    private StateRow ChangeRow(CapabilityChange change, int index)
    {
        CapabilityNode? node = _host?.Graph.Node(change.Capability);

        bool visibility = change.Kind is CapabilityChangeKind.BecameUnreadable
            or CapabilityChangeKind.BecameReadable;

        return new StateRow(
            Name: node is null ? change.Capability.Value : T(node.DisplayKey),
            Detail: change.EvidenceChanged ? T("cli.diff.sourceChanged") : string.Empty,
            Value: $"{StatusText(change.Before)}  →  {StatusText(change.After)}",
            Glyph: visibility ? Gl("GlyphSearch") : Gl("GlyphSpark"),
            Tone: visibility ? B("RowHover") : B("AccentSoft"),
            Accent: visibility ? B("Faint") : B("Accent"),
            RowBg: Stripe(index));
    }

    private async void OnCompareRun(object sender, RoutedEventArgs e)
    {
        await BusyAsync("app.status.scanning", RescanAsync);
        await RenderCompareAsync();
    }
}

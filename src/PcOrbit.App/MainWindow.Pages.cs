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

/// <param name="Chosen">
/// Whether this row is included in the next clean. Two-way bound, so the tick box is the state
/// rather than a picture of it.
/// </param>
/// <param name="CanChoose">
/// False for a row with nothing in it, and for one this product does not remove. Disabled and
/// still visible: the row's job is to account for the space, and a missing row accounts for
/// nothing.
/// </param>
public sealed class CleanupRow(
    string id,
    string name,
    string value,
    string note,
    string glyph,
    double percent,
    bool canChoose,
    Brush accent,
    Brush rowBg)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public string Value { get; } = value;

    public string Note { get; } = note;

    public string Glyph { get; } = glyph;

    public double Percent { get; } = percent;

    public bool CanChoose { get; } = canChoose;

    public bool Chosen { get; set; } = canChoose;

    public Brush Accent { get; } = accent;

    public Brush RowBg { get; } = rowBg;
}

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
    /// <summary>
    /// The load each page is on, by page key: running, finished, or absent for never started.
    /// </summary>
    /// <remarks>
    /// A set of names was enough while a page only ever loaded because somebody opened it. Now that
    /// they warm in the background, arriving at a page mid-load has to join that load rather than
    /// see its name in a set and conclude there is nothing to wait for — which would leave the
    /// progress bar off while the page was still empty.
    /// </remarks>
    private readonly Dictionary<string, Task> _loading = new(StringComparer.Ordinal);

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
        CleanupPurge.Content = T("app.cleanup.purge");
        CleanupListTitle.Text = T("app.cleanup.list");

        StartupListTitle.Text = T("app.startup.list");

        DriverProblemTitle.Text = T("app.drivers.problems");
        DriverAllTitle.Text = T("app.drivers.all");

        TimelineListTitle.Text = T("app.timeline.list");
        TimelineGapTitle.Text = T("app.timeline.gap");

        ApplyAppsPageStrings();
        ApplyBiosStrings();

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
    private Task EnsurePageLoadedAsync(Page page)
    {
        if (_host is null)
        {
            return Task.CompletedTask;
        }

        if (_loading.TryGetValue(page.Key, out Task? running))
        {
            return running;
        }

        Task load = LoadPageAsync(page);

        _loading[page.Key] = load;

        return load;
    }

    /// <summary>
    /// Opens a page and shows the bar until whatever is filling it has finished.
    /// </summary>
    /// <remarks>
    /// Several of these read the machine and take seconds doing it. Without the bar the page sits
    /// there with its headings and no content, which reads as broken rather than as busy.
    /// </remarks>
    private async Task ShowAndLoadAsync(Page page)
    {
        Task load = EnsurePageLoadedAsync(page);

        if (load.IsCompleted)
        {
            return;
        }

        PageBusy.Visibility = Visibility.Visible;

        try
        {
            await load;
        }
        finally
        {
            // Only if this is still the page on screen. A slow page the user has already navigated
            // away from must not clear the bar of the one they moved to.
            if (_page is not null && ReferenceEquals(_page, page))
            {
                PageBusy.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Fills every page that is not on screen, one at a time, in the background.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The complaint this answers is that changing tab meant waiting, every time. Each page runs
    /// its own PowerShell batch or disk walk, and doing that on arrival means the wait lands
    /// exactly when the user is looking at an empty page. Doing it beforehand means it lands while
    /// they are reading the page they are already on.
    /// </para>
    /// <para>
    /// One at a time rather than all at once, deliberately: eight concurrent PowerShell sessions
    /// would make the machine slower than the wait they were meant to remove. And started only
    /// after the first scan is in, so it competes with nothing the user is actually waiting for.
    /// </para>
    /// </remarks>
    private async Task WarmPagesAsync()
    {
        foreach (Page page in AllPages)
        {
            if (_host is null)
            {
                return;
            }

            if (_page is not null && ReferenceEquals(page, _page))
            {
                continue;
            }

            try
            {
                await EnsurePageLoadedAsync(page);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
            {
                // A page that cannot fill itself in the background is not an error worth a dialog.
                // It will report its own problem when the user opens it.
                _loading.Remove(page.Key);
            }

            // Back of the queue between pages, so warming never competes with a click.
            await Task.Delay(150);
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
        else if (ReferenceEquals(page.View, ViewBios))
        {
            RenderBios();
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

    private void InvalidatePages() => _loading.Clear();

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

    /// <summary>The rows the user has left ticked, by id.</summary>
    private readonly HashSet<string> _cleanupSkipped = new(StringComparer.Ordinal);

    private async Task RenderCleanupAsync()
    {
        if (_host is null)
        {
            return;
        }

        _cleanup = await Task.Run(() => _host.CleanupScanner.SurveyAsync());

        RenderCleanupTotals();

        CleanupListHint.Text = _cleanup.IsComplete
            ? string.Empty
            : T("app.cleanup.partial", Args(("count", N(_cleanup.Problems.Count))));

        // The bar is each entry's share of the largest entry, so the page reads as "where the space
        // is" rather than as a set of unrelated numbers.
        double largest = _cleanup.Candidates.Count == 0 ? 1 : Math.Max(1, _cleanup.Candidates.Max(c => c.Bytes));

        CleanupList.ItemsSource = _cleanup.Candidates.Select((candidate, i) => new CleanupRow(
            candidate.Id,
            T($"cleanup.item.{candidate.Id}"),
            $"{candidate.MegaBytes:N0} MB",
            NoteFor(candidate),
            GlyphFor(candidate),
            candidate.Bytes / largest * 100d,
            candidate.CanReclaim,
            candidate.Trust switch
            {
                CleanupTrust.Regenerable when candidate.CanReclaim => B("Good"),
                CleanupTrust.Reclaimable when candidate.CanReclaim => B("Accent"),
                CleanupTrust.Protected => B("Warn"),
                _ => B("Faint"),
            },
            Stripe(i))
        {
            Chosen = candidate.CanReclaim && !_cleanupSkipped.Contains(candidate.Id),
        }).ToList();
    }

    /// <summary>
    /// What happens to this row, in one line, for every row.
    /// </summary>
    /// <remarks>
    /// Including the ones nothing happens to. "Shown only" with no reason attached is the sort of
    /// line that makes a tool feel like it is hiding something; the reason is short and it is the
    /// whole justification for the row existing.
    /// </remarks>
    private string NoteFor(CleanupCandidate candidate) => candidate switch
    {
        { Trust: CleanupTrust.Protected } => T($"cleanup.why.{candidate.Id}"),
        { CanReclaim: false, Trust: CleanupTrust.ReportOnly } => T($"cleanup.why.{candidate.Id}"),
        { CanReclaim: false } => T("cli.clean.alreadyClear"),

        { Trust: CleanupTrust.Regenerable } =>
            T("app.cleanup.deletesNow", Args(("count", N(candidate.FileCount)))),

        _ => T("app.cleanup.willMove", Args(
            ("count", N(candidate.FileCount)),
            ("days", N((int)(_host?.Quarantine.Retention.TotalDays ?? 30))))),
    };

    private static string GlyphFor(CleanupCandidate candidate) => candidate switch
    {
        { Trust: CleanupTrust.Protected } => GlyphOf("GlyphWarn"),
        { CanReclaim: false } => GlyphOf("GlyphInfo"),
        { Trust: CleanupTrust.Regenerable } => GlyphOf("GlyphCleanup"),
        _ => GlyphOf("GlyphRecovery"),
    };

    private void RenderCleanupTotals()
    {
        if (_host is null)
        {
            return;
        }

        IReadOnlyList<CleanupCandidate> chosen = ChosenCandidates();

        long now = chosen.Where(c => c.FreesSpaceNow).Sum(c => c.Bytes);
        long later = chosen.Where(c => !c.FreesSpaceNow).Sum(c => c.Bytes);

        CleanupTotal.Text = $"{(now + later) / 1024d / 1024d:N0} MB";

        CleanupSplit.Text = T("app.cleanup.split", Args(
            ("now", N((int)Math.Round(now / 1024d / 1024d))),
            ("later", N((int)Math.Round(later / 1024d / 1024d)))));

        // The honest sentence, on the page rather than buried in a confirmation.
        CleanupNote.Text = T("app.cleanup.note", Args(
            ("days", N((int)_host.Quarantine.Retention.TotalDays)),
            ("reported", N((int)Math.Round(_cleanup.ReportedBytes / 1024d / 1024d)))));

        CleanupApply.IsEnabled = chosen.Count > 0;
        CleanupPurge.IsEnabled = chosen.Count > 0;
    }

    private IReadOnlyList<CleanupCandidate> ChosenCandidates() =>
        [.. _cleanup.Candidates.Where(c => c.CanReclaim && !_cleanupSkipped.Contains(c.Id))];

    private void OnCleanupChoice(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string id } box)
        {
            return;
        }

        if (box.IsChecked == true)
        {
            _cleanupSkipped.Remove(id);
        }
        else
        {
            _cleanupSkipped.Add(id);
        }

        RenderCleanupTotals();
    }

    private async void OnCleanupApply(object sender, RoutedEventArgs e) => await CleanAsync(alsoPurge: false);

    private async void OnCleanupPurge(object sender, RoutedEventArgs e) => await CleanAsync(alsoPurge: true);

    /// <summary>
    /// Runs both routes, and optionally empties the quarantine afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The app used to call <c>QuarantineAsync</c> for everything, which is why its only button
    /// said "move to quarantine" and why the disk did not get smaller when it was pressed. The
    /// split has existed in the engine since the trust levels were added; this is the page finally
    /// using it. A cache that rebuilds itself goes now, and the space is back immediately;
    /// everything else waits out its window where it can be recovered.
    /// </para>
    /// <para>
    /// <paramref name="alsoPurge"/> is the delete button. It does the same work and then releases
    /// the quarantine, so every byte is free at once and none of it can be restored. The
    /// confirmation says exactly that before anything happens.
    /// </para>
    /// </remarks>
    private async Task CleanAsync(bool alsoPurge)
    {
        if (_host is null)
        {
            return;
        }

        IReadOnlyList<CleanupCandidate> chosen = ChosenCandidates();

        if (chosen.Count == 0)
        {
            return;
        }

        List<CleanupCandidate> deleteNow = [.. chosen.Where(c => c.FreesSpaceNow)];
        List<CleanupCandidate> quarantine = [.. chosen.Where(c => !c.FreesSpaceNow)];

        long total = chosen.Sum(c => c.Bytes);

        // Spec 21.7: the cost is stated before the button does anything, and for the second button
        // the cost is that there is no way back.
        if (MessageBox.Show(
                this,
                T(alsoPurge ? "app.cleanup.confirmPurge" : "app.cleanup.confirm", Args(
                    ("mb", N((int)Math.Round(total / 1024d / 1024d))),
                    ("now", N((int)Math.Round(deleteNow.Sum(c => c.Bytes) / 1024d / 1024d))),
                    ("days", N((int)_host.Quarantine.Retention.TotalDays)))),
                T("app.title"),
                MessageBoxButton.OKCancel,
                alsoPurge ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        long freed = 0;
        int locked = 0;
        int moved = 0;

        await BusyAsync("app.cleanup.working", async () =>
        {
            if (deleteNow.Count > 0)
            {
                CleanupDeletion deletion = await _host.Quarantine.DeleteRegenerableAsync(deleteNow);

                freed += deletion.Freed;
                locked = deletion.Locked;
            }

            if (quarantine.Count > 0)
            {
                QuarantineBatch batch = await _host.Quarantine.QuarantineAsync(quarantine);

                moved = batch.Files.Count;

                if (alsoPurge)
                {
                    freed += await _host.Quarantine.PurgeAllAsync();
                }
                else
                {
                    freed += 0;
                }
            }
            else if (alsoPurge)
            {
                freed += await _host.Quarantine.PurgeAllAsync();
            }
        });

        var summary = new List<string>
        {
            T(alsoPurge ? "app.cleanup.doneGone" : "app.cleanup.doneSplit", Args(
                ("mb", N((int)Math.Round(freed / 1024d / 1024d))),
                ("moved", N(moved)),
                ("days", N((int)_host.Quarantine.Retention.TotalDays)))),
        };

        // A running browser holds its own cache open. Expected, and said out loud rather than
        // quietly subtracted from the total the user was shown a moment ago.
        if (locked > 0)
        {
            summary.Add(T("cli.clean.locked", Args(("count", N(locked)))));
        }

        MessageBox.Show(
            this,
            string.Join("\n\n", summary),
            T("app.title"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        await RenderCleanupAsync();

        // The quarantine list on Recovery is now out of date.
        _loading.Remove(nameof(ViewRecovery));
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
                // Publisher first, because it is the thing that answers "should this be here?".
                // The location follows in plain language; the user does not want a registry path
                // and, on this page, does not need one — the evidence still carries it.
                Detail: $"{entry.Publisher ?? T("app.startup.unknownPublisher")} · "
                    + T($"startup.location.{Camel(entry.Location.ToString())}"),
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

using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcOrbit.Core.Checkup;
using PcOrbit.Core.Events;
using PcOrbit.Core.Missions;
using PcOrbit.Core.Model;
using PcOrbit.Core.Navigation;
using PcOrbit.Core.Preflight;

namespace PcOrbit.App;

public sealed record MissionCard(
    string Id,
    string Title,
    string Description,
    string Glyph,
    string Meta,
    string Recovery,
    string OpenLabel,
    Brush Tint,
    Brush Ink);

/// <param name="Kind">"Direct evidence" or "same time" — the memo's distinction, kept on every row.</param>
public sealed record IncidentRow(
    string When,
    string Category,
    string Change,
    string Kind,
    Brush KindTint,
    Brush KindInk,
    string TestLabel,
    Visibility TestVisible,
    string TestTag,
    Brush RowBg);

public sealed record LadderRow(
    string Step,
    string Title,
    string Detail,
    string Readiness,
    Brush ReadyTint,
    Brush ReadyInk,
    string OpenLabel,
    string Tag,
    bool Enabled,
    Brush RowBg);

/// <summary>
/// Orchestration: a sentence finds a mission, every finding leads somewhere, an incident gets a
/// window and a next test, and recovery is a ladder climbed from the bottom.
/// </summary>
/// <remarks>
/// <para>
/// The investment memo's diagnosis was that the app had enough capability and too little
/// orchestration: the rail is organised by subsystem and problems are not, so a person had to
/// translate "máy chậm sau update" into Changes → Timeline → the 48 hours before the symptom
/// themselves. This file is that translation. It adds no new capability. Every route it follows
/// opens a page or an outcome that already existed; every incident it explains is built from the
/// <c>TimelineBuilder.Preceding</c> that had tests and no caller.
/// </para>
/// <para>
/// One place opens a route, whatever asked for it — a finding, a mission card, an incident test,
/// a ladder rung. The four surfaces cannot drift apart on what "open" means.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private Timeline? _timeline;

    // ---------------------------------------------------------------- routing

    /// <summary>The one place a <see cref="Route"/> becomes something on screen.</summary>
    private void FollowRoute(Route route)
    {
        if (_host is null)
        {
            return;
        }

        switch (route.Kind)
        {
            case RouteKind.Outcome:
                SelectOutcome(route.Target);
                GoTo(ViewPlan);
                break;

            case RouteKind.Page:
            case RouteKind.Guide:
                if (ViewFor(route.Target) is { } view)
                {
                    GoTo(view);
                    ArriveWith(view, route.Query);
                }

                break;

            case RouteKind.Settings:
            case RouteKind.Web:
                if (route.IsWellFormed)
                {
                    OpenExternal(route.Target);
                }

                break;
        }
    }

    private FrameworkElement? ViewFor(string pageKey) => pageKey switch
    {
        PageKeys.Dashboard => ViewDashboard,
        PageKeys.Readings => ViewStatus,
        PageKeys.Hardware => ViewHardware,
        PageKeys.Performance => ViewPerf,
        PageKeys.Bios => ViewBios,
        PageKeys.Security => ViewSecurity,
        PageKeys.Recovery => ViewRecovery,
        PageKeys.Cleanup => ViewCleanup,
        PageKeys.Startup => ViewStartup,
        PageKeys.Drivers => ViewDrivers,
        PageKeys.Apps => ViewApps,
        PageKeys.Office => ViewOffice,
        PageKeys.Windows => ViewWindows,
        PageKeys.Plan => ViewPlan,
        PageKeys.Timeline => ViewTimeline,
        PageKeys.Compare => ViewCompare,
        _ => null,
    };

    /// <summary>What a page does with the query a route carried it: a search pre-filled, a mode chosen.</summary>
    private void ArriveWith(FrameworkElement view, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        if (ReferenceEquals(view, ViewBios))
        {
            FirmwareSearch.Text = query;
        }
        else if (ReferenceEquals(view, ViewApps))
        {
            AppsSearch.Text = query;
        }
        else if (ReferenceEquals(view, ViewTimeline) && query == "incident")
        {
            IncidentThreeDays.IsChecked = true;
        }
    }

    private static void OpenExternal(string target)
    {
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Nothing registered to open it. Rare, and the page it came from is still on screen.
        }
    }

    private void OnFindingRoute(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string code })
        {
            return;
        }

        Finding? finding = _findings.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.Ordinal));

        if (finding?.NextRoute is { } route)
        {
            FollowRoute(route);
        }
    }

    private string RouteLabel(Route route) => route.Kind switch
    {
        RouteKind.Outcome => T("app.route.outcome"),
        RouteKind.Guide => T("app.route.guide"),
        RouteKind.Settings => T("app.route.settings"),
        RouteKind.Web => T("app.route.web"),
        _ => T("app.route.page"),
    };

    // ---------------------------------------------------------------- mission center

    private void ApplyMissionStrings()
    {
        MissionTitle.Text = T("app.mission.title");
        MissionSearch.Tag = T("app.mission.prompt");
        MissionNoMatch.Text = T("app.mission.noMatch");
        RenderMissions();
    }

    private void OnMissionSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (Ready && MissionResults is not null)
        {
            RenderMissions();
        }
    }

    /// <summary>
    /// The mission cards: all of them when nothing is typed, the matches in score order otherwise.
    /// </summary>
    private void RenderMissions()
    {
        if (_host is null)
        {
            return;
        }

        string query = MissionSearch.Text;

        IReadOnlyList<Mission> missions = string.IsNullOrWhiteSpace(query)
            ? _host.Missions.All
            : [.. _host.Missions.Match(query, key => T(key)).Select(m => m.Mission)];

        MissionResults.ItemsSource = missions.Select(m =>
        {
            (Brush tint, Brush ink) = MissionTone(m.Route.Kind);

            string restart = T($"app.mission.restart.{Camel(m.Restart.ToString())}");
            string meta = m.EstimatedMinutes > 0
                ? $"{T("app.mission.time", Args(("minutes", N(m.EstimatedMinutes))))} · {restart}"
                : restart;

            return new MissionCard(
                m.Id,
                T(m.TitleKey),
                T(m.DescriptionKey),
                GlyphOf(m.Glyph),
                meta,
                T(m.RecoveryKey),
                RouteLabel(m.Route),
                tint,
                ink);
        }).ToList();

        MissionNoMatch.Visibility = missions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static (Brush Tint, Brush Ink) MissionTone(RouteKind kind) => kind switch
    {
        RouteKind.Outcome => (Freeze("#E0E7FF"), Freeze("#4338CA")),
        RouteKind.Guide => (Freeze("#FEF3C7"), Freeze("#B45309")),
        _ => (Freeze("#DBEAFE"), Freeze("#1D4ED8")),
    };

    private void OnMissionOpen(object sender, RoutedEventArgs e)
    {
        if (_host is null || sender is not Button { Tag: string id })
        {
            return;
        }

        if (_host.Missions.Find(id) is { } mission)
        {
            FollowRoute(mission.Route);
        }
    }

    /// <summary>Ctrl+K from anywhere: the mission box, with what was there selected.</summary>
    private void FocusMissionSearch()
    {
        GoTo(ViewDashboard);
        MissionSearch.Focus();
        MissionSearch.SelectAll();
    }

    // ---------------------------------------------------------------- incident mode

    private void ApplyIncidentStrings()
    {
        IncidentTitle.Text = T("app.incident.title");
        IncidentHint.Text = T("app.incident.hint");
        IncidentWhen.Text = T("app.incident.when");
        IncidentToday.Content = T("app.incident.today");
        IncidentYesterday.Content = T("app.incident.yesterday");
        IncidentThreeDays.Content = T("app.incident.threeDays");
        IncidentWeek.Content = T("app.incident.week");
        IncidentEmpty.Text = T("app.incident.none");
        IncidentRescan.Content = T("app.incident.rescan");
    }

    private void OnIncidentWindowChanged(object sender, RoutedEventArgs e)
    {
        if (Ready && IncidentList is not null)
        {
            RenderIncident();
        }
    }

    /// <summary>
    /// What changed in the 48 hours before the symptom, and the smallest reversible test for each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built from <c>TimelineBuilder.Preceding</c>, which had tests and no caller. The moment is
    /// chosen by the user from four chips; the window is fixed at two days, because a symptom
    /// noticed on Monday morning was usually caused by something on Friday night.
    /// </para>
    /// <para>
    /// Every row says which kind of evidence it is. A crash or a device error <em>is</em> the
    /// symptom's own trace: direct. An update or a driver install merely happened first: the same
    /// time, and that is all it proves. The memo asked for that line to be drawn on every row and
    /// it is, because "the update did it" is the conclusion people reach without help, and the
    /// honest tool is the one that says how sure it is.
    /// </para>
    /// </remarks>
    private void RenderIncident()
    {
        if (_host is null || _timeline is null)
        {
            IncidentCard.Visibility = Visibility.Collapsed;
            return;
        }

        IncidentCard.Visibility = Visibility.Visible;

        DateTimeOffset now = _host.Clock.Now;
        DateTimeOffset moment =
            IncidentToday.IsChecked == true ? now
            : IncidentYesterday.IsChecked == true ? now.AddDays(-1)
            : IncidentWeek.IsChecked == true ? now.AddDays(-7)
            : now.AddDays(-3);

        IReadOnlyList<ChangeEvent> preceding = TimelineBuilder.Preceding(_timeline, moment, TimeSpan.FromHours(48));

        IncidentEmpty.Visibility = preceding.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        IncidentList.ItemsSource = preceding.Select((ev, i) =>
        {
            bool direct = ev.Category is EventCategory.Crash or EventCategory.Device;

            (string label, string tag) = ev switch
            {
                { Category: EventCategory.Transaction, RelatedTransaction: not null } => (T("app.incident.test.undo"), "undo"),
                { Category: EventCategory.Update } => (T("app.incident.test.update"), "update"),
                { Category: EventCategory.Driver } => (T("app.incident.test.driver"), "driver"),
                _ => (string.Empty, string.Empty),
            };

            return new IncidentRow(
                ev.Timestamp.LocalDateTime.ToString("dd/MM HH:mm", CultureInfo.CurrentCulture),
                T($"event.category.{Camel(ev.Category.ToString())}"),
                ev.After is { } after ? $"{ev.Component} → {after}" : ev.Component,
                T(direct ? "app.incident.direct" : "app.incident.correlated"),
                direct ? B("BadSoft") : B("Hair"),
                direct ? B("Bad") : B("Muted"),
                label,
                label.Length == 0 ? Visibility.Collapsed : Visibility.Visible,
                tag,
                Stripe(i));
        }).ToList();
    }

    /// <summary>The smallest reversible test: undo our own change, look at the driver, or open Windows' update history.</summary>
    private void OnIncidentTest(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        FollowRoute(tag switch
        {
            "undo" => Route.ToPage(PageKeys.Plan, "history"),
            "driver" => Route.ToPage(PageKeys.Drivers),
            _ => Route.ToSettings("ms-settings:windowsupdate-history"),
        });
    }

    private async void OnIncidentRescan(object sender, RoutedEventArgs e)
    {
        await RescanAsync();
        GoTo(ViewCompare);
    }

    // ---------------------------------------------------------------- recovery ladder

    private void ApplyLadderStrings()
    {
        LadderTitle.Text = T("app.ladder.title");
        LadderHint.Text = T("app.ladder.hint");
    }

    /// <summary>
    /// Recovery from the least disruptive option up, each with its readiness read from the machine.
    /// </summary>
    /// <remarks>
    /// The ISO reinstall used to be the only recovery the app knew how to start, which made the
    /// most disruptive option the first one offered. Windows has four gentler ones, and they are
    /// listed in the order a careful technician would try them. Each rung's readiness comes from
    /// readings the app already has — WinRE, drive encryption, restore points, the Windows build —
    /// and a rung whose precondition is unreadable says so rather than pretending to be green.
    /// </remarks>
    private void RenderLadder()
    {
        if (_snapshot is null)
        {
            return;
        }

        int build = _snapshot.Machine.OsBuild;
        CapabilityValue winre = _snapshot.ValueOf(CoreCapabilities.RecoveryEnvironment);
        CapabilityValue restore = _snapshot.ValueOf(CoreCapabilities.SystemRestore);
        CapabilityValue restoreAge = _snapshot.ValueOf(CoreCapabilities.RestorePointAgeDays);
        CapabilityValue bitlocker = _snapshot.ValueOf(CoreCapabilities.BitLockerSystemDrive);

        (string Readiness, Brush Tint, Brush Ink) Ready() => (T("app.ladder.ready"), B("GoodSoft"), B("Good"));
        (string Readiness, Brush Tint, Brush Ink) Attention(string key) => (T(key), B("WarnSoft"), B("Warn"));
        (string Readiness, Brush Tint, Brush Ink) Unknown() => (T("app.ladder.unknown"), B("Hair"), B("Muted"));

        List<LadderRow> rows = [];
        int i = 0;

        void Add(string key, (string Readiness, Brush Tint, Brush Ink) state, string tag, bool enabled)
        {
            rows.Add(new LadderRow(
                N(rows.Count + 1),
                T($"app.ladder.{key}.title"),
                T($"app.ladder.{key}.detail"),
                state.Readiness,
                state.Tint,
                state.Ink,
                T("app.ladder.open"),
                tag,
                enabled,
                Stripe(i++)));
        }

        // 1. Reinstall through Windows Update, keeping everything. Windows 11 22H2 and later.
        if (build >= 22621)
        {
            Add("wuRepair", Ready(), "wu-repair", enabled: true);
        }

        // 2. Uninstall the most recent update.
        Add("uninstallUpdate", Ready(), "update-history", enabled: true);

        // 3. System Restore, when it is on and has a point to go back to.
        var restoreState = restore.Status switch
        {
            CapabilityStatus.Enabled when restoreAge.IsKnown => Ready(),
            CapabilityStatus.Enabled => Attention("app.ladder.noPoint"),
            CapabilityStatus.Disabled => Attention("app.ladder.restoreOff"),
            _ => Unknown(),
        };

        Add("systemRestore", restoreState, restore.Status == CapabilityStatus.Disabled ? "restore-on" : "restore", enabled: true);

        // 4. Startup Repair in the recovery environment.
        var winreState = winre.Status switch
        {
            CapabilityStatus.Enabled when bitlocker.Status == CapabilityStatus.Enabled => Attention("app.ladder.bitlockerKey"),
            CapabilityStatus.Enabled => Ready(),
            CapabilityStatus.Disabled => Attention("app.ladder.winreOff"),
            _ => Unknown(),
        };

        Add("winre", winreState, "winre", enabled: winre.Status != CapabilityStatus.Disabled);

        // 5. Reset this PC.
        Add("reset", bitlocker.Status == CapabilityStatus.Enabled ? Attention("app.ladder.bitlockerKey") : Ready(), "reset", enabled: true);

        // 6. Repair install from an ISO — the page that already exists.
        Add("iso", Ready(), "iso", enabled: true);

        LadderList.ItemsSource = rows;
    }

    private void OnLadderOpen(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        switch (tag)
        {
            case "wu-repair":
            case "reset":
                FollowRoute(Route.ToSettings("ms-settings:recovery"));
                break;

            case "update-history":
                FollowRoute(Route.ToSettings("ms-settings:windowsupdate-history"));
                break;

            case "restore-on":
                FollowRoute(Route.ToOutcome("outcome.system-restore-on"));
                break;

            case "restore":
                // Windows' own System Restore wizard. It shows the points and does the work.
                OpenExternal("rstrui.exe");
                break;

            case "winre":
                RestartIntoRecovery();
                break;

            case "iso":
                FollowRoute(Route.ToPage(PageKeys.Windows));
                break;
        }
    }

    /// <summary>
    /// Restart into the recovery environment, where Startup Repair lives.
    /// </summary>
    /// <remarks>
    /// The same shape as the restart into firmware setup: ten seconds' warning and a way to cancel,
    /// because a restart the user did not brace for is the one that loses their unsaved work. An
    /// encrypted drive is told about its recovery key first, because WinRE asks for it.
    /// </remarks>
    private void RestartIntoRecovery()
    {
        if (_snapshot is null)
        {
            return;
        }

        bool encrypted = _snapshot.ValueOf(CoreCapabilities.BitLockerSystemDrive).Status == CapabilityStatus.Enabled;

        string message = T("app.ladder.winreConfirm") + (encrypted ? "\n\n" + T("firmware.consequence.bitlockerKey") : string.Empty);

        if (MessageBox.Show(this, message, T("app.title"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            using Process? started = Process.Start(new ProcessStartInfo("shutdown.exe", "/r /o /t 10")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            MessageBox.Show(this, T("app.ladder.winreStarted"), T("app.title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("app.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

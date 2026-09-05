using System.Globalization;
using PcOrbit.Adapters.Windows;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Apps;
using PcOrbit.Core.Firmware;
using PcOrbit.Core.Checkup;
using PcOrbit.Core.Cleanup;
using PcOrbit.Core.Compare;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Events;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Guides;
using PcOrbit.Core.Serialization;
using PcOrbit.Core.Model;
using PcOrbit.Core.Outcomes;
using PcOrbit.Core.Preflight;
using PcOrbit.Core.Transactions;

namespace PcOrbit.Cli;

/// <summary>Process exit codes, so the CLI can be used from a script or a CI job.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;

    /// <summary>The outcome was not reached, or preflight blocked the plan.</summary>
    public const int NotReached = 2;

    /// <summary>Work is staged and waiting for a restart. Not a failure.</summary>
    public const int RestartRequired = 3;
}

/// <summary>
/// The commands.
/// </summary>
/// <remarks>
/// No product logic lives here. Each command reads the machine, asks the compiler, prints, and
/// hands work to the transaction engine — which is what lets the desktop UI sit on the same seams
/// later without reimplementing anything.
/// <para>
/// User-facing prose goes through the string catalog (spec 21.11). The <c>--verbose</c> lines and
/// <c>doctor</c> stay in English on purpose: they are developer and support output, and spec 21.11
/// wants evidence and logs to keep their original values so a support engineer reading a report
/// sees the same text the machine produced.
/// </para>
/// </remarks>
public static class Commands
{
    // ---------------------------------------------------------------- scan

    public static async Task<int> ScanAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        StateSnapshot snapshot = await host.Scanner.ScanAsync(ct).ConfigureAwait(false);
        await host.Snapshots.SaveAsync(snapshot, ct).ConfigureAwait(false);

        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(new
            {
                snapshot.Id,
                snapshot.TakenAt,
                snapshot.Machine,
                snapshot.Machine.Fingerprint,
                Tier = SupportTierResolver.Resolve(snapshot.Machine, host.Catalog, FirmwareGuide(host), host.Today).ToString(),
                snapshot.Readings,
            }));

            return ExitCodes.Ok;
        }

        PrintMachineHeader(host, snapshot, output);
        Output.Heading(output.Text("cli.scan.heading"));

        foreach (CapabilityReading reading in snapshot.Readings)
        {
            CapabilityNode? node = host.Graph.Node(reading.Capability);
            string name = node is null
                ? reading.Capability.Value
                : output.CapabilityName(reading.Capability, node.DisplayKey);

            // "Unknown days" reads as a measurement. A unit belongs to a number, so it only appears
            // when there is one.
            string unit = node?.Unit is { } u && reading.Value.IsKnown ? " " + u : string.Empty;

            Output.Line($"  {name,-42} {output.Status(reading.Value)}{unit}");

            // Spec 6.1 and 6.6: an Unknown always says why, and Advanced mode can see every source.
            if (!reading.Value.IsKnown)
            {
                Output.Line($"  {"",-42} - {reading.Evidence.Source}");
            }
            else if (output.Verbose)
            {
                Output.Line($"  {"",-42} - {reading.Evidence.SourceKind}: {reading.Evidence.Source} [{reading.Evidence.Confidence}]");
            }
        }

        Output.Line();
        Output.Line(output.Text("cli.scan.saved", Output.Args(
            ("id", snapshot.Id),
            ("path", host.Database.DatabasePath))));

        return ExitCodes.Ok;
    }

    // ---------------------------------------------------------------- checkup

    public static async Task<int> CheckupAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        StateSnapshot snapshot = await host.Scanner.ScanAsync(ct).ConfigureAwait(false);
        await host.Snapshots.SaveAsync(snapshot, ct).ConfigureAwait(false);

        var context = new CheckupContext(snapshot, host.Graph, host.Catalog);
        IReadOnlyList<Finding> findings = PcOrbitHost.Checkup.Run(context);
        HealthVerdict verdict = HealthScore.Evaluate(context, findings);

        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(new { Verdict = verdict, Findings = findings }));
            return ExitCodes.Ok;
        }

        PrintMachineHeader(host, snapshot, output);

        // Spec 21.5 point 4: "nothing to do" is a good result and gets designed properly, not
        // reduced to an empty list.
        if (findings.Count == 0)
        {
            Output.Heading(output.Text("cli.checkup.ok.title"));
            Output.Line("  " + output.Text("cli.checkup.ok.body"));
            PrintUnreadable(verdict, output);
            return ExitCodes.Ok;
        }

        Output.Heading(output.Text("cli.checkup.heading", Output.Args(("count", Output.Number(findings.Count)))));
        PrintUnreadable(verdict, output);

        foreach (Finding finding in findings)
        {
            // The four lines of spec 21.6: what, what you gain, why it is safe, what it costs.
            Output.Line();
            Output.Line($"  [{finding.Severity}] {output.Text(finding.TitleKey, finding.Arguments)}");
            Output.Line($"    {output.Text(finding.BenefitKey, finding.Arguments)}");
            Output.Line($"    {output.Text(finding.SafetyKey, finding.Arguments)}");

            // Named, not summarised: "Secure Boot and TPM are off" and "your PC is too old" lead
            // to completely different decisions, and only the list separates them.
            foreach (CapabilityId related in finding.RelatedCapabilities)
            {
                CapabilityNode? node = host.Graph.Node(related);

                Output.Line($"      - {(node is null ? related.Value : output.CapabilityName(related, node.DisplayKey))}"
                    + $": {output.Status(snapshot.ValueOf(related))}");
            }

            Output.Line($"    {output.Text("cli.checkup.cost", Output.Args(
                ("restart", output.Restart(finding.Restart)),
                ("seconds", Output.Number(Math.Max(1, finding.EstimatedSeconds)))))}");

            if (finding.HasFix)
            {
                string how = finding.SuggestedOutcomeId is { } outcomeId
                    ? $"pco plan {outcomeId}"
                    : $"pco apply ... ({finding.SuggestedActionId})";

                Output.Line($"    {output.Text("cli.checkup.fix", Output.Args(("how", how)))}");
            }
            else
            {
                Output.Line($"    {output.Text("cli.checkup.noFix")}");
            }

            if (output.Verbose)
            {
                Output.Line($"    Evidence: {finding.Evidence.SourceKind}: {finding.Evidence.Source} [{finding.Evidence.Confidence}]");
            }
        }

        return ExitCodes.Ok;
    }

    // ---------------------------------------------------------------- plan

    public static async Task<int> PlanAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);

        Outcome outcome = RequireOutcome(host, options);

        StateSnapshot snapshot = await host.Scanner.ScanAsync(ct).ConfigureAwait(false);
        await host.Snapshots.SaveAsync(snapshot, ct).ConfigureAwait(false);

        Plan plan = host.CreateCompiler().Compile(snapshot, outcome);
        PreflightReport preflight = RunPreflight(host, options, plan, snapshot);

        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(new { Plan = plan, Preflight = preflight }));
            return Verdict(plan, preflight);
        }

        PrintMachineHeader(host, snapshot, output);
        PrintPlan(host, plan, preflight, output);
        return Verdict(plan, preflight);
    }

    // ---------------------------------------------------------------- apply

    public static async Task<int> ApplyAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        Outcome outcome = RequireOutcome(host, options);

        StateSnapshot snapshot = await host.Scanner.ScanAsync(ct).ConfigureAwait(false);
        await host.Snapshots.SaveAsync(snapshot, ct).ConfigureAwait(false);

        Plan plan = host.CreateCompiler().Compile(snapshot, outcome);
        PreflightReport preflight = RunPreflight(host, options, plan, snapshot);

        PrintMachineHeader(host, snapshot, output);
        PrintPlan(host, plan, preflight, output);

        if (plan.Outlook == PlanOutlook.AlreadySatisfied)
        {
            return ExitCodes.Ok;
        }

        ExecutionMode mode = options.DryRun ? ExecutionMode.DryRun : ExecutionMode.Apply;

        // A plan the compiler could not build cannot be simulated either — there is nothing to
        // walk through. But a preflight blocker is about the real apply, and refusing to preview
        // a plan because the user lacks the rights to run it for real would be backwards: the
        // preview is exactly how they find out what they will need.
        if (plan.HasBlockers || (preflight.IsBlocked && mode == ExecutionMode.Apply))
        {
            Output.Line();
            Output.Error(output.Text("cli.apply.blocked"));
            return ExitCodes.NotReached;
        }

        if (preflight.IsBlocked)
        {
            Output.Line();
            Output.Line("  " + output.Text("cli.apply.dryRunDespiteBlockers"));
        }

        if (mode == ExecutionMode.Apply && !options.AssumeYes && !Confirm(plan, output))
        {
            Output.Line(output.Text("cli.apply.cancelled"));
            return ExitCodes.Ok;
        }

        // Spec 9.1: the plan the user reviewed is locked by hash before anything is executed.
        Transaction transaction = host.Engine.Begin(plan, mode, plan.Hash);

        Transaction result = await host.Engine
            .RunAsync(transaction, host.Transactions, host.Events, ct)
            .ConfigureAwait(false);

        PrintTransactionResult(result, output, options);
        return TransactionExitCode(result);
    }

    // ---------------------------------------------------------------- undo

    /// <summary>
    /// Undoes a transaction: the CLI face of spec 21.9's Undo panel.
    /// </summary>
    /// <remarks>
    /// Undo is a reverse transaction through the same preview → apply → verify as the original, so
    /// this command is deliberately shaped like <see cref="ApplyAsync"/>: show what will change and
    /// what it costs, run preflight, confirm, execute, report exact counts. What cannot be undone
    /// automatically is listed with the reason rather than silently dropped (spec 9.4, 21.9).
    /// </remarks>
    public static async Task<int> UndoAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        Transaction? source;

        if (options.FirstArgument is { } id)
        {
            source = await host.Transactions.LoadAsync(id, ct).ConfigureAwait(false);

            if (source is null)
            {
                Output.Error(output.Text("cli.undo.notFound", Output.Args(("id", id))));
                return ExitCodes.Error;
            }
        }
        else
        {
            // No id: the most recent transaction that still has changes in effect — the top line
            // of the "Recently changed" panel (spec 21.9).
            IReadOnlyList<Transaction> recent = await host.Transactions
                .ListRecentAsync(50, ct)
                .ConfigureAwait(false);

            source = recent.FirstOrDefault(t =>
                t.Kind == TransactionKind.Apply
                && t.State != TransactionState.RolledBack
                && t.Steps.Any(HasTakenEffect));

            if (source is null)
            {
                Output.Line(output.Text("cli.undo.nothing"));
                return ExitCodes.Ok;
            }
        }

        if (source.Kind == TransactionKind.Undo)
        {
            Output.Error(output.Text("cli.undo.isUndo", Output.Args(("id", source.Id))));
            return ExitCodes.Error;
        }

        if (source.State == TransactionState.RolledBack)
        {
            Output.Line(output.Text("cli.undo.alreadyRolledBack", Output.Args(("id", source.Id))));
            return ExitCodes.Ok;
        }

        UndoPlan undo = new UndoPlanner(host.Clock).Plan(source);

        Output.Heading(output.Text("cli.undo.heading", Output.Args(
            ("id", source.Id),
            ("title", output.Text(FindTitleKey(host, source.Plan))))));

        if (undo.Excluded.Count > 0)
        {
            Output.Line("  " + output.Text("cli.undo.excluded.heading"));

            foreach (UndoExclusion exclusion in undo.Excluded)
            {
                string key = exclusion.Reason switch
                {
                    UndoStepBlocker.ByHand => "cli.undo.excluded.byHand",
                    UndoStepBlocker.NotAutomatic => "cli.undo.excluded.notAutomatic",
                    _ => "cli.undo.excluded.previousUnknown",
                };

                Output.Line("    - " + output.Text(key, Output.Args(
                    ("action", output.Text(exclusion.ActionTitleKey)))));
            }

            Output.Line();
        }

        if (undo.Plan is null)
        {
            Output.Line("  " + output.Text(undo.Excluded.Count > 0
                ? "cli.undo.noAutomatic"
                : "cli.undo.nothingChanged"));

            return undo.Excluded.Count > 0 ? ExitCodes.NotReached : ExitCodes.Ok;
        }

        // A fresh scan, exactly as apply does: preflight decisions belong to the machine as it is
        // now, not as it was when the original transaction ran.
        StateSnapshot snapshot = await host.Scanner.ScanAsync(ct).ConfigureAwait(false);
        await host.Snapshots.SaveAsync(snapshot, ct).ConfigureAwait(false);

        PreflightReport preflight = RunPreflight(host, options, undo.Plan, snapshot);
        PrintPlanDetails(host, undo.Plan, preflight, output);

        ExecutionMode mode = options.DryRun ? ExecutionMode.DryRun : ExecutionMode.Apply;

        if (preflight.IsBlocked && mode == ExecutionMode.Apply)
        {
            Output.Line();
            Output.Error(output.Text("cli.apply.blocked"));
            return ExitCodes.NotReached;
        }

        if (preflight.IsBlocked)
        {
            Output.Line();
            Output.Line("  " + output.Text("cli.apply.dryRunDespiteBlockers"));
        }

        if (mode == ExecutionMode.Apply && !options.AssumeYes && !ConfirmUndo(undo.Plan, output))
        {
            Output.Line(output.Text("cli.apply.cancelled"));
            return ExitCodes.Ok;
        }

        Transaction transaction = host.Engine.BeginUndo(undo.Plan, source, mode, undo.Plan.Hash);

        Transaction result = await host.Engine
            .RunAsync(transaction, host.Transactions, host.Events, ct)
            .ConfigureAwait(false);

        PrintTransactionResult(result, output, options);

        if (result.IsFinished && mode == ExecutionMode.Apply)
        {
            await ReportReconciledSourceAsync(host, result, output, ct).ConfigureAwait(false);
        }

        return TransactionExitCode(result);
    }

    private static bool HasTakenEffect(StepExecution step) => step.State is StepState.Applied
        or StepState.Verified
        or StepState.AwaitingRestart
        or StepState.AwaitingUserAction
        or StepState.VerifyFailed;

    private static bool ConfirmUndo(Plan plan, Output output)
    {
        Output.Line();
        Console.Write("  " + output.Text("cli.undo.confirm", Output.Args(
            ("count", Output.Number(plan.Cost.Changes)))) + " [y/N] ");

        string? answer = Console.ReadLine();
        return answer is not null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ReportReconciledSourceAsync(
        PcOrbitHost host,
        Transaction undoTransaction,
        Output output,
        CancellationToken ct)
    {
        Transaction? reconciled = await host.Engine
            .ReconcileUndoAsync(undoTransaction, host.Transactions, host.Events, ct)
            .ConfigureAwait(false);

        if (reconciled is null)
        {
            return;
        }

        Output.Line();
        Output.Line("  " + output.Text(
            reconciled.State == TransactionState.RolledBack
                ? "cli.undo.sourceRolledBack"
                : "cli.undo.sourcePartial",
            Output.Args(("id", reconciled.Id))));
    }

    // ---------------------------------------------------------------- resume

    public static async Task<int> ResumeAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(output);

        IReadOnlyList<Transaction> unfinished = await host.Transactions
            .ListUnfinishedAsync(ct)
            .ConfigureAwait(false);

        if (unfinished.Count == 0)
        {
            Output.Line(output.Text("cli.resume.nothing"));
            return ExitCodes.Ok;
        }

        int worst = ExitCodes.Ok;

        foreach (Transaction pending in unfinished)
        {
            Output.Heading(output.Text("cli.resume.heading", Output.Args(
                ("id", pending.Id),
                ("state", StateText(output, pending.State)))));

            if (pending.State != TransactionState.AwaitingRestart)
            {
                // Mid-flight when the process died. Spec 9.3 wants this state readable and
                // recoverable, not silently retried: a crash during apply deserves a human look.
                TransactionTally tally = pending.Tally;

                Output.Line("  " + output.Text("cli.resume.midFlight"));
                Output.Line("  " + output.Text("cli.resume.midFlightCounts", Output.Args(
                    ("completed", Output.Number(tally.Completed)),
                    ("failed", Output.Number(tally.Failed)),
                    ("pending", Output.Number(tally.Pending)))));
                Output.Line("  " + output.Text("cli.resume.midFlightAdvice"));

                worst = Math.Max(worst, ExitCodes.NotReached);
                continue;
            }

            if (string.Equals(pending.StartBootId, host.Boot.CurrentBootId, StringComparison.Ordinal))
            {
                Output.Line("  " + output.Text("cli.resume.stillWaiting", Output.Args(
                    ("restart", output.Restart(pending.PendingRestart)))));
                Output.Line("  " + output.Text("cli.resume.sameBoot"));

                worst = Math.Max(worst, ExitCodes.RestartRequired);
                continue;
            }

            Transaction result = await host.Engine
                .ResumeAsync(pending, host.Transactions, host.Events, ct)
                .ConfigureAwait(false);

            PrintTransactionResult(result, output, options);

            // A finished undo also settles the transaction it undoes (spec 21.9).
            if (result is { Kind: TransactionKind.Undo, IsFinished: true })
            {
                await ReportReconciledSourceAsync(host, result, output, ct).ConfigureAwait(false);
            }

            worst = Math.Max(worst, TransactionExitCode(result));
        }

        return worst;
    }

    // ---------------------------------------------------------------- history

    public static async Task<int> HistoryAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        IReadOnlyList<ChangeEvent> events = await host.Events
            .QueryAsync(new EventQuery(Limit: options.Limit), ct)
            .ConfigureAwait(false);

        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(events));
            return ExitCodes.Ok;
        }

        if (events.Count == 0)
        {
            Output.Line(output.Text("cli.history.empty"));
            return ExitCodes.Ok;
        }

        Output.Heading(output.Text("cli.history.heading", Output.Args(("count", Output.Number(events.Count)))));

        foreach (ChangeEvent e in events)
        {
            string change = (e.Before, e.After) switch
            {
                (null, null) => string.Empty,
                (null, { } after) => $"-> {after}",
                ({ } before, null) => $"{before} ->",
                ({ } before, { } after) => $"{before} -> {after}",
            };

            string when = e.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            Output.Line($"  {when}  {e.Category,-11} {e.Component,-40} {change}");

            if (output.Verbose)
            {
                Output.Line($"  {"",-19}  tx={e.RelatedTransaction ?? "-"} boot={e.RelatedRestart ?? "-"} by={e.Initiator} confidence={e.Confidence}");
            }
        }

        return ExitCodes.Ok;
    }

    // ---------------------------------------------------------------- apps

    /// <summary>
    /// The application catalogue, and what is already installed.
    /// </summary>
    /// <remarks>
    /// A curated list rather than a search over the whole repository, because a search box would
    /// make the allowlist meaningless and hand the job of judging thirty thousand packages to
    /// someone who came here to avoid exactly that (ADR 0006).
    /// </remarks>
    /// <summary>
    /// What the manufacturer's firmware interface reports on this machine.
    /// </summary>
    /// <remarks>
    /// Read-only, and there is no <c>--apply</c>. Writing a firmware setting needs the consequences
    /// on screen and a confirmation sized to the risk, and a terminal flag is the wrong shape for
    /// that (ADR 0007). This exists so the interface can be checked on a model without opening the
    /// app — which is exactly the evidence <c>docs/capability-matrix.md</c> asks for.
    /// </remarks>
    public static async Task<int> BiosAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        _ = output;

        FirmwareInterface firmware = await host.Firmware.ReadAsync(ct).ConfigureAwait(false);

        Output.Heading(host.Strings.Format("app.firmware.title"));

        // Developer output, so the vendor's own names and the query behind them stay as they are
        // (spec 21.11). This is the page a support engineer reads six months later.
        Output.Line($"  vendor              {firmware.Vendor ?? "(none)"}");
        Output.Line($"  availability        {firmware.Availability}");
        Output.Line($"  supervisor password {firmware.PasswordRequired}");
        Output.Line($"  settings            {firmware.Settings.Count}");

        if (firmware.Problem is { } problem)
        {
            Output.Line($"  problem             {problem}");
        }

        Output.Line();

        if (firmware.Settings.Count == 0)
        {
            Output.Line("  " + host.Strings.Format(
                firmware.Availability switch
                {
                    FirmwareAvailability.NeedsElevation => "app.firmware.needsElevation",
                    FirmwareAvailability.NeedsVendorTool => "app.firmware.dellNeedsTool",
                    FirmwareAvailability.ModelDoesNotImplement => "app.firmware.modelHasNone",
                    _ => "app.firmware.noInterface",
                },
                new Dictionary<string, string>(StringComparer.Ordinal) { ["vendor"] = firmware.Vendor ?? "" }));

            // From the stored scan rather than a fresh one: the guides are chosen by manufacturer,
            // which does not change between scans, and a firmware read should not cost a full sweep
            // of the machine.
            StateSnapshot? scan = await host.Snapshots.LoadLatestAsync(ct).ConfigureAwait(false);

            Output.Heading(host.Strings.Format("app.guide.title"));

            if (scan is null)
            {
                Output.Line("  " + host.Strings.Format("app.guide.none"));
            }
            else
            {
                PrintGuides(host, scan.Machine);
            }

            return ExitCodes.Ok;
        }

        foreach (FirmwareSetting setting in firmware.Settings)
        {
            Output.Line($"  {setting.Name,-40} {setting.Current,-18} {setting.Risk}");

            if (options.Verbose)
            {
                Output.Line($"      accepts: {(setting.Options.Count == 0 ? "(not stated)" : string.Join(" | ", setting.Options))}");
                Output.Line($"      via:     {setting.Evidence.Query}");
            }
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// What to do by hand, per this machine's manufacturer.
    /// </summary>
    /// <remarks>
    /// Printed after the interface report because it is the answer to what that report usually
    /// says. The setting name is the vendor's own English, untranslated, because it has to match a
    /// screen this product does not control (spec 21.11).
    /// </remarks>
    private static void PrintGuides(PcOrbitHost host, MachineIdentity machine)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        var any = false;

        foreach (GuideData guide in host.Guides.Values.OrderBy(g => g.Capability.Value, StringComparer.Ordinal))
        {
            if (guide.SelectFor(machine, today) is not { } entry)
            {
                if (guide.IsExpired(today))
                {
                    any = true;
                    Output.Line();
                    Output.Line($"  {host.Strings.Format($"cap.{guide.Capability.Value}")}");
                    Output.Line("      " + host.Strings.Format("app.guide.expired"));
                }

                continue;
            }

            any = true;

            Output.Line();
            Output.Line($"  {host.Strings.Format($"cap.{guide.Capability.Value}")}");
            Output.Line($"      setting   {entry.SettingName}");
            Output.Line($"      path      {string.Join("  >  ", entry.MenuPath)}");
            Output.Line($"      enter     {string.Join(" / ", entry.EnterKeys ?? ["F2", "Del"])}");

            if (entry.SaveKeys is { Count: > 0 } save)
            {
                Output.Line($"      save      {string.Join(" / ", save)}");
            }

            if (entry.AlternateNames is { Count: > 0 } alternates)
            {
                Output.Line($"      also      {string.Join(", ", alternates)}");
            }

            Output.Line($"      tier      {entry.Tier}");
        }

        if (!any)
        {
            Output.Line();
            Output.Line("  " + host.Strings.Format("app.guide.none"));
        }
    }

    public static async Task<int> AppsAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        // 'apps install <id>' / 'apps remove <id>'
        if (options.Arguments.Count >= 2 && options.Arguments[0] is "install" or "remove")
        {
            return await ChangeAppAsync(host, options, output, ct).ConfigureAwait(false);
        }

        InstalledApps installed = await host.AppService.InstalledAsync(ct).ConfigureAwait(false);

        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(new { host.Apps.All, Installed = installed.Ids, installed.Problem }));
            return ExitCodes.Ok;
        }

        Output.Heading(output.Text("cli.apps.heading", Output.Args(
            ("count", Output.Number(host.Apps.All.Count)))));

        if (installed.Problem is { } problem)
        {
            Output.Line("  " + problem);
            Output.Line();
        }

        foreach (string category in host.Apps.Categories)
        {
            Output.Line();
            Output.Line("  " + output.Text(category));

            foreach (CatalogApp app in host.Apps.InCategory(category))
            {
                bool here = installed.Ids.Contains(app.Id);

                string mark = !installed.IsKnown ? "?" : here ? "*" : " ";
                string state = !installed.IsKnown
                    ? string.Empty
                    : here ? output.Text("cli.apps.installed") : string.Empty;

                Output.Line($"    {mark} {Truncate(app.Name, 30),-30} {state,-14} {output.Text(app.DescriptionKey)}");

                if (output.Verbose)
                {
                    Output.Line($"      {"",-30} {app.Id}  ·  {app.Publisher}");
                }
            }
        }

        Output.Line();
        Output.Line("  " + output.Text("cli.apps.how"));

        return ExitCodes.Ok;
    }

    private static async Task<int> ChangeAppAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        bool install = options.Arguments[0] == "install";
        string id = options.Arguments[1];

        CatalogApp? app = host.Apps.Find(id);

        if (app is null)
        {
            Output.Error(output.Text("cli.apps.notInCatalogue", Output.Args(("id", id))));
            return ExitCodes.NotReached;
        }

        // Installing software downloads and runs an installer. That is worth a sentence and a
        // confirmation, not a silent side effect of a command that looked like a query.
        if (!options.AssumeYes)
        {
            Output.Line(output.Text(
                install ? "cli.apps.confirmInstall" : "cli.apps.confirmRemove",
                Output.Args(("name", app.Name), ("publisher", app.Publisher))));

            Console.Write("  > ");
            string? answer = Console.ReadLine()?.Trim();

            if (answer is not ("y" or "Y" or "yes" or "Yes"))
            {
                return ExitCodes.NotReached;
            }
        }

        Output.Line(output.Text(install ? "cli.apps.installing" : "cli.apps.removing",
            Output.Args(("name", app.Name))));

        AppChangeResult result = install
            ? await host.AppService.InstallAsync(app.Id, ct).ConfigureAwait(false)
            : await host.AppService.UninstallAsync(app.Id, ct).ConfigureAwait(false);

        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(result));
            return result.Verified ? ExitCodes.Ok : ExitCodes.NotReached;
        }

        if (result.Verified)
        {
            Output.Line("  " + output.Text(
                install ? "cli.apps.installed.done" : "cli.apps.removed.done",
                Output.Args(("name", app.Name))));

            return ExitCodes.Ok;
        }

        Output.Error(result.Problem ?? output.Text("cli.apps.notVerified", Output.Args(("name", app.Name))));
        return ExitCodes.NotReached;
    }

    // ---------------------------------------------------------------- clean

    /// <summary>
    /// Measures reclaimable space, and — with <c>--apply</c> — moves it to quarantine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is deleted. Files are moved into a store that keeps their original path for thirty
    /// days, so <c>pco restore</c> puts them back. That is the whole reason this feature could ship:
    /// undo everywhere else in this product means restoring the value read before the change, and a
    /// deleted file has no such value (ADR 0004, ADR 0005).
    /// </para>
    /// <para>
    /// The honest cost of that is stated rather than hidden: the disk does not get smaller until
    /// the retention window closes. An undo that quietly does not work would be the worse trade.
    /// </para>
    /// </remarks>
    public static async Task<int> CleanAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        CleanupSurvey survey = await host.CleanupScanner.SurveyAsync(ct).ConfigureAwait(false);

        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(survey));
            return ExitCodes.Ok;
        }

        Output.Heading(output.Text("cli.clean.heading"));

        foreach (CleanupCandidate candidate in survey.Candidates)
        {
            // Keyed on the candidate id, not the category: two temp folders are the same category
            // and different places, and one line saying "Temporary files" twice tells the reader
            // nothing about which one holds the three gigabytes.
            string label = output.Text($"cleanup.item.{candidate.Id}");
            string size = Output.Number((int)Math.Round(candidate.MegaBytes));
            string mark = candidate.CanReclaim ? " " : "·";

            // Three states, not two. "We will not remove this" and "there is nothing here to
            // remove" are different facts, and collapsing them libels an empty folder.
            string note = candidate.Trust is CleanupTrust.ReportOnly or CleanupTrust.Protected
                ? output.Text("cli.clean.reportOnly")
                : !candidate.CanReclaim
                    ? output.Text("cli.clean.alreadyClear")
                    : candidate.FreesSpaceNow
                        ? output.Text("cli.clean.regenerable")
                        : output.Text("cli.clean.reclaimable");

            Output.Line($"  {mark} {Truncate(label, 34),-34} {size,8} MB  {note}");

            if (output.Verbose)
            {
                Output.Line($"    {"",-34} {candidate.Path}");
                Output.Line($"    {"",-34} {candidate.Evidence.Source}");
            }
        }

        Output.Line();
        Output.Line("  " + output.Text("cli.clean.total", Output.Args(
            ("reclaimable", Output.Number((int)Math.Round(survey.ReclaimableBytes / 1024d / 1024d))),
            ("now", Output.Number((int)Math.Round(survey.ImmediateBytes / 1024d / 1024d))),
            ("reported", Output.Number((int)Math.Round(survey.ReportedBytes / 1024d / 1024d))))));

        // A survey that could not read part of the disk is a floor, not a total, and says so.
        foreach (string problem in survey.Problems)
        {
            Output.Line($"    - {problem}");
        }

        if (!options.Apply)
        {
            Output.Line();
            Output.Line("  " + output.Text("cli.clean.previewOnly"));
            return ExitCodes.Ok;
        }

        List<CleanupCandidate> actionable = [.. survey.Candidates.Where(c => c.CanReclaim)];

        if (actionable.Count == 0)
        {
            Output.Line();
            Output.Line("  " + output.Text("cli.clean.nothingToDo"));
            return ExitCodes.Ok;
        }

        if (!options.AssumeYes && !ConfirmCleanup(survey, output))
        {
            return ExitCodes.NotReached;
        }

        // Two routes, because the categories differ in what an undo could usefully restore.
        List<CleanupCandidate> regenerable = [.. actionable.Where(c => c.FreesSpaceNow)];
        List<CleanupCandidate> reclaimable = [.. actionable.Where(c => !c.FreesSpaceNow)];

        Output.Line();

        if (regenerable.Count > 0)
        {
            CleanupDeletion deletion = await host.Quarantine
                .DeleteRegenerableAsync(regenerable, null, ct)
                .ConfigureAwait(false);

            Output.Line("  " + output.Text("cli.clean.freed", Output.Args(
                ("mb", Output.Number((int)Math.Round(deletion.Freed / 1024d / 1024d))),
                ("files", Output.Number(deletion.Removed)))));

            // A running browser holds its own cache open. Saying so is the difference between an
            // honest total and a number that quietly did not happen.
            if (deletion.Locked > 0)
            {
                Output.Line("    " + output.Text("cli.clean.locked", Output.Args(
                    ("count", Output.Number(deletion.Locked)))));
            }
        }

        if (reclaimable.Count > 0)
        {
            QuarantineBatch batch = await host.Quarantine.QuarantineAsync(reclaimable, null, ct).ConfigureAwait(false);

            Output.Line("  " + output.Text("cli.clean.done", Output.Args(
                ("files", Output.Number(batch.Files.Count)),
                ("mb", Output.Number((int)Math.Round(batch.Bytes / 1024d / 1024d))),
                ("id", batch.Id),
                ("expires", batch.ExpiresAt.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)))));

            foreach (string failure in batch.Failures.Take(5))
            {
                Output.Line($"    - {failure}");
            }
        }

        return ExitCodes.Ok;
    }

    private static bool ConfirmCleanup(CleanupSurvey survey, Output output)
    {
        Output.Line();
        Output.Line("  " + output.Text("cli.clean.confirm", Output.Args(
            ("mb", Output.Number((int)Math.Round(survey.ReclaimableBytes / 1024d / 1024d))))));

        Console.Write("  > ");
        string? answer = Console.ReadLine();

        return answer is not null
            && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
                || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Lists quarantine batches, or puts one back.</summary>
    public static async Task<int> RestoreAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        // 'restore purge' — the user asking for their disk back before the window closes. Theirs
        // to ask, so long as what they give up is said first.
        if (options.FirstArgument == "purge")
        {
            long released = await host.Quarantine.PurgeAllAsync(ct).ConfigureAwait(false);

            Output.Line(output.Text("cli.restore.purged", Output.Args(
                ("mb", Output.Number((int)Math.Round(released / 1024d / 1024d))))));

            return ExitCodes.Ok;
        }

        if (options.FirstArgument is not { } batchId)
        {
            IReadOnlyList<QuarantineBatch> batches = await host.Quarantine.ListAsync(ct).ConfigureAwait(false);

            if (options.Json)
            {
                Output.Line(DataLocator.ToJson(batches));
                return ExitCodes.Ok;
            }

            Output.Heading(output.Text("cli.restore.heading"));

            if (batches.Count == 0)
            {
                Output.Line("  " + output.Text("cli.restore.empty"));
                return ExitCodes.Ok;
            }

            foreach (QuarantineBatch batch in batches)
            {
                Output.Line($"  {batch.Id}  {batch.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm}  "
                    + $"{batch.Files.Count,5} files  {batch.Bytes / 1024 / 1024,6} MB  "
                    + output.Text("cli.restore.expires", Output.Args(
                        ("date", batch.ExpiresAt.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)))));
            }

            Output.Line();
            Output.Line("  " + output.Text("cli.restore.how"));
            return ExitCodes.Ok;
        }

        QuarantineRestore restore = await host.Quarantine.RestoreAsync(batchId, ct).ConfigureAwait(false);

        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(restore));
            return restore.IsComplete ? ExitCodes.Ok : ExitCodes.NotReached;
        }

        Output.Line(output.Text("cli.restore.done", Output.Args(("count", Output.Number(restore.Restored)))));

        foreach (string failure in restore.Failures)
        {
            Output.Line($"  - {failure}");
        }

        return restore.IsComplete ? ExitCodes.Ok : ExitCodes.NotReached;
    }

    // ---------------------------------------------------------------- drivers

    /// <summary>
    /// The driver behind every device, and Windows' own verdict on whether it is working.
    /// </summary>
    /// <remarks>
    /// Sorted with faulty devices first and never by date. An old driver is not a fault, and a list
    /// that implies otherwise is how people are talked into installing something worse than what
    /// they had (ADR 0004).
    /// </remarks>
    public static async Task<int> DriversAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        DriverInventoryResult inventory = await host.Drivers.ReadAsync(ct).ConfigureAwait(false);

        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(inventory));
            return ExitCodes.Ok;
        }

        if (inventory.Problem is { } problem)
        {
            Output.Error(problem);
            return ExitCodes.Error;
        }

        Output.Heading(output.Text("cli.drivers.heading", Output.Args(
            ("count", Output.Number(inventory.Drivers.Count)))));

        if (inventory.Faulty.Count > 0)
        {
            Output.Line("  " + output.Text("cli.drivers.faulty", Output.Args(
                ("count", Output.Number(inventory.Faulty.Count)))));

            foreach (DriverEntry driver in inventory.Faulty)
            {
                Output.Line($"    ! {Truncate(driver.DeviceName, 46),-46} "
                    + output.Text("cli.drivers.problemCode", Output.Args(
                        ("code", Output.Number(driver.ProblemCode ?? 0)))));
            }

            Output.Line();
        }
        else
        {
            Output.Line("  " + output.Text("cli.drivers.allWorking"));
            Output.Line();
        }

        // The full list only in verbose: two hundred working devices is not information.
        if (output.Verbose)
        {
            foreach (DriverEntry driver in inventory.Drivers)
            {
                Output.Line($"    {Truncate(driver.DeviceName, 46),-46} {driver.Version,-18} "
                    + $"{driver.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—",-12} {driver.Provider}");
            }
        }
        else
        {
            Output.Line("  " + output.Text("cli.drivers.verboseHint"));
        }

        return ExitCodes.Ok;
    }

    // ---------------------------------------------------------------- startup

    /// <summary>
    /// Lists what starts with Windows.
    /// </summary>
    /// <remarks>
    /// Read-only, and it says so at the end rather than leaving the reader hunting for a button
    /// that is not there. Disabling an entry needs a parameter kind the action model does not have
    /// yet (ADR 0004), and a list that pretends otherwise would be worse than one that explains.
    /// </remarks>
    public static async Task<int> StartupAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        // 'startup on <name>' / 'startup off <name>' — the change half. The name is checked against
        // the machine's own inventory inside the controller before any command is built (ADR 0005).
        if (options.Arguments.Count >= 2 && options.Arguments[0] is "on" or "off")
        {
            bool enable = options.Arguments[0] == "on";
            string name = string.Join(' ', options.Arguments.Skip(1));

            StartupChangeResult change = await host.StartupControl
                .SetEnabledAsync(name, enable, ct)
                .ConfigureAwait(false);

            if (options.Json)
            {
                Output.Line(DataLocator.ToJson(change));
                return change.Verified ? ExitCodes.Ok : ExitCodes.NotReached;
            }

            if (change.Problem is { } refusal)
            {
                Output.Error(refusal);
                return ExitCodes.NotReached;
            }

            Output.Line(output.Text(
                change.Applied ? "cli.startup.changed" : "cli.startup.alreadyThere",
                Output.Args(
                    ("name", change.Name),
                    ("state", output.Text(enable ? "status.enabled" : "status.disabled")))));

            Output.Line("  " + output.Text("cli.startup.undo", Output.Args(
                ("how", $"pco startup {(enable ? "off" : "on")} {change.Name}"))));

            return ExitCodes.Ok;
        }

        StartupInventoryResult inventory = await host.Startup.ReadAsync(ct).ConfigureAwait(false);

        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(inventory));
            return ExitCodes.Ok;
        }

        if (inventory.Problem is { } problem)
        {
            Output.Error(problem);
            return ExitCodes.Error;
        }

        Output.Heading(output.Text("cli.startup.heading", Output.Args(
            ("count", Output.Number(inventory.Entries.Count)))));

        if (inventory.Entries.Count == 0)
        {
            Output.Line("  " + output.Text("cli.startup.empty"));
            return ExitCodes.Ok;
        }

        foreach (IGrouping<StartupLocation, StartupEntry> group in inventory.Entries.GroupBy(e => e.Location))
        {
            Output.Line();
            Output.Line("  " + output.Text($"startup.location.{Camel(group.Key.ToString())}"));

            foreach (StartupEntry entry in group)
            {
                // Three states, not two. "We could not tell" is its own word, because an entry
                // silently printed as on is an entry the reader will not think to check.
                string state = entry.Enabled switch
                {
                    true => output.Text("status.enabled"),
                    false => output.Text("status.disabled"),
                    null => output.Text("status.unknown"),
                };

                Output.Line($"    {Truncate(entry.Name, 38),-38} {state}");

                if (output.Verbose)
                {
                    Output.Line($"    {"",-38} {Truncate(entry.Command, 90)}");
                    Output.Line($"    {"",-38} {entry.Evidence.SourceKind}: {entry.Evidence.Source} [{entry.Evidence.Confidence}]");
                }
            }
        }

        Output.Line();
        Output.Line("  " + output.Text("cli.startup.readOnly"));

        return ExitCodes.Ok;
    }

    // ---------------------------------------------------------------- timeline

    /// <summary>
    /// Everything that changed on this machine recently, whoever changed it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>history</c> answers "what did PC Orbit do". This answers the question people actually
    /// arrive with: <em>what changed just before it started doing that?</em> Windows updates,
    /// driver installs, blue screens, hardware errors and restore points, on one axis with our own
    /// transactions (deep research §7.3).
    /// </para>
    /// <para>
    /// It is a correlation view and says so. Nothing here claims a cause — the ordering is the
    /// evidence, and the reader draws the conclusion.
    /// </para>
    /// </remarks>
    public static async Task<int> TimelineAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        DateTimeOffset since = host.Clock.Now.AddDays(-options.Days);

        IReadOnlyList<ChangeEvent> own = await host.Events
            .QueryAsync(new EventQuery(Since: since, Limit: options.Limit), ct)
            .ConfigureAwait(false);

        List<ChangeSourceResult> external = [];

        foreach (IChangeSource source in host.ChangeSources)
        {
            external.Add(await source.ReadAsync(since, ct).ConfigureAwait(false));
        }

        Timeline timeline = TimelineBuilder.Build(own, external, since, options.Limit);

        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(timeline));
            return ExitCodes.Ok;
        }

        Output.Heading(output.Text("cli.timeline.heading", Output.Args(
            ("days", Output.Number(options.Days)))));

        // Said before the list, not after: a reader who has already scrolled the events has
        // already decided the machine was quiet.
        if (timeline.Unavailable.Count > 0)
        {
            Output.Line("  " + output.Text(
                "cli.timeline.unavailable",
                Output.Args(("count", Output.Number(timeline.Unavailable.Count)))));

            foreach (UnavailableSource unavailable in timeline.Unavailable)
            {
                Output.Line($"    - {unavailable.Problem}");
            }
        }

        if (timeline.Events.Count == 0)
        {
            Output.Line("  " + output.Text("cli.timeline.empty", Output.Args(("days", Output.Number(options.Days)))));
            return ExitCodes.Ok;
        }

        Output.Line();

        foreach (ChangeEvent e in timeline.Events)
        {
            string when = e.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            string mark = e.Source == EventSource.PcOrbit ? "*" : " ";
            string after = e.After is { } value ? $" -> {value}" : string.Empty;

            Output.Line($"  {mark} {when}  {e.Category,-11} {Truncate(e.Component, 52)}{after}");

            if (output.Verbose)
            {
                Output.Line($"    {"",-19}  source={e.Source} evidence={e.Evidence?.Source ?? "-"}");
            }
        }

        Output.Line();
        Output.Line("  " + output.Text("cli.timeline.correlationOnly"));

        return ExitCodes.Ok;
    }

    /// <summary>Keeps one long update title from wrapping the whole table.</summary>
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    /// <summary>Enum name to string-catalog key suffix: <c>UserRegistry</c> to <c>userRegistry</c>.</summary>
    private static string Camel(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    // ---------------------------------------------------------------- diff

    /// <summary>
    /// Scans, then shows what has moved since a previous scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The research's BIOS Baseline &amp; Diff (§8.5), and the answer to the question nobody can
    /// currently answer after a firmware update or a CMOS reset: <em>which settings actually
    /// changed?</em> Vendors reset far more than they announce, and "compare it to how it was" has
    /// until now meant remembering.
    /// </para>
    /// <para>
    /// Readings we lost sight of are listed apart from readings that moved. An unelevated scan
    /// cannot see the TPM or drive encryption, and rolling those into the same list would report a
    /// BIOS update as having switched off the security chip (ADR 0004).
    /// </para>
    /// </remarks>
    public static async Task<int> DiffAsync(PcOrbitHost host, CliOptions options, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        StateSnapshot after = await host.Scanner.ScanAsync(ct).ConfigureAwait(false);
        StateSnapshot? before = await ResolveBaselineAsync(host, options, after, ct).ConfigureAwait(false);

        // Saved after the baseline is chosen, so "the scan before this one" cannot be this one.
        await host.Snapshots.SaveAsync(after, ct).ConfigureAwait(false);

        if (before is null)
        {
            Output.Line(output.Text("cli.diff.noBaseline"));
            return ExitCodes.Ok;
        }

        SnapshotComparison comparison = SnapshotDiff.Compare(before, after);

        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(comparison));
            return ExitCodes.Ok;
        }

        PrintMachineHeader(host, after, output);

        Output.Heading(output.Text("cli.diff.heading", Output.Args(
            ("before", before.TakenAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)),
            ("after", after.TakenAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)))));

        // Comparing two different PCs is a legitimate thing to want and an illegitimate thing to
        // present as one machine's history, so it is said out loud rather than silently allowed.
        if (!comparison.SameMachine)
        {
            Output.Line("  " + output.Text("cli.diff.differentMachine"));
        }

        if (comparison.IsUnchanged)
        {
            Output.Line("  " + output.Text("cli.diff.identical"));
            return ExitCodes.Ok;
        }

        PrintChanges(host, output, comparison.RealChanges, "cli.diff.changed");
        PrintChanges(host, output, comparison.VisibilityChanges, "cli.diff.visibility");

        return ExitCodes.Ok;
    }

    /// <summary>
    /// The snapshot to compare against: the one named on the command line, or the newest earlier
    /// scan of this same machine.
    /// </summary>
    private static async Task<StateSnapshot?> ResolveBaselineAsync(
        PcOrbitHost host,
        CliOptions options,
        StateSnapshot current,
        CancellationToken ct)
    {
        if (options.FirstArgument is { } explicitId)
        {
            return await host.Snapshots.LoadAsync(explicitId, ct).ConfigureAwait(false)
                ?? throw new CliUsageException($"No stored scan has the id '{explicitId}'. Run 'pco diff' with no id to use the previous scan.");
        }

        IReadOnlyList<SnapshotSummary> recent = await host.Snapshots
            .ListRecentAsync(50, ct)
            .ConfigureAwait(false);

        SnapshotSummary? previous = recent.FirstOrDefault(s =>
            s.Id != current.Id
            && string.Equals(s.MachineFingerprint, current.Machine.Fingerprint, StringComparison.Ordinal));

        return previous is null
            ? null
            : await host.Snapshots.LoadAsync(previous.Id, ct).ConfigureAwait(false);
    }

    private static void PrintChanges(
        PcOrbitHost host,
        Output output,
        IReadOnlyList<CapabilityChange> changes,
        string headingKey)
    {
        if (changes.Count == 0)
        {
            return;
        }

        Output.Line();
        Output.Line("  " + output.Text(headingKey, Output.Args(("count", Output.Number(changes.Count)))));

        foreach (CapabilityChange change in changes)
        {
            CapabilityNode? node = host.Graph.Node(change.Capability);

            string name = node is null
                ? change.Capability.Value
                : output.CapabilityName(change.Capability, node.DisplayKey);

            Output.Line($"    {name,-40} {output.Status(change.Before)} -> {output.Status(change.After)}");

            // A value that moved at the same time as its source moved is a weaker claim than one
            // that moved on its own, and the reader should be able to tell which they are looking at.
            if (change.EvidenceChanged)
            {
                Output.Line($"    {"",-40} {output.Text("cli.diff.sourceChanged")}");
            }

            if (output.Verbose)
            {
                Output.Line($"    {"",-40} was: {change.BeforeEvidence?.Source ?? "-"}");
                Output.Line($"    {"",-40} now: {change.AfterEvidence?.Source ?? "-"}");
            }
        }
    }

    // ---------------------------------------------------------------- outcomes

    public static int Outcomes(PcOrbitHost host, CliOptions options, Output output)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(host.Outcomes));
            return ExitCodes.Ok;
        }

        Output.Heading(output.Text("cli.outcomes.heading"));

        foreach (Outcome outcome in host.Outcomes)
        {
            Output.Line($"  {outcome.Id}  (v{outcome.Version})");
            Output.Line($"    {output.Text(outcome.TitleKey)}");
            Output.Line($"    {output.Text(outcome.DescriptionKey)}");
            Output.Line($"    {output.Text("cli.outcomes.summary", Output.Args(
                ("requires", Output.Number(outcome.Requires.Count)),
                ("checks", Output.Number(outcome.Verify.Count))))}");
            Output.Line();
        }

        return ExitCodes.Ok;
    }

    // ---------------------------------------------------------------- doctor

    /// <summary>
    /// Validates everything that ships as data, plus the executor allowlist.
    /// </summary>
    /// <remarks>
    /// Meant for CI as much as for users: spec 28 asks for graph, compiler and manifest validation,
    /// and this is the command that fails a build when shipped data and shipped code disagree.
    /// Output stays English — it is developer output, not product surface.
    /// </remarks>
    /// <summary>
    /// Knowledge-pack expiry, reported before it bites rather than after.
    /// </summary>
    /// <remarks>
    /// An expired pack fails closed at the point of use, which is right for the user and useless
    /// for whoever ships the build — they find out from a support ticket. This runs in CI, so the
    /// warning arrives while there is still time to revalidate the pack (ADR 0004).
    /// </remarks>
    private static IEnumerable<string> CheckGuidePacks(PcOrbitHost host)
    {
        DateOnly today = host.Today;
        DateOnly soon = today.AddDays(60);

        foreach ((string id, GuideData guide) in host.Guides.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (guide.ExpiresOn is not { } expiry)
            {
                yield return $"Guide pack '{id}' declares no expiresOn, so nothing will ever stop it being "
                    + "used after the vendor menus it describes have moved.";
                continue;
            }

            if (guide.IsExpired(today))
            {
                yield return $"Guide pack '{id}' expired on {expiry:yyyy-MM-dd}. Every guided step using it "
                    + "now refuses, and machines relying on it have dropped to read-only.";
            }
            else if (expiry <= soon)
            {
                yield return $"Guide pack '{id}' expires on {expiry:yyyy-MM-dd}, within 60 days. Revalidate it "
                    + "against current vendor firmware before it lapses.";
            }
        }
    }

    public static int Doctor(PcOrbitHost host, CliOptions options, Output output)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);

        List<string> problems = [];

        Output.Heading("Data and wiring check");

        Output.Line($"  data directory      {host.DataDirectory}");
        Output.Line($"  graph               {host.Graph.Version} · {host.Graph.Nodes.Count} nodes · {host.Graph.Edges.Count} edges");
        Output.Line($"  actions             {host.Catalog.All.Count}");
        Output.Line($"  outcomes            {host.Outcomes.Count}");
        Output.Line($"  guides              {host.Guides.Count}");
        Output.Line($"  applications        {host.Apps.All.Count}");
        Output.Line($"  missions            {host.Missions.All.Count}");

        problems.AddRange(MissionCatalogLoader.Validate(
            host.Missions,
            host.Outcomes.Select(o => o.Id).ToHashSet(StringComparer.Ordinal)));
        Output.Line($"  strings locale      {host.Strings.Locale}");
        Output.Line($"  executors allowed   {host.Executors.AllowedIds.Count}");
        Output.Line($"  elevated            {host.Elevation.IsElevated}");

        problems.AddRange(host.Graph.Validate());

        foreach (string unresolvable in host.Executors.FindUnresolvable(host.Catalog))
        {
            problems.Add($"Action references an executor that is not in the allowlist: {unresolvable}");
        }

        // Every capability an action or outcome mentions must exist in the graph, or a plan would
        // silently skip it.
        foreach (ActionDefinition action in host.Catalog.All)
        {
            foreach (CapabilityAssertion assertion in action.Provides.Concat(action.Requires))
            {
                if (!host.Graph.Contains(assertion.Capability))
                {
                    problems.Add($"Action '{action.Id}' mentions '{assertion.Capability}', which the graph does not declare.");
                }
            }

            if (action.IsManualStep && string.IsNullOrWhiteSpace(action.GuideId))
            {
                problems.Add($"Action '{action.Id}' is guided but names no guideId, so the user would get no instructions.");
            }

            if (action.GuideId is { } guideId && !host.Guides.ContainsKey(guideId))
            {
                problems.Add($"Action '{action.Id}' names guide '{guideId}', which is not installed.");
            }

            if (!host.Strings.Contains(action.TitleKey))
            {
                problems.Add($"Action '{action.Id}' has title key '{action.TitleKey}', which the '{host.Strings.Locale}' catalog does not have.");
            }
        }

        foreach (Outcome outcome in host.Outcomes)
        {
            foreach (CapabilityRequirement requirement in outcome.Requires)
            {
                if (!host.Graph.Contains(requirement.Capability))
                {
                    problems.Add($"Outcome '{outcome.Id}' requires '{requirement.Capability}', which the graph does not declare.");
                }
            }

            foreach (VerificationCheck check in outcome.Verify)
            {
                if (!host.Graph.Contains(check.Check))
                {
                    problems.Add($"Outcome '{outcome.Id}' verifies '{check.Check}', which the graph does not declare.");
                }
            }
        }

        // Spec 21.11: a display key with no string is a UI that has to invent a name.
        foreach (CapabilityNode node in host.Graph.Nodes)
        {
            if (!host.Strings.Contains(node.DisplayKey))
            {
                problems.Add($"Capability '{node.Id}' has display key '{node.DisplayKey}', which the '{host.Strings.Locale}' catalog does not have.");
            }
        }

        problems.AddRange(CheckGuidePacks(host));

        // Spec 21.11: an app whose description key does not ship is a card with a broken marker on
        // it. The name and publisher stay untranslated on purpose; everything around them does not.
        foreach (CatalogApp app in host.Apps.All)
        {
            foreach (string key in new[] { app.DescriptionKey, app.CategoryKey })
            {
                if (!host.Strings.Contains(key))
                {
                    problems.Add($"Application '{app.Id}' uses string '{key}', which the '{host.Strings.Locale}' catalog does not have.");
                }
            }
        }

        Output.Line();

        if (problems.Count == 0)
        {
            Output.Line("No problems found.");
            return ExitCodes.Ok;
        }

        Output.Error($"{problems.Count} problem(s):");

        foreach (string problem in problems)
        {
            Output.Error($"  - {problem}");
        }

        return ExitCodes.Error;
    }

    // ---------------------------------------------------------------- shared output

    private static Outcome RequireOutcome(PcOrbitHost host, CliOptions options)
    {
        if (options.FirstArgument is not { } outcomeId)
        {
            throw new CliUsageException("Which outcome? Run 'pco outcomes' for the list.");
        }

        return host.FindOutcome(outcomeId)
            ?? throw new CliUsageException($"No outcome '{outcomeId}'. Run 'pco outcomes' for the list.");
    }

    private static GuideData? FirmwareGuide(PcOrbitHost host) =>
        host.Guides.TryGetValue("guide.firmware.virtualization", out GuideData? guide) ? guide : null;

    private static void PrintMachineHeader(PcOrbitHost host, StateSnapshot snapshot, Output output)
    {
        SupportTier tier = SupportTierResolver.Resolve(snapshot.Machine, host.Catalog, FirmwareGuide(host), host.Today);

        Output.Line();
        Output.Line(output.Text("cli.header.pc", Output.Args(("name", snapshot.Machine.DisplayName))));
        Output.Line("  " + output.Text("cli.header.os", Output.Args(
            ("edition", snapshot.Machine.OsEdition),
            ("build", Output.Number(snapshot.Machine.OsBuild)),
            ("cpu", snapshot.Machine.CpuName))));
        Output.Line("  " + output.Text("cli.header.support", Output.Args(
            ("tier", output.Text(SupportTierResolver.DisplayKey(tier))))));

        if (!host.Elevation.IsElevated)
        {
            // Spec 21.10: a standard user still gets the full scan and checkup; what they get told
            // is which actions would need administrator rights.
            Output.Line("  " + output.Text("cli.header.standardUser"));
        }
    }

    /// <summary>
    /// Says how much of the machine the checkup could not see.
    /// </summary>
    /// <remarks>
    /// "Nothing found" and "nothing found, and twelve things were unreadable" are different
    /// results, and only one of them means the PC is fine. No rule fires on Unknown (spec 6.6),
    /// so without this line the good news would be reporting the absence of evidence as evidence
    /// of absence (ADR 0004).
    /// </remarks>
    private static void PrintUnreadable(HealthVerdict verdict, Output output)
    {
        if (!verdict.IsPartial)
        {
            return;
        }

        Output.Line("  " + output.Text(
            "health.partial",
            Output.Args(("count", Output.Number(verdict.UnreadableCount)))));
    }

    private static PreflightReport RunPreflight(
        PcOrbitHost host,
        CliOptions options,
        Plan plan,
        StateSnapshot snapshot)
    {
        HashSet<string> acknowledgements = [];

        if (options.RecoveryKeyConfirmed)
        {
            acknowledgements.Add(PreflightContext.Ack.BitLockerKeyConfirmed);
        }

        return PreflightRunner.Default.Run(
            new PreflightContext(plan, snapshot, host.Elevation.IsElevated, acknowledgements));
    }

    private static void PrintPlan(PcOrbitHost host, Plan plan, PreflightReport preflight, Output output)
    {
        Output.Heading(output.Text("cli.plan.heading", Output.Args(("title", output.Text(FindTitleKey(host, plan))))));

        if (plan.Outlook == PlanOutlook.AlreadySatisfied)
        {
            Output.Line("  " + output.Text("plan.outcome.alreadyReached"));
            return;
        }

        if (plan.Outlook == PlanOutlook.NotReachable)
        {
            Output.Line("  " + output.Text("plan.outcome.notReachable"));
        }

        PrintPlanDetails(host, plan, preflight, output);
    }

    private static void PrintPlanDetails(PcOrbitHost host, Plan plan, PreflightReport preflight, Output output)
    {
        output.PlanCostHeader(plan.Cost);

        foreach (PlanPhase phase in plan.Phases)
        {
            string stage = output.Text("cli.plan.stage", Output.Args(("number", Output.Number(phase.Index + 1))));

            string suffix = phase.RestartAfter == RestartKind.None
                ? string.Empty
                : "  (" + output.Text("cli.plan.thenRestart", Output.Args(("restart", output.Restart(phase.RestartAfter)))) + ")";

            Output.Line();
            Output.Line($"  {stage}{suffix}");

            foreach (PlanStep step in phase.Steps)
            {
                CapabilityNode? node = host.Graph.Node(step.Capability);
                string name = node is null
                    ? step.Capability.Value
                    : output.CapabilityName(step.Capability, node.DisplayKey);

                Output.Line($"    {step.Ordinal + 1}. {output.Text(step.Action.TitleKey)}");
                Output.Line($"       {name}: {output.Status(step.CurrentValue)} -> {output.Status(step.DesiredValue)}");

                // Spec step 6: every action explains why it is in the plan.
                string reason = step.RequiredByOutcome
                    ? output.Text("cli.plan.why.asked")
                    : step.RequiredBy.Count > 0
                        ? output.Text("cli.plan.why.neededBy", Output.Args(
                            ("capabilities", string.Join(", ", step.RequiredBy.Select(c => c.Value)))))
                        : output.Text("cli.plan.why.part");

                Output.Line($"       {output.Text("cli.plan.why", Output.Args(("reason", reason)))}");
                Output.Line($"       {output.Text("cli.plan.stepMeta", Output.Args(
                    ("mode", output.WriteModeLabel(step.Action.WriteMode)),
                    ("risk", output.Text($"cli.risk.{Output.Camel(step.Action.Risk.ToString())}")),
                    ("reversible", output.Reversibility(step.Action.Reversible.Mode)),
                    ("restart", output.Restart(step.Action.Restart))))}");

                if (output.Verbose)
                {
                    Output.Line($"       action {step.Action.Id}@{step.Action.Version} via {step.Action.Executor}");
                }
            }
        }

        if (plan.Issues.Count > 0)
        {
            Output.Line();
            Output.Line("  " + output.Text("cli.plan.notes"));

            foreach (PlanIssue issue in plan.Issues.OrderByDescending(i => i.Severity))
            {
                Output.Line($"    [{issue.Severity}] {issue.Code}: {issue.Detail}");
            }
        }

        if (preflight.Findings.Count > 0)
        {
            Output.Line();
            Output.Line("  " + output.Text("cli.plan.beforeApply"));

            foreach (PreflightFinding finding in preflight.Findings.OrderByDescending(f => f.Verdict))
            {
                string label = output.Text(finding.Verdict == PreflightVerdict.Blocked
                    ? "cli.label.blocked"
                    : "cli.label.note");

                Output.Line($"    [{label}] {output.Text(finding.MessageKey, finding.Arguments)}");

                if (finding.Verdict == PreflightVerdict.Blocked && finding.Kind == PreflightKind.Bitlocker)
                {
                    Output.Line($"             {output.Text("cli.plan.recoveryKeyHint")}");
                }

                if (output.Verbose && finding.Detail is { } detail)
                {
                    Output.Line($"             {detail}");
                }
            }
        }

        Output.Line();
        Output.Line("  " + output.Text("cli.plan.hash", Output.Args(
            ("hash", plan.Hash[..16]),
            ("graph", plan.GraphVersion),
            ("rules", plan.RuleVersion))));
        Output.Line("  " + output.Text("cli.plan.hashExplain"));
    }

    private static string FindTitleKey(PcOrbitHost host, Plan plan) =>
        host.Outcomes.FirstOrDefault(o => o.Id == plan.OutcomeId)?.TitleKey ?? plan.OutcomeId;

    private static bool Confirm(Plan plan, Output output)
    {
        Output.Line();
        Console.Write("  " + output.Text("cli.apply.confirm", Output.Args(
            ("count", Output.Number(plan.Cost.Changes)))) + " [y/N] ");

        string? answer = Console.ReadLine();
        return answer is not null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintTransactionResult(Transaction transaction, Output output, CliOptions options)
    {
        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(transaction));
            return;
        }

        TransactionTally tally = transaction.Tally;

        Output.Heading(output.Text("cli.result.heading", Output.Args(
            ("state", StateText(output, transaction.State)))));

        // Spec 9.4: exact counts, never a single "Done" for a multi-step transaction.
        Output.Line($"  {output.Text("cli.result.completed"),-22} {tally.Completed}");
        Output.Line($"  {output.Text("cli.result.failed"),-22} {tally.Failed}");
        Output.Line($"  {output.Text("cli.result.pending"),-22} {tally.Pending}");

        if (transaction.OutcomeVerdictKey is { } verdict)
        {
            Output.Line($"  {output.Text("cli.result.outcome"),-22} {output.Text(verdict)}");
        }

        Output.Line();

        foreach (StepExecution step in transaction.Steps)
        {
            Output.Line($"  {StepSymbol(step.State)} {step.ActionId}");

            Output.Line("      " + (step.Actual is { } actual
                ? output.Text("cli.result.stepActual", Output.Args(
                    ("capability", step.Capability.Value),
                    ("before", step.Before.Canonical),
                    ("requested", step.Requested.Canonical),
                    ("actual", actual.Canonical)))
                : output.Text("cli.result.step", Output.Args(
                    ("capability", step.Capability.Value),
                    ("before", step.Before.Canonical),
                    ("requested", step.Requested.Canonical)))));

            if (step.MessageKey is { } key)
            {
                Output.Line($"      {output.Text(key)}");
            }

            // Detail is technical, so it shows when it is actionable: a failure, a step the user
            // has to perform, Advanced mode — or a dry run, where "what it would have done" is
            // the entire reason someone asked.
            if (step.Detail is { } detail
                && (output.Verbose
                    || step.IsTerminalFailure
                    || step.State == StepState.AwaitingUserAction
                    || transaction.Mode == ExecutionMode.DryRun))
            {
                Output.Line($"      {detail}");
            }
        }

        if (transaction.NeedsRestart)
        {
            Output.Line();
            Output.Line("  " + output.Text("cli.result.next", Output.Args(
                ("restart", output.Restart(transaction.PendingRestart)))));
            Output.Line("  " + output.Text("cli.result.noExpiry"));
        }

        if (transaction.Mode == ExecutionMode.DryRun)
        {
            Output.Line();
            Output.Line("  " + output.Text("cli.result.dryRun"));
        }
    }

    private static string StateText(Output output, TransactionState state) =>
        output.Text($"transaction.state.{Output.Camel(state.ToString())}");

    private static string StepSymbol(StepState state) => state switch
    {
        StepState.Verified => "[ok]  ",
        StepState.Applied => "[done]",
        StepState.Skipped => "[skip]",
        StepState.AwaitingRestart => "[wait]",
        StepState.AwaitingUserAction => "[you] ",
        StepState.VerifyFailed => "[????]",
        StepState.Failed => "[fail]",
        StepState.RolledBack => "[back]",
        _ => "[    ]",
    };

    private static int Verdict(Plan plan, PreflightReport preflight) =>
        plan.HasBlockers || preflight.IsBlocked || plan.Outlook == PlanOutlook.NotReachable
            ? ExitCodes.NotReached
            : ExitCodes.Ok;

    private static int TransactionExitCode(Transaction transaction) => transaction.State switch
    {
        TransactionState.Completed => ExitCodes.Ok,
        TransactionState.AwaitingRestart => ExitCodes.RestartRequired,
        TransactionState.PartiallyCompleted or TransactionState.Failed => ExitCodes.NotReached,
        _ => ExitCodes.Ok,
    };
}

using System.Globalization;
using PcOrbit.Adapters.Windows;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Checkup;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Events;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Guides;
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
                Tier = SupportTierResolver.Resolve(snapshot.Machine, host.Catalog, FirmwareGuide(host)).ToString(),
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

            string unit = node?.Unit is { } u ? " " + u : string.Empty;

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

        IReadOnlyList<Finding> findings = PcOrbitHost.Checkup.Run(
            new CheckupContext(snapshot, host.Graph, host.Catalog));

        if (options.Json)
        {
            Output.Line(DataLocator.ToJson(findings));
            return ExitCodes.Ok;
        }

        PrintMachineHeader(host, snapshot, output);

        // Spec 21.5 point 4: "nothing to do" is a good result and gets designed properly, not
        // reduced to an empty list.
        if (findings.Count == 0)
        {
            Output.Heading(output.Text("cli.checkup.ok.title"));
            Output.Line("  " + output.Text("cli.checkup.ok.body"));
            return ExitCodes.Ok;
        }

        Output.Heading(output.Text("cli.checkup.heading", Output.Args(("count", Output.Number(findings.Count)))));

        foreach (Finding finding in findings)
        {
            // The four lines of spec 21.6: what, what you gain, why it is safe, what it costs.
            Output.Line();
            Output.Line($"  [{finding.Severity}] {output.Text(finding.TitleKey, finding.Arguments)}");
            Output.Line($"    {output.Text(finding.BenefitKey, finding.Arguments)}");
            Output.Line($"    {output.Text(finding.SafetyKey, finding.Arguments)}");
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
        SupportTier tier = SupportTierResolver.Resolve(snapshot.Machine, host.Catalog, FirmwareGuide(host));

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

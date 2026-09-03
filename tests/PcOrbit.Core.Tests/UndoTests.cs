using PcOrbit.Core.Actions;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Events;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;
using PcOrbit.Core.Outcomes;
using PcOrbit.Core.Transactions;

namespace PcOrbit.Core.Tests;

/// <summary>
/// Undo against the shipped graph and manifests: spec 21.9 says undo is a reverse transaction
/// through the same preview, apply and verify — so these tests exercise it through the same
/// engine, including the restart boundary, the lying executor, and the values nobody ever read.
/// </summary>
public sealed class UndoTests
{
    private static readonly CapabilityGraph Graph = ShippedData.Graph();
    private static readonly ActionCatalog Catalog = ShippedData.Catalog();

    private static Outcome DockerOutcome =>
        ShippedData.Outcomes().Single(o => o.Id == "outcome.docker-wsl2-ready");

    private static StateSnapshot NeedsWslAndVersion() =>
        SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Disabled)
            .With("wsl.default-version", CapabilityValue.Scalar("1"))
            .Build();

    private static StateSnapshot NeedsOnlyTheWslVersion() =>
        SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("1"))
            .Build();

    private static FakeReader MachineBeforeAnything() => new FakeReader()
        .Set("cpu.virtualization", CapabilityValue.Supported)
        .Set("firmware.cpu.virtualization", CapabilityValue.Enabled)
        .Set("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
        .Set("windows.feature.wsl", CapabilityValue.Disabled)
        .Set("wsl.default-version", CapabilityValue.Scalar("1"))
        .Set("workload.wsl2-ready", CapabilityValue.Scalar("notReady"))
        .Set("workload.docker-wsl2-ready", CapabilityValue.Scalar("notReady"));

    private sealed record Harness(
        TransactionEngine Engine,
        UndoPlanner Planner,
        Plan Plan,
        FakeReader Reader,
        FakeBootSession Boot,
        RecordingTransactionStore Store,
        RecordingEventLog Events,
        Dictionary<string, ScriptedExecutor> Executors);

    private static Harness Build(StateSnapshot snapshot, FakeReader reader)
    {
        var boot = new FakeBootSession();
        var clock = new FakeClock();
        var ids = new SequentialIds();

        Dictionary<string, ScriptedExecutor> executors = new(StringComparer.Ordinal);

        foreach (string id in new[]
        {
            "windows.optional-feature.enable",
            "windows.optional-feature.disable",
            "wsl.set-default-version",
            "windows.system-restore.enable",
            "security.bitlocker.suspend",
            "display.set-refresh-rate",
            "firmware.guided-change",
            "firmware.vendor.dell",
        })
        {
            executors[id] = new ScriptedExecutor(id);
        }

        var engine = new TransactionEngine(
            new ExecutorRegistry(executors.Values), reader, clock, ids, boot, "test");

        Plan plan = new StateCompiler(Graph, Catalog, null, clock, ids).Compile(snapshot, DockerOutcome);

        return new Harness(
            engine,
            new UndoPlanner(clock, ids),
            plan,
            reader,
            boot,
            new RecordingTransactionStore(),
            new RecordingEventLog(),
            executors);
    }

    /// <summary>Applies the two-stage Docker plan to completion: apply, reboot, resume, verify.</summary>
    private static async Task<Transaction> CompleteForwardRunAsync(Harness h)
    {
        h.Executors["windows.optional-feature.enable"].Result = ApplyOutcome.PendingRestart("staged");

        h.Executors["wsl.set-default-version"].OnApply = _ =>
        {
            h.Reader.Set("wsl.default-version", CapabilityValue.Scalar("2"));
            h.Reader.Set("workload.wsl2-ready", CapabilityValue.Scalar("ready"));
            h.Reader.Set("workload.docker-wsl2-ready", CapabilityValue.Scalar("ready"));
        };

        Transaction started = h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash);
        Transaction waiting = await h.Engine.RunAsync(started, h.Store, h.Events);

        h.Boot.Reboot();
        h.Reader.Set("windows.feature.wsl", CapabilityValue.Enabled);

        Transaction completed = await h.Engine.ResumeAsync(waiting, h.Store, h.Events);
        Assert.Equal(TransactionState.Completed, completed.State);

        return completed;
    }

    // ---------------------------------------------------------------- planning

    /// <summary>
    /// The reverse plan restores each value read before the original change, in reverse order —
    /// the last thing applied is the first thing restored.
    /// </summary>
    [Fact]
    public async Task PlansTheUndoInReverseOrderTargetingTheValuesFromBefore()
    {
        Harness h = Build(NeedsWslAndVersion(), MachineBeforeAnything());
        Transaction source = await CompleteForwardRunAsync(h);

        UndoPlan undo = h.Planner.Plan(source);

        Assert.NotNull(undo.Plan);
        Assert.Empty(undo.Excluded);

        List<PlanStep> steps = [.. undo.Plan!.Steps];
        Assert.Equal(2, steps.Count);

        // The WSL default version was the last forward step, so it is undone first.
        Assert.Equal("wsl.default-version", steps[0].Capability.Value);
        Assert.Equal("1", steps[0].DesiredValue.Canonical);

        Assert.Equal("windows.feature.wsl", steps[1].Capability.Value);
        Assert.Equal(CapabilityValue.Disabled, steps[1].DesiredValue);

        // Turning the component back off still costs the one restart, and the preview says so.
        Assert.Equal(RestartKind.Windows, undo.Plan.Phases[^1].RestartAfter);
        Assert.Equal(1, undo.Plan.Cost.Restarts);

        // The undo verifies against the machine: every restored capability, at its old value.
        Assert.Contains(undo.Plan.FinalVerification, c => c.Check.Value == "wsl.default-version" && c.Expected.Canonical == "1");
        Assert.Contains(undo.Plan.FinalVerification, c => c.Check.Value == "windows.feature.wsl" && c.Expected == CapabilityValue.Disabled);
    }

    // ---------------------------------------------------------------- the full reverse transaction

    /// <summary>
    /// The whole undo path: rollback, restart boundary, resume, verify against the machine —
    /// then the source transaction is marked rolled back, step by step.
    /// </summary>
    [Fact]
    public async Task UndoRunsRollbacksVerifiesAgainstTheMachineAndSettlesTheSource()
    {
        Harness h = Build(NeedsWslAndVersion(), MachineBeforeAnything());
        Transaction source = await CompleteForwardRunAsync(h);

        UndoPlan undo = h.Planner.Plan(source);

        h.Executors["wsl.set-default-version"].OnRollback = _ =>
            h.Reader.Set("wsl.default-version", CapabilityValue.Scalar("1"));

        h.Executors["windows.optional-feature.enable"].RollbackResult =
            ApplyOutcome.PendingRestart("staged removal");

        Transaction started = h.Engine.BeginUndo(undo.Plan!, source, ExecutionMode.Apply, undo.Plan!.Hash);

        Assert.Equal(TransactionKind.Undo, started.Kind);
        Assert.Equal(source.Id, started.UndoOf);

        Transaction waiting = await h.Engine.RunAsync(started, h.Store, h.Events);

        // The executor was asked to roll back, with the old value as the requested target and the
        // machine's current value as Before — never the other way round.
        ActionExecutionContext rollback = Assert.Single(h.Executors["wsl.set-default-version"].RolledBack);
        Assert.Equal("1", rollback.Requested.Canonical);
        Assert.Equal("2", rollback.Before.Canonical);

        Assert.Equal(TransactionState.AwaitingRestart, waiting.State);

        h.Boot.Reboot("boot-third");
        h.Reader.Set("windows.feature.wsl", CapabilityValue.Disabled);

        Transaction finished = await h.Engine.ResumeAsync(waiting, h.Store, h.Events);

        Assert.Equal(TransactionState.Completed, finished.State);
        Assert.Equal("verify.reached", finished.OutcomeVerdictKey);

        // Undo events land in the Rollback category, tied to the undo transaction (spec 14.2).
        Assert.Contains(h.Events.Events, e => e.Category == EventCategory.Rollback && e.RelatedTransaction == finished.Id);

        Transaction? settled = await h.Engine.ReconcileUndoAsync(finished, h.Store, h.Events);

        Assert.NotNull(settled);
        Assert.Equal(source.Id, settled!.Id);
        Assert.Equal(TransactionState.RolledBack, settled.State);
        Assert.All(settled.Steps, s => Assert.Equal(StepState.RolledBack, s.State));
    }

    /// <summary>
    /// The forward rule applies in reverse too: an executor that claims the rollback worked while
    /// the machine still reads the new value produces a failed verification — and the source
    /// transaction is <em>not</em> marked rolled back on the strength of that claim.
    /// </summary>
    [Fact]
    public async Task AnExecutorThatLiesAboutRollingBackFailsVerification()
    {
        FakeReader reader = MachineBeforeAnything()
            .Set("windows.feature.wsl", CapabilityValue.Enabled);

        Harness h = Build(NeedsOnlyTheWslVersion(), reader);

        h.Executors["wsl.set-default-version"].OnApply = _ =>
            h.Reader.Set("wsl.default-version", CapabilityValue.Scalar("2"));

        Transaction source = await h.Engine.RunAsync(
            h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash), h.Store, h.Events);

        UndoPlan undo = h.Planner.Plan(source);

        // Claims success; the fake machine stays at version 2.
        h.Executors["wsl.set-default-version"].RollbackResult = ApplyOutcome.Applied("claims rollback");

        Transaction result = await h.Engine.RunAsync(
            h.Engine.BeginUndo(undo.Plan!, source, ExecutionMode.Apply, undo.Plan!.Hash),
            h.Store,
            h.Events);

        StepExecution step = Assert.Single(result.Steps);
        Assert.Equal(StepState.VerifyFailed, step.State);
        Assert.Equal("2", step.Actual?.Canonical);

        Transaction? settled = await h.Engine.ReconcileUndoAsync(result, h.Store, h.Events);

        Assert.NotNull(settled);
        Assert.NotEqual(TransactionState.RolledBack, settled!.State);
        Assert.DoesNotContain(settled.Steps, s => s.State == StepState.RolledBack);
    }

    // ---------------------------------------------------------------- what undo refuses

    /// <summary>
    /// A value that was never read before the change is never "restored": inventing the target
    /// would turn Unknown into a write (spec 6.6, 27.13). The step is reported, not guessed at.
    /// </summary>
    [Fact]
    public async Task NeverRestoresAValueThatWasNeverRead()
    {
        // The reader cannot see the WSL default version at all, so the forward step records
        // Unknown as its before-value.
        FakeReader reader = MachineBeforeAnything()
            .Set("windows.feature.wsl", CapabilityValue.Enabled)
            .Remove("wsl.default-version");

        Harness h = Build(NeedsOnlyTheWslVersion(), reader);

        Transaction source = await h.Engine.RunAsync(
            h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash), h.Store, h.Events);

        UndoPlan undo = h.Planner.Plan(source);

        Assert.Null(undo.Plan);

        UndoExclusion exclusion = Assert.Single(undo.Excluded);
        Assert.Equal(UndoStepBlocker.PreviousValueUnknown, exclusion.Reason);
        Assert.Equal("wsl.default-version", exclusion.Capability.Value);
    }

    /// <summary>
    /// A step the user performed by hand is undone by hand. The exclusion says so instead of the
    /// app pretending it can reach into the firmware (spec 21.9).
    /// </summary>
    [Fact]
    public async Task AGuidedFirmwareStepIsExcludedAsByHand()
    {
        // Virtual Machine Platform is off too: were it on, the compiler would (rightly) infer that
        // firmware virtualization is actually enabled and plan no guided step at all (spec 8.3.4).
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Disabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Disabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("2"))
            .Build();

        FakeReader reader = new FakeReader()
            .Set("cpu.virtualization", CapabilityValue.Supported)
            .Set("firmware.cpu.virtualization", CapabilityValue.Disabled)
            .Set("windows.feature.virtual-machine-platform", CapabilityValue.Disabled)
            .Set("windows.feature.wsl", CapabilityValue.Enabled)
            .Set("wsl.default-version", CapabilityValue.Scalar("2"));

        Harness h = Build(snapshot, reader);

        h.Executors["firmware.guided-change"].Result =
            new ApplyOutcome(ApplyStatus.AwaitingUserAction, "action.guided.staged", "one step in firmware");

        Transaction source = await h.Engine.RunAsync(
            h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash), h.Store, h.Events);

        Assert.Equal(TransactionState.AwaitingRestart, source.State);

        UndoPlan undo = h.Planner.Plan(source);

        Assert.Null(undo.Plan);
        Assert.Contains(undo.Excluded, e => e.Reason == UndoStepBlocker.ByHand
            && e.Capability.Value == "firmware.cpu.virtualization");
    }

    [Fact]
    public async Task RefusesToUndoAnUndo()
    {
        Harness h = Build(NeedsWslAndVersion(), MachineBeforeAnything());
        Transaction source = await CompleteForwardRunAsync(h);

        UndoPlan undo = h.Planner.Plan(source);
        Transaction undoTransaction = h.Engine.BeginUndo(undo.Plan!, source, ExecutionMode.Apply, undo.Plan!.Hash);

        Assert.Throws<InvalidOperationException>(() => h.Planner.Plan(undoTransaction));
        Assert.Throws<InvalidOperationException>(
            () => h.Engine.BeginUndo(undo.Plan!, undoTransaction, ExecutionMode.Apply, undo.Plan!.Hash));
    }

    // ---------------------------------------------------------------- dry run

    /// <summary>A dry-run undo simulates everything and leaves nothing behind, like a forward dry run.</summary>
    [Fact]
    public async Task DryRunUndoWritesNothingAnywhere()
    {
        Harness h = Build(NeedsWslAndVersion(), MachineBeforeAnything());
        Transaction source = await CompleteForwardRunAsync(h);

        UndoPlan undo = h.Planner.Plan(source);

        var store = new RecordingTransactionStore();
        var events = new RecordingEventLog();

        Transaction result = await h.Engine.RunAsync(
            h.Engine.BeginUndo(undo.Plan!, source, ExecutionMode.DryRun, undo.Plan!.Hash),
            store,
            events);

        Assert.Equal(0, store.SaveCount);
        Assert.Empty(events.Events);
        Assert.Equal(TransactionState.Completed, result.State);

        // Nothing happened, so there is nothing to reconcile onto the source.
        Assert.Null(await h.Engine.ReconcileUndoAsync(result, store, events));

        // The executors were consulted in dry-run mode, never for real.
        Assert.All(
            h.Executors["wsl.set-default-version"].RolledBack,
            context => Assert.Equal(ExecutionMode.DryRun, context.Mode));
    }
}

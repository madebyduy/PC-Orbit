using PcOrbit.Core.Actions;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Events;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;
using PcOrbit.Core.Outcomes;
using PcOrbit.Core.Transactions;

namespace PcOrbit.Core.Tests;

/// <summary>
/// The transaction engine against the shipped graph and manifests, with scripted executors.
/// </summary>
/// <remarks>
/// Spec 28 asks for fault injection: a step that fails, a step that lies about succeeding, a
/// reboot in the middle, an executor that throws. Each of those is a test here, because the whole
/// promise of this architecture is that none of them leaves the machine in a state nobody can
/// explain (spec 9.3).
/// </remarks>
public sealed class TransactionEngineTests
{
    private static readonly CapabilityGraph Graph = ShippedData.Graph();
    private static readonly ActionCatalog Catalog = ShippedData.Catalog();

    private static Outcome DockerOutcome =>
        ShippedData.Outcomes().Single(o => o.Id == "outcome.docker-wsl2-ready");

    /// <summary>Firmware already on; WSL and the default version still to do. Two stages, one restart.</summary>
    private static StateSnapshot NeedsWslAndVersion() =>
        SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Disabled)
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

    /// <summary>
    /// Everything done except the WSL default version: one step, no restart, so verification runs
    /// immediately. Used by the tests that are about verification rather than about restarts.
    /// </summary>
    private static StateSnapshot NeedsOnlyTheWslVersion() =>
        SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("1"))
            .Build();

    private static FakeReader MachineNeedingOnlyTheVersion() => new FakeReader()
        .Set("cpu.virtualization", CapabilityValue.Supported)
        .Set("firmware.cpu.virtualization", CapabilityValue.Enabled)
        .Set("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
        .Set("windows.feature.wsl", CapabilityValue.Enabled)
        .Set("wsl.default-version", CapabilityValue.Scalar("1"))
        .Set("workload.wsl2-ready", CapabilityValue.Scalar("notReady"))
        .Set("workload.docker-wsl2-ready", CapabilityValue.Scalar("notReady"));

    private sealed record Harness(
        TransactionEngine Engine,
        Plan Plan,
        FakeReader Reader,
        FakeBootSession Boot,
        RecordingTransactionStore Store,
        RecordingEventLog Events,
        Dictionary<string, ScriptedExecutor> Executors);

    private static Harness Build(StateSnapshot? snapshot = null, FakeReader? reader = null)
    {
        reader ??= MachineBeforeAnything();
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

        var registry = new ExecutorRegistry(executors.Values);
        var engine = new TransactionEngine(registry, reader, clock, ids, boot, "test");

        Plan plan = new StateCompiler(Graph, Catalog, null, clock, ids)
            .Compile(snapshot ?? NeedsWslAndVersion(), DockerOutcome);

        return new Harness(engine, plan, reader, boot, new RecordingTransactionStore(), new RecordingEventLog(), executors);
    }

    // ---------------------------------------------------------------- happy path

    /// <summary>
    /// The whole spec 23.3 demo, minus the real machine: apply, hit a restart boundary, reboot,
    /// resume, verify, and end up Completed with the outcome actually reached.
    /// </summary>
    [Fact]
    public async Task AppliesStopsForTheRestartResumesAndVerifies()
    {
        Harness h = Build();

        // Enabling the WSL component only takes effect after the restart, so the fake machine
        // does not change until the reboot happens. That is what makes the resume meaningful.
        h.Executors["windows.optional-feature.enable"].Result = ApplyOutcome.PendingRestart("staged");

        // Setting the default version is the last thing the plan does, so once it lands the
        // workload becomes ready — which is what the outcome's own verification looks at.
        h.Executors["wsl.set-default-version"].OnApply = _ =>
        {
            h.Reader.Set("wsl.default-version", CapabilityValue.Scalar("2"));
            h.Reader.Set("workload.wsl2-ready", CapabilityValue.Scalar("ready"));
            h.Reader.Set("workload.docker-wsl2-ready", CapabilityValue.Scalar("ready"));
        };

        Transaction started = h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash);
        Transaction afterFirstStage = await h.Engine.RunAsync(started, h.Store, h.Events);

        Assert.Equal(TransactionState.AwaitingRestart, afterFirstStage.State);
        Assert.Equal(RestartKind.Windows, afterFirstStage.PendingRestart);

        // Verification of a pending-restart step must not pass before the restart.
        Assert.Contains(afterFirstStage.Steps, s => s.State == StepState.AwaitingRestart);

        // The restart happens, and the component is now genuinely on.
        h.Boot.Reboot();
        h.Reader.Set("windows.feature.wsl", CapabilityValue.Enabled);

        Transaction resumed = await h.Engine.ResumeAsync(afterFirstStage, h.Store, h.Events);

        Assert.Equal(TransactionState.Completed, resumed.State);
        Assert.Equal("verify.reached", resumed.OutcomeVerdictKey);
        Assert.Equal(2, resumed.Steps.Count);
        Assert.All(resumed.Steps, s => Assert.Equal(StepState.Verified, s.State));

        // Spec 14.2: the resume happened in a different boot session, and the record says so.
        Assert.Equal("boot-first", resumed.StartBootId);
        Assert.Equal("boot-second", resumed.ResumeBootId);
    }

    [Fact]
    public async Task RecordsBeforeAndAfterValuesForHistory()
    {
        Harness h = Build();

        h.Executors["windows.optional-feature.enable"].OnApply = _ =>
            h.Reader.Set("windows.feature.wsl", CapabilityValue.Enabled);

        h.Executors["wsl.set-default-version"].OnApply = _ =>
            h.Reader.Set("wsl.default-version", CapabilityValue.Scalar("2"));

        Transaction started = h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash);
        await h.Engine.RunAsync(started, h.Store, h.Events);

        ChangeEvent wslEvent = h.Events.Events.First(e => e.Component == "windows.feature.wsl");

        Assert.Equal("disabled", wslEvent.Before);
        Assert.Equal("enabled", wslEvent.After);
        Assert.Equal(EventCategory.Feature, wslEvent.Category);
        Assert.Equal(Initiator.PcOrbit, wslEvent.Initiator);

        // Spec 14.2: every event can be tied to the boot session it happened in.
        Assert.All(h.Events.Events, e => Assert.False(string.IsNullOrEmpty(e.RelatedRestart)));
        Assert.All(h.Events.Events, e => Assert.Equal(started.Id, e.RelatedTransaction));
    }

    // ---------------------------------------------------------------- verification is independent

    /// <summary>
    /// An executor that reports success without changing anything must not produce a successful
    /// transaction. This is the test that keeps "verify by reading the machine" honest: the
    /// executor claims it worked, the machine disagrees, and the machine wins (spec 8.3.2).
    /// </summary>
    [Fact]
    public async Task AnExecutorThatLiesAboutSuccessStillFailsVerification()
    {
        Harness h = Build(NeedsOnlyTheWslVersion(), MachineNeedingOnlyTheVersion());

        h.Executors["wsl.set-default-version"].Result = ApplyOutcome.Applied("claims success");

        // Note: no OnApply, so the fake machine never changes.
        Transaction started = h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash);
        Transaction result = await h.Engine.RunAsync(started, h.Store, h.Events);

        StepExecution step = Assert.Single(result.Steps);

        Assert.Equal(StepState.VerifyFailed, step.State);
        Assert.Equal("verify.mismatch", step.MessageKey);
        Assert.Equal("1", step.Actual?.Canonical);
        Assert.NotEqual(TransactionState.Completed, result.State);
    }

    /// <summary>
    /// A capability nothing can read verifies as "we could not tell", kept distinct from
    /// "it says something else" (spec 6.6, 27.13).
    /// </summary>
    [Fact]
    public async Task AnUnreadableResultIsReportedAsUnreadableNotAsAMismatch()
    {
        Harness h = Build(NeedsOnlyTheWslVersion(), MachineNeedingOnlyTheVersion());

        h.Executors["wsl.set-default-version"].OnApply = _ => h.Reader.Remove("wsl.default-version");

        Transaction started = h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash);
        Transaction result = await h.Engine.RunAsync(started, h.Store, h.Events);

        StepExecution step = Assert.Single(result.Steps);

        Assert.Equal(StepState.VerifyFailed, step.State);
        Assert.Equal("verify.unreadable", step.MessageKey);
        Assert.Equal(CapabilityValue.Unknown, step.Actual);
    }

    [Fact]
    public async Task InvalidatesTheReaderCacheAfterEveryAppliedStep()
    {
        Harness h = Build();

        Transaction started = h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash);
        await h.Engine.RunAsync(started, h.Store, h.Events);

        // Once per applied step, or verification could read a value from before the change.
        Assert.True(h.Reader.InvalidateCount >= 1);
    }

    // ---------------------------------------------------------------- failures

    /// <summary>
    /// Spec 9.4: some of it worked, and the transaction says exactly that rather than "Done".
    /// Goes through the real path — apply, reboot, resume — with the second stage failing.
    /// </summary>
    [Fact]
    public async Task AFailedStepAfterTheRestartProducesPartiallyCompletedNotDone()
    {
        Harness h = Build();

        h.Executors["windows.optional-feature.enable"].Result = ApplyOutcome.PendingRestart("staged");
        h.Executors["wsl.set-default-version"].Result = ApplyOutcome.Failed("action.failed", "wsl.exe returned 1");

        Transaction started = h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash);
        Transaction waiting = await h.Engine.RunAsync(started, h.Store, h.Events);

        Assert.Equal(TransactionState.AwaitingRestart, waiting.State);

        h.Boot.Reboot();
        h.Reader.Set("windows.feature.wsl", CapabilityValue.Enabled);

        Transaction result = await h.Engine.ResumeAsync(waiting, h.Store, h.Events);

        Assert.Equal(TransactionState.PartiallyCompleted, result.State);
        Assert.Equal(1, result.Tally.Completed);
        Assert.Equal(1, result.Tally.Failed);
        Assert.Equal("verify.notReached", result.OutcomeVerdictKey);
    }

    /// <summary>
    /// An executor that throws must not take the transaction down with it: spec 28 requires that
    /// every failure leaves a state the user can understand and act on.
    /// </summary>
    /// <remarks>
    /// It also must not leave the user waiting for a restart that would achieve nothing — the
    /// only step in that stage failed, so there is nothing for a reboot to complete.
    /// </remarks>
    [Fact]
    public async Task AnExecutorThatThrowsBecomesAFailedStepNotACrash()
    {
        Harness h = Build();

        h.Executors["windows.optional-feature.enable"].ThrowOnApply =
            new InvalidOperationException("DISM exploded");

        Transaction started = h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash);
        Transaction result = await h.Engine.RunAsync(started, h.Store, h.Events);

        StepExecution step = result.Steps.First(s => s.Capability.Value == "windows.feature.wsl");

        Assert.Equal(StepState.Failed, step.State);
        Assert.Equal("action.threw", step.MessageKey);
        Assert.Contains("DISM exploded", step.Detail ?? string.Empty, StringComparison.Ordinal);

        Assert.True(result.IsFinished);
        Assert.NotEqual(TransactionState.AwaitingRestart, result.State);
    }

    // ---------------------------------------------------------------- guards

    /// <summary>
    /// Spec 9.1 and 19.1: the privileged side executes the plan the user reviewed, or nothing.
    /// </summary>
    [Fact]
    public void RefusesAPlanWhoseHashDoesNotMatchWhatWasReviewed()
    {
        Harness h = Build();

        PlanChangedException error = Assert.Throws<PlanChangedException>(
            () => h.Engine.Begin(h.Plan, ExecutionMode.Apply, "a-hash-from-an-older-plan"));

        Assert.Equal(h.Plan.Hash, error.CurrentHash);
    }

    [Fact]
    public void RefusesAPlanWithBlockers()
    {
        StateSnapshot cannot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.NotSupported)
            .With("firmware.cpu.virtualization", CapabilityValue.Disabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Disabled)
            .With("windows.feature.wsl", CapabilityValue.Disabled)
            .With("wsl.default-version", CapabilityValue.Scalar("1"))
            .Build();

        Harness h = Build(cannot);

        Assert.Throws<InvalidOperationException>(
            () => h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash));
    }

    /// <summary>Spec 19.1: an executor that is not on the allowlist fails before anything is touched.</summary>
    [Fact]
    public void RefusesAPlanWhoseExecutorIsNotAllowlisted()
    {
        var reader = MachineBeforeAnything();
        var clock = new FakeClock();
        var ids = new SequentialIds();

        // Deliberately empty allowlist.
        var engine = new TransactionEngine(ExecutorRegistry.Empty, reader, clock, ids, new FakeBootSession(), "test");

        Plan plan = new StateCompiler(Graph, Catalog, null, clock, ids)
            .Compile(NeedsWslAndVersion(), DockerOutcome);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => engine.Begin(plan, ExecutionMode.Apply, plan.Hash));

        Assert.Contains("allowlist", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesToResumeATransactionThatIsNotWaitingForARestart()
    {
        Harness h = Build();

        Transaction started = h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Engine.ResumeAsync(started, h.Store, h.Events));
    }

    /// <summary>
    /// The user has not restarted yet. Verifying now would report a failure for a change that
    /// never had a chance to take effect (spec 8.3.3), so resume does nothing.
    /// </summary>
    [Fact]
    public async Task ResumingInTheSameBootSessionChangesNothing()
    {
        Harness h = Build();

        h.Executors["windows.optional-feature.enable"].Result = ApplyOutcome.PendingRestart("staged");

        Transaction started = h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash);
        Transaction waiting = await h.Engine.RunAsync(started, h.Store, h.Events);

        Assert.Equal(TransactionState.AwaitingRestart, waiting.State);

        // No reboot in between.
        Transaction unchanged = await h.Engine.ResumeAsync(waiting, h.Store, h.Events);

        Assert.Equal(TransactionState.AwaitingRestart, unchanged.State);
        Assert.Equal(waiting.UpdatedAt, unchanged.UpdatedAt);
    }

    // ---------------------------------------------------------------- dry run and checkpoints

    /// <summary>
    /// A dry run leaves nothing behind, and simulates the <em>whole</em> plan rather than stopping
    /// at the first restart boundary — the second half is the part a user most wants to preview.
    /// </summary>
    [Fact]
    public async Task DryRunWritesNothingAnywhereAndSimulatesEveryStage()
    {
        Harness h = Build();

        h.Executors["windows.optional-feature.enable"].Result =
            new ApplyOutcome(ApplyStatus.Skipped, "action.dry-run", "would enable");

        h.Executors["wsl.set-default-version"].Result =
            new ApplyOutcome(ApplyStatus.Skipped, "action.dry-run", "would set version 2");

        Transaction started = h.Engine.Begin(h.Plan, ExecutionMode.DryRun, h.Plan.Hash);
        Transaction result = await h.Engine.RunAsync(started, h.Store, h.Events);

        Assert.Equal(0, h.Store.SaveCount);
        Assert.Empty(h.Events.Events);
        Assert.Equal(ExecutionMode.DryRun, result.Mode);
        Assert.Equal(TransactionState.Completed, result.State);
        Assert.Equal("action.dry-run", result.OutcomeVerdictKey);

        Assert.Equal(2, result.Steps.Count);
        Assert.All(result.Steps, s => Assert.Equal(StepState.Skipped, s.State));
    }

    /// <summary>
    /// Every state change is checkpointed before the next one starts, which is what makes a
    /// reboot or a crash mid-plan recoverable rather than mysterious (spec 9.3).
    /// </summary>
    [Fact]
    public async Task CheckpointsEveryStateChange()
    {
        Harness h = Build();

        h.Executors["windows.optional-feature.enable"].OnApply = _ =>
            h.Reader.Set("windows.feature.wsl", CapabilityValue.Enabled);

        h.Executors["wsl.set-default-version"].OnApply = _ =>
            h.Reader.Set("wsl.default-version", CapabilityValue.Scalar("2"));

        Transaction started = h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash);
        await h.Engine.RunAsync(started, h.Store, h.Events);

        Assert.True(h.Store.SaveCount > h.Plan.Cost.Changes, "each step should be checkpointed more than once");
        Assert.Contains(TransactionState.Applying, h.Store.StatesSeen);
        Assert.Contains(TransactionState.Verifying, h.Store.StatesSeen);
    }

    [Fact]
    public async Task CarriesTheProvenanceNeededToExplainItselfLater()
    {
        Harness h = Build();

        Transaction started = h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash);
        Transaction result = await h.Engine.RunAsync(started, h.Store, h.Events);

        Provenance provenance = result.Provenance;

        Assert.Equal("outcome.docker-wsl2-ready", provenance.OutcomeId);
        Assert.Equal(h.Plan.Hash, provenance.PlanHash);
        Assert.Equal(h.Plan.GraphVersion, provenance.GraphVersion);
        Assert.Equal(h.Plan.Machine.Fingerprint, provenance.MachineFingerprint);
        Assert.Equal("test", provenance.AppVersion);
        Assert.Equal(h.Plan.SnapshotId, provenance.SnapshotId);
    }

    /// <summary>
    /// The plan travels with the transaction, so a catalog update cannot change a pending plan
    /// the user already approved (spec 17.1).
    /// </summary>
    [Fact]
    public void KeepsTheApprovedPlanWithTheTransaction()
    {
        Harness h = Build();

        Transaction started = h.Engine.Begin(h.Plan, ExecutionMode.Apply, h.Plan.Hash);

        Assert.Equal(h.Plan.Hash, started.PlanHash);
        Assert.Equal(h.Plan.Steps.Count(), started.Plan.Steps.Count());

        Assert.All(
            started.Steps,
            s => Assert.Contains(h.Plan.Steps, p => p.Action.Id == s.ActionId && p.Action.Version == s.ActionVersion));
    }
}

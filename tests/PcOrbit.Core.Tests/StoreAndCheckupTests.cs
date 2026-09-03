using PcOrbit.Core.Actions;
using PcOrbit.Core.Checkup;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Events;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;
using PcOrbit.Core.Transactions;
using PcOrbit.Store;

namespace PcOrbit.Core.Tests;

/// <summary>
/// The local store, exercised the way the engine uses it: checkpoint a transaction (plan and all),
/// read it back, and query history.
/// </summary>
/// <remarks>
/// Spec step 7 is the reason these matter now rather than later: if the event schema is not right
/// from v0.1, Drift and Regression will have to reconstruct history from inconsistent logs.
/// </remarks>
public sealed class SqliteStoreTests : IDisposable
{
    private readonly PcOrbitDatabase _database = PcOrbitDatabase.OpenTemporary();

    public void Dispose()
    {
        try
        {
            File.Delete(_database.DatabasePath);
        }
        catch (IOException)
        {
            // A test artefact in the temp directory; the OS will get it eventually.
        }
    }

    private static Plan SamplePlan()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("1"))
            .Build();

        return new StateCompiler(ShippedData.Graph(), ShippedData.Catalog(), null, new FakeClock(), new SequentialIds())
            .Compile(snapshot, ShippedData.Outcomes().Single(o => o.Id == "outcome.docker-wsl2-ready"));
    }

    [Fact]
    public async Task SnapshotsRoundTripWithTheirEvidence()
    {
        var store = new SqliteSnapshotStore(_database);

        StateSnapshot original = SnapshotBuilder.For(Machines.DellIntelLaptop)
            .With("firmware.secure-boot", CapabilityValue.Enabled)
            .WithUnknown("firmware.tpm.version", "needs administrator rights")
            .Build();

        await store.SaveAsync(original);
        StateSnapshot? loaded = await store.LoadAsync(original.Id);

        Assert.NotNull(loaded);
        Assert.Equal(original.Machine.Fingerprint, loaded.Machine.Fingerprint);
        Assert.Equal(CapabilityValue.Enabled, loaded.ValueOf(CapabilityId.Parse("firmware.secure-boot")));

        // The reason an Unknown is Unknown has to survive storage, or a support report months
        // later says "unknown" and nothing more (spec 6.1).
        CapabilityReading? tpm = loaded.ReadingOf(CapabilityId.Parse("firmware.tpm.version"));
        Assert.NotNull(tpm);
        Assert.False(tpm.Value.IsKnown);
        Assert.Contains("administrator", tpm.Evidence.Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadLatestReturnsTheMostRecentSnapshot()
    {
        var store = new SqliteSnapshotStore(_database);

        var older = new StateSnapshot(
            "snap-old",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Machines.AsusAmdDesktop,
            []);

        var newer = new StateSnapshot(
            "snap-new",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            Machines.AsusAmdDesktop,
            []);

        await store.SaveAsync(older);
        await store.SaveAsync(newer);

        StateSnapshot? latest = await store.LoadLatestAsync();
        Assert.Equal("snap-new", latest?.Id);
    }

    /// <summary>
    /// The checkpoint carries the whole plan, because spec 17.1 says a catalog update must not
    /// change a plan the user already approved. Round-tripping it is what makes resume-after-reboot
    /// possible in the first place.
    /// </summary>
    [Fact]
    public async Task ATransactionCheckpointRoundTripsIncludingItsPlan()
    {
        var store = new SqliteTransactionStore(_database);
        Plan plan = SamplePlan();

        var transaction = new Transaction(
            Id: "tx-001",
            Plan: plan,
            State: TransactionState.AwaitingRestart,
            CurrentPhase: 0,
            Steps:
            [
                new StepExecution(
                    Ordinal: 0,
                    PhaseIndex: 0,
                    ActionId: "wsl.set-default-version-2",
                    ActionVersion: "1.0.0",
                    Capability: CapabilityId.Parse("wsl.default-version"),
                    Before: CapabilityValue.Scalar("1"),
                    Requested: CapabilityValue.Scalar("2"),
                    State: StepState.AwaitingRestart,
                    Detail: "staged"),
            ],
            Provenance: Provenance.FromPlan(plan, "test"),
            Mode: ExecutionMode.Apply,
            CreatedAt: new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 9, 3, 10, 1, 0, TimeSpan.Zero),
            StartBootId: "boot-first",
            PendingRestart: RestartKind.Windows);

        await store.SaveAsync(transaction);
        Transaction? loaded = await store.LoadAsync("tx-001");

        Assert.NotNull(loaded);
        Assert.Equal(TransactionState.AwaitingRestart, loaded.State);
        Assert.Equal(RestartKind.Windows, loaded.PendingRestart);
        Assert.Equal("boot-first", loaded.StartBootId);

        // The plan came back intact, action manifests included.
        Assert.Equal(plan.Hash, loaded.PlanHash);
        Assert.Equal(plan.Steps.Count(), loaded.Plan.Steps.Count());
        Assert.Equal("wsl.set-default-version", loaded.Plan.Steps.First().Action.Executor);
        Assert.Equal(plan.Machine.Fingerprint, loaded.Provenance.MachineFingerprint);
    }

    [Fact]
    public async Task CheckpointingTheSameTransactionTwiceKeepsTheLatestState()
    {
        var store = new SqliteTransactionStore(_database);
        Plan plan = SamplePlan();

        var first = new Transaction(
            "tx-002",
            plan,
            TransactionState.Applying,
            0,
            [],
            Provenance.FromPlan(plan, "test"),
            ExecutionMode.Apply,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            "boot-first");

        await store.SaveAsync(first);
        await store.SaveAsync(first with { State = TransactionState.Completed });

        Transaction? loaded = await store.LoadAsync("tx-002");
        Assert.Equal(TransactionState.Completed, loaded?.State);

        // Finished transactions are not what resume looks for.
        Assert.Empty(await store.ListUnfinishedAsync());
    }

    [Fact]
    public async Task ListUnfinishedFindsExactlyWhatResumeNeeds()
    {
        var store = new SqliteTransactionStore(_database);
        Plan plan = SamplePlan();

        Transaction Make(string id, TransactionState state) => new(
            id,
            plan,
            state,
            0,
            [],
            Provenance.FromPlan(plan, "test"),
            ExecutionMode.Apply,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            "boot-first");

        await store.SaveAsync(Make("tx-waiting", TransactionState.AwaitingRestart));
        await store.SaveAsync(Make("tx-done", TransactionState.Completed));
        await store.SaveAsync(Make("tx-partial", TransactionState.PartiallyCompleted));
        await store.SaveAsync(Make("tx-midflight", TransactionState.Applying));

        IReadOnlyList<Transaction> unfinished = await store.ListUnfinishedAsync();

        Assert.Equal(
            ["tx-midflight", "tx-waiting"],
            unfinished.Select(t => t.Id).Order());
    }

    [Fact]
    public async Task EventsAreQueryableByComponentAndTransaction()
    {
        var log = new SqliteEventLog(_database);

        ChangeEvent Make(string id, string component, string? transaction, DateTimeOffset at) => new(
            Id: id,
            Timestamp: at,
            Source: EventSource.PcOrbit,
            Category: EventCategory.Feature,
            Component: component,
            Before: "disabled",
            After: "enabled",
            Initiator: Initiator.PcOrbit,
            Confidence: Confidence.High,
            Evidence: new Evidence(EvidenceSourceKind.Wmi, "Win32_OptionalFeature", Confidence.High),
            RelatedTransaction: transaction,
            RelatedRestart: "boot-first");

        DateTimeOffset now = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

        await log.AppendAsync(Make("ev-1", "windows.feature.wsl", "tx-1", now));
        await log.AppendAsync(Make("ev-2", "wsl.default-version", "tx-1", now.AddMinutes(1)));
        await log.AppendAsync(Make("ev-3", "windows.feature.wsl", "tx-2", now.AddMinutes(2)));

        Assert.Equal(3, (await log.QueryAsync(new EventQuery())).Count);

        IReadOnlyList<ChangeEvent> byComponent = await log.QueryAsync(
            new EventQuery(Component: "windows.feature.wsl"));

        Assert.Equal(["ev-3", "ev-1"], byComponent.Select(e => e.Id));

        IReadOnlyList<ChangeEvent> byTransaction = await log.QueryAsync(new EventQuery(TransactionId: "tx-1"));
        Assert.Equal(2, byTransaction.Count);

        IReadOnlyList<ChangeEvent> since = await log.QueryAsync(new EventQuery(Since: now.AddMinutes(2)));
        Assert.Equal(["ev-3"], since.Select(e => e.Id));
    }

    [Fact]
    public async Task EventEvidenceSurvivesStorage()
    {
        var log = new SqliteEventLog(_database);

        await log.AppendAsync(new ChangeEvent(
            Id: "ev-evidence",
            Timestamp: DateTimeOffset.UnixEpoch,
            Source: EventSource.PcOrbit,
            Category: EventCategory.Firmware,
            Component: "firmware.cpu.virtualization",
            Before: "disabled",
            After: "enabled",
            Initiator: Initiator.User,
            Confidence: Confidence.High,
            Evidence: new Evidence(
                EvidenceSourceKind.Wmi,
                "Win32_Processor.VirtualizationFirmwareEnabled",
                Confidence.High,
                Query: "SELECT * FROM Win32_Processor",
                RawResult: "True"),
            RelatedRestart: "boot-second",
            MessageKey: "verify.reached"));

        ChangeEvent loaded = (await log.QueryAsync(new EventQuery())).Single();

        Assert.Equal(EvidenceSourceKind.Wmi, loaded.Evidence?.SourceKind);
        Assert.Equal("True", loaded.Evidence?.RawResult);
        Assert.Equal("boot-second", loaded.RelatedRestart);
        Assert.Equal("verify.reached", loaded.MessageKey);
        Assert.Equal(Initiator.User, loaded.Initiator);
    }

    [Fact]
    public void OpeningAnExistingDatabaseTwiceDoesNotReapplyTheSchema()
    {
        string path = _database.DatabasePath;

        PcOrbitDatabase reopened = PcOrbitDatabase.Open(path);
        Assert.Equal(path, reopened.DatabasePath);
    }
}

/// <summary>Spec 23.1.G — the findings v0.1 has to produce, and the ones it must not invent.</summary>
public sealed class CheckupTests
{
    private static readonly CapabilityGraph Graph = ShippedData.Graph();
    private static readonly ActionCatalog Catalog = ShippedData.Catalog();

    private static IReadOnlyList<Finding> Run(StateSnapshot snapshot) =>
        CheckupEngine.Default.Run(new CheckupContext(snapshot, Graph, Catalog));

    [Fact]
    public void ReportsFirmwareVirtualizationOffWhenTheCpuSupportsIt()
    {
        IReadOnlyList<Finding> findings = Run(SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Disabled)
            .Build());

        Finding finding = findings.Single(f => f.Code == "firmware.virtualization-disabled");

        Assert.Equal(FindingSeverity.Attention, finding.Severity);
        Assert.Equal("outcome.docker-wsl2-ready", finding.SuggestedOutcomeId);
        Assert.Equal(RestartKind.Firmware, finding.Restart);
        Assert.True(finding.HasFix);
    }

    /// <summary>
    /// Spec 21.3 and 27.13: an unreadable value must not become a finding. Guessing here would
    /// send someone into their BIOS for no reason.
    /// </summary>
    [Fact]
    public void SaysNothingWhenTheFirmwareValueCouldNotBeRead()
    {
        IReadOnlyList<Finding> findings = Run(SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .WithUnknown("firmware.cpu.virtualization", "needs administrator rights")
            .Build());

        Assert.DoesNotContain(findings, f => f.Code == "firmware.virtualization-disabled");
    }

    [Fact]
    public void ReportsARefreshRateBelowWhatTheDisplaySupports()
    {
        IReadOnlyList<Finding> findings = Run(SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("display.current-refresh-rate", CapabilityValue.Scalar("60"))
            .With("display.max-refresh-rate", CapabilityValue.Scalar("165"))
            .Build());

        Finding finding = findings.Single(f => f.Code == "display.refresh-rate-mismatch");

        Assert.Equal("60", finding.Arguments["current"]);
        Assert.Equal("165", finding.Arguments["max"]);
        Assert.Equal("display.set-refresh-rate.max", finding.SuggestedActionId);

        // Spec 23.1.H: the safety line promises auto-revert, so the countdown has to be real.
        Assert.Equal("15", finding.Arguments["seconds"]);
    }

    [Fact]
    public void SaysNothingWhenTheDisplayIsAlreadyAtItsBest()
    {
        IReadOnlyList<Finding> findings = Run(SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("display.current-refresh-rate", CapabilityValue.Scalar("165"))
            .With("display.max-refresh-rate", CapabilityValue.Scalar("165"))
            .Build());

        Assert.DoesNotContain(findings, f => f.Code == "display.refresh-rate-mismatch");
    }

    [Fact]
    public void ReportsMemoryRunningBelowItsRatedSpeed()
    {
        IReadOnlyList<Finding> findings = Run(SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("memory.rated-speed", CapabilityValue.Scalar("6000"))
            .With("memory.current-speed", CapabilityValue.Scalar("4800"))
            .Build());

        Finding finding = findings.Single(f => f.Code == "memory.speed-mismatch");

        // Read-only in v0.1 on purpose: enabling a memory profile is a firmware change that can
        // make a PC unstable, so the finding explains rather than offering a button.
        Assert.False(finding.HasFix);
        Assert.Equal("6000", finding.Arguments["rated"]);
    }

    /// <summary>
    /// A finding that fires on measurement noise trains people to ignore findings, so a few MT/s
    /// of disagreement between the SPD table and the reported clock is not a finding.
    /// </summary>
    [Fact]
    public void IgnoresATinyMemorySpeedDifference()
    {
        IReadOnlyList<Finding> findings = Run(SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("memory.rated-speed", CapabilityValue.Scalar("5600"))
            .With("memory.current-speed", CapabilityValue.Scalar("5599"))
            .Build());

        Assert.DoesNotContain(findings, f => f.Code == "memory.speed-mismatch");
    }

    [Fact]
    public void ReportsAFeatureWhoseDependencyIsOff()
    {
        IReadOnlyList<Finding> findings = Run(SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("windows.feature.hyper-v", CapabilityValue.Enabled)
            .With("firmware.cpu.virtualization", CapabilityValue.Disabled)
            .Build());

        Assert.Contains(findings, f => f.Code.StartsWith("windows.feature-dependency-mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void WarnsAboutTheRecoveryKeyOnAnEncryptedMachine()
    {
        IReadOnlyList<Finding> findings = Run(SnapshotBuilder.For(Machines.DellIntelLaptop)
            .With("security.bitlocker.system-drive", CapabilityValue.Scalar("on"))
            .Build());

        Finding finding = findings.Single(f => f.Code == "security.bitlocker-recovery-key-unconfirmed");

        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.False(finding.HasFix);
    }

    /// <summary>Spec 21.5 point 4: a healthy machine produces an empty list, and that is a result.</summary>
    [Fact]
    public void AHealthyMachineProducesNoFindings()
    {
        IReadOnlyList<Finding> findings = Run(SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("2"))
            .With("display.current-refresh-rate", CapabilityValue.Scalar("165"))
            .With("display.max-refresh-rate", CapabilityValue.Scalar("165"))
            .With("memory.rated-speed", CapabilityValue.Scalar("6000"))
            .With("memory.current-speed", CapabilityValue.Scalar("6000"))
            .With("windows.system-restore", CapabilityValue.Enabled)
            .With("security.bitlocker.system-drive", CapabilityValue.Scalar("off"))
            .Build());

        Assert.Empty(findings);
    }

    [Fact]
    public void FindingsComeBackWorstFirstAndInAStableOrder()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Disabled)
            .With("windows.system-restore", CapabilityValue.Disabled)
            .With("display.current-refresh-rate", CapabilityValue.Scalar("60"))
            .With("display.max-refresh-rate", CapabilityValue.Scalar("165"))
            .Build();

        IReadOnlyList<Finding> first = Run(snapshot);
        IReadOnlyList<Finding> second = Run(snapshot);

        Assert.Equal(first.Select(f => f.Code), second.Select(f => f.Code));
        Assert.Equal(FindingSeverity.Warning, first[0].Severity);
    }
}

public sealed class WorkloadEvaluatorTests
{
    private static readonly CapabilityGraph Graph = ShippedData.Graph();

    private static CapabilityReading Evaluate(StateSnapshot snapshot) =>
        WorkloadEvaluator.Evaluate(
            CapabilityId.Parse("workload.docker-wsl2-ready"),
            snapshot,
            Graph,
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void ReadyWhenEveryRequirementIsSatisfied()
    {
        CapabilityReading reading = Evaluate(SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("2"))
            .With("wsl.installed", CapabilityValue.Present)
            .Build());

        Assert.Equal(WorkloadEvaluator.Ready, reading.Value.Raw);
        Assert.Equal(Confidence.High, reading.Evidence.Confidence);
    }

    [Fact]
    public void NotReadyNamesWhatIsMissing()
    {
        CapabilityReading reading = Evaluate(SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Disabled)
            .With("wsl.default-version", CapabilityValue.Scalar("2"))
            .With("wsl.installed", CapabilityValue.Present)
            .Build());

        Assert.Equal(WorkloadEvaluator.NotReady, reading.Value.Raw);
        Assert.Contains("windows.feature.wsl", reading.Evidence.Source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A workload whose requirements cannot all be read is Unknown, not "not ready". Spec 6.6:
    /// we do not turn a failed read into a verdict.
    /// </summary>
    [Fact]
    public void UnknownPropagatesRatherThanBecomingNotReady()
    {
        CapabilityReading reading = Evaluate(SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .WithUnknown("wsl.default-version")
            .With("wsl.installed", CapabilityValue.Present)
            .Build());

        Assert.False(reading.Value.IsKnown);
        Assert.Equal(Confidence.None, reading.Evidence.Confidence);
        Assert.Contains("wsl.default-version", reading.Evidence.Source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A genuine miss outranks an unreadable one: if something is definitely wrong, say so rather
    /// than hiding behind what we could not read.
    /// </summary>
    [Fact]
    public void ADefiniteMissBeatsAnUnreadableOne()
    {
        CapabilityReading reading = Evaluate(SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Disabled)
            .WithUnknown("wsl.default-version")
            .Build());

        Assert.Equal(WorkloadEvaluator.NotReady, reading.Value.Raw);
    }

    [Fact]
    public void EvidenceNamesTheGraphVersionItWasDerivedFrom()
    {
        CapabilityReading reading = Evaluate(SnapshotBuilder.For(Machines.AsusAmdDesktop).Build());

        Assert.Equal(EvidenceSourceKind.Inference, reading.Evidence.SourceKind);
        Assert.Contains(Graph.Version, reading.Evidence.Source, StringComparison.Ordinal);
    }
}

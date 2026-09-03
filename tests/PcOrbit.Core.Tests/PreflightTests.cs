using PcOrbit.Core.Actions;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;
using PcOrbit.Core.Outcomes;
using PcOrbit.Core.Preflight;

namespace PcOrbit.Core.Tests;

/// <summary>
/// Preflight, and in particular the BitLocker gate.
/// </summary>
/// <remarks>
/// Spec 10.3 calls this the single biggest real-world way an ordinary user breaks their PC with a
/// tool like this: change something in firmware, reboot, and Windows asks for a 48-digit key they
/// have never seen. Most consumer Windows 11 laptops ship with Device Encryption on and the owner
/// does not know. So these tests treat it as a hard gate, including the "we could not read it"
/// case, which is not evidence that the drive is unencrypted.
/// </remarks>
public sealed class PreflightTests
{
    private static readonly CapabilityGraph Graph = ShippedData.Graph();
    private static readonly ActionCatalog Catalog = ShippedData.Catalog();

    private static Outcome DockerOutcome =>
        ShippedData.Outcomes().Single(o => o.Id == "outcome.docker-wsl2-ready");

    /// <summary>A plan that includes the guided firmware step, which is what pulls in the BitLocker check.</summary>
    private static (Plan Plan, StateSnapshot Snapshot) FirmwarePlan(
        CapabilityValue encryption,
        bool onBattery = false)
    {
        var builder = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Disabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Disabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("2"))
            .With("windows.system-restore", CapabilityValue.Enabled)
            .With("storage.system-drive.free-gb", CapabilityValue.Scalar("120"))
            .With("power.on-battery", CapabilityValue.Scalar(onBattery ? "yes" : "no"))
            .With("power.battery-percent", CapabilityValue.Scalar("18"));

        if (encryption.IsKnown)
        {
            builder.With("security.bitlocker.system-drive", encryption);
        }
        else
        {
            builder.WithUnknown("security.bitlocker.system-drive", "needs administrator rights");
        }

        StateSnapshot snapshot = builder.Build();

        Plan plan = new StateCompiler(Graph, Catalog, null, new FakeClock(), new SequentialIds())
            .Compile(snapshot, DockerOutcome);

        return (plan, snapshot);
    }

    private static PreflightReport Run(
        Plan plan,
        StateSnapshot snapshot,
        bool elevated = true,
        params string[] acknowledgements) =>
        PreflightRunner.Default.Run(
            new PreflightContext(plan, snapshot, elevated, new HashSet<string>(acknowledgements, StringComparer.Ordinal)));

    [Fact]
    public void EncryptedDriveBlocksAFirmwarePlan()
    {
        (Plan plan, StateSnapshot snapshot) = FirmwarePlan(CapabilityValue.Scalar("on"));

        PreflightReport report = Run(plan, snapshot);

        Assert.True(report.IsBlocked);

        PreflightFinding finding = report.Blockers.Single(f => f.Kind == PreflightKind.Bitlocker);
        Assert.Equal("preflight.bitlocker.blocked", finding.MessageKey);
    }

    /// <summary>
    /// Reading the encryption state needs administrator rights, which a guided firmware step does
    /// not. "Could not read" therefore blocks too — spec 10.3 says mandatory, no exceptions.
    /// </summary>
    [Fact]
    public void AnUnreadableEncryptionStateAlsoBlocks()
    {
        (Plan plan, StateSnapshot snapshot) = FirmwarePlan(CapabilityValue.Unknown);

        PreflightReport report = Run(plan, snapshot);

        PreflightFinding finding = report.Blockers.Single(f => f.Kind == PreflightKind.Bitlocker);
        Assert.Equal("preflight.bitlocker.unknown", finding.MessageKey);
    }

    [Fact]
    public void ConfirmingTheRecoveryKeyClearsTheBlock()
    {
        (Plan plan, StateSnapshot snapshot) = FirmwarePlan(CapabilityValue.Scalar("on"));

        PreflightReport report = Run(
            plan,
            snapshot,
            elevated: true,
            PreflightContext.Ack.BitLockerKeyConfirmed);

        Assert.DoesNotContain(report.Blockers, f => f.Kind == PreflightKind.Bitlocker);
    }

    [Fact]
    public void AnUnencryptedDriveRaisesNothing()
    {
        (Plan plan, StateSnapshot snapshot) = FirmwarePlan(CapabilityValue.Scalar("off"));

        PreflightReport report = Run(plan, snapshot);

        Assert.DoesNotContain(report.Findings, f => f.Kind == PreflightKind.Bitlocker);
    }

    /// <summary>
    /// The second of the two choices spec 10.3 offers: the plan itself suspends encryption for one
    /// restart, so the user does not have to go hunting for a key.
    /// </summary>
    [Fact]
    public void APlanThatSuspendsEncryptionSatisfiesTheGateByItself()
    {
        (Plan plan, StateSnapshot snapshot) = FirmwarePlan(CapabilityValue.Scalar("on"));

        ActionDefinition suspend = Catalog.ById("security.bitlocker.suspend-one-reboot")!;

        // Splice the suspend step into the plan the way a Change Basket would.
        var extraStep = new PlanStep(
            Ordinal: 99,
            Capability: CapabilityId.Parse("security.bitlocker.system-drive"),
            CurrentValue: CapabilityValue.Scalar("on"),
            DesiredValue: CapabilityValue.Scalar("suspended"),
            Action: suspend,
            Group: StepGroup.Online,
            RequiredByOutcome: false,
            RequiredBy: []);

        PlanPhase firstPhase = plan.Phases[0];

        Plan withSuspend = plan with
        {
            Phases = [firstPhase with { Steps = [extraStep, .. firstPhase.Steps] }, .. plan.Phases.Skip(1)],
        };

        PreflightReport report = Run(withSuspend, snapshot);

        Assert.DoesNotContain(report.Blockers, f => f.Kind == PreflightKind.Bitlocker);
    }

    /// <summary>Spec 21.10: a standard user gets told before they commit, not halfway through.</summary>
    [Fact]
    public void MissingAdministratorRightsBlocksAPlanThatNeedsThem()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Disabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("2"))
            .Build();

        Plan plan = new StateCompiler(Graph, Catalog, null, new FakeClock(), new SequentialIds())
            .Compile(snapshot, DockerOutcome);

        Assert.True(plan.RequiresElevation);

        PreflightReport blocked = Run(plan, snapshot, elevated: false);
        Assert.Contains(blocked.Blockers, f => f.Kind == PreflightKind.Elevation);

        PreflightReport allowed = Run(plan, snapshot, elevated: true);
        Assert.DoesNotContain(allowed.Findings, f => f.Kind == PreflightKind.Elevation);
    }

    [Fact]
    public void BatteryIsAWarningNotABlock()
    {
        (Plan plan, StateSnapshot snapshot) = FirmwarePlan(CapabilityValue.Scalar("off"), onBattery: true);

        PreflightReport report = Run(plan, snapshot);

        PreflightFinding finding = report.Warnings.Single(f => f.Kind == PreflightKind.PowerSource);

        Assert.Equal("preflight.powerSource.warning", finding.MessageKey);
        Assert.Equal("18", finding.Arguments["percent"]);
    }

    /// <summary>Only the checks the plan's actions actually asked for get run.</summary>
    [Fact]
    public void RunsOnlyTheChecksThePlanDeclares()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("1"))
            .WithUnknown("security.bitlocker.system-drive")
            .Build();

        Plan plan = new StateCompiler(Graph, Catalog, null, new FakeClock(), new SequentialIds())
            .Compile(snapshot, DockerOutcome);

        // The only step is setting the WSL default version: no elevation, no restart, no firmware.
        Assert.Empty(plan.RequiredPreflight);

        PreflightReport report = Run(plan, snapshot, elevated: false);

        // Notably no BitLocker finding, even though the encryption state is unreadable: nothing in
        // this plan goes anywhere near firmware or boot.
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void ARestartInThePlanWarnsAboutUnsavedWork()
    {
        (Plan plan, StateSnapshot snapshot) = FirmwarePlan(CapabilityValue.Scalar("off"));

        Assert.True(plan.Cost.Restarts > 0);

        PreflightReport report = Run(plan, snapshot);
        Assert.Contains(report.Warnings, f => f.Kind == PreflightKind.UnsavedWork);
    }
}

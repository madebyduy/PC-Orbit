using System.Text;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;
using PcOrbit.Core.Outcomes;

namespace PcOrbit.Core.Tests;

/// <summary>
/// Golden tests for the State Compiler (spec 28: "same snapshot + same rules must produce the
/// same plan").
/// </summary>
/// <remarks>
/// <para>
/// These run against the graph, outcomes and action manifests that actually ship, not against
/// fixtures invented for the tests. That is the point: the thing under test is the combination of
/// engine and data, and a graph edit that changes a plan should fail here loudly.
/// </para>
/// <para>
/// Each case is written as the machine state on the left and the expected plan on the right, as
/// text. When one fails, the diff reads like a plan rather than like an object graph.
/// </para>
/// </remarks>
public sealed class StateCompilerGoldenTests
{
    private static readonly CapabilityGraph Graph = ShippedData.Graph();
    private static readonly ActionCatalog Catalog = ShippedData.Catalog();

    private static Outcome DockerOutcome =>
        ShippedData.Outcomes().Single(o => o.Id == "outcome.docker-wsl2-ready");

    private static Outcome DisplayOutcome =>
        ShippedData.Outcomes().Single(o => o.Id == "outcome.display-max-refresh");

    private static StateCompiler NewCompiler(CompilerOptions? options = null) =>
        new(Graph, Catalog, options, new FakeClock(), new SequentialIds());

    /// <summary>
    /// The machine in spec 12.1 "Machine A": the CPU can do it, firmware cannot, WSL is on, VMP is
    /// off, and the default WSL version is 1.
    /// </summary>
    [Fact]
    public void MachineMissingEverythingGetsTheFullOrderedPlan()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Disabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Disabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("1"))
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, DockerOutcome);

        Assert.Equal(
            """
            stage 1 -> restart Firmware
              firmware.cpu.virtualization: disabled -> enabled  [firmware.virtualization.enable.guided-generic / GuidedGeneric]
            stage 2 -> restart Windows
              windows.feature.virtual-machine-platform: disabled -> enabled  [windows.feature.enable.virtual-machine-platform / Auto]
            stage 3 -> restart None
              wsl.default-version: 1 -> 2  [wsl.set-default-version-2 / Auto]
            """,
            Describe(plan),
            ignoreLineEndingDifferences: true);

        // Three stages, but only the two that need one carry a restart.
        Assert.Equal(2, plan.Cost.Restarts);
        Assert.Equal(1, plan.Cost.ManualSteps);
        Assert.Equal(PlanOutlook.ReachableWithManualSteps, plan.Outlook);
    }

    /// <summary>Spec 12.1 "Machine B": same outcome, a much smaller plan.</summary>
    [Fact]
    public void MachineNeedingOnlyTheWslVersionGetsOneStep()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("1"))
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, DockerOutcome);

        Assert.Equal(
            """
            stage 1 -> restart None
              wsl.default-version: 1 -> 2  [wsl.set-default-version-2 / Auto]
            """,
            Describe(plan),
            ignoreLineEndingDifferences: true);

        Assert.Equal(0, plan.Cost.Restarts);
        Assert.Equal(PlanOutlook.Reachable, plan.Outlook);
    }

    [Fact]
    public void MachineThatIsAlreadyThereGetsNoPlanAtAll()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("2"))
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, DockerOutcome);

        Assert.Empty(plan.Phases);
        Assert.Equal(PlanOutlook.AlreadySatisfied, plan.Outlook);
        Assert.Equal(PlanCost.Empty, plan.Cost);
    }

    /// <summary>
    /// The one restart promise from spec 21.8: two Windows components that each need a restart
    /// still cost exactly one restart, because nothing forces them apart.
    /// </summary>
    [Fact]
    public void TwoRestartNeedingComponentsShareOneRestart()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Disabled)
            .With("windows.feature.wsl", CapabilityValue.Disabled)
            .With("wsl.default-version", CapabilityValue.Scalar("1"))
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, DockerOutcome);

        Assert.Equal(
            """
            stage 1 -> restart Windows
              windows.feature.virtual-machine-platform: disabled -> enabled  [windows.feature.enable.virtual-machine-platform / Auto]
              windows.feature.wsl: disabled -> enabled  [windows.feature.enable.wsl / Auto]
            stage 2 -> restart None
              wsl.default-version: 1 -> 2  [wsl.set-default-version-2 / Auto]
            """,
            Describe(plan),
            ignoreLineEndingDifferences: true);

        Assert.Equal(1, plan.Cost.Restarts);
    }

    /// <summary>
    /// A CPU that cannot virtualise makes the outcome unreachable, and the plan says so with a
    /// blocker rather than offering steps that cannot work (spec 12.3).
    /// </summary>
    [Fact]
    public void OutcomeIsNotReachableWhenTheHardwareCannotDoIt()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.NotSupported)
            .With("firmware.cpu.virtualization", CapabilityValue.Disabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Disabled)
            .With("windows.feature.wsl", CapabilityValue.Disabled)
            .WithUnknown("wsl.default-version")
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, DockerOutcome);

        Assert.Equal(PlanOutlook.NotReachable, plan.Outlook);
        Assert.True(plan.HasBlockers);

        Assert.Contains(
            plan.Issues,
            i => i.Severity == PlanIssueSeverity.Blocker
                && i.Capability?.Value == "cpu.virtualization");
    }

    /// <summary>
    /// Spec 8.3.4: the machine reports firmware virtualization as off, but Virtual Machine Platform
    /// is running — which it cannot do without it. The plan must not march the user into their
    /// BIOS to fix something already correct.
    /// </summary>
    [Fact]
    public void DoesNotPlanAFirmwareTripWhenSomethingDependingOnItIsAlreadyRunning()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Disabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("2"))
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, DockerOutcome);

        Assert.Empty(plan.Phases);
        Assert.Equal(PlanOutlook.AlreadySatisfied, plan.Outlook);

        Assert.Contains(
            plan.Issues,
            i => i.Code == "reading.implied-by-dependent" && i.Severity == PlanIssueSeverity.Info);
    }

    /// <summary>
    /// An unreadable value is not treated as "off", but it does not stop the plan either: the step
    /// goes in, flagged, and the real value is verified afterwards (spec 6.6).
    /// </summary>
    [Fact]
    public void UnknownReadingIsPlannedButFlagged()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .WithUnknown("wsl.default-version", "the registry value has never been set")
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, DockerOutcome);

        Assert.Single(plan.Steps);
        Assert.Contains(plan.Issues, i => i.Code == "reading.unknown" && i.Severity == PlanIssueSeverity.Warning);
        Assert.False(plan.HasBlockers);
    }

    /// <summary>
    /// With guided steps disallowed, the same machine becomes unreachable rather than getting a
    /// plan that quietly upgrades a Guided step into an Auto one (spec 12.3).
    /// </summary>
    [Fact]
    public void RefusingManualStepsMakesAFirmwareOutcomeUnreachable()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Disabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Disabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("2"))
            .Build();

        Plan plan = NewCompiler(CompilerOptions.Default with { AllowManualSteps = false })
            .Compile(snapshot, DockerOutcome);

        Assert.Equal(PlanOutlook.NotReachable, plan.Outlook);
        Assert.Contains(plan.Issues, i => i.Code == "capability.no-usable-action");
    }

    /// <summary>
    /// Decision 19 and spec 8.3.5: no machine gets an automatic firmware write until a vendor
    /// adapter has been verified, so even a Dell gets the guided route today.
    /// </summary>
    [Fact]
    public void DellStillGetsTheGuidedRouteBecauseNoVendorAdapterIsVerifiedYet()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.DellIntelLaptop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Disabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Disabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("2"))
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, DockerOutcome);

        PlanStep firmwareStep = plan.Steps.Single(s => s.Capability.Value == "firmware.cpu.virtualization");

        Assert.Equal(WriteMode.GuidedGeneric, firmwareStep.Action.WriteMode);
        Assert.True(firmwareStep.IsManualStep);

        // No Auto firmware action is even a candidate, because the vendor manifest is not loaded.
        Assert.DoesNotContain(plan.Steps, s => s.Action.Executor == "firmware.vendor.dell");
    }

    /// <summary>Every step can say why it exists (spec 21.6, step 6).</summary>
    [Fact]
    public void EverySteExplainsWhyItIsInThePlan()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Disabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Disabled)
            .With("windows.feature.wsl", CapabilityValue.Disabled)
            .With("wsl.default-version", CapabilityValue.Scalar("1"))
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, DockerOutcome);

        Assert.All(plan.Steps, step =>
            Assert.True(
                step.RequiredByOutcome || step.RequiredBy.Count > 0,
                $"Step for '{step.Capability}' cannot explain why it exists."));
    }

    /// <summary>
    /// Every checkup finding with a fix names an outcome the user can actually plan and apply —
    /// this is the System Restore one, end to end through the compiler.
    /// </summary>
    [Fact]
    public void SystemRestoreOutcomeGetsOneStepWhenItIsOff()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("windows.system-restore", CapabilityValue.Disabled)
            .Build();

        Outcome outcome = ShippedData.Outcomes().Single(o => o.Id == "outcome.system-restore-on");
        Plan plan = NewCompiler().Compile(snapshot, outcome);

        Assert.Equal(
            """
            stage 1 -> restart None
              windows.system-restore: disabled -> enabled  [windows.system-restore.enable / Auto]
            """,
            Describe(plan),
            ignoreLineEndingDifferences: true);

        Assert.Equal(PlanOutlook.Reachable, plan.Outlook);
    }

    // ---------------------------------------------------------------- the "max" sentinel (spec 12.2)

    /// <summary>
    /// "max" is not a value until the compiler resolves it against this machine's ceiling via the
    /// graph's limits edge. The reviewed plan, its hash and every verification then carry the real
    /// number — 60 → 165 here, something else on another monitor.
    /// </summary>
    [Fact]
    public void DisplayBelowItsMaximumGetsOneStepWithTheResolvedNumber()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("display.current-refresh-rate", CapabilityValue.Scalar("60"))
            .With("display.max-refresh-rate", CapabilityValue.Scalar("165"))
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, DisplayOutcome);

        Assert.Equal(
            """
            stage 1 -> restart None
              display.current-refresh-rate: 60 -> 165  [display.set-refresh-rate.max / Auto]
            """,
            Describe(plan),
            ignoreLineEndingDifferences: true);

        Assert.Equal(PlanOutlook.Reachable, plan.Outlook);
        Assert.Equal(0, plan.Cost.Restarts);

        // The resolution is visible, not silent: the plan can say where 165 came from.
        Assert.Contains(plan.Issues, i => i.Code == "requirement.max-resolved" && i.Severity == PlanIssueSeverity.Info);

        // Verification — the action's own and the outcome's — compares against the number,
        // because the machine will never report the word "max".
        PlanStep step = Assert.Single(plan.Steps);
        Assert.Equal("165", Assert.Single(step.Action.Verify).Expected.Canonical);
        Assert.Equal("165", Assert.Single(plan.FinalVerification).Expected.Canonical);
    }

    [Fact]
    public void DisplayAlreadyAtItsMaximumIsAlreadySatisfied()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("display.current-refresh-rate", CapabilityValue.Scalar("165"))
            .With("display.max-refresh-rate", CapabilityValue.Scalar("165"))
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, DisplayOutcome);

        Assert.Empty(plan.Phases);
        Assert.Equal(PlanOutlook.AlreadySatisfied, plan.Outlook);
    }

    /// <summary>
    /// No readable ceiling means no value to aim at and no way to verify the result. That is a
    /// stated blocker, never a guessed number and never a silently dropped step (spec 6.6).
    /// </summary>
    [Fact]
    public void AnUnreadableMaximumBlocksInsteadOfGuessing()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("display.current-refresh-rate", CapabilityValue.Scalar("60"))
            .WithUnknown("display.max-refresh-rate", "the display driver reported no modes")
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, DisplayOutcome);

        Assert.Empty(plan.Steps);
        Assert.Equal(PlanOutlook.NotReachable, plan.Outlook);

        Assert.Contains(
            plan.Issues,
            i => i.Code == "capability.limit-unknown"
                && i.Severity == PlanIssueSeverity.Blocker
                && i.Capability?.Value == "display.current-refresh-rate");
    }

    /// <summary>Raw string literals carry whatever the source file uses; comparison should not care.</summary>
    private static string Normalise(string text) => text.ReplaceLineEndings(Lf);

    private const string Lf = "\n";

    private const char LfChar = (char)10;

    /// <summary>
    /// A compact, readable rendering of the plan: stages, restarts, capability transitions and the
    /// chosen action. Everything a reviewer needs, nothing that changes between runs.
    /// </summary>
    // ---------------------------------------------------------------- Windows 11 readiness

    private static Outcome Windows11Outcome =>
        ShippedData.Outcomes().Single(o => o.Id == "outcome.windows11-ready");

    /// <summary>
    /// Both firmware switches are off but the hardware is capable. One restart, two guided steps,
    /// and nothing the user cannot actually do.
    /// </summary>
    [Fact]
    public void AMachineWithSecureBootAndTheChipOffGetsOneFirmwareTrip()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.Windows10Desktop)
            .With("firmware.boot-mode", CapabilityValue.Scalar("uefi"))
            .With("firmware.tpm.version", CapabilityValue.Scalar("2.0"))
            .With("firmware.tpm.ready", CapabilityValue.Disabled)
            .With("firmware.secure-boot", CapabilityValue.Disabled)
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, Windows11Outcome);

        Assert.Equal(
            """
            stage 1 -> restart Firmware
              firmware.tpm.ready: disabled -> enabled  [firmware.tpm.enable.guided-generic / GuidedGeneric]
              firmware.secure-boot: disabled -> enabled  [firmware.secure-boot.enable.guided-generic / GuidedGeneric]
            """,
            Describe(plan),
            ignoreLineEndingDifferences: true);

        // Spec 21.8: two firmware settings, one trip into the setup screen.
        Assert.Equal(1, plan.Cost.Restarts);
        Assert.Equal(2, plan.Cost.ManualSteps);
        Assert.Equal(PlanOutlook.ReachableWithManualSteps, plan.Outlook);
    }

    /// <summary>
    /// A machine booting in legacy mode cannot get there. Nothing in the catalog converts a disk to
    /// GPT and this codebase is not going to add one, so the compiler says so instead of producing
    /// a plan that would fail at the last step (spec 27.13).
    /// </summary>
    [Fact]
    public void AMachineBootingInLegacyModeIsToldItCannotGetThere()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.Windows10Desktop)
            .With("firmware.boot-mode", CapabilityValue.Scalar("legacy"))
            .With("firmware.tpm.version", CapabilityValue.Scalar("2.0"))
            .With("firmware.tpm.ready", CapabilityValue.Enabled)
            .With("firmware.secure-boot", CapabilityValue.Disabled)
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, Windows11Outcome);

        Assert.Equal(PlanOutlook.NotReachable, plan.Outlook);

        // Two blockers, and both are worth saying. The first is that nothing can change the boot
        // mode; the second is that Secure Boot depends on it, which is why the more obvious step
        // is not offered either. Reporting only the first would leave the user wondering why they
        // cannot simply turn Secure Boot on.
        Assert.Equal(
            ["action.unmet-precondition", "capability.no-action-exists"],
            plan.Issues.Where(i => i.Severity == PlanIssueSeverity.Blocker).Select(i => i.Code).Order());

        Assert.All(
            plan.Issues.Where(i => i.Severity == PlanIssueSeverity.Blocker),
            i => Assert.Equal("firmware.boot-mode", i.Capability?.Value));
    }

    /// <summary>
    /// A TPM 1.2 chip is a hardware fact, not a setting. The plan must not imply otherwise.
    /// </summary>
    [Fact]
    public void AMachineWithAnOldSecurityChipIsToldItCannotGetThere()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.Windows10Desktop)
            .With("firmware.boot-mode", CapabilityValue.Scalar("uefi"))
            .With("firmware.tpm.version", CapabilityValue.Scalar("1.2"))
            .With("firmware.tpm.ready", CapabilityValue.Enabled)
            .With("firmware.secure-boot", CapabilityValue.Enabled)
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, Windows11Outcome);

        Assert.Equal(PlanOutlook.NotReachable, plan.Outlook);
        Assert.Contains(plan.Issues, i => i.Capability?.Value == "firmware.tpm.version");
    }

    [Fact]
    public void AMachineThatAlreadyMeetsTheRequirementsGetsNoPlan()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.Windows10Desktop)
            .With("firmware.boot-mode", CapabilityValue.Scalar("uefi"))
            .With("firmware.tpm.version", CapabilityValue.Scalar("2.0"))
            .With("firmware.tpm.ready", CapabilityValue.Enabled)
            .With("firmware.secure-boot", CapabilityValue.Enabled)
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, Windows11Outcome);

        Assert.Equal(PlanOutlook.AlreadySatisfied, plan.Outlook);
        Assert.Empty(plan.Steps);
    }

    /// <summary>
    /// "We could not read your TPM version" and "your TPM is too old" are different sentences that
    /// send a person to different places — one of them to a shop. The compiler must not collapse
    /// the first into the second just because neither produces a step (spec 6.6, 27.13).
    /// </summary>
    [Fact]
    public void AnUnreadableRequirementWithNoActionIsNotReportedAsIneligibleHardware()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.Windows10Desktop)
            .With("firmware.boot-mode", CapabilityValue.Scalar("uefi"))
            .WithUnknown("firmware.tpm.version", "Win32_Tpm needs administrator rights")
            .With("firmware.tpm.ready", CapabilityValue.Enabled)
            .With("firmware.secure-boot", CapabilityValue.Enabled)
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, Windows11Outcome);

        PlanIssue blocker = Assert.Single(plan.Issues, i => i.Severity == PlanIssueSeverity.Blocker);

        Assert.Equal("capability.unreadable-and-unwritable", blocker.Code);
        Assert.DoesNotContain("Nothing in the action catalog", blocker.Detail, StringComparison.Ordinal);
    }

    /// <summary>The same shape, read successfully, keeps the plain "no action exists" wording.</summary>
    [Fact]
    public void ARequirementReadAsFailingKeepsThePlainNoActionWording()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.Windows10Desktop)
            .With("firmware.boot-mode", CapabilityValue.Scalar("uefi"))
            .With("firmware.tpm.version", CapabilityValue.Scalar("1.2"))
            .With("firmware.tpm.ready", CapabilityValue.Enabled)
            .With("firmware.secure-boot", CapabilityValue.Enabled)
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, Windows11Outcome);

        Assert.Equal(
            "capability.no-action-exists",
            Assert.Single(plan.Issues, i => i.Severity == PlanIssueSeverity.Blocker).Code);
    }

    // ---------------------------------------------------------------- snapshot freshness

    /// <summary>
    /// A plan compiled from an old scan describes a machine that may have moved on, and the plan
    /// hash would lock that stale picture in for approval. It says so (ADR 0004).
    /// </summary>
    [Fact]
    public void APlanCompiledFromAStaleScanSaysSo()
    {
        var clock = new FakeClock();

        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("1"))
            .Build();

        clock.Advance(TimeSpan.FromHours(3));

        var compiler = new StateCompiler(Graph, Catalog, CompilerOptions.Default, clock, new SequentialIds());
        Plan plan = compiler.Compile(snapshot, DockerOutcome);

        PlanIssue stale = Assert.Single(plan.Issues, i => i.Code == "snapshot.stale");
        Assert.Equal(PlanIssueSeverity.Warning, stale.Severity);

        // A warning, not a blocker: the engine still verifies against the live machine, so a stale
        // scan makes the preview misleading rather than the apply unsafe.
        Assert.False(plan.HasBlockers);
        Assert.Single(plan.Steps);
    }

    [Fact]
    public void APlanCompiledFromAFreshScanSaysNothingAboutStaleness()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("1"))
            .Build();

        Plan plan = NewCompiler().Compile(snapshot, DockerOutcome);

        Assert.DoesNotContain(plan.Issues, i => i.Code == "snapshot.stale");
    }

    /// <summary>
    /// Replaying a stored snapshot on purpose — a support engineer reading an old plan — must be
    /// able to switch the check off rather than being told it is stale on every line.
    /// </summary>
    [Fact]
    public void TheFreshnessCheckCanBeSwitchedOffForADeliberateReplay()
    {
        var clock = new FakeClock();

        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .With("firmware.cpu.virtualization", CapabilityValue.Enabled)
            .With("windows.feature.virtual-machine-platform", CapabilityValue.Enabled)
            .With("windows.feature.wsl", CapabilityValue.Enabled)
            .With("wsl.default-version", CapabilityValue.Scalar("1"))
            .Build();

        clock.Advance(TimeSpan.FromDays(30));

        var compiler = new StateCompiler(
            Graph,
            Catalog,
            CompilerOptions.Default with { MaxSnapshotAge = null },
            clock,
            new SequentialIds());

        Assert.DoesNotContain(compiler.Compile(snapshot, DockerOutcome).Issues, i => i.Code == "snapshot.stale");
    }

    private static string Describe(Plan plan)
    {
        var text = new StringBuilder();

        foreach (PlanPhase phase in plan.Phases)
        {
            text.Append("stage ").Append(phase.Index + 1)
                .Append(" -> restart ").Append(phase.RestartAfter)
                .Append(LfChar);

            foreach (PlanStep step in phase.Steps)
            {
                text.Append("  ").Append(step.Capability)
                    .Append(": ").Append(step.CurrentValue.Canonical)
                    .Append(" -> ").Append(step.DesiredValue.Canonical)
                    .Append("  [").Append(step.Action.Id).Append(" / ").Append(step.Action.WriteMode).Append(']')
                    .Append(LfChar);
            }
        }

        return Normalise(text.ToString()).TrimEnd();
    }
}

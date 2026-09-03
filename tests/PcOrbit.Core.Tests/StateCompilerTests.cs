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

    /// <summary>Raw string literals carry whatever the source file uses; comparison should not care.</summary>
    private static string Normalise(string text) => text.ReplaceLineEndings(Lf);

    private const string Lf = "\n";

    private const char LfChar = (char)10;

    /// <summary>
    /// A compact, readable rendering of the plan: stages, restarts, capability transitions and the
    /// chosen action. Everything a reviewer needs, nothing that changes between runs.
    /// </summary>
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

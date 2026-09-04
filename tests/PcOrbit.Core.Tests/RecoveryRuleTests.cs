using PcOrbit.Core.Actions;
using PcOrbit.Core.Checkup;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Tests;

/// <summary>
/// The rules added from the deep research (ADR 0004). Each one is tested twice: once for the case
/// it exists to catch, and once for the unreadable machine it must stay silent on.
/// </summary>
public sealed class RecoveryRuleTests
{
    private static readonly CapabilityGraph Graph = ShippedData.Graph();
    private static readonly ActionCatalog Catalog = ShippedData.Catalog();

    private static IReadOnlyList<Finding> Run(ICheckupRule rule, StateSnapshot snapshot) =>
        [.. rule.Evaluate(new CheckupContext(snapshot, Graph, Catalog))];

    // ---------------------------------------------------------------- recovery environment

    [Fact]
    public void AMachineWithNoRecoveryEnvironmentIsWarnedAboutIt()
    {
        IReadOnlyList<Finding> findings = Run(
            new RecoveryReadinessRule(),
            SnapshotBuilder.For(Machines.AsusAmdDesktop)
                .With("recovery.winre", CapabilityValue.Disabled)
                .Build());

        Finding finding = Assert.Single(findings);

        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal("recovery.environment-unavailable", finding.Code);
    }

    /// <summary>
    /// The reading needs administrator rights. Telling somebody their rescue path is gone because
    /// we were not allowed to look would be worse than saying nothing (spec 6.6, 27.13).
    /// </summary>
    [Fact]
    public void AnUnreadableRecoveryEnvironmentRaisesNothing()
    {
        Assert.Empty(Run(
            new RecoveryReadinessRule(),
            SnapshotBuilder.For(Machines.AsusAmdDesktop)
                .WithUnknown("recovery.winre", "ReAgent.xml needs administrator rights")
                .Build()));
    }

    [Fact]
    public void AHealthyRecoveryEnvironmentRaisesNothing()
    {
        Assert.Empty(Run(
            new RecoveryReadinessRule(),
            SnapshotBuilder.For(Machines.AsusAmdDesktop)
                .With("recovery.winre", CapabilityValue.Enabled)
                .Build()));
    }

    /// <summary>
    /// Half-applied is its own state. It is registered and it has an image and it still will not
    /// boot, so it gets its own sentence rather than being rounded to "off".
    /// </summary>
    [Fact]
    public void AHalfAppliedRecoveryUpdateIsItsOwnFinding()
    {
        Finding finding = Assert.Single(Run(
            new RecoveryReadinessRule(),
            SnapshotBuilder.For(Machines.AsusAmdDesktop)
                .With("recovery.winre", CapabilityValue.Scalar("staged"))
                .Build()));

        Assert.Equal("recovery.environment-staged", finding.Code);
        Assert.Equal("finding.recovery.environment-staged.title", finding.TitleKey);
    }

    // ---------------------------------------------------------------- restore point freshness

    [Fact]
    public void AnAncientRestorePointIsReported()
    {
        Finding finding = Assert.Single(Run(
            new RestorePointFreshnessRule(),
            SnapshotBuilder.For(Machines.AsusAmdDesktop)
                .With("windows.system-restore", CapabilityValue.Enabled)
                .With("recovery.restore-point.age-days", CapabilityValue.Scalar(400))
                .Build()));

        Assert.Equal("recovery.restore-point-stale", finding.Code);
        Assert.Equal("400", finding.Arguments["days"]);
    }

    [Fact]
    public void ARecentRestorePointRaisesNothing()
    {
        Assert.Empty(Run(
            new RestorePointFreshnessRule(),
            SnapshotBuilder.For(Machines.AsusAmdDesktop)
                .With("windows.system-restore", CapabilityValue.Enabled)
                .With("recovery.restore-point.age-days", CapabilityValue.Scalar(3))
                .Build()));
    }

    /// <summary>
    /// When protection is off, SystemRestoreRule already says the bigger thing. Two findings about
    /// one subject trains people to skim.
    /// </summary>
    [Fact]
    public void StaleRestorePointsAreNotReportedWhileSystemRestoreIsOff()
    {
        Assert.Empty(Run(
            new RestorePointFreshnessRule(),
            SnapshotBuilder.For(Machines.AsusAmdDesktop)
                .With("windows.system-restore", CapabilityValue.Disabled)
                .With("recovery.restore-point.age-days", CapabilityValue.Scalar(400))
                .Build()));
    }

    // ---------------------------------------------------------------- Windows 11 readiness

    [Fact]
    public void AWindows10MachineWithTheChipTurnedOffIsOfferedTheFirmwareRoute()
    {
        Finding finding = Assert.Single(Run(
            new Windows11ReadinessRule(),
            SnapshotBuilder.For(Machines.Windows10Desktop)
                .With("firmware.boot-mode", CapabilityValue.Scalar("uefi"))
                .With("firmware.tpm.version", CapabilityValue.Scalar("2.0"))
                .With("firmware.tpm.ready", CapabilityValue.Disabled)
                .With("firmware.secure-boot", CapabilityValue.Disabled)
                .With("workload.windows11-ready", CapabilityValue.Scalar(WorkloadEvaluator.NotReady))
                .Build()));

        Assert.Equal("outcome.windows11-ready", finding.SuggestedOutcomeId);
        Assert.Equal("finding.workload.windows11-not-ready.benefit.firmware", finding.BenefitKey);

        // Named, so "switched off" and "not present" cannot be confused for one another.
        Assert.Equal(
            ["firmware.secure-boot", "firmware.tpm.ready"],
            finding.RelatedCapabilities.Select(c => c.Value).Order());
    }

    /// <summary>
    /// A TPM that reports 1.2 is not a settings problem, and offering a plan would waste somebody's
    /// afternoon in a BIOS screen.
    /// </summary>
    [Fact]
    public void AMachineWhoseHardwareCannotQualifyIsNotOfferedAPlan()
    {
        Finding finding = Assert.Single(Run(
            new Windows11ReadinessRule(),
            SnapshotBuilder.For(Machines.Windows10Desktop)
                .With("firmware.boot-mode", CapabilityValue.Scalar("legacy"))
                .With("firmware.tpm.version", CapabilityValue.Scalar("1.2"))
                .With("firmware.tpm.ready", CapabilityValue.Enabled)
                .With("firmware.secure-boot", CapabilityValue.Enabled)
                .With("workload.windows11-ready", CapabilityValue.Scalar(WorkloadEvaluator.NotReady))
                .Build()));

        Assert.Null(finding.SuggestedOutcomeId);
        Assert.False(finding.HasFix);
        Assert.Equal("finding.workload.windows11-not-ready.benefit.hardware", finding.BenefitKey);
    }

    /// <summary>
    /// The mistake this rule is built to avoid: "your PC cannot run Windows 11" because
    /// <c>Win32_Tpm</c> needed administrator rights.
    /// </summary>
    [Fact]
    public void AMachineWhoseRequirementsCouldNotBeReadIsToldNothing()
    {
        Assert.Empty(Run(
            new Windows11ReadinessRule(),
            SnapshotBuilder.For(Machines.Windows10Desktop)
                .With("firmware.boot-mode", CapabilityValue.Scalar("uefi"))
                .WithUnknown("firmware.tpm.version", "Win32_Tpm needs administrator rights")
                .WithUnknown("firmware.tpm.ready", "Win32_Tpm needs administrator rights")
                .With("firmware.secure-boot", CapabilityValue.Enabled)
                .WithUnknown("workload.windows11-ready", "a requirement could not be read")
                .Build()));
    }

    [Fact]
    public void AMachineAlreadyOnWindows11IsToldNothing()
    {
        Assert.Empty(Run(
            new Windows11ReadinessRule(),

            // Everything failing, but the machine is already running Windows 11 — so the question
            // the rule asks does not apply to it.
            SnapshotBuilder.For(Machines.AsusAmdDesktop)
                .With("firmware.boot-mode", CapabilityValue.Scalar("legacy"))
                .With("firmware.tpm.version", CapabilityValue.Scalar("1.2"))
                .With("firmware.tpm.ready", CapabilityValue.Disabled)
                .With("firmware.secure-boot", CapabilityValue.Disabled)
                .With("workload.windows11-ready", CapabilityValue.Scalar(WorkloadEvaluator.NotReady))
                .Build()));
    }

    // ---------------------------------------------------------------- derived workloads

    /// <summary>
    /// The readiness workloads are derived from the same requires edges the compiler plans from,
    /// so a requirement that could not be read makes the workload Unknown rather than Not ready.
    /// </summary>
    [Fact]
    public void AWorkloadWithOneUnreadableRequirementIsUnknownNotNotReady()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.Windows10Desktop)
            .With("firmware.boot-mode", CapabilityValue.Scalar("uefi"))
            .With("firmware.tpm.version", CapabilityValue.Scalar("2.0"))
            .With("firmware.tpm.ready", CapabilityValue.Enabled)
            .WithUnknown("firmware.secure-boot", "needs administrator rights")
            .Build();

        CapabilityReading reading = WorkloadEvaluator.Evaluate(
            CapabilityId.Parse("workload.windows11-ready"),
            snapshot,
            Graph,
            DateTimeOffset.UnixEpoch);

        Assert.False(reading.Value.IsKnown);
    }

    [Fact]
    public void AWorkloadWithEveryRequirementMetIsReady()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.Windows10Desktop)
            .With("firmware.boot-mode", CapabilityValue.Scalar("uefi"))
            .With("firmware.tpm.version", CapabilityValue.Scalar("2.0"))
            .With("firmware.tpm.ready", CapabilityValue.Enabled)
            .With("firmware.secure-boot", CapabilityValue.Enabled)
            .Build();

        CapabilityReading reading = WorkloadEvaluator.Evaluate(
            CapabilityId.Parse("workload.windows11-ready"),
            snapshot,
            Graph,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(WorkloadEvaluator.Ready, reading.Value.Canonical);
    }

    /// <summary>
    /// A TPM 2.0 chip satisfies a <c>&gt;=2.0</c> requirement; a 1.2 chip does not. Worth pinning:
    /// the whole Windows 11 question turns on this comparison being numeric rather than textual.
    /// </summary>
    [Theory]
    [InlineData("2.0", true)]
    [InlineData("2.1", true)]
    [InlineData("1.2", false)]
    public void TheTpmVersionRequirementComparesNumerically(string version, bool expected)
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.Windows10Desktop)
            .With("firmware.boot-mode", CapabilityValue.Scalar("uefi"))
            .With("firmware.tpm.version", CapabilityValue.Scalar(version))
            .With("firmware.tpm.ready", CapabilityValue.Enabled)
            .With("firmware.secure-boot", CapabilityValue.Enabled)
            .Build();

        CapabilityReading reading = WorkloadEvaluator.Evaluate(
            CapabilityId.Parse("workload.windows11-ready"),
            snapshot,
            Graph,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, reading.Value.Canonical == WorkloadEvaluator.Ready);
    }
}

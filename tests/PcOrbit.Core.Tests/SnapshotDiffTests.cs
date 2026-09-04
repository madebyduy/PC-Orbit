using PcOrbit.Core.Compare;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Tests;

/// <summary>
/// The BIOS baseline diff. Its whole value depends on one distinction: a value that moved, versus
/// a value we stopped being able to see.
/// </summary>
public sealed class SnapshotDiffTests
{
    private static StateSnapshot Snapshot(string id, Action<SnapshotBuilder> build, MachineIdentity? machine = null)
    {
        var builder = new SnapshotBuilder(machine ?? Machines.AsusAmdDesktop, id);
        build(builder);
        return builder.Build();
    }

    [Fact]
    public void TwoIdenticalScansProduceNothing()
    {
        StateSnapshot before = Snapshot("snap-a", b => b
            .With("firmware.secure-boot", CapabilityValue.Enabled)
            .With("firmware.boot-mode", CapabilityValue.Scalar("uefi")));

        StateSnapshot after = Snapshot("snap-b", b => b
            .With("firmware.secure-boot", CapabilityValue.Enabled)
            .With("firmware.boot-mode", CapabilityValue.Scalar("uefi")));

        SnapshotComparison comparison = SnapshotDiff.Compare(before, after);

        Assert.True(comparison.IsUnchanged);
        Assert.True(comparison.SameMachine);
    }

    [Fact]
    public void AValueThatMovedIsAChange()
    {
        StateSnapshot before = Snapshot("snap-a", b => b.With("firmware.secure-boot", CapabilityValue.Enabled));
        StateSnapshot after = Snapshot("snap-b", b => b.With("firmware.secure-boot", CapabilityValue.Disabled));

        CapabilityChange change = Assert.Single(SnapshotDiff.Compare(before, after).RealChanges);

        Assert.Equal(CapabilityChangeKind.Changed, change.Kind);
        Assert.Equal("enabled", change.Before.Canonical);
        Assert.Equal("disabled", change.After.Canonical);
    }

    /// <summary>
    /// The failure this class exists to prevent. An unelevated scan cannot read the TPM; comparing
    /// it to an elevated one must not announce that the security chip was switched off.
    /// </summary>
    [Fact]
    public void AReadingWeLostIsNotReportedAsTheValueChanging()
    {
        StateSnapshot before = Snapshot("snap-a", b => b.With("firmware.tpm.ready", CapabilityValue.Enabled));
        StateSnapshot after = Snapshot("snap-b", b => b.WithUnknown("firmware.tpm.ready", "needs administrator rights"));

        SnapshotComparison comparison = SnapshotDiff.Compare(before, after);

        Assert.Empty(comparison.RealChanges);

        CapabilityChange change = Assert.Single(comparison.VisibilityChanges);
        Assert.Equal(CapabilityChangeKind.BecameUnreadable, change.Kind);
    }

    [Fact]
    public void AReadingWeGainedIsAlsoAVisibilityChangeRatherThanAValueChange()
    {
        StateSnapshot before = Snapshot("snap-a", b => b.WithUnknown("firmware.tpm.ready", "needs administrator rights"));
        StateSnapshot after = Snapshot("snap-b", b => b.With("firmware.tpm.ready", CapabilityValue.Enabled));

        SnapshotComparison comparison = SnapshotDiff.Compare(before, after);

        Assert.Empty(comparison.RealChanges);
        Assert.Equal(CapabilityChangeKind.BecameReadable, Assert.Single(comparison.VisibilityChanges).Kind);
    }

    /// <summary>Two scans that could read nothing have nothing to say to each other.</summary>
    [Fact]
    public void UnknownOnBothSidesIsNotNews()
    {
        StateSnapshot before = Snapshot("snap-a", b => b.WithUnknown("firmware.tpm.ready"));
        StateSnapshot after = Snapshot("snap-b", b => b.WithUnknown("firmware.tpm.ready"));

        Assert.True(SnapshotDiff.Compare(before, after).IsUnchanged);
    }

    [Fact]
    public void ACapabilityOnlyOneScanKnewAboutIsAddedOrRemoved()
    {
        StateSnapshot before = Snapshot("snap-a", b => b.With("firmware.secure-boot", CapabilityValue.Enabled));

        StateSnapshot after = Snapshot("snap-b", b => b
            .With("firmware.secure-boot", CapabilityValue.Enabled)
            .With("recovery.winre", CapabilityValue.Enabled));

        CapabilityChange change = Assert.Single(SnapshotDiff.Compare(before, after).RealChanges);

        Assert.Equal(CapabilityChangeKind.Added, change.Kind);
        Assert.Equal("recovery.winre", change.Capability.Value);

        Assert.Equal(
            CapabilityChangeKind.Removed,
            Assert.Single(SnapshotDiff.Compare(after, before).RealChanges).Kind);
    }

    [Fact]
    public void ComparingTwoDifferentMachinesSaysSo()
    {
        StateSnapshot before = Snapshot("snap-a", b => b.With("firmware.secure-boot", CapabilityValue.Enabled));

        StateSnapshot after = Snapshot(
            "snap-b",
            b => b.With("firmware.secure-boot", CapabilityValue.Enabled),
            Machines.DellIntelLaptop);

        Assert.False(SnapshotDiff.Compare(before, after).SameMachine);
    }

    /// <summary>
    /// A value read a different way is a weaker claim than one that moved on its own, and the
    /// change carries enough to tell them apart.
    /// </summary>
    [Fact]
    public void AChangeKnowsWhenItsSourceChangedToo()
    {
        var before = new StateSnapshot(
            "snap-a",
            DateTimeOffset.UnixEpoch,
            Machines.AsusAmdDesktop,
            [
                new CapabilityReading(
                    CapabilityId.Parse("firmware.secure-boot"),
                    CapabilityValue.Enabled,
                    new Evidence(EvidenceSourceKind.PowerShell, "Confirm-SecureBootUEFI", Confidence.High),
                    DateTimeOffset.UnixEpoch),
            ]);

        var after = new StateSnapshot(
            "snap-b",
            DateTimeOffset.UnixEpoch.AddDays(1),
            Machines.AsusAmdDesktop,
            [
                new CapabilityReading(
                    CapabilityId.Parse("firmware.secure-boot"),
                    CapabilityValue.Disabled,
                    new Evidence(EvidenceSourceKind.Registry, "UEFISecureBootEnabled", Confidence.High),
                    DateTimeOffset.UnixEpoch.AddDays(1)),
            ]);

        Assert.True(Assert.Single(SnapshotDiff.Compare(before, after).Changes).EvidenceChanged);
    }
}

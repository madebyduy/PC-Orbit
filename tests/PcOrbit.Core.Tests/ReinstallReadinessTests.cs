using PcOrbit.Adapters.Windows;
using PcOrbit.Core.Model;
using PcOrbit.Core.Setup;

namespace PcOrbit.Core.Tests;

/// <summary>
/// The gate in front of a reinstall. This is the most consequential refusal in the product — past
/// it, Windows Setup is in charge and there is no checkpoint to come back to.
/// </summary>
public sealed class ReinstallReadinessTests
{
    private static readonly WindowsMediaService Service = new();

    private static MediaContents GoodMedia(string architecture = "x64") => new(
        IsWindowsMedia: true,
        Architecture: architecture,
        Build: "10.0.26100.1",
        Editions: [new MediaEdition(1, "Windows 11 Pro", null)],
        Evidence: Evidence.Missing("test"));

    /// <summary>A machine in a fit state: unencrypted, on mains, with room.</summary>
    private static SnapshotBuilder Ready() =>
        SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("security.bitlocker.system-drive", CapabilityValue.Scalar("off"))
            .With("power.on-battery", CapabilityValue.Scalar("no"))
            .With("storage.system-drive.free-gb", CapabilityValue.Scalar(120))
            .With("recovery.winre", CapabilityValue.Enabled);

    private static ReinstallReadiness Assess(SnapshotBuilder machine, MediaContents? media = null) =>
        Service.Assess(machine.Build(), media ?? GoodMedia(), ReinstallScope.KeepEverything);

    [Fact]
    public void AMachineInAFitStateMayProceed()
    {
        ReinstallReadiness readiness = Assess(Ready());

        Assert.True(readiness.CanProceed);
        Assert.Empty(readiness.Blockers);
    }

    // ---------------------------------------------------------------- the media

    [Fact]
    public void SomethingThatIsNotWindowsMediaIsRefused()
    {
        MediaContents notWindows = new(false, null, null, [], Evidence.Missing("test"), "It is a Linux ISO.");

        Assert.False(Assess(Ready(), notWindows).CanProceed);
    }

    /// <summary>
    /// A 32-bit image against a 64-bit installation fails part way through rather than at the
    /// start, which is the worst possible moment to discover it.
    /// </summary>
    [Fact]
    public void MediaOfTheWrongArchitectureIsRefusedBeforeAnythingStarts()
    {
        Assert.False(Assess(Ready(), GoodMedia("x86")).CanProceed);
        Assert.True(Assess(Ready(), GoodMedia("arm64")).CanProceed);
    }

    // ---------------------------------------------------------------- the machine

    [Fact]
    public void AnEncryptedDriveIsRefusedUntilItIsDealtWith()
    {
        ReinstallReadiness readiness = Assess(
            Ready().With("security.bitlocker.system-drive", CapabilityValue.Scalar("on")));

        Assert.False(readiness.CanProceed);
        Assert.Contains(readiness.Blockers, b => b.Contains("recovery key", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reading the encryption state needs administrator rights, so a failed read is not evidence
    /// the drive is unencrypted — and here that distinction is worth a 48-digit key (spec 10.3).
    /// </summary>
    [Fact]
    public void AnUnreadableEncryptionStateBlocksJustAsHardAsAnEncryptedOne()
    {
        ReinstallReadiness readiness = Assess(
            Ready().WithUnknown("security.bitlocker.system-drive", "needs administrator rights"));

        Assert.False(readiness.CanProceed);
    }

    [Fact]
    public void ALaptopOnBatteryIsRefused()
    {
        ReinstallReadiness readiness = Assess(
            Ready().With("power.on-battery", CapabilityValue.Scalar("yes")));

        Assert.False(readiness.CanProceed);
        Assert.Contains(readiness.Blockers, b => b.Contains("battery", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TooLittleDiskIsRefused()
    {
        Assert.False(Assess(Ready().With("storage.system-drive.free-gb", CapabilityValue.Scalar(8))).CanProceed);
        Assert.True(Assess(Ready().With("storage.system-drive.free-gb", CapabilityValue.Scalar(45))).CanProceed);
    }

    /// <summary>
    /// No recovery environment is a warning, not a blocker: it removes the way back if the upgrade
    /// fails, but it does not make the upgrade itself more likely to fail.
    /// </summary>
    [Fact]
    public void NoRecoveryEnvironmentWarnsRatherThanBlocks()
    {
        ReinstallReadiness readiness = Assess(
            Ready().With("recovery.winre", CapabilityValue.Disabled));

        Assert.True(readiness.CanProceed);
        Assert.NotEmpty(readiness.Warnings);
    }

    [Fact]
    public void KeepingOnlyFilesSaysSoBeforeAnythingStarts()
    {
        ReinstallReadiness readiness = Service.Assess(
            Ready().Build(),
            GoodMedia(),
            ReinstallScope.KeepFilesOnly);

        Assert.Contains(readiness.Warnings, w => w.Contains("Applications", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the gate itself

    /// <summary>
    /// The refusal lives in the service, not in the UI. A caller that skipped the assessment does
    /// not get to start a reinstall by not asking.
    /// </summary>
    [Fact]
    public async Task StartingIsRefusedWhileAnythingBlocks()
    {
        var blocked = new ReinstallReadiness(
            ReinstallScope.KeepEverything,
            ["This drive is encrypted."],
            []);

        ReinstallStart start = await Service.StartAsync("C:\\nowhere\\windows.iso", blocked);

        Assert.False(start.Launched);
        Assert.Contains("encrypted", start.Problem, StringComparison.OrdinalIgnoreCase);
    }
}

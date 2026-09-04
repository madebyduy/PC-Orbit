using PcOrbit.Core.Firmware;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Tests;

/// <summary>
/// The decisions around writing a firmware setting.
/// </summary>
/// <remarks>
/// Everything here is the part that can be tested without the hardware: which settings are refused
/// outright, which value a caller is allowed to ask for, and what the user has to be told before
/// the write. The vendor call itself needs a machine whose firmware exposes the interface, and this
/// suite does not pretend to cover it — see ADR 0007 for what that means for shipping.
/// </remarks>
public sealed class FirmwareSettingsTests
{
    private static FirmwareSetting Setting(
        string name,
        string current = "Enable",
        params string[] options) =>
        new(
            name,
            current,
            options.Length > 0 ? options : ["Enable", "Disable"],
            FirmwareRiskTable.For(name),
            new Evidence(EvidenceSourceKind.VendorApi, "test", Confidence.High));

    // ---------------------------------------------------------------- the risk table

    /// <summary>
    /// The pair the whole table exists for.
    /// </summary>
    /// <remarks>
    /// <c>SecurityChip</c> and <c>ClearSecurityChip</c> differ by the word that makes one of them
    /// permanent. Turning the chip off can be turned back on; clearing it destroys the keys
    /// BitLocker and Windows Hello are built on, and putting the setting back gives you a working
    /// chip with none of the old keys in it. A first-match-wins scan would tell these apart by luck.
    /// </remarks>
    [Fact]
    public void ClearingTheSecurityChipIsRefusedWhileTurningItOffIsMerelySerious()
    {
        Assert.Equal(FirmwareRisk.Refused, FirmwareRiskTable.For("ClearSecurityChip"));
        Assert.Equal(FirmwareRisk.Serious, FirmwareRiskTable.For("SecurityChip"));
    }

    [Theory]
    [InlineData("SetSupervisorPassword")]
    [InlineData("Clear Static Password")]
    [InlineData("PhysicalPresenceForTpmProvision")]
    [InlineData("LoadSetupDefaults")]
    [InlineData("SecureFlashUpdate")]
    public void ChangesWithNoWayBackAreRefused(string name) =>
        Assert.Equal(FirmwareRisk.Refused, FirmwareRiskTable.For(name));

    [Theory]
    [InlineData("SecureBoot")]
    [InlineData("BootMode")]
    [InlineData("SATA Operation")]
    [InlineData("CSMSupport")]
    public void ChangesThatCanStopAMachineStartingAreSerious(string name) =>
        Assert.Equal(FirmwareRisk.Serious, FirmwareRiskTable.For(name));

    [Theory]
    [InlineData("VirtualizationTechnology")]
    [InlineData("WakeOnLAN")]
    [InlineData("Camera Access")]
    public void ChangesYouPutBackByChoosingAgainAreCareful(string name) =>
        Assert.Equal(FirmwareRisk.Careful, FirmwareRiskTable.For(name));

    [Theory]
    [InlineData("BootLogoDisplay")]
    [InlineData("FnKeyAsPrimary")]
    public void CosmeticChangesAreRoutine(string name) =>
        Assert.Equal(FirmwareRisk.Routine, FirmwareRiskTable.For(name));

    /// <summary>
    /// A machine can carry settings nobody has classified, and the safe reading of "we do not know
    /// what this does" is not "it is harmless".
    /// </summary>
    [Fact]
    public void ASettingNobodyHasClassifiedIsNeverRoutine()
    {
        Assert.Equal(FirmwareRisk.Careful, FirmwareRiskTable.For("AcmeFlibbertigibbetMode"));
        Assert.Equal(FirmwareRisk.Careful, FirmwareRiskTable.For(""));
    }

    /// <summary>Vendors punctuate differently; the table must not be fooled by a space.</summary>
    [Theory]
    [InlineData("Secure Boot")]
    [InlineData("Secure-Boot")]
    [InlineData("SECUREBOOT")]
    public void SpacingAndPunctuationDoNotHideAMatch(string name) =>
        Assert.Equal(FirmwareRisk.Serious, FirmwareRiskTable.For(name));

    // ---------------------------------------------------------------- the allowlist

    /// <summary>
    /// The allowlist is the firmware's own list, and the caller cannot extend it (ADR 0005).
    /// </summary>
    [Fact]
    public void AValueTheFirmwareDidNotOfferIsRefused()
    {
        FirmwareSetting setting = Setting("VirtualizationTechnology", "Disable");

        Assert.True(setting.Accepts("Enable"));
        Assert.False(setting.Accepts("Enabled"));
        Assert.False(setting.Accepts("yes"));

        FirmwareChangePlan plan = FirmwareChangePlan.For(setting, "yes", Snapshot(), false);

        Assert.False(plan.CanProceed);
        Assert.Contains("firmware.blocked.notOffered", plan.Blockers);
    }

    [Fact]
    public void ASettingWithNowhereToGoIsNotOfferedForWriting()
    {
        Assert.False(Setting("BootLogoDisplay", "Enable", "Enable").Writable);
        Assert.True(Setting("BootLogoDisplay", "Enable", "Enable", "Disable").Writable);
    }

    [Fact]
    public void ARefusedSettingIsNotWritableEvenWithSomewhereToGo() =>
        Assert.False(Setting("ClearSecurityChip", "No", "Yes", "No").Writable);

    [Fact]
    public void WritingWhatIsAlreadySetIsRefusedRatherThanDoneTwice()
    {
        FirmwareChangePlan plan = FirmwareChangePlan.For(
            Setting("VirtualizationTechnology", "Enable"), "Enable", Snapshot(), false);

        Assert.False(plan.CanProceed);
        Assert.Contains("firmware.blocked.alreadySet", plan.Blockers);
    }

    // ---------------------------------------------------------------- the preflight

    /// <summary>
    /// The prompt nobody expects. Turning Secure Boot off with BitLocker on breaks nothing, and the
    /// next start asks for a 48-digit key most people have never seen.
    /// </summary>
    [Fact]
    public void TurningOffSecureBootOnAnEncryptedMachineWarnsAboutTheRecoveryKey()
    {
        FirmwareChangePlan plan = FirmwareChangePlan.For(
            Setting("SecureBoot", "Enable"), "Disable", Snapshot(encrypted: CapabilityValue.Enabled), false);

        Assert.True(plan.CanProceed);
        Assert.Contains("firmware.consequence.bitlockerKey", plan.Consequences);
        Assert.True(plan.NeedsTypedConfirmation);
    }

    /// <summary>
    /// Unreadable encryption is not a blocker. Without administrator rights it is unreadable on
    /// most machines, and refusing every firmware change on that basis would refuse nearly all of
    /// them — so it is said instead, which is the honest form of "we could not rule this out".
    /// </summary>
    [Fact]
    public void UnreadableEncryptionIsSaidRatherThanTreatedAsAbsent()
    {
        FirmwareChangePlan plan = FirmwareChangePlan.For(
            Setting("SecureBoot", "Enable"), "Disable", Snapshot(encrypted: CapabilityValue.Unknown), false);

        Assert.True(plan.CanProceed);
        Assert.Contains("firmware.consequence.bitlockerUnknown", plan.Consequences);
        Assert.DoesNotContain("firmware.consequence.bitlockerKey", plan.Consequences);
    }

    [Fact]
    public void AChangeThatDoesNotTouchMeasuredBootSaysNothingAboutRecoveryKeys()
    {
        FirmwareChangePlan plan = FirmwareChangePlan.For(
            Setting("WakeOnLAN", "Disable"), "Enable", Snapshot(encrypted: CapabilityValue.Enabled), false);

        Assert.DoesNotContain("firmware.consequence.bitlockerKey", plan.Consequences);
        Assert.False(plan.NeedsTypedConfirmation);
    }

    /// <summary>
    /// A firmware change decided without knowing whether the disk is encrypted is the one this must
    /// not wave through.
    /// </summary>
    [Fact]
    public void WithNoScanToCheckAgainstNothingIsAllowed()
    {
        FirmwareChangePlan plan = FirmwareChangePlan.For(
            Setting("SecureBoot", "Enable"), "Disable", snapshot: null, firmwarePasswordSet: false);

        Assert.False(plan.CanProceed);
        Assert.Contains("firmware.blocked.noScan", plan.Blockers);
    }

    [Fact]
    public void EveryChangeSaysItNeedsARestart()
    {
        FirmwareChangePlan plan = FirmwareChangePlan.For(
            Setting("FnKeyAsPrimary", "Disable"), "Enable", Snapshot(), false);

        Assert.Contains("firmware.consequence.restart", plan.Consequences);
    }

    [Fact]
    public void AFirmwarePasswordIsAskedForRatherThanDiscoveredOnFailure()
    {
        FirmwareChangePlan plan = FirmwareChangePlan.For(
            Setting("WakeOnLAN", "Disable"), "Enable", Snapshot(), firmwarePasswordSet: true);

        Assert.True(plan.NeedsPassword);
        Assert.Contains("firmware.consequence.password", plan.Consequences);
    }

    // ---------------------------------------------------------------- the interface

    [Fact]
    public void ClassesRegisteredButSilentIsNotAUsableInterface()
    {
        // What a consumer Lenovo does with the commercial line's interface: all four classes are
        // registered by the driver, and every one of them returns nothing.
        var registered = new FirmwareInterface(InterfacePresent: false, "Lenovo", false, []);

        Assert.False(registered.IsUsable);
        Assert.False(FirmwareInterface.None("Lenovo").IsUsable);
    }

    [Fact]
    public void AnInterfaceThatAnsweredWithSettingsIsUsable()
    {
        var live = new FirmwareInterface(true, "Lenovo", false, [Setting("SecureBoot")]);

        Assert.True(live.IsUsable);
    }

    /// <summary>A machine whose only interesting fact is whether its system drive is encrypted.</summary>
    private static StateSnapshot Snapshot(CapabilityValue? encrypted = null) =>
        new SnapshotBuilder()
            .With("security.bitlocker.system-drive", encrypted ?? CapabilityValue.Disabled)
            .Build();
}

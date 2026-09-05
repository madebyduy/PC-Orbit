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

    /// <summary>
    /// Found on the first machine that answered. BootOrder reads as a colon-joined list of the
    /// same names it offers as options, and a one-value picker would have replaced the whole list
    /// with one device while looking like it was choosing a first boot device.
    /// </summary>
    [Fact]
    public void AListValuedSettingIsNotOfferedToAOneValuePicker()
    {
        FirmwareSetting bootOrder = Setting(
            "BootOrder", "USBCD:USBFDD:NVMe0:NVMe1", "HDD0", "USBCD", "USBFDD", "NVMe0", "NVMe1", "PXEBOOT");

        Assert.True(bootOrder.IsCompound);
        Assert.False(bootOrder.Writable);
    }

    /// <summary>
    /// The write the reorder dialog makes: the same names, another order. Accepted as a whole,
    /// refused when a part is not an option or a device appears twice.
    /// </summary>
    [Fact]
    public void AReorderedListOfTheSameOptionsIsAcceptedAndAStrangerOrADuplicateIsNot()
    {
        FirmwareSetting bootOrder = Setting("BootOrder", "USBCD:NVMe0:PXEBOOT", "USBCD", "NVMe0", "PXEBOOT", "HDD0");

        Assert.True(bootOrder.Accepts("NVMe0:USBCD:PXEBOOT"));
        Assert.True(bootOrder.Reorderable);
        Assert.False(bootOrder.Accepts("NVMe0:FLOPPY:PXEBOOT"));
        Assert.False(bootOrder.Accepts("NVMe0:NVMe0:PXEBOOT"));

        FirmwareChangePlan plan = FirmwareChangePlan.For(bootOrder, "NVMe0:USBCD:PXEBOOT", Snapshot(), false);

        Assert.True(plan.CanProceed);
        Assert.True(plan.NeedsTypedConfirmation);
    }

    [Fact]
    public void ARefusedListIsNotReorderableEither() =>
        Assert.False(Setting("PasswordDeviceList", "A:B", "A", "B").Reorderable);

    /// <summary>A colon is not enough on its own: a time is not a list of options.</summary>
    [Fact]
    public void AColonInsideAScalarDoesNotMakeItAList()
    {
        FirmwareSetting alarm = Setting("AlarmTime", "00:00:00", "HH/MM/SS");

        Assert.False(alarm.IsCompound);

        // Not writable either, but for the ordinary reason: one option is nowhere to go.
        Assert.False(alarm.Writable);
    }

    [Fact]
    public void ASingleDeviceValueFromTheSameOptionsIsStillWritable() =>
        Assert.True(Setting("NetworkBoot", "PXEBOOT", "HDD0", "PXEBOOT", "NVMe0").Writable);

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

    /// <summary>
    /// The bug this enum exists for.
    /// </summary>
    /// <remarks>
    /// Every ACPI-WMI class under <c>root\WMI</c> returns zero instances to a process without
    /// administrator rights — <c>MSAcpi_ThermalZoneTemperature</c> included, and that one works on
    /// every machine ever made. The first version read that emptiness as "your model does not have
    /// this" and said so to standard users as settled fact. An empty answer nobody had the rights
    /// to obtain is not an answer about the hardware.
    /// </remarks>
    [Fact]
    public void NotBeingAllowedToAskIsNotTheSameAsBeingToldNo()
    {
        var unasked = new FirmwareInterface(FirmwareAvailability.NeedsElevation, "Lenovo", false, []);
        var asked = new FirmwareInterface(FirmwareAvailability.ModelDoesNotImplement, "Lenovo", false, []);

        Assert.False(unasked.IsUsable);
        Assert.False(asked.IsUsable);

        // Both show no settings. Only one of them is a finding about the machine.
        Assert.False(unasked.AnswerIsAboutTheMachine);
        Assert.True(asked.AnswerIsAboutTheMachine);
    }

    [Theory]
    [InlineData(FirmwareAvailability.NoInterface)]
    [InlineData(FirmwareAvailability.ModelDoesNotImplement)]
    [InlineData(FirmwareAvailability.NeedsVendorTool)]
    public void EveryAnswerExceptElevationIsAboutTheMachine(FirmwareAvailability availability) =>
        Assert.True(new FirmwareInterface(availability, "Lenovo", false, []).AnswerIsAboutTheMachine);

    [Fact]
    public void AnInterfaceThatAnsweredWithSettingsIsUsable()
    {
        var live = new FirmwareInterface(
            FirmwareAvailability.Available, "Lenovo", false, [Setting("SecureBoot")]);

        Assert.True(live.IsUsable);
    }

    /// <summary>Available with nothing in it would be a contradiction, and is not treated as usable.</summary>
    [Fact]
    public void AvailableWithNoSettingsIsStillNotUsable() =>
        Assert.False(new FirmwareInterface(FirmwareAvailability.Available, "HP", false, []).IsUsable);

    [Fact]
    public void NoneMeansNoInterfaceRatherThanAnUnaskedQuestion() =>
        Assert.Equal(FirmwareAvailability.NoInterface, FirmwareInterface.None("Lenovo").Availability);

    /// <summary>A machine whose only interesting fact is whether its system drive is encrypted.</summary>
    private static StateSnapshot Snapshot(CapabilityValue? encrypted = null) =>
        new SnapshotBuilder()
            .With("security.bitlocker.system-drive", encrypted ?? CapabilityValue.Disabled)
            .Build();
}

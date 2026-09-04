using PcOrbit.Core.Checkup;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Tests;

/// <summary>
/// The firmware advisory: the one place this product talks about BIOS updates, and the one place
/// it must not offer to perform one (ADR 0006, spec decision 18).
/// </summary>
public sealed class FirmwareAdvisoryTests
{
    private static readonly CapabilityGraph Graph = ShippedData.Graph();

    private static IReadOnlyList<Finding> Run(StateSnapshot snapshot) =>
        [.. new FirmwareAdvisoryRule().Evaluate(new CheckupContext(snapshot, Graph, ShippedData.Catalog()))];

    private static SnapshotBuilder Machine(int biosAgeDays, bool viaWindowsUpdate) =>
        SnapshotBuilder.For(Machines.DellIntelLaptop)
            .With("firmware.bios.age-days", CapabilityValue.Scalar(biosAgeDays))
            .With("firmware.bios.version", CapabilityValue.Scalar("1.15.0"))
            .With(
                "firmware.update-delivery",
                viaWindowsUpdate ? CapabilityValue.Present : CapabilityValue.Absent);

    [Fact]
    public void ARecentBiosSaysNothing()
    {
        Assert.Empty(Run(Machine(biosAgeDays: 400, viaWindowsUpdate: false).Build()));
    }

    [Fact]
    public void AnOldBiosIsMentionedAsInformationRatherThanAsAFault()
    {
        Finding finding = Assert.Single(Run(Machine(biosAgeDays: 1500, viaWindowsUpdate: true).Build()));

        // Info, not Warning. A machine that has run its shipping firmware for four years without
        // trouble does not have a problem, and saying it does is invented urgency.
        Assert.Equal(FindingSeverity.Info, finding.Severity);
    }

    /// <summary>
    /// The line this rule exists to hold. Everything else in the checkup that can be fixed offers
    /// to fix it; this one must not, because the failure mode is a PC that will not turn on.
    /// </summary>
    [Fact]
    public void TheAdvisoryNeverOffersToDoAnything()
    {
        Finding finding = Assert.Single(Run(Machine(biosAgeDays: 1500, viaWindowsUpdate: true).Build()));

        Assert.False(finding.HasFix);
        Assert.Null(finding.SuggestedActionId);
        Assert.Null(finding.SuggestedOutcomeId);
    }

    /// <summary>
    /// A machine with no EFI System Resource Table will never be sent firmware by Windows Update.
    /// Someone waiting for one is waiting for something that cannot arrive, and that is the whole
    /// reason this rule is worth having.
    /// </summary>
    [Fact]
    public void AMachineWindowsUpdateCannotReachGetsADifferentSentence()
    {
        Finding viaUpdate = Assert.Single(Run(Machine(1500, viaWindowsUpdate: true).Build()));
        Finding vendorOnly = Assert.Single(Run(Machine(1500, viaWindowsUpdate: false).Build()));

        Assert.Equal("finding.firmware.bios-old.benefit.windowsUpdate", viaUpdate.BenefitKey);
        Assert.Equal("finding.firmware.bios-old.benefit.vendorOnly", vendorOnly.BenefitKey);
    }

    [Fact]
    public void AnUnreadableBiosDateSaysNothing()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.DellIntelLaptop)
            .WithUnknown("firmware.bios.age-days", "Win32_BIOS.ReleaseDate was not reported")
            .Build();

        Assert.Empty(Run(snapshot));
    }

    // ---------------------------------------------------------------- vendor support

    [Theory]
    [InlineData("LENOVO", "support.lenovo.com")]
    [InlineData("Dell Inc.", "dell.com")]
    [InlineData("HP", "support.hp.com")]
    [InlineData("ASUSTeK COMPUTER INC.", "asus.com")]
    [InlineData("Micro-Star International Co., Ltd.", "msi.com")]
    public void AKnownVendorGetsItsOwnSupportSite(string vendor, string expected)
    {
        MachineIdentity machine = Machines.DellIntelLaptop with
        {
            SystemVendor = vendor,
            BaseBoardVendor = vendor,
        };

        string? url = VendorSupport.SupportUrlFor(machine);

        Assert.NotNull(url);
        Assert.Contains(expected, url, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A vendor we do not have a page for gets nothing, not a search-engine query. Sending someone
    /// looking for firmware to a list of results is how they end up on a driver-pack site.
    /// </summary>
    [Fact]
    public void AnUnknownVendorGetsNoLinkAtAll()
    {
        MachineIdentity machine = Machines.DellIntelLaptop with
        {
            SystemVendor = "Some Vendor Nobody Has Heard Of",
            BaseBoardVendor = "Some Vendor Nobody Has Heard Of",
        };

        Assert.Null(VendorSupport.SupportUrlFor(machine));
    }

    /// <summary>
    /// Every link is a vendor's own front door over HTTPS, never a path built from model strings we
    /// guessed at — a constructed URL that 404s sends the user straight back to a search engine.
    /// </summary>
    [Fact]
    public void EverySupportLinkIsAPlainHttpsVendorSite()
    {
        string[] vendors = ["LENOVO", "Dell Inc.", "HP", "ASUSTeK COMPUTER INC.", "Acer",
            "Micro-Star International Co., Ltd.", "Gigabyte Technology Co., Ltd.", "ASRock",
            "Microsoft Corporation", "Samsung", "LG Electronics"];

        foreach (string vendor in vendors)
        {
            MachineIdentity machine = Machines.DellIntelLaptop with
            {
                SystemVendor = vendor,
                BaseBoardVendor = vendor,
            };

            string url = Assert.IsType<string>(VendorSupport.SupportUrlFor(machine));

            Assert.StartsWith("https://", url, StringComparison.Ordinal);
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out _), $"'{url}' is not a usable URL.");
        }
    }
}

using PcOrbit.Core.Model;

namespace PcOrbit.Core.Tests;

public sealed class CapabilityIdTests
{
    [Theory]
    [InlineData("cpu.virtualization")]
    [InlineData("firmware.cpu.virtualization")]
    [InlineData("windows.feature.virtual-machine-platform")]
    [InlineData("os.build")]
    public void AcceptsLowercaseDottedIds(string value) =>
        Assert.Equal(value, CapabilityId.Parse(value).Value);

    [Theory]
    [InlineData("")]
    [InlineData("Firmware.Cpu")]          // capitals would make ids ambiguous across files
    [InlineData("firmware..cpu")]
    [InlineData(".firmware")]
    [InlineData("firmware.")]
    [InlineData("firmware cpu")]
    [InlineData("firmware/cpu")]
    public void RejectsAnythingElse(string value) =>
        Assert.False(CapabilityId.TryParse(value, out _));

    [Fact]
    public void DefaultInstanceThrowsRatherThanPretendingToBeValid()
    {
        CapabilityId empty = default;
        Assert.Throws<InvalidOperationException>(() => empty.Value);
    }

    [Fact]
    public void OrdersOrdinallySoPlansAreReproducible()
    {
        List<CapabilityId> ids =
        [
            CapabilityId.Parse("b.one"),
            CapabilityId.Parse("a.two"),
            CapabilityId.Parse("a.one"),
        ];

        Assert.Equal(
            ["a.one", "a.two", "b.one"],
            ids.Order().Select(i => i.Value));
    }
}

public sealed class CapabilityValueTests
{
    [Fact]
    public void UnknownIsTheDefault() =>
        Assert.Equal(CapabilityStatus.Unknown, default(CapabilityValue).Status);

    /// <summary>
    /// Spec 6.6, 21.3 and 27.13 all say the same thing from different angles: a read that failed
    /// must never pass for a value. This is the test that keeps that true.
    /// </summary>
    [Fact]
    public void UnknownSatisfiesNothing()
    {
        Assert.False(CapabilityValue.Unknown.Satisfies(CapabilityValue.Enabled));
        Assert.False(CapabilityValue.Unknown.Satisfies(CapabilityValue.Disabled));
        Assert.False(CapabilityValue.Unknown.Satisfies(CapabilityValue.Unknown));
        Assert.False(CapabilityValue.Unknown.Satisfies(CapabilityValue.Scalar("2")));
    }

    [Fact]
    public void StatesMatchOnlyThemselves()
    {
        Assert.True(CapabilityValue.Enabled.Satisfies(CapabilityValue.Enabled));
        Assert.False(CapabilityValue.Enabled.Satisfies(CapabilityValue.Disabled));
        Assert.False(CapabilityValue.Supported.Satisfies(CapabilityValue.Enabled));
    }

    [Fact]
    public void ScalarsMatchCaseInsensitively()
    {
        Assert.True(CapabilityValue.Scalar("UEFI").Satisfies(CapabilityValue.Scalar("uefi")));
        Assert.True(CapabilityValue.Scalar(2).Satisfies(CapabilityValue.Scalar("2")));
        Assert.False(CapabilityValue.Scalar(1).Satisfies(CapabilityValue.Scalar("2")));
    }

    [Theory]
    [InlineData("5600", ">=5600", true)]
    [InlineData("6000", ">=5600", true)]
    [InlineData("4800", ">=5600", false)]
    [InlineData("not-a-number", ">=5600", false)]
    public void ScalarsSupportAtLeastComparisons(string actual, string expected, bool satisfied) =>
        Assert.Equal(satisfied, CapabilityValue.Scalar(actual).Satisfies(CapabilityValue.Parse(expected)));

    [Theory]
    [InlineData("enabled", CapabilityStatus.Enabled)]
    [InlineData("on", CapabilityStatus.Enabled)]
    [InlineData("disabled", CapabilityStatus.Disabled)]
    [InlineData("off", CapabilityStatus.Disabled)]
    [InlineData("supported", CapabilityStatus.Supported)]
    [InlineData("unknown", CapabilityStatus.Unknown)]
    [InlineData("2", CapabilityStatus.Value)]
    [InlineData("ready", CapabilityStatus.Value)]
    public void ParsesTheTextFormUsedInManifests(string text, CapabilityStatus expected) =>
        Assert.Equal(expected, CapabilityValue.Parse(text).Status);

    [Fact]
    public void CanonicalTextRoundTrips()
    {
        foreach (CapabilityValue value in new[]
        {
            CapabilityValue.Enabled,
            CapabilityValue.Disabled,
            CapabilityValue.Supported,
            CapabilityValue.NotSupported,
            CapabilityValue.Present,
            CapabilityValue.Absent,
            CapabilityValue.Unknown,
            CapabilityValue.Scalar("165"),
        })
        {
            Assert.Equal(value, CapabilityValue.Parse(value.Canonical));
        }
    }
}

public sealed class VendorMatcherTests
{
    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "ASUS")]
    [InlineData("ASUSTeK COMPUTER INC.", "ASUSTeK COMPUTER INC.")]
    [InlineData("Gigabyte Technology Co., Ltd.", "GIGABYTE")]
    [InlineData("Dell Inc.", "Dell Inc.")]
    [InlineData("Dell Inc.", "Dell")]
    [InlineData("LENOVO", "Lenovo")]
    [InlineData("Hewlett-Packard", "Hewlett-Packard")]
    [InlineData("HP Inc.", "HP")]
    public void MatchesTheSameVendorAcrossSmbiosSpellings(string actual, string candidate) =>
        Assert.True(VendorMatcher.Matches(actual, candidate));

    [Theory]
    [InlineData("Dell Inc.", "LENOVO")]
    [InlineData("ASUSTeK COMPUTER INC.", "ASRock")]
    [InlineData("", "Dell")]
    [InlineData(null, "Dell")]
    public void DoesNotMatchDifferentVendors(string? actual, string candidate) =>
        Assert.False(VendorMatcher.Matches(actual, candidate));

    [Fact]
    public void ModelMatchIsALooseContains()
    {
        Assert.True(VendorMatcher.ModelMatchesAny("Latitude 5440 Rugged", ["Latitude 5440"]));
        Assert.True(VendorMatcher.ModelMatchesAny("TUF GAMING B650-PLUS WIFI", ["B650-PLUS"]));
        Assert.False(VendorMatcher.ModelMatchesAny("Latitude 5440", ["OptiPlex 7010"]));
    }
}

public sealed class MachineIdentityTests
{
    [Fact]
    public void FingerprintIsStableForTheSameMachine()
    {
        MachineIdentity copy = Machines.AsusAmdDesktop with { };

        Assert.Equal(Machines.AsusAmdDesktop.Fingerprint, copy.Fingerprint);
        Assert.Equal(16, copy.Fingerprint.Length);
    }

    /// <summary>Spec 19.3: the fingerprint identifies hardware, never a person or a machine name.</summary>
    [Fact]
    public void FingerprintIgnoresThingsThatChangeWithoutTheHardware()
    {
        MachineIdentity sameHardware = Machines.DellIntelLaptop with
        {
            OsEdition = "Enterprise",
            IsLaptop = false,
        };

        Assert.Equal(Machines.DellIntelLaptop.Fingerprint, sameHardware.Fingerprint);
    }

    [Fact]
    public void FingerprintChangesWhenTheFirmwareDoes()
    {
        MachineIdentity updated = Machines.AsusAmdDesktop with { BiosVersion = "2703" };
        Assert.NotEqual(Machines.AsusAmdDesktop.Fingerprint, updated.Fingerprint);
    }

    /// <summary>
    /// Plenty of self-built desktops report the SMBIOS placeholders, so the board is the only
    /// useful name for them.
    /// </summary>
    [Fact]
    public void FallsBackToTheBoardWhenTheModelIsAnSmbiosPlaceholder() =>
        Assert.Equal("ASUSTeK COMPUTER INC. TUF GAMING B650-PLUS WIFI", Machines.AsusAmdDesktop.DisplayName);

    [Fact]
    public void UsesTheSystemModelWhenThereIsARealOne() =>
        Assert.Equal("Dell Inc. Latitude 5440", Machines.DellIntelLaptop.DisplayName);
}

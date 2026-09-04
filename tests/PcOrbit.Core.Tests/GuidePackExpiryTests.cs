using PcOrbit.Core.Actions;
using PcOrbit.Core.Guides;
using PcOrbit.Core.Model;
using PcOrbit.Core.Serialization;

namespace PcOrbit.Core.Tests;

/// <summary>
/// Knowledge packs expire (ADR 0004). These tests hold the gate shut, because the failure it
/// prevents — sending someone into a firmware menu that no longer exists on their BIOS — is
/// exactly the class of mistake this product cannot afford.
/// </summary>
public sealed class GuidePackExpiryTests
{
    private const string Pack = """
        {
          "id": "guide.test.pack",
          "version": "1.0.0",
          "capability": "firmware.cpu.virtualization",
          "targetState": "enabled",
          "expiresOn": "2026-12-31",
          "entries": [],
          "fallback": {
            "tier": "guidedGeneric",
            "settingName": "Virtualization Technology",
            "menuPath": ["Advanced", "CPU Configuration"]
          }
        }
        """;

    [Fact]
    public void APackInsideItsLifeStillGivesInstructions()
    {
        GuideData guide = GuideDataLoader.Load(Pack);

        Assert.False(guide.IsExpired(new DateOnly(2026, 12, 31)));
        Assert.NotNull(guide.SelectFor(Machines.AsusAmdDesktop, new DateOnly(2026, 12, 31)));
    }

    [Fact]
    public void APackPastItsDateGivesNothingAtAll()
    {
        GuideData guide = GuideDataLoader.Load(Pack);

        Assert.True(guide.IsExpired(new DateOnly(2027, 1, 1)));
        Assert.Null(guide.SelectFor(Machines.AsusAmdDesktop, new DateOnly(2027, 1, 1)));
    }

    /// <summary>
    /// The support tier is a promise about what the app can still do for this machine, so it has
    /// to fall on its own when the evidence behind it lapses — nobody will remember to lower it.
    /// </summary>
    [Fact]
    public void AnExpiredPackDropsTheMachineToReadOnly()
    {
        GuideData guide = GuideDataLoader.Load(Pack);
        ActionCatalog catalog = ShippedData.Catalog();

        Assert.Equal(
            SupportTier.GuidedGeneric,
            SupportTierResolver.Resolve(Machines.AsusAmdDesktop, catalog, guide, new DateOnly(2026, 12, 1)));

        Assert.Equal(
            SupportTier.ReadOnly,
            SupportTierResolver.Resolve(Machines.AsusAmdDesktop, catalog, guide, new DateOnly(2027, 1, 1)));
    }

    /// <summary>
    /// A pack with no date never expires. Allowed, but it has to be a deliberate omission rather
    /// than something a typo can produce — see the next test.
    /// </summary>
    [Fact]
    public void APackWithNoDateIsTreatedAsNeverExpiring()
    {
        GuideData guide = GuideDataLoader.Load(Pack.Replace(
            "\"expiresOn\": \"2026-12-31\",",
            string.Empty,
            StringComparison.Ordinal));

        Assert.Null(guide.ExpiresOn);
        Assert.False(guide.IsExpired(new DateOnly(2099, 1, 1)));
    }

    /// <summary>
    /// A date nobody can parse must not quietly become "no expiry" — that would make a typo the
    /// most permissive setting in the file, which is the wrong direction for a gate to fail in.
    /// </summary>
    [Fact]
    public void AnUnparseableExpiryDateIsRejectedAtLoad()
    {
        DataFileException failure = Assert.Throws<DataFileException>(() => GuideDataLoader.Load(
            Pack.Replace("2026-12-31", "next year", StringComparison.Ordinal)));

        Assert.Contains("expiresOn", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every pack that actually ships carries a date. Without this, adding a new guide file and
    /// forgetting the field would silently opt it out of the whole mechanism.
    /// </summary>
    [Fact]
    public void EveryShippedPackDeclaresAnExpiryDate()
    {
        foreach ((string id, GuideData guide) in ShippedData.Guides())
        {
            Assert.True(
                guide.ExpiresOn is not null,
                $"Guide pack '{id}' has no expiresOn. Vendor firmware menus move between revisions, "
                + "so a pack has to carry the date its evidence stops counting (ADR 0004).");
        }
    }

    /// <summary>
    /// And none of them has already lapsed, which would mean shipping a build whose guided steps
    /// refuse themselves on day one.
    /// </summary>
    [Fact]
    public void NoShippedPackHasAlreadyExpired()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);

        foreach ((string id, GuideData guide) in ShippedData.Guides())
        {
            Assert.False(
                guide.IsExpired(today),
                $"Guide pack '{id}' expired on {guide.ExpiresOn:yyyy-MM-dd}. Revalidate it against current "
                + "vendor firmware and move the date, rather than removing it.");
        }
    }
}

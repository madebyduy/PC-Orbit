using PcOrbit.Core.Checkup;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Localization;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Tests;

/// <summary>
/// The Windows drive filling up is the most common cause of "my PC got slow and updates fail",
/// and the one finding on this machine that no action can fix — so it has to explain itself well.
/// </summary>
public sealed class DiskSpaceRuleTests
{
    private static readonly CapabilityGraph Graph = ShippedData.Graph();

    private static IReadOnlyList<Finding> Run(string? freeGb)
    {
        SnapshotBuilder builder = SnapshotBuilder.For(Machines.AsusAmdDesktop);

        _ = freeGb is null
            ? builder.WithUnknown("storage.system-drive.free-gb", "could not read the drive")
            : builder.With("storage.system-drive.free-gb", CapabilityValue.Scalar(freeGb));

        return new CheckupEngine([new DiskSpaceRule()])
            .Run(new CheckupContext(builder.Build(), Graph, ShippedData.Catalog()));
    }

    [Fact]
    public void SaysNothingWhenThereIsPlentyOfRoom()
    {
        Assert.Empty(Run("240"));
        Assert.Empty(Run("25"));
    }

    [Fact]
    public void FlagsADriveTooFullForTheNextFeatureUpdate()
    {
        Finding finding = Assert.Single(Run("15.8"));

        Assert.Equal("storage.system-drive-low", finding.Code);
        Assert.Equal(FindingSeverity.Attention, finding.Severity);
        Assert.Equal("15.8", finding.Arguments["free"]);

        // Nothing in v0.1 deletes files on the user's behalf, so this explains rather than offers.
        Assert.False(finding.HasFix);
    }

    /// <summary>Below the level where Windows itself starts failing, this is a warning.</summary>
    [Fact]
    public void ADriveAboutToRunOutIsAWarning()
    {
        Finding finding = Assert.Single(Run("4"));

        Assert.Equal(FindingSeverity.Warning, finding.Severity);
    }

    /// <summary>
    /// An unreadable value never becomes a finding — the rule cannot tell whether the drive is
    /// full, and guessing would be exactly what spec 6.6 forbids.
    /// </summary>
    [Fact]
    public void AnUnreadableDriveRaisesNothing() => Assert.Empty(Run(null));

    [Fact]
    public void EveryLineOfTheFindingExistsInBothLanguages()
    {
        Finding finding = Assert.Single(Run("15.8"));

        foreach (string locale in new[] { "en", "vi" })
        {
            IStringCatalog catalog = JsonStringCatalog.LoadForLocale(ShippedData.I18nDirectory, locale);

            foreach (string key in new[] { finding.TitleKey, finding.BenefitKey, finding.SafetyKey })
            {
                Assert.True(catalog.Contains(key), $"'{locale}' has no string for '{key}'.");

                // Formatting must succeed with the arguments the rule actually passes: a finding
                // that throws while being rendered is a finding the user never sees.
                string text = catalog.Format(key, finding.Arguments);
                Assert.DoesNotContain("[[", text, StringComparison.Ordinal);
            }
        }
    }
}

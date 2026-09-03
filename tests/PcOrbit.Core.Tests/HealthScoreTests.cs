using PcOrbit.Core.Checkup;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Tests;

/// <summary>
/// The health score is a presentation of findings and nothing else (spec 8.1). These tests pin
/// that down, because a score that drifts away from the findings is a number that lies.
/// </summary>
public sealed class HealthScoreTests
{
    private static Finding Finding(FindingSeverity severity, string code = "test.rule") => new(
        Code: code,
        Severity: severity,
        TitleKey: "t",
        BenefitKey: "b",
        SafetyKey: "s",
        Arguments: new Dictionary<string, string>(StringComparer.Ordinal),
        Evidence: new Evidence(EvidenceSourceKind.Wmi, "test", Confidence.High));

    [Fact]
    public void AMachineWithNoFindingsScoresFullMarks()
    {
        HealthVerdict verdict = HealthScore.Evaluate([]);

        Assert.Equal(100, verdict.Score);
        Assert.Equal("health.excellent", verdict.LabelKey);
    }

    /// <summary>
    /// The rules never raise a finding from an unreadable value, so an unreadable machine has no
    /// findings and scores 100. The score is not a guess about what we could not see.
    /// </summary>
    [Fact]
    public void ScoreOnlyEverMovesBecauseOfFindings()
    {
        Assert.Equal(HealthScore.Evaluate([]).Score, HealthScore.Evaluate([]).Score);
        Assert.True(HealthScore.Evaluate([Finding(FindingSeverity.Info)]).Score < 100);
    }

    [Fact]
    public void AWarningCostsMoreThanSomethingWorthALook()
    {
        int warning = HealthScore.Evaluate([Finding(FindingSeverity.Warning)]).Score;
        int attention = HealthScore.Evaluate([Finding(FindingSeverity.Attention)]).Score;
        int info = HealthScore.Evaluate([Finding(FindingSeverity.Info)]).Score;

        Assert.True(warning < attention, "a warning should cost more than an attention item");
        Assert.True(attention < info, "an attention item should cost more than an info item");
    }

    /// <summary>
    /// A checkup that found things worth fixing is not a broken PC, so the number stays in a range
    /// the findings can actually justify.
    /// </summary>
    [Fact]
    public void ScoreNeverFallsBelowTheFloorHoweverManyFindings()
    {
        List<Finding> many = [.. Enumerable.Range(0, 40).Select(i => Finding(FindingSeverity.Warning, $"rule.{i}"))];

        HealthVerdict verdict = HealthScore.Evaluate(many);

        Assert.Equal(40, verdict.Score);
        Assert.Equal("health.needsWork", verdict.LabelKey);
    }

    [Fact]
    public void EveryLabelIsAStringCatalogKeyThatShips()
    {
        string[] keys =
        [
            HealthScore.Evaluate([]).LabelKey,
            HealthScore.Evaluate([Finding(FindingSeverity.Attention)]).LabelKey,
            HealthScore.Evaluate([Finding(FindingSeverity.Warning), Finding(FindingSeverity.Warning, "b")]).LabelKey,
            HealthScore.Evaluate([.. Enumerable.Range(0, 40).Select(i => Finding(FindingSeverity.Warning, $"r{i}"))]).LabelKey,
        ];

        foreach (string locale in new[] { "en", "vi" })
        {
            Localization.IStringCatalog catalog =
                Localization.JsonStringCatalog.LoadForLocale(ShippedData.I18nDirectory, locale);

            foreach (string key in keys.Distinct())
            {
                Assert.True(catalog.Contains(key), $"'{locale}' has no string for health label '{key}'.");
            }
        }
    }
}

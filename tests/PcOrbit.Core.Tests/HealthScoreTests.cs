using PcOrbit.Core.Checkup;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Tests;

/// <summary>
/// The health score is a presentation of findings and nothing else (spec 8.1). These tests pin
/// that down, because a score that drifts away from the findings is a number that lies.
/// </summary>
public sealed class HealthScoreTests
{
    private static readonly CapabilityGraph Graph = ShippedData.Graph();

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
        HealthVerdict verdict = HealthScore.Evaluate([], unreadableCount: 0);

        Assert.Equal(100, verdict.Score);
        Assert.Equal("health.excellent", verdict.LabelKey);
        Assert.False(verdict.IsPartial);
    }

    /// <summary>
    /// The rules never raise a finding from an unreadable value, so an unreadable machine has no
    /// findings and still scores 100. The score is not a guess about what we could not see — which
    /// is exactly why it has to travel with the count of what it could not see.
    /// </summary>
    [Fact]
    public void ScoreOnlyEverMovesBecauseOfFindings()
    {
        Assert.Equal(100, HealthScore.Evaluate([], unreadableCount: 9).Score);
        Assert.True(HealthScore.Evaluate([Finding(FindingSeverity.Info)], 0).Score < 100);
    }

    /// <summary>
    /// The one thing that stops "we found nothing" from being read as "your PC is fine" when in
    /// fact half the machine was unreadable (ADR 0004).
    /// </summary>
    [Fact]
    public void AVerdictSaysHowMuchOfTheMachineItCouldNotSee()
    {
        HealthVerdict blind = HealthScore.Evaluate([], unreadableCount: 12);

        Assert.Equal(100, blind.Score);
        Assert.Equal(12, blind.UnreadableCount);
        Assert.True(blind.IsPartial, "a score derived from a partly unreadable machine must say so");
    }

    [Fact]
    public void TheVerdictCarriesTheNumberOfFindingsItWasBuiltFrom()
    {
        HealthVerdict verdict = HealthScore.Evaluate(
            [Finding(FindingSeverity.Warning), Finding(FindingSeverity.Info, "b")],
            unreadableCount: 0);

        Assert.Equal(2, verdict.FindingCount);
    }

    [Fact]
    public void AWarningCostsMoreThanSomethingWorthALook()
    {
        int warning = HealthScore.Evaluate([Finding(FindingSeverity.Warning)], 0).Score;
        int attention = HealthScore.Evaluate([Finding(FindingSeverity.Attention)], 0).Score;
        int info = HealthScore.Evaluate([Finding(FindingSeverity.Info)], 0).Score;

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

        HealthVerdict verdict = HealthScore.Evaluate(many, unreadableCount: 0);

        Assert.Equal(40, verdict.Score);
        Assert.Equal("health.needsWork", verdict.LabelKey);
    }

    // ---------------------------------------------------------------- unreadable counting

    [Fact]
    public void UnreadableCountsOnlyValuesThisMachineWasSupposedToAnswerFor()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Supported)
            .WithUnknown("firmware.secure-boot", "needs administrator rights")
            .WithUnknown("firmware.tpm.ready", "Win32_Tpm needs administrator rights")
            .Build();

        Assert.Equal(2, HealthScore.CountUnreadable(snapshot, Graph));
    }

    /// <summary>
    /// A node declared <c>observable: false</c> is expected to read Unknown. Counting it would turn
    /// an honest declaration in the graph into a permanent complaint in the UI.
    /// </summary>
    [Fact]
    public void ACapabilityDeclaredUnobservableIsNotCountedAsUnreadable()
    {
        CapabilityNode? rebar = Graph.Node(CapabilityId.Parse("firmware.rebar"));

        Assert.NotNull(rebar);
        Assert.False(rebar.Observable, "this test is meaningless if firmware.rebar becomes observable");

        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .WithUnknown("firmware.rebar", "needs the GPU driver API")
            .Build();

        Assert.Equal(0, HealthScore.CountUnreadable(snapshot, Graph));
    }

    [Fact]
    public void EvaluatingFromACheckupContextTakesTheCountFromTheSnapshot()
    {
        StateSnapshot snapshot = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .WithUnknown("firmware.secure-boot", "needs administrator rights")
            .Build();

        HealthVerdict verdict = HealthScore.Evaluate(
            new CheckupContext(snapshot, Graph, ShippedData.Catalog()),
            []);

        Assert.Equal(1, verdict.UnreadableCount);
        Assert.True(verdict.IsPartial);
    }

    [Fact]
    public void EveryLabelIsAStringCatalogKeyThatShips()
    {
        string[] keys =
        [
            HealthScore.Evaluate([], 0).LabelKey,
            HealthScore.Evaluate([Finding(FindingSeverity.Attention)], 0).LabelKey,
            HealthScore.Evaluate([Finding(FindingSeverity.Warning), Finding(FindingSeverity.Warning, "b")], 0).LabelKey,
            HealthScore.Evaluate([.. Enumerable.Range(0, 40).Select(i => Finding(FindingSeverity.Warning, $"r{i}"))], 0).LabelKey,
            "health.partial",
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

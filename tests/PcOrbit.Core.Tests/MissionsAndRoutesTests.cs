using PcOrbit.Core.Checkup;
using PcOrbit.Core.Missions;
using PcOrbit.Core.Model;
using PcOrbit.Core.Navigation;
using PcOrbit.Core.Serialization;

namespace PcOrbit.Core.Tests;

/// <summary>
/// The two things the investment memo asked for first: a sentence finds a mission, and no finding
/// ends in a dead end.
/// </summary>
public sealed class MissionsAndRoutesTests
{
    private static MissionCatalog Shipped() =>
        MissionCatalogLoader.LoadDirectory(Path.Combine(ShippedData.Directory, "missions"));

    private static string Title(string key) => key;

    // ---------------------------------------------------------------- folding

    [Theory]
    [InlineData("Máy Chậm", "may cham")]
    [InlineData("đầy ổ", "day o")]
    [InlineData("KHỞI ĐỘNG", "khoi dong")]
    [InlineData("docker", "docker")]
    public void FoldingMakesAccentsAndCaseIrrelevant(string text, string expected) =>
        Assert.Equal(expected, TextFold.Fold(text));

    // ---------------------------------------------------------------- matching

    /// <summary>The sentence from the memo, typed three ways, lands on the same mission.</summary>
    [Theory]
    [InlineData("máy chậm sau update")]
    [InlineData("may cham sau update")]
    [InlineData("MÁY CHẬM")]
    public void ThePcGotSlowAfterAnUpdateFindsIncidentMode(string query)
    {
        IReadOnlyList<MissionMatch> matches = Shipped().Match(query, Title);

        Assert.NotEmpty(matches);
        Assert.Equal("mission.slow-after-update", matches[0].Mission.Id);
        Assert.Equal(PageKeys.Timeline, matches[0].Mission.Route.Target);
    }

    [Theory]
    [InlineData("docker", "mission.docker-wsl")]
    [InlineData("ổ c đầy", "mission.free-space")]
    [InlineData("không có âm thanh", "mission.device-not-working")]
    [InlineData("secure boot", "mission.firmware-switches")]
    [InlineData("hoàn tác", "mission.undo")]
    [InlineData("windows 11", "mission.windows11-ready")]
    public void CommonProblemsFindTheirMission(string query, string expected) =>
        Assert.Equal(expected, Shipped().Match(query, Title)[0].Mission.Id);

    /// <summary>
    /// Every word has to land. "máy" alone is in many aliases; "máy in" (a printer) is in none, so a
    /// query nobody wrote a mission for returns nothing rather than the nearest-sounding one.
    /// </summary>
    [Fact]
    public void AQueryWithAWordNoMissionKnowsReturnsNothingRatherThanAGuess() =>
        Assert.Empty(Shipped().Match("máy in không in được", Title));

    [Fact]
    public void AnEmptyQueryMatchesNothing()
    {
        Assert.Empty(Shipped().Match(string.Empty, Title));
        Assert.Empty(Shipped().Match("   ", Title));
    }

    [Fact]
    public void MatchingIsDeterministic()
    {
        MissionCatalog catalog = Shipped();

        string[] first = [.. catalog.Match("bios", Title).Select(m => m.Mission.Id)];
        string[] second = [.. catalog.Match("bios", Title).Select(m => m.Mission.Id)];

        Assert.Equal(first, second);
    }

    // ---------------------------------------------------------------- the shipped file

    [Fact]
    public void EveryShippedMissionRoutesSomewhereTheAppCanOpen()
    {
        MissionCatalog catalog = Shipped();

        Assert.NotEmpty(catalog.All);
        Assert.All(catalog.All, m => Assert.True(m.Route.IsWellFormed, $"{m.Id} routes to {m.Route.Kind}:{m.Route.Target}"));

        HashSet<string> outcomes = [.. ShippedData.Outcomes().Select(o => o.Id)];

        Assert.Empty(MissionCatalogLoader.Validate(catalog, outcomes));
    }

    [Fact]
    public void AMissionPointingAtAPageNobodyBuiltIsRefusedByTheLoader()
    {
        const string json = """
            { "version": "0.1.0", "missions": [ {
              "id": "mission.x", "titleKey": "t", "descriptionKey": "d", "aliases": ["x"],
              "route": { "kind": "page", "target": "not-a-page" },
              "estimatedMinutes": 1, "restart": "none", "recoveryKey": "r" } ] }
            """;

        Assert.Throws<DataFileException>(() => MissionCatalogLoader.Load(json));
    }

    [Fact]
    public void AMissionRoutingToAnOutcomeThatIsNotShippedFailsValidation()
    {
        const string json = """
            { "version": "0.1.0", "missions": [ {
              "id": "mission.x", "titleKey": "t", "descriptionKey": "d", "aliases": ["x"],
              "route": { "kind": "outcome", "target": "outcome.nope" },
              "estimatedMinutes": 1, "restart": "none", "recoveryKey": "r" } ] }
            """;

        var catalog = new MissionCatalog(MissionCatalogLoader.Load(json));

        Assert.Single(MissionCatalogLoader.Validate(catalog, new HashSet<string>(StringComparer.Ordinal)));
    }

    // ---------------------------------------------------------------- routes

    [Theory]
    [InlineData(RouteKind.Page, "cleanup", true)]
    [InlineData(RouteKind.Page, "somewhere", false)]
    [InlineData(RouteKind.Settings, "ms-settings:recovery", true)]
    [InlineData(RouteKind.Settings, "recovery", false)]
    [InlineData(RouteKind.Web, "https://support.lenovo.com/", true)]
    [InlineData(RouteKind.Web, "http://support.lenovo.com/", false)]
    [InlineData(RouteKind.Outcome, "outcome.system-restore-on", true)]
    public void ARouteKnowsWhetherTheAppCanOpenIt(RouteKind kind, string target, bool wellFormed) =>
        Assert.Equal(wellFormed, new Route(kind, target).IsWellFormed);

    /// <summary>
    /// The memo's second gate: findings with a visible and safe next route, 100 percent. Every rule
    /// is provoked on a machine where everything is off, missing or unreadable, and each finding it
    /// raises must lead somewhere.
    /// </summary>
    [Fact]
    public void NoFindingIsADeadEnd()
    {
        StateSnapshot worst = SnapshotBuilder.For(Machines.AsusAmdDesktop)
            .With("cpu.virtualization", CapabilityValue.Enabled)
            .With("firmware.cpu.virtualization", CapabilityValue.Disabled)
            .With("windows.system-restore", CapabilityValue.Disabled)
            .With("storage.system-drive.free-gb", CapabilityValue.Scalar(4))
            .With("security.bitlocker.system-drive", CapabilityValue.Enabled)
            .With("recovery.winre", CapabilityValue.Disabled)
            .With("recovery.restore-point.age-days", CapabilityValue.Scalar(400))
            .With("firmware.bios.age-days", CapabilityValue.Scalar(1500))
            .With("firmware.secure-boot", CapabilityValue.Disabled)
            .With("firmware.tpm.version", CapabilityValue.Scalar("1.2"))
            .With("memory.rated-speed", CapabilityValue.Scalar(3200))
            .With("memory.current-speed", CapabilityValue.Scalar(2133))
            .With("display.current-refresh-rate", CapabilityValue.Scalar(60))
            .With("display.max-refresh-rate", CapabilityValue.Scalar(144))
            .Build();

        IReadOnlyList<Finding> findings = CheckupEngine.Default.Run(
            new CheckupContext(worst, ShippedData.Graph(), ShippedData.Catalog()));

        Assert.NotEmpty(findings);

        List<string> deadEnds = [.. findings.Where(f => f.NextRoute is null).Select(f => f.Code)];

        Assert.True(deadEnds.Count == 0, "These findings tell the user something is wrong and give them nowhere to go: " + string.Join(", ", deadEnds));
        Assert.All(findings, f => Assert.True(f.NextRoute!.IsWellFormed, $"{f.Code} routes to {f.NextRoute!.Kind}:{f.NextRoute!.Target}"));
    }

    [Fact]
    public void AnOutcomeIsTheRouteWhenAFindingHasOne()
    {
        var finding = new Finding(
            "x", FindingSeverity.Warning, "t", "b", "s",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Evidence(EvidenceSourceKind.Wmi, "test", Confidence.High),
            SuggestedOutcomeId: "outcome.system-restore-on",
            Route: Route.ToPage(PageKeys.Recovery));

        // The outcome wins: it is the only route that ends in a verified change.
        Assert.Equal(RouteKind.Outcome, finding.NextRoute!.Kind);
    }
}

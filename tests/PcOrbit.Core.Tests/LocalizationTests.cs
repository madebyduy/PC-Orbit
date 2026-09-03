using System.Globalization;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Localization;

namespace PcOrbit.Core.Tests;

public sealed class IcuMessageFormatterTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi");

    private static string Format(string pattern, CultureInfo culture, params (string, string)[] args) =>
        IcuMessageFormatter.Format(pattern, Args(args), culture);

    private static Dictionary<string, string> Args((string Key, string Value)[] pairs)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);

        foreach ((string key, string value) in pairs)
        {
            result[key] = value;
        }

        return result;
    }

    [Fact]
    public void SubstitutesNamedArguments() =>
        Assert.Equal(
            "60 Hz but supports 165 Hz",
            Format("{current} Hz but supports {max} Hz", English, ("current", "60"), ("max", "165")));

    [Fact]
    public void LeavesAMissingArgumentVisibleRatherThanBlank() =>
        Assert.Equal("{max} Hz", Format("{max} Hz", English));

    [Theory]
    [InlineData("0", "no restart")]
    [InlineData("1", "1 restart")]
    [InlineData("3", "3 restarts")]
    public void HandlesEnglishPlurals(string count, string expected) =>
        Assert.Equal(
            expected,
            Format(
                "{restarts, plural, =0 {no restart} one {# restart} other {# restarts}}",
                English,
                ("restarts", count)));

    /// <summary>
    /// Vietnamese has one plural form. Spec 21.11 wants that difference handled by the message
    /// format, not by the code building sentences.
    /// </summary>
    [Theory]
    [InlineData("0", "không cần khởi động lại")]
    [InlineData("1", "1 lần khởi động lại")]
    [InlineData("3", "3 lần khởi động lại")]
    public void HandlesVietnamesePlurals(string count, string expected) =>
        Assert.Equal(
            expected,
            Format(
                "{restarts, plural, =0 {không cần khởi động lại} other {# lần khởi động lại}}",
                Vietnamese,
                ("restarts", count)));

    [Fact]
    public void HandlesSelect()
    {
        const string pattern = "{mode, select, auto {automatic} other {you do it}}";

        Assert.Equal("automatic", Format(pattern, English, ("mode", "auto")));
        Assert.Equal("you do it", Format(pattern, English, ("mode", "guidedGeneric")));
    }

    [Fact]
    public void HandlesArgumentsNestedInsidePluralOptions() =>
        Assert.Equal(
            "2 changes on ASUS",
            Format(
                "{count, plural, one {# change on {vendor}} other {# changes on {vendor}}}",
                English,
                ("count", "2"),
                ("vendor", "ASUS")));

    [Fact]
    public void UnbalancedBracesEmitVisiblyBrokenTextRatherThanThrowing()
    {
        string result = Format("{count, plural, one {# change", English, ("count", "1"));

        Assert.False(string.IsNullOrEmpty(result));
        Assert.Contains("{", result, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubledApostropheBecomesOne() =>
        Assert.Equal("it's on", Format("it''s on", English));
}

public sealed class PluralRulesTests
{
    [Theory]
    [InlineData("en", 1, "one")]
    [InlineData("en", 0, "other")]
    [InlineData("en", 2, "other")]
    [InlineData("vi", 1, "other")]
    [InlineData("vi", 5, "other")]
    [InlineData("ja", 1, "other")]
    [InlineData("fr", 0, "one")]
    [InlineData("fr", 1, "one")]
    [InlineData("fr", 2, "other")]
    public void MatchesTheLanguagesWeShipOrPlanFor(string language, int number, string expected) =>
        Assert.Equal(expected, PluralRules.CategoryOf(CultureInfo.GetCultureInfo(language), number));
}

/// <summary>
/// The CI gate spec 21.11 asks for: shipping two languages means keeping them in step, and no
/// string the product can display may be missing from either.
/// </summary>
public sealed class StringCatalogTests
{
    private static JsonStringCatalog Load(string locale) =>
        JsonStringCatalog.LoadFile(Path.Combine(ShippedData.I18nDirectory, $"{locale}.json"));

    [Fact]
    public void EnglishAndVietnameseHaveExactlyTheSameKeys()
    {
        HashSet<string> english = [.. Load("en").Keys];
        HashSet<string> vietnamese = [.. Load("vi").Keys];

        Assert.Empty(english.Except(vietnamese));
        Assert.Empty(vietnamese.Except(english));
    }

    [Fact]
    public void EveryCapabilityHasADisplayNameInBothLanguages()
    {
        CapabilityGraph graph = ShippedData.Graph();

        foreach (string locale in new[] { "en", "vi" })
        {
            JsonStringCatalog catalog = Load(locale);

            foreach (CapabilityNode node in graph.Nodes)
            {
                Assert.True(
                    catalog.Contains(node.DisplayKey),
                    $"'{locale}' has no string for '{node.DisplayKey}' (capability '{node.Id}').");
            }
        }
    }

    [Fact]
    public void EveryActionAndOutcomeTitleExistsInBothLanguages()
    {
        List<string> keys =
        [
            .. ShippedData.Catalog().All.Select(a => a.TitleKey),
            .. ShippedData.Outcomes().SelectMany(o => new[] { o.TitleKey, o.DescriptionKey }),
        ];

        foreach (string locale in new[] { "en", "vi" })
        {
            JsonStringCatalog catalog = Load(locale);

            foreach (string key in keys)
            {
                Assert.True(catalog.Contains(key), $"'{locale}' has no string for '{key}'.");
            }
        }
    }

    /// <summary>
    /// Every pattern must survive formatting. A translator typing an unbalanced brace should fail
    /// the build, not produce mangled text in front of a user.
    /// </summary>
    [Fact]
    public void EveryPatternFormatsWithoutLeavingBrokenIcuSyntax()
    {
        foreach (string locale in new[] { "en", "vi" })
        {
            JsonStringCatalog catalog = Load(locale);

            foreach (string key in catalog.Keys)
            {
                string formatted = catalog.Format(key, Placeholders);

                Assert.False(
                    formatted.Contains("plural,", StringComparison.Ordinal)
                    || formatted.Contains("select,", StringComparison.Ordinal),
                    $"'{locale}' key '{key}' still contains raw ICU syntax after formatting: {formatted}");
            }
        }
    }

    /// <summary>
    /// A missing key is visibly broken rather than silently empty, and the English catalog covers
    /// for a missing translation so the user still gets an actionable sentence.
    /// </summary>
    [Fact]
    public void FallsBackToEnglishThenToAVisibleMarker()
    {
        JsonStringCatalog english = Load("en");
        var sparse = new JsonStringCatalog("vi", new Dictionary<string, string>(StringComparer.Ordinal), english);

        Assert.Equal(english.Format("status.enabled"), sparse.Format("status.enabled"));
        Assert.Equal("[[does.not.exist]]", sparse.Format("does.not.exist"));
    }

    [Fact]
    public void LoadForLocaleFallsBackToEnglishForAnUnshippedLanguage()
    {
        IStringCatalog german = JsonStringCatalog.LoadForLocale(ShippedData.I18nDirectory, "de-DE");

        Assert.Equal("en", german.Locale);
        Assert.True(german.Contains("status.enabled"));
    }

    [Fact]
    public void LoadForLocaleAcceptsARegionalTag()
    {
        IStringCatalog vietnamese = JsonStringCatalog.LoadForLocale(ShippedData.I18nDirectory, "vi-VN");
        Assert.Equal("vi", vietnamese.Locale);
    }

    /// <summary>
    /// Arguments for every placeholder any shipped string uses. Kept in one place so adding a new
    /// argument name to a message forces a decision here rather than failing mysteriously.
    /// </summary>
    private static Dictionary<string, string> Placeholders { get; } = new(StringComparer.Ordinal)
    {
        ["value"] = "test",
        ["current"] = "60",
        ["max"] = "165",
        ["rated"] = "5600",
        ["seconds"] = "15",
        ["feature"] = "Hyper-V",
        ["dependency"] = "Virtual Machine Platform",
        ["changes"] = "2",
        ["restarts"] = "1",
        ["minutes"] = "4",
        ["manual"] = "1",
        ["count"] = "2",
        ["reason"] = "test reason",
        ["percent"] = "18",
        ["free"] = "3",
        ["needed"] = "5",
        ["name"] = "Test PC",
        ["edition"] = "Professional",
        ["build"] = "26100",
        ["cpu"] = "Test CPU",
        ["tier"] = "Tier 3",
        ["id"] = "snap-001",
        ["path"] = "C:\\test.db",
        ["how"] = "pco plan x",
        ["title"] = "Test outcome",
        ["number"] = "1",
        ["restart"] = "no restart",
        ["mode"] = "auto",
        ["risk"] = "low",
        ["reversible"] = "undoable",
        ["hash"] = "abcdef",
        ["graph"] = "0.1.0",
        ["rules"] = "0.1.0",
        ["state"] = "Completed",
        ["capability"] = "windows.feature.wsl",
        ["before"] = "disabled",
        ["requested"] = "enabled",
        ["actual"] = "enabled",
        ["completed"] = "1",
        ["failed"] = "0",
        ["pending"] = "0",
        ["requires"] = "5",
        ["checks"] = "1",
        ["capabilities"] = "windows.feature.wsl",
    };
}

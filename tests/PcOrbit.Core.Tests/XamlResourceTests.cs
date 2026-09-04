using System.Text.RegularExpressions;

namespace PcOrbit.Core.Tests;

/// <summary>
/// Every <c>{StaticResource}</c> the window asks for must exist in the application's resources.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <c>RowBg</c> did not. It was referenced from six places and defined in
/// none, and the app still started — because a <c>DataTemplate</c>'s content is parsed the first
/// time the template is applied, not when the window loads. So the failure was invisible until
/// somebody opened Plan &amp; Apply and a plan rendered, which is the screen where the Apply button
/// lives. A crash there is the worst place in this product to have one.
/// </para>
/// <para>
/// A text check rather than a WPF one: loading real XAML needs an STA thread and an
/// <c>Application</c>, and the bug is a name that is not there — which a parser is not needed to
/// see. The cost of that is that it cannot check types, only existence, which is the half that
/// was actually broken.
/// </para>
/// </remarks>
public sealed partial class XamlResourceTests
{
    /// <summary>
    /// The source folder, found by walking up from the test binary.
    /// </summary>
    /// <remarks>
    /// Not derived from <see cref="ShippedData.Directory"/>: the build copies <c>data/</c> next to
    /// the test assembly, so that path points into <c>bin/</c> and its parent is not the repository.
    /// This looks for the file it actually needs instead of assuming a layout.
    /// </remarks>
    private static string AppDirectory
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);

            while (current is not null)
            {
                string candidate = Path.Combine(current.FullName, "src", "PcOrbit.App");

                if (File.Exists(Path.Combine(candidate, "App.xaml")))
                {
                    return candidate;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException(
                $"Could not find src/PcOrbit.App above '{AppContext.BaseDirectory}'.");
        }
    }

    private static IReadOnlySet<string> DefinedKeys()
    {
        string app = File.ReadAllText(Path.Combine(AppDirectory, "App.xaml"));

        return KeyDefinition()
            .Matches(app)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(AppDirectory, "*.xaml", SearchOption.TopDirectoryOnly);

    [Fact]
    public void EveryStaticResourceTheWindowAsksForIsDefined()
    {
        IReadOnlySet<string> defined = DefinedKeys();
        List<string> missing = [];

        foreach (string file in XamlFiles())
        {
            string xaml = File.ReadAllText(file);

            // A file may define its own keys locally; those count as defined for that file.
            HashSet<string> local = [.. defined];

            foreach (Match match in KeyDefinition().Matches(xaml))
            {
                local.Add(match.Groups[1].Value);
            }

            foreach (Match match in StaticResourceUse().Matches(xaml))
            {
                string key = match.Groups[1].Value.Trim();

                if (!local.Contains(key))
                {
                    missing.Add($"{Path.GetFileName(file)} uses '{key}'");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "These StaticResource keys are referenced but never defined. Inside a DataTemplate that "
            + "throws only when the template is first applied, so it will not show up at startup: "
            + string.Join("; ", missing.Distinct()));
    }

    /// <summary>
    /// Two elements in the window share no name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Splitting one page into two is exactly when a name gets duplicated by accident — moving the
    /// security card out of the dashboard produced a second <c>SecurityTitle</c> on the first try.
    /// </para>
    /// <para>
    /// Only the window is checked. Names inside a <c>ControlTemplate</c> are scoped to that
    /// template, so <c>App.xaml</c> legitimately declares <c>Bg</c> and <c>PART_Track</c> many
    /// times over, and flagging those would be wrong rather than strict.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoTwoElementsShareAName()
    {
        foreach (string file in XamlFiles().Where(f =>
            Path.GetFileName(f).StartsWith("MainWindow", StringComparison.Ordinal)))
        {
            List<string> names = [.. ElementName()
                .Matches(File.ReadAllText(file))
                .Select(m => m.Groups[1].Value)];

            List<string> duplicated = [.. names
                .GroupBy(n => n, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)];

            Assert.True(
                duplicated.Count == 0,
                $"{Path.GetFileName(file)} declares these names more than once: {string.Join(", ", duplicated)}");
        }
    }

    /// <summary>
    /// Every style the code looks up by name exists too.
    /// </summary>
    /// <remarks>
    /// The same bug as the one above, one level over, and a worse one: a lookup that misses throws
    /// at the moment the user reaches the control, with no XAML to inspect afterwards. The pivot
    /// strip is built in code precisely so that the tabs cannot disagree with the section table,
    /// which means its style is reached this way and nothing in the XAML mentions it.
    ///
    /// Covers the three one-letter helpers as well as the direct lookups, because they are where
    /// most of these keys are actually written: <c>B</c> for a brush, <c>G</c> for a drawn icon,
    /// <c>GlyphOf</c> for a font character.
    /// </remarks>
    [Fact]
    public void EveryStyleTheCodeLooksUpByNameIsDefined()
    {
        IReadOnlySet<string> defined = DefinedKeys();
        List<string> missing = [];

        foreach (string file in Directory.EnumerateFiles(AppDirectory, "*.cs", SearchOption.TopDirectoryOnly))
        {
            foreach (Match match in CodeResourceUse().Matches(File.ReadAllText(file)))
            {
                string key = match.Groups[1].Value;

                if (!defined.Contains(key))
                {
                    missing.Add($"{Path.GetFileName(file)} looks up '{key}'");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "These resource keys are looked up from code but never defined, which throws when the "
            + "user reaches the control rather than at startup: " + string.Join("; ", missing.Distinct()));
    }

    [GeneratedRegex(@"(?:FindResource\(|Resources\[|\bB\(|\bG\(|\bGlyphOf\()""([^""]+)""")]
    private static partial Regex CodeResourceUse();

    /// <summary>
    /// A range control's Value binding names its mode.
    /// </summary>
    /// <remarks>
    /// <c>ProgressBar.Value</c> and <c>Slider.Value</c> bind TwoWay by default, and a TwoWay binding
    /// to a get-only property throws when the template is applied — which is when the page opens,
    /// not when the app starts. That is how clicking Tune-up came to close the app: the cleanup rows
    /// had grown a class with read-only properties and the bar under each one tried to write back.
    /// Row models here are read-only by design, so every such binding says OneWay.
    /// </remarks>
    [Fact]
    public void EveryRangeValueBindingNamesItsMode()
    {
        List<string> offending = [];

        foreach (string file in XamlFiles())
        {
            foreach (Match match in RangeValueBinding().Matches(File.ReadAllText(file)))
            {
                if (!match.Value.Contains("Mode=", StringComparison.Ordinal))
                {
                    offending.Add($"{Path.GetFileName(file)}: {match.Value}");
                }
            }
        }

        Assert.True(
            offending.Count == 0,
            "These Value bindings default to TwoWay and will throw on a read-only row property the "
            + "moment the page opens: " + string.Join("; ", offending));
    }

    [GeneratedRegex(@"<(?:ProgressBar|Slider)[^>]*?Value=""\{Binding[^}]*\}")]
    private static partial Regex RangeValueBinding();

    /// <summary>
    /// A style's parent is defined above it.
    /// </summary>
    /// <remarks>
    /// <c>BasedOn="{StaticResource X}"</c> is resolved while the dictionary is still being read, so
    /// X has to exist already; a parent defined further down is "Cannot find resource named X" at
    /// startup, before a single window opens. That is how a new block of styles inserted above the
    /// button it inherited from took the whole app down on launch.
    /// </remarks>
    [Fact]
    public void EveryStyleParentIsDefinedBeforeItsChild()
    {
        string app = File.ReadAllText(Path.Combine(AppDirectory, "App.xaml"));
        List<string> forward = [];

        foreach (Match match in BasedOnUse().Matches(app))
        {
            string parent = match.Groups[1].Value;
            int definedAt = app.IndexOf($"x:Key=\"{parent}\"", StringComparison.Ordinal);

            if (definedAt < 0 || definedAt > match.Index)
            {
                forward.Add(parent);
            }
        }

        Assert.True(
            forward.Count == 0,
            "These styles inherit from a parent defined later in the file (or not at all), which fails "
            + "at startup: " + string.Join(", ", forward.Distinct()));
    }

    [GeneratedRegex(@"BasedOn=""\{StaticResource\s+([^}]+)\}""")]
    private static partial Regex BasedOnUse();

    [GeneratedRegex(@"x:Key=""([^""]+)""")]
    private static partial Regex KeyDefinition();

    [GeneratedRegex(@"\{StaticResource\s+([^}]+)\}")]
    private static partial Regex StaticResourceUse();

    [GeneratedRegex(@"x:Name=""([^""]+)""")]
    private static partial Regex ElementName();
}

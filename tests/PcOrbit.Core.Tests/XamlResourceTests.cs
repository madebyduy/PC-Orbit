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

    [GeneratedRegex(@"x:Key=""([^""]+)""")]
    private static partial Regex KeyDefinition();

    [GeneratedRegex(@"\{StaticResource\s+([^}]+)\}")]
    private static partial Regex StaticResourceUse();

    [GeneratedRegex(@"x:Name=""([^""]+)""")]
    private static partial Regex ElementName();
}

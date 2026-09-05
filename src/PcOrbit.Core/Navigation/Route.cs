namespace PcOrbit.Core.Navigation;

/// <summary>Where a finding or a mission leads. Every kind has a destination the app can open.</summary>
public enum RouteKind
{
    /// <summary>An outcome: opens the plan page with it selected. The only kind that ends in Apply.</summary>
    Outcome = 0,

    /// <summary>A page in this app, by <see cref="PageKeys"/>, optionally with a search pre-filled.</summary>
    Page,

    /// <summary>The BIOS page, searched to the setting the guide is about.</summary>
    Guide,

    /// <summary>A Windows Settings page, by <c>ms-settings:</c> URI.</summary>
    Settings,

    /// <summary>A web page — always HTTPS, always a front door the product already knows.</summary>
    Web,
}

/// <summary>
/// The next place to go.
/// </summary>
/// <remarks>
/// <para>
/// The dashboard used to hide its button when a finding had no outcome. That was the right call
/// about automation — a change nobody can verify does not get an Apply button — and the wrong
/// call about the person reading it, who was left with a warning and nowhere to go. A route is the
/// answer to "so what do I do": an outcome when there is one, and otherwise the page, guide,
/// Settings screen or support site that is the honest next step. A finding without a route is a
/// dead end, and a test now refuses one.
/// </para>
/// <para>
/// Missions lead the same way. The route is one type on purpose: whatever the app can open from a
/// finding it can open from a search result, and the code that opens it exists once.
/// </para>
/// </remarks>
/// <param name="Query">For a page or guide, text to put in that page's search box on arrival.</param>
public sealed record Route(RouteKind Kind, string Target, string? Query = null)
{
    public static Route ToOutcome(string outcomeId) => new(RouteKind.Outcome, outcomeId);

    public static Route ToPage(string pageKey, string? query = null) => new(RouteKind.Page, pageKey, query);

    public static Route ToGuide(string settingSearch) => new(RouteKind.Guide, PageKeys.Bios, settingSearch);

    public static Route ToSettings(string uri) => new(RouteKind.Settings, uri);

    public static Route ToWeb(string url) => new(RouteKind.Web, url);

    /// <summary>Whether the target is one the app can actually open.</summary>
    public bool IsWellFormed => Kind switch
    {
        RouteKind.Page or RouteKind.Guide => PageKeys.All.Contains(Target),
        RouteKind.Settings => Target.StartsWith("ms-settings:", StringComparison.Ordinal),
        RouteKind.Web => Uri.TryCreate(Target, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps,
        RouteKind.Outcome => Target.StartsWith("outcome.", StringComparison.Ordinal),
        _ => false,
    };
}

/// <summary>
/// The pages of the app, by a stable key that data files can name.
/// </summary>
/// <remarks>
/// The UI maps these to views; data never names a view. Adding a page means adding a key here, so
/// a mission file that points at a page nobody built fails in <c>doctor</c> rather than in a click.
/// </remarks>
public static class PageKeys
{
    public const string Dashboard = "dashboard";
    public const string Readings = "readings";
    public const string Hardware = "hardware";
    public const string Performance = "performance";
    public const string Bios = "bios";
    public const string Security = "security";
    public const string Recovery = "recovery";
    public const string Cleanup = "cleanup";
    public const string Startup = "startup";
    public const string Drivers = "drivers";
    public const string Apps = "apps";
    public const string Office = "office";
    public const string Windows = "windows";
    public const string Plan = "plan";
    public const string Timeline = "timeline";
    public const string Compare = "compare";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Dashboard, Readings, Hardware, Performance, Bios, Security, Recovery, Cleanup, Startup,
        Drivers, Apps, Office, Windows, Plan, Timeline, Compare,
    };
}

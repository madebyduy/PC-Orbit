using PcOrbit.Core.Model;

namespace PcOrbit.Core.Apps;

/// <param name="Id">
/// The package identifier in Microsoft's repository, e.g. <c>Mozilla.Firefox</c>. This is the
/// allowlist: nothing outside the shipped catalogue can be installed.
/// </param>
/// <param name="Name">
/// The product's own name. Never translated — it is what the user is looking for on screen, and
/// what they will see again in Add or Remove Programs.
/// </param>
/// <param name="DescriptionKey">i18n key. The one line of prose about the app.</param>
/// <param name="CategoryKey">i18n key for the group this sits in.</param>
/// <param name="InstalledAs">
/// Names this product goes by in Add or Remove Programs, when they differ from <paramref name="Name"/>.
/// </param>
public sealed record CatalogApp(
    string Id,
    string Name,
    string Publisher,
    string DescriptionKey,
    string CategoryKey,
    IReadOnlyList<string>? InstalledAs = null)
{
    /// <summary>
    /// Every display name this product might appear under, <see cref="Name"/> included.
    /// </summary>
    public IReadOnlyList<string> DisplayNames => InstalledAs is { Count: > 0 } names ? names : [Name];

    /// <summary>
    /// Whether an installed program's display name is this product.
    /// </summary>
    /// <remarks>
    /// Equal, or followed by a space. The trailing space is what keeps the rule useful rather than
    /// merely permissive: "Google Chrome" has to find "Google Chrome Beta", because someone running
    /// Beta has Chrome and being told otherwise is a lie they can see. But "Git" must not find
    /// "GitHub Desktop", and a bare prefix test would.
    /// </remarks>
    public bool IsCalled(string displayName) =>
        DisplayNames.Any(n =>
            displayName.Equals(n, StringComparison.OrdinalIgnoreCase)
            || displayName.StartsWith(n + " ", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The applications this product will install, and only these.
/// </summary>
/// <remarks>
/// <para>
/// Shipped as data, reviewed like data. Every entry is a package in Microsoft's own winget
/// repository, which hashes what it downloads and refuses a mismatch — so "we installed what you
/// asked for" is something this product can actually stand behind.
/// </para>
/// <para>
/// A curated list rather than a search box, deliberately. A search box over the whole repository
/// would make the allowlist meaningless and put the burden of judging thirty thousand packages on
/// a user who came here precisely because they did not want to judge software. Adding an app is a
/// data change somebody reviews.
/// </para>
/// </remarks>
public sealed class AppCatalog(IEnumerable<CatalogApp> apps)
{
    private readonly IReadOnlyList<CatalogApp> _apps = [.. apps];

    public static AppCatalog Empty { get; } = new([]);

    public IReadOnlyList<CatalogApp> All => _apps;

    /// <summary>The category keys in the order they should be shown.</summary>
    public IReadOnlyList<string> Categories =>
        [.. _apps.Select(a => a.CategoryKey).Distinct(StringComparer.Ordinal)];

    public IReadOnlyList<CatalogApp> InCategory(string categoryKey) =>
        [.. _apps.Where(a => string.Equals(a.CategoryKey, categoryKey, StringComparison.Ordinal))];

    /// <summary>Null when the id is not one this product ships. That is the allowlist check.</summary>
    public CatalogApp? Find(string id) =>
        _apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <param name="Installed">
/// Null when winget could not be asked. Not the same as "no", and the difference decides whether
/// the button says Install or says nothing at all.
/// </param>
public sealed record AppState(string Id, bool? Installed, Evidence Evidence);

/// <param name="Verified">
/// True only when the machine was asked again afterwards and agreed. An installer's exit code is
/// not evidence that anything arrived (spec 6.4).
/// </param>
public sealed record AppChangeResult(
    string Id,
    bool Install,
    bool Applied,
    bool Verified,
    string? Problem = null);

/// <param name="Problem">Non-null when the installed set could not be read, with the reason.</param>
/// <param name="FoundAs">
/// For each id, the name the machine actually calls it.
/// </param>
/// <remarks>
/// <para>
/// Two sources, because one was not enough. winget knows what it installed and what it can
/// correlate to its repository, and it correlates by exact package id — so a machine running
/// Chrome Beta answers "no" to <c>Google.Chrome</c>, which is technically true and useless to the
/// person looking at a row labelled Google Chrome and knowing perfectly well that they have it.
/// </para>
/// <para>
/// So Add or Remove Programs is read as well. It is the list Windows itself shows, and a product
/// that put an entry there is installed whether or not any package manager claims it.
/// <see cref="FoundAs"/> carries the name that proved it, so the row can say which.
/// </para>
/// </remarks>
public sealed record InstalledApps(
    IReadOnlySet<string> Ids,
    string? Problem = null,
    IReadOnlyDictionary<string, string>? FoundAs = null)
{
    public static InstalledApps Unknown(string problem) =>
        new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), problem);

    public bool IsKnown => Problem is null;

    /// <summary>The name the machine calls this, when it differs from the catalogue's own.</summary>
    public string? NameOnThisMachine(string id) =>
        FoundAs is not null && FoundAs.TryGetValue(id, out string? name) ? name : null;
}

/// <summary>
/// Installs and removes applications from the shipped catalogue.
/// </summary>
/// <remarks>
/// Every method verifies by asking the machine again rather than by trusting the installer, and
/// every id is checked against <see cref="AppCatalog"/> before a command is built. Uninstall is the
/// inverse of install, so the reverse path cannot drift from the forward one.
/// </remarks>
public interface IAppService
{
    /// <summary>False when winget is not on this machine, which is a thing to say rather than fail on.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    Task<InstalledApps> InstalledAsync(CancellationToken cancellationToken = default);

    Task<AppChangeResult> InstallAsync(string id, CancellationToken cancellationToken = default);

    Task<AppChangeResult> UninstallAsync(string id, CancellationToken cancellationToken = default);
}

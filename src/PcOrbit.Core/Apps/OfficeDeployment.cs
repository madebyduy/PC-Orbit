using PcOrbit.Core.Model;

namespace PcOrbit.Core.Apps;

/// <summary>Which Office product to lay down. These are Microsoft's own product ids.</summary>
public enum OfficeProduct
{
    /// <summary>Microsoft 365 Apps for business or personal, subscription.</summary>
    Microsoft365Apps = 0,

    /// <summary>Office 2024 Home and Business, one-off purchase.</summary>
    HomeBusiness2024,

    /// <summary>Office 2024 Professional Plus, volume.</summary>
    ProPlus2024,
}

/// <param name="Product">What to install.</param>
/// <param name="Language">A culture name Office understands, e.g. <c>vi-vn</c> or <c>en-us</c>.</param>
/// <param name="ExcludedApps">
/// Apps to leave out. The point of the deployment tool over a plain installer: a machine that will
/// never run Access or Publisher does not have to carry them.
/// </param>
/// <param name="SixtyFourBit">32-bit Office exists for old add-ins; the default is 64.</param>
public sealed record OfficePlan(
    OfficeProduct Product,
    string Language,
    IReadOnlyList<string> ExcludedApps,
    bool SixtyFourBit = true)
{
    /// <summary>Apps the deployment tool can be told to skip, by its own names.</summary>
    public static IReadOnlyList<string> ExcludableApps { get; } =
        ["Access", "Publisher", "OneNote", "Outlook", "Teams", "Lync", "Groove", "Bing"];

    public string ProductId => Product switch
    {
        OfficeProduct.HomeBusiness2024 => "HomeBusiness2024Retail",
        OfficeProduct.ProPlus2024 => "ProPlus2024Volume",
        _ => "O365BusinessRetail",
    };
}

/// <param name="Installed">Null when it could not be determined.</param>
/// <param name="Product">What is there now, in Microsoft's own words.</param>
public sealed record OfficeState(bool? Installed, string? Product, string? Version, Evidence Evidence);

/// <param name="Stage">Where the run got to, so a failure says which half failed.</param>
public sealed record OfficeInstallResult(
    string Stage,
    bool Completed,
    string? Problem = null);

/// <summary>
/// Installs Office through Microsoft's Office Deployment Tool.
/// </summary>
/// <remarks>
/// <para>
/// The supported path, and the one that produces a licensed installation rather than a working one:
/// the deployment tool downloads from Microsoft's own content delivery network and hands the result
/// to the normal activation flow. This product supplies no keys and bypasses no licensing — an
/// Office that will not activate is a problem between the user and Microsoft, and pretending
/// otherwise is what separates a deployment tool from something else.
/// </para>
/// <para>
/// Two phases, reported separately, because they fail for different reasons and the difference
/// matters to whoever is fixing it. Download pulls several hundred megabytes and fails on the
/// network; configure runs the installer and fails on disk, on rights, or on an existing
/// installation in the way.
/// </para>
/// </remarks>
public interface IOfficeService
{
    /// <summary>What Office, if any, is on this machine now.</summary>
    Task<OfficeState> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The XML the deployment tool will be given, for the user to read before anything runs.
    /// </summary>
    /// <remarks>
    /// Shown rather than hidden. It is the whole instruction — product, language, architecture,
    /// what is excluded — in a form that can be checked, and spec 21.7 wants the cost of a change
    /// visible before Apply rather than described afterwards.
    /// </remarks>
    string DescribeConfiguration(OfficePlan plan);

    /// <param name="progress">Stage names as the run moves through them.</param>
    Task<OfficeInstallResult> InstallAsync(
        OfficePlan plan,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

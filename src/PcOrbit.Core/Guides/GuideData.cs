using PcOrbit.Core.Actions;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Guides;

public enum GuideTier
{
    /// <summary>Vendor-level instructions. Honest about the fact that the menu may differ.</summary>
    GuidedGeneric = 0,

    /// <summary>Confirmed on a real machine of this model. Never claimed from a manual alone.</summary>
    GuidedVerified,
}

public sealed record GuideVerification(string Model, string BiosVersion, DateOnly Date);

public sealed record GuideMatch(
    IReadOnlyList<string>? Vendors = null,
    IReadOnlyList<string>? Models = null,
    IReadOnlyList<string>? BiosFamilies = null,
    IReadOnlyList<CpuVendor>? CpuVendors = null)
{
    public bool Matches(MachineIdentity machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        if (Vendors is { Count: > 0 } vendors
            && !VendorMatcher.MatchesAny(machine.SystemVendor, vendors)
            && !VendorMatcher.MatchesAny(machine.BaseBoardVendor, vendors))
        {
            return false;
        }

        if (Models is { Count: > 0 } models
            && !VendorMatcher.ModelMatchesAny(machine.SystemModel, models)
            && !VendorMatcher.ModelMatchesAny(machine.BaseBoardProduct, models))
        {
            return false;
        }

        if (BiosFamilies is { Count: > 0 } families
            && !families.Any(f => machine.BiosVersion.Contains(f, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (CpuVendors is { Count: > 0 } cpus && !cpus.Contains(machine.CpuVendor))
        {
            return false;
        }

        return true;
    }

    /// <summary>How specific this match is. Higher wins, so a per-model entry beats a per-vendor one.</summary>
    public int Specificity =>
        (Models is { Count: > 0 } ? 4 : 0)
        + (BiosFamilies is { Count: > 0 } ? 2 : 0)
        + (CpuVendors is { Count: > 0 } ? 1 : 0)
        + (Vendors is { Count: > 0 } ? 1 : 0);
}

/// <param name="SettingName">
/// The exact English label as printed in that firmware. Never translated — the user has to find
/// it on a screen we do not control (spec 21.11).
/// </param>
/// <param name="MenuPath">Where to click, in order.</param>
/// <param name="EnterKeys">Keys that open firmware setup on this machine.</param>
/// <param name="SaveKeys">How to save. Not saving is a top cause of "I did it but it did not work".</param>
public sealed record GuideEntry(
    GuideTier Tier,
    string SettingName,
    IReadOnlyList<string> MenuPath,
    GuideMatch? Match = null,
    IReadOnlyList<string>? AlternateNames = null,
    IReadOnlyList<string>? EnterKeys = null,
    IReadOnlyList<string>? SaveKeys = null,
    string? SearchKey = null,
    string? NoteKey = null,
    string? ImageRef = null,
    IReadOnlyList<GuideVerification>? VerifiedOn = null);

/// <summary>
/// Instructions for the one step the user performs themselves (spec 8.3.3).
/// </summary>
/// <remarks>
/// Guided is not an error screen and not a fallback: for anyone with a self-built desktop it is
/// the normal path, so it gets designed and versioned like a feature. Shipped as data so guide
/// coverage can grow — and be reviewed — without shipping a new app build.
/// </remarks>
public sealed record GuideData(
    string Id,
    string Version,
    CapabilityId Capability,
    CapabilityValue TargetState,
    IReadOnlyList<GuideEntry> Entries,
    GuideEntry Fallback)
{
    /// <summary>
    /// The best instructions we have for this machine. Always returns something: the vendor-level
    /// or generic entry, clearly labelled as such, beats telling the user nothing.
    /// </summary>
    public GuideEntry SelectFor(MachineIdentity machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        return Entries
            .Where(e => e.Match?.Matches(machine) ?? false)
            .OrderByDescending(e => e.Match!.Specificity)
            .ThenByDescending(e => e.Tier)
            .FirstOrDefault()
            ?? Fallback;
    }
}

/// <summary>
/// Which tier this machine sits in, and therefore what the app is allowed to promise (spec 18.4).
/// </summary>
public static class SupportTierResolver
{
    /// <summary>
    /// Resolves the tier from evidence we have, not from marketing: a vendor write adapter that
    /// actually matches this machine, or guide data actually verified on this model.
    /// </summary>
    public static SupportTier Resolve(MachineIdentity machine, ActionCatalog catalog, GuideData? firmwareGuide)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(catalog);

        // A virtual machine has no firmware setup the user can meaningfully reach, and unknown
        // hardware gets inventory only (spec 18.4 Tier 0).
        if (machine.IsVirtualMachine || machine.CpuVendor == CpuVendor.Unknown)
        {
            return SupportTier.ReadOnly;
        }

        bool hasVendorWrite = catalog.All.Any(a =>
            a.WriteMode == WriteMode.Auto
            && a.Provides.Any(p => p.Capability.Value.StartsWith("firmware.", StringComparison.Ordinal))
            && a.Compatibility.Vendors is { Count: > 0 }
            && a.Compatibility.Matches(machine));

        if (hasVendorWrite)
        {
            return SupportTier.Full;
        }

        if (firmwareGuide is null)
        {
            return SupportTier.ReadOnly;
        }

        GuideEntry entry = firmwareGuide.SelectFor(machine);

        return entry.Tier == GuideTier.GuidedVerified && entry.VerifiedOn is { Count: > 0 }
            ? SupportTier.GuidedVerified
            : SupportTier.GuidedGeneric;
    }

    public static string DisplayKey(SupportTier tier) => tier switch
    {
        SupportTier.Full => "tier.full",
        SupportTier.GuidedVerified => "tier.guidedVerified",
        SupportTier.GuidedGeneric => "tier.guidedGeneric",
        _ => "tier.readOnly",
    };
}

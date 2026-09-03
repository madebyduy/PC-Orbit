using System.Text;

namespace PcOrbit.Core.Model;

/// <summary>
/// Vendor strings from SMBIOS are a mess: "ASUSTeK COMPUTER INC.", "ASUS", "Asustek Computer Inc",
/// "Hewlett-Packard", "HP Inc.". One matcher, used by graph scopes, action compatibility and
/// guide-data selection, so a machine cannot be Tier 3 in one place and Tier 2 in another.
/// </summary>
public static class VendorMatcher
{
    /// <summary>True when <paramref name="actual"/> is the same vendor as <paramref name="candidate"/>.</summary>
    public static bool Matches(string? actual, string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        string a = Normalize(actual);
        string b = Normalize(candidate);

        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        // "asustekcomputerinc" vs "asus": one is a prefix of the other after normalising.
        return a.StartsWith(b, StringComparison.Ordinal)
            || b.StartsWith(a, StringComparison.Ordinal);
    }

    public static bool MatchesAny(string? actual, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.Any(c => Matches(actual, c));
    }

    /// <summary>Loose model match: a listed "Latitude 5440" matches "Latitude 5440 Rugged".</summary>
    public static bool ModelMatchesAny(string? actualModel, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (string.IsNullOrWhiteSpace(actualModel))
        {
            return false;
        }

        string a = Normalize(actualModel);
        return candidates
            .Select(Normalize)
            .Any(b => b.Length > 0 && a.Contains(b, StringComparison.Ordinal));
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        // Drop the corporate noise so "asustekcomputerinc" and "asustek" agree.
        string normalized = builder.ToString();

        foreach (string suffix in Suffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.Ordinal))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }

        return normalized;
    }

    private static readonly string[] Suffixes =
    [
        "corporation", "corporated", "incorporated", "company", "computerinc",
        "coltd", "gmbh", "llc", "ltd", "inc", "co",
    ];
}

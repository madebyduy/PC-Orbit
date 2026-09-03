using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PcOrbit.Core.Serialization;

namespace PcOrbit.Core.Localization;

/// <summary>
/// Every user-visible string, keyed by a stable id.
/// </summary>
/// <remarks>
/// Spec 21.11: no display string is hard-coded in logic. The engine and the checkup rules hand
/// the UI keys plus arguments, which is also why a support report can be produced in the helper's
/// language while the user keeps theirs (spec 19.3).
/// </remarks>
public interface IStringCatalog
{
    string Locale { get; }

    bool Contains(string key);

    /// <summary>
    /// The formatted string, or a visibly broken marker for a missing key. Never throws: a missing
    /// translation must not be able to crash the app the user opened because their PC is broken.
    /// </summary>
    string Format(string key, IReadOnlyDictionary<string, string>? arguments = null);
}

public sealed class JsonStringCatalog : IStringCatalog
{
    private readonly ImmutableDictionary<string, string> _strings;
    private readonly IStringCatalog? _fallback;
    private readonly CultureInfo _culture;

    public JsonStringCatalog(
        string locale,
        IReadOnlyDictionary<string, string> strings,
        IStringCatalog? fallback = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentNullException.ThrowIfNull(strings);

        Locale = locale;
        _strings = strings.ToImmutableDictionary(StringComparer.Ordinal);
        _fallback = fallback;
        _culture = CultureInfo.GetCultureInfo(locale);
    }

    public string Locale { get; }

    public IReadOnlyCollection<string> Keys => _strings.Keys.ToImmutableArray();

    public bool Contains(string key) =>
        _strings.ContainsKey(key) || (_fallback?.Contains(key) ?? false);

    public string Format(string key, IReadOnlyDictionary<string, string>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_strings.TryGetValue(key, out string? pattern))
        {
            return IcuMessageFormatter.Format(pattern, arguments, _culture);
        }

        if (_fallback is not null && _fallback.Contains(key))
        {
            return _fallback.Format(key, arguments);
        }

        // Loud but harmless. A CI test asserts this never happens for a shipped locale.
        return $"[[{key}]]";
    }

    /// <summary>Loads <c>data/i18n/{locale}.json</c>, falling back to another catalog per key.</summary>
    public static JsonStringCatalog LoadFile(string path, IStringCatalog? fallback = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        CatalogFile file;

        try
        {
            file = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(path), JsonDefaults.DataFiles)
                ?? throw new DataFileException(path, "file is empty.");
        }
        catch (JsonException ex)
        {
            throw new DataFileException(path, $"not valid JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(file.Locale))
        {
            throw new DataFileException(path, "locale is required.");
        }

        return new JsonStringCatalog(file.Locale, file.Strings ?? [], fallback);
    }

    /// <summary>
    /// Loads the requested locale with English underneath it.
    /// </summary>
    /// <remarks>
    /// Spec 21.11 ships English and Vietnamese together in v0.1. English is the fallback because a
    /// missing Vietnamese string should show the English sentence, not a broken key — the user can
    /// still act on it.
    /// </remarks>
    public static IStringCatalog LoadForLocale(string directory, string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);

        string englishPath = Path.Combine(directory, "en.json");
        JsonStringCatalog english = LoadFile(englishPath);

        string language = locale.Split('-')[0].ToLowerInvariant();

        if (language == "en")
        {
            return english;
        }

        string localePath = Path.Combine(directory, $"{language}.json");

        return File.Exists(localePath) ? LoadFile(localePath, english) : english;
    }

    private sealed record CatalogFile(string? Locale, Dictionary<string, string>? Strings);
}

using System.Text.Json;
using PcOrbit.Core.Serialization;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// Reads the inventory JSON one section at a time.
/// </summary>
/// <remarks>
/// <para>
/// Deserialising the whole document into one object is the obvious approach and the wrong one: a
/// single unexpected value anywhere loses every reading. That is not hypothetical — Windows
/// PowerShell renders <c>DateTime</c> as <c>/Date(...)/</c>, so one date field in
/// <c>Win32_BIOS</c> was enough to turn an entire scan into Unknowns.
/// </para>
/// <para>
/// So each section is parsed independently and failures are recorded per section. A machine where
/// TPM cannot be read still reports its memory speed, and the reading that failed carries the
/// reason it failed rather than a reason borrowed from elsewhere (spec 8.3.1, 21.3).
/// </para>
/// </remarks>
internal sealed class InventoryDocument : IDisposable
{
    private readonly JsonDocument? _document;
    private readonly Dictionary<string, string> _sectionProblems = new(StringComparer.Ordinal);

    private InventoryDocument(JsonDocument? document, string? globalProblem)
    {
        _document = document;
        GlobalProblem = globalProblem;
    }

    /// <summary>Non-null when the whole document was unusable. Then every section is Unknown.</summary>
    internal string? GlobalProblem { get; }

    internal static InventoryDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new InventoryDocument(null, "the inventory script produced no output");
        }

        try
        {
            return new InventoryDocument(JsonDocument.Parse(json), null);
        }
        catch (JsonException ex)
        {
            return new InventoryDocument(null, $"the inventory output was not valid JSON: {ex.Message}");
        }
    }

    /// <summary>The section as <typeparamref name="T"/>, or null with the reason recorded.</summary>
    internal T? Section<T>(string name)
        where T : class
    {
        if (!TryGetProperty(name, out JsonElement element))
        {
            return null;
        }

        try
        {
            return element.Deserialize<T>(JsonDefaults.DataFiles);
        }
        catch (JsonException ex)
        {
            _sectionProblems[name] = $"'{name}' could not be read: {ex.Message}";
            return null;
        }
    }

    internal List<T> List<T>(string name)
    {
        if (!TryGetProperty(name, out JsonElement element))
        {
            return [];
        }

        try
        {
            // PowerShell collapses a single-element array to the element itself, so accept both.
            return element.ValueKind == JsonValueKind.Array
                ? element.Deserialize<List<T>>(JsonDefaults.DataFiles) ?? []
                : [element.Deserialize<T>(JsonDefaults.DataFiles)!];
        }
        catch (JsonException ex)
        {
            _sectionProblems[name] = $"'{name}' could not be read: {ex.Message}";
            return [];
        }
    }

    internal bool? Bool(string name) =>
        TryGetProperty(name, out JsonElement element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;

    internal int? Int(string name) =>
        TryGetProperty(name, out JsonElement element) && element.TryGetInt32(out int value)
            ? value
            : null;

    internal string? String(string name) =>
        TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>
    /// Why a section is missing: the section-specific reason, or the document-wide one.
    /// </summary>
    internal string? ProblemFor(string name) =>
        _sectionProblems.TryGetValue(name, out string? problem)
            ? problem
            : GlobalProblem ?? (_document is not null && !TryGetProperty(name, out _)
                ? $"'{name}' was not present in the inventory output"
                : null);

    public void Dispose() => _document?.Dispose();

    private bool TryGetProperty(string name, out JsonElement element)
    {
        element = default;

        if (_document is null)
        {
            return false;
        }

        return _document.RootElement.TryGetProperty(name, out element)
            && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }
}

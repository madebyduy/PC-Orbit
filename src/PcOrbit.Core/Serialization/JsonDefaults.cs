using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcOrbit.Core.Serialization;

/// <summary>
/// One set of JSON rules for every data file we ship and every record we persist.
/// </summary>
public static class JsonDefaults
{
    /// <summary>For reading shipped data files (graph, outcomes, manifests, guides, strings).</summary>
    public static JsonSerializerOptions DataFiles { get; } = Build(indented: false);

    /// <summary>For writing anything a human may open: snapshots, plans, support reports.</summary>
    public static JsonSerializerOptions Readable { get; } = Build(indented: true);

    private static JsonSerializerOptions Build(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new CapabilityIdJsonConverter());
        options.Converters.Add(new CapabilityValueJsonConverter());
        // populateMissingResolver: true installs the reflection-based resolver. These options are
        // shared and frozen so no caller can mutate them halfway through a run.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>Thrown when a shipped data file is malformed. Never swallowed: bad data must be loud.</summary>
public sealed class DataFileException(string path, string problem)
    : Exception($"{path}: {problem}")
{
    public string Path { get; } = path;
}

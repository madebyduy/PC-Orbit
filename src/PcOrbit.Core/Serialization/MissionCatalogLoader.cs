using System.Text.Json;
using System.Text.Json.Serialization;
using PcOrbit.Core.Missions;
using PcOrbit.Core.Navigation;

namespace PcOrbit.Core.Serialization;

/// <summary>
/// Reads <c>*.missions.json</c> and refuses anything that would leave a person at a dead end.
/// </summary>
/// <remarks>
/// The loader is where a mission that points nowhere is caught: a page key nobody built, an outcome
/// id nothing ships, a route with no target. Missions are shipped data and reviewed like data, and
/// this is the review a machine can do. <c>doctor</c> runs it against the outcomes actually loaded,
/// so a route to an outcome that was renamed fails in CI rather than in a click.
/// </remarks>
public static class MissionCatalogLoader
{
    public static MissionCatalog LoadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            return MissionCatalog.Empty;
        }

        List<Mission> missions = [];

        foreach (string file in Directory
            .EnumerateFiles(directory, "*.json")
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            missions.AddRange(Load(File.ReadAllText(file), file));
        }

        List<string> duplicates =
        [
            .. missions.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key),
        ];

        return duplicates.Count > 0
            ? throw new DataFileException(directory, $"mission id declared twice: {string.Join(", ", duplicates)}.")
            : new MissionCatalog(missions);
    }

    public static IReadOnlyList<Mission> Load(string json, string path = "(inline)")
    {
        MissionFile file;

        try
        {
            file = JsonSerializer.Deserialize<MissionFile>(json, JsonDefaults.DataFiles)
                ?? throw new DataFileException(path, "file is empty.");
        }
        catch (JsonException ex)
        {
            throw new DataFileException(path, $"not valid JSON: {ex.Message}");
        }

        List<Mission> missions = [];

        foreach (MissionDto dto in file.Missions ?? [])
        {
            if (string.IsNullOrWhiteSpace(dto.Id) || !dto.Id.StartsWith("mission.", StringComparison.Ordinal))
            {
                throw new DataFileException(path, "every mission needs an id starting with 'mission.'.");
            }

            if (string.IsNullOrWhiteSpace(dto.TitleKey) || string.IsNullOrWhiteSpace(dto.DescriptionKey) || string.IsNullOrWhiteSpace(dto.RecoveryKey))
            {
                throw new DataFileException(path, $"mission '{dto.Id}' needs titleKey, descriptionKey and recoveryKey.");
            }

            if (dto.Aliases is not { Count: > 0 })
            {
                throw new DataFileException(path, $"mission '{dto.Id}' has no aliases, so nothing typed could find it.");
            }

            if (dto.Route is null || string.IsNullOrWhiteSpace(dto.Route.Target)
                || !Enum.TryParse(dto.Route.Kind, ignoreCase: true, out RouteKind kind))
            {
                throw new DataFileException(path, $"mission '{dto.Id}' needs a route with a known kind and a target.");
            }

            var route = new Route(kind, dto.Route.Target, dto.Route.Query);

            if (!route.IsWellFormed)
            {
                throw new DataFileException(path, $"mission '{dto.Id}' routes to '{dto.Route.Kind}:{dto.Route.Target}', which the app cannot open.");
            }

            if (!Enum.TryParse(dto.Restart ?? "none", ignoreCase: true, out MissionRestart restart))
            {
                throw new DataFileException(path, $"mission '{dto.Id}' has an unknown restart value '{dto.Restart}'.");
            }

            missions.Add(new Mission(
                dto.Id,
                dto.TitleKey,
                dto.DescriptionKey,
                dto.Aliases,
                route,
                dto.Inspects ?? [],
                Math.Max(0, dto.EstimatedMinutes),
                restart,
                dto.RecoveryKey,
                string.IsNullOrWhiteSpace(dto.Glyph) ? "GlyphSpark" : dto.Glyph));
        }

        return missions;
    }

    /// <summary>
    /// The checks that need the rest of the shipped data: does every outcome route point at an
    /// outcome that exists?
    /// </summary>
    public static IReadOnlyList<string> Validate(MissionCatalog catalog, IReadOnlySet<string> outcomeIds)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(outcomeIds);

        List<string> problems = [];

        foreach (Mission mission in catalog.All)
        {
            if (mission.Route.Kind == RouteKind.Outcome && !outcomeIds.Contains(mission.Route.Target))
            {
                problems.Add($"mission '{mission.Id}' routes to outcome '{mission.Route.Target}', which is not shipped.");
            }
        }

        return problems;
    }

    private sealed record MissionFile(
        [property: JsonPropertyName("$schema")] string? Schema,
        string? Version,
        List<MissionDto>? Missions);

    private sealed record RouteDto(string? Kind, string? Target, string? Query);

    private sealed record MissionDto(
        string? Id,
        string? TitleKey,
        string? DescriptionKey,
        List<string>? Aliases,
        RouteDto? Route,
        List<string>? Inspects,
        int EstimatedMinutes,
        string? Restart,
        string? RecoveryKey,
        string? Glyph);
}

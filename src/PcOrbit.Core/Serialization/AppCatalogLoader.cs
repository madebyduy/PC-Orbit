using System.Text.Json;
using PcOrbit.Core.Apps;

namespace PcOrbit.Core.Serialization;

/// <summary>
/// Loads the shipped application catalogue.
/// </summary>
/// <remarks>
/// Strict, like every other loader here: a malformed entry is a packaging error and guessing around
/// it would produce a catalogue with a hole in it. A duplicate id is refused outright, because two
/// entries for one package means the installed-state lookup silently picks one.
/// </remarks>
public static class AppCatalogLoader
{
    public static AppCatalog LoadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            return AppCatalog.Empty;
        }

        List<CatalogApp> apps = [];

        foreach (string file in Directory
            .EnumerateFiles(directory, "*.json")
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            apps.AddRange(Load(File.ReadAllText(file), file));
        }

        List<string> duplicates =
        [
            .. apps.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key),
        ];

        return duplicates.Count > 0
            ? throw new DataFileException(directory, $"application id declared twice: {string.Join(", ", duplicates)}.")
            : new AppCatalog(apps);
    }

    public static IReadOnlyList<CatalogApp> Load(string json, string path = "(inline)")
    {
        CatalogFile file;

        try
        {
            file = JsonSerializer.Deserialize<CatalogFile>(json, JsonDefaults.DataFiles)
                ?? throw new DataFileException(path, "file is empty.");
        }
        catch (JsonException ex)
        {
            throw new DataFileException(path, $"not valid JSON: {ex.Message}");
        }

        List<CatalogApp> apps = [];

        foreach (AppDto dto in file.Apps ?? [])
        {
            if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Name))
            {
                throw new DataFileException(path, "every application needs an id and a name.");
            }

            if (string.IsNullOrWhiteSpace(dto.DescriptionKey) || string.IsNullOrWhiteSpace(dto.CategoryKey))
            {
                throw new DataFileException(
                    path,
                    $"application '{dto.Id}' has no descriptionKey or categoryKey. Both are string-catalog "
                    + "keys, because the prose around an app is translated even though its name is not "
                    + "(spec 21.11).");
            }

            apps.Add(new CatalogApp(
                dto.Id,
                dto.Name,
                dto.Publisher ?? "Unknown",
                dto.DescriptionKey,
                dto.CategoryKey));
        }

        return apps;
    }

    private sealed record CatalogFile(string? Version, List<AppDto>? Apps);

    private sealed record AppDto(
        string? Id,
        string? Name,
        string? Publisher,
        string? CategoryKey,
        string? DescriptionKey);
}

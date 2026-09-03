using System.Text.Json;
using PcOrbit.Core.Model;
using PcOrbit.Core.Outcomes;

namespace PcOrbit.Core.Serialization;

public static class OutcomeLoader
{
    public static IReadOnlyList<Outcome> LoadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return
        [
            .. Directory
                .EnumerateFiles(directory, "*.outcome.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(LoadFile),
        ];
    }

    public static Outcome LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Load(File.ReadAllText(path), path);
    }

    public static Outcome Load(string json, string path = "(inline)")
    {
        OutcomeDto dto;

        try
        {
            dto = JsonSerializer.Deserialize<OutcomeDto>(json, JsonDefaults.DataFiles)
                ?? throw new DataFileException(path, "file is empty.");
        }
        catch (JsonException ex)
        {
            throw new DataFileException(path, $"not valid JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(dto.Id))
        {
            throw new DataFileException(path, "id is required.");
        }

        if (string.IsNullOrWhiteSpace(dto.Version))
        {
            throw new DataFileException(path, "version is required — plan provenance records it (spec 9.5).");
        }

        if (dto.Requires is not { Count: > 0 })
        {
            throw new DataFileException(path, "an outcome with no requirements cannot be compiled.");
        }

        if (dto.Verify is not { Count: > 0 })
        {
            throw new DataFileException(
                path,
                "an outcome must declare how to verify it. Without verification we would be reporting success we never checked (spec 6.4).");
        }

        List<CapabilityRequirement> requires =
        [
            .. dto.Requires.Select(r => new CapabilityRequirement(
                CapabilityGraphLoader.ParseId(r.Capability, path, "requires.capability"),
                CapabilityValue.Parse(r.State ?? throw new DataFileException(path, $"requires '{r.Capability}' has no state.")),
                r.Optional)),
        ];

        List<VerificationCheck> verify =
        [
            .. dto.Verify.Select(v => new VerificationCheck(
                CapabilityGraphLoader.ParseId(v.Check, path, "verify.check"),
                CapabilityValue.Parse(v.Expected ?? throw new DataFileException(path, $"verify '{v.Check}' has no expected value.")))),
        ];

        return new Outcome(
            Id: dto.Id,
            Version: dto.Version,
            TitleKey: dto.TitleKey ?? $"{dto.Id}.title",
            DescriptionKey: dto.DescriptionKey ?? $"{dto.Id}.description",
            Category: dto.Category,
            Requires: requires,
            Verify: verify);
    }

    private sealed record OutcomeDto(
        string? Id,
        string? Version,
        string? TitleKey,
        string? DescriptionKey,
        OutcomeCategory Category,
        List<RequirementDto>? Requires,
        List<VerifyDto>? Verify);

    private sealed record RequirementDto(string? Capability, string? State, bool Optional = false);

    private sealed record VerifyDto(string? Check, string? Expected);
}

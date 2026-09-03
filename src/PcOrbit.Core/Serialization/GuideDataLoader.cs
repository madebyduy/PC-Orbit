using System.Text.Json;
using PcOrbit.Core.Guides;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Serialization;

public static class GuideDataLoader
{
    public static IReadOnlyDictionary<string, GuideData> LoadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Dictionary<string, GuideData> guides = new(StringComparer.Ordinal);

        foreach (string file in Directory
            .EnumerateFiles(directory, "*.guide.json")
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            GuideData guide = LoadFile(file);

            if (!guides.TryAdd(guide.Id, guide))
            {
                throw new DataFileException(file, $"guide id '{guide.Id}' is declared twice.");
            }
        }

        return guides;
    }

    public static GuideData LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Load(File.ReadAllText(path), path);
    }

    public static GuideData Load(string json, string path = "(inline)")
    {
        GuideFile file;

        try
        {
            file = JsonSerializer.Deserialize<GuideFile>(json, JsonDefaults.DataFiles)
                ?? throw new DataFileException(path, "file is empty.");
        }
        catch (JsonException ex)
        {
            throw new DataFileException(path, $"not valid JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(file.Id) || string.IsNullOrWhiteSpace(file.Version))
        {
            throw new DataFileException(path, "id and version are required.");
        }

        if (file.Fallback is null)
        {
            throw new DataFileException(
                path,
                "a fallback entry is required: an unrecognised model must still get instructions, "
                + "clearly labelled as generic (spec 8.3.3).");
        }

        return new GuideData(
            Id: file.Id,
            Version: file.Version,
            Capability: CapabilityGraphLoader.ParseId(file.Capability, path, "guide capability"),
            TargetState: CapabilityValue.Parse(file.TargetState ?? "enabled"),
            Entries: [.. (file.Entries ?? []).Select(e => ToEntry(e, path, requireMatch: true))],
            Fallback: ToEntry(file.Fallback, path, requireMatch: false));
    }

    private static GuideEntry ToEntry(EntryDto dto, string path, bool requireMatch)
    {
        if (string.IsNullOrWhiteSpace(dto.SettingName))
        {
            throw new DataFileException(path, "every guide entry needs the exact setting name shown in firmware.");
        }

        if (dto.MenuPath is not { Count: > 0 })
        {
            throw new DataFileException(path, $"guide entry '{dto.SettingName}' has no menuPath.");
        }

        GuideTier tier = dto.Tier ?? GuideTier.GuidedGeneric;

        // Spec 18.4: a tier is earned with evidence from a real machine. Enforced at load so a
        // well-meaning data contribution cannot upgrade a promise the app cannot keep.
        if (tier == GuideTier.GuidedVerified && dto.VerifiedOn is not { Count: > 0 })
        {
            throw new DataFileException(
                path,
                $"guide entry '{dto.SettingName}' claims guidedVerified but lists no machine it was "
                + "verified on. Tier 2 needs evidence from real hardware, not a vendor manual.");
        }

        if (requireMatch && dto.Match is null)
        {
            throw new DataFileException(
                path,
                $"guide entry '{dto.SettingName}' has no match block, so it would apply to every machine. "
                + "Use the fallback entry for that.");
        }

        return new GuideEntry(
            Tier: tier,
            SettingName: dto.SettingName,
            MenuPath: dto.MenuPath,
            Match: dto.Match is null
                ? null
                : new GuideMatch(dto.Match.Vendors, dto.Match.Models, dto.Match.BiosFamilies, dto.Match.CpuVendors),
            AlternateNames: dto.AlternateNames,
            EnterKeys: dto.EnterKeys,
            SaveKeys: dto.SaveKeys,
            SearchKey: dto.SearchKey,
            NoteKey: dto.NoteKey,
            ImageRef: dto.ImageRef,
            VerifiedOn:
            [
                .. (dto.VerifiedOn ?? []).Select(v => new GuideVerification(
                    v.Model ?? "unknown",
                    v.BiosVersion ?? "unknown",
                    DateOnly.TryParse(v.Date, out DateOnly date) ? date : default)),
            ]);
    }

    private sealed record GuideFile(
        string? Id,
        string? Version,
        string? Capability,
        string? TargetState,
        List<EntryDto>? Entries,
        EntryDto? Fallback);

    private sealed record EntryDto(
        MatchDto? Match,
        GuideTier? Tier,
        string? SettingName,
        List<string>? AlternateNames,
        List<string>? MenuPath,
        List<string>? EnterKeys,
        List<string>? SaveKeys,
        string? SearchKey,
        string? NoteKey,
        string? ImageRef,
        List<VerifiedOnDto>? VerifiedOn);

    private sealed record MatchDto(
        List<string>? Vendors,
        List<string>? Models,
        List<string>? BiosFamilies,
        List<CpuVendor>? CpuVendors);

    private sealed record VerifiedOnDto(string? Model, string? BiosVersion, string? Date);
}

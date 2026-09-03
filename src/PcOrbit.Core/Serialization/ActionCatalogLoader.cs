using System.Text.Json;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Serialization;

public static class ActionCatalogLoader
{
    public static ActionCatalog LoadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        List<ActionDefinition> actions = [];

        foreach (string file in Directory
            .EnumerateFiles(directory, "*.actions.json")
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            actions.AddRange(LoadFile(file));
        }

        return new ActionCatalog(actions);
    }

    public static IReadOnlyList<ActionDefinition> LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Load(File.ReadAllText(path), path);
    }

    public static IReadOnlyList<ActionDefinition> Load(string json, string path = "(inline)")
    {
        List<ActionDto> dtos;

        try
        {
            dtos = JsonSerializer.Deserialize<List<ActionDto>>(json, JsonDefaults.DataFiles)
                ?? throw new DataFileException(path, "file is empty.");
        }
        catch (JsonException ex)
        {
            throw new DataFileException(path, $"not valid JSON: {ex.Message}");
        }

        return [.. dtos.Select(dto => ToDefinition(dto, path))];
    }

    private static ActionDefinition ToDefinition(ActionDto dto, string path)
    {
        if (string.IsNullOrWhiteSpace(dto.Id))
        {
            throw new DataFileException(path, "action id is required.");
        }

        if (string.IsNullOrWhiteSpace(dto.Version))
        {
            throw new DataFileException(path, $"action '{dto.Id}': version is required (spec 17.1).");
        }

        if (string.IsNullOrWhiteSpace(dto.Executor))
        {
            throw new DataFileException(
                path,
                $"action '{dto.Id}': executor is required. A manifest never carries script — it names a reviewed adapter (spec 17.1).");
        }

        if (dto.Provides is not { Count: > 0 })
        {
            throw new DataFileException(path, $"action '{dto.Id}': must declare what capability state it provides.");
        }

        if (dto.Verify is not { Count: > 0 })
        {
            throw new DataFileException(
                path,
                $"action '{dto.Id}': must declare verification. Spec 28 makes verify mandatory for every action.");
        }

        if (dto.Reversible is null)
        {
            throw new DataFileException(
                path,
                $"action '{dto.Id}': must declare reversibility, even if the answer is 'none' (spec 9.1).");
        }

        WriteMode writeMode = dto.WriteMode
            ?? throw new DataFileException(path, $"action '{dto.Id}': writeMode is required.");

        // Spec 8.3.5 / decision 19: nothing may claim Auto for firmware without a vendor scope.
        // The check lives here so a data contribution cannot quietly promise automation.
        bool touchesFirmware = dto.Provides.Any(p => p.Capability?.StartsWith("firmware.", StringComparison.Ordinal) == true);

        if (touchesFirmware
            && writeMode == WriteMode.Auto
            && dto.Compatibility?.Vendors is not { Count: > 0 })
        {
            throw new DataFileException(
                path,
                $"action '{dto.Id}': claims Auto for a firmware capability without a vendor restriction. "
                + "Auto firmware writes are only allowed through a verified vendor adapter (spec 8.3.5).");
        }

        return new ActionDefinition(
            Id: dto.Id,
            Version: dto.Version,
            TitleKey: dto.TitleKey ?? $"action.{dto.Id}.title",
            Executor: dto.Executor,
            Parameters: dto.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Provides: [.. dto.Provides.Select(p => ToAssertion(p, dto.Id, path))],
            Requires: [.. (dto.Requires ?? []).Select(r => ToAssertion(r, dto.Id, path))],
            WriteMode: writeMode,
            Privilege: dto.Privilege ?? PrivilegeLevel.Administrator,
            Risk: dto.Risk ?? RiskClass.Medium,
            Restart: dto.Restart ?? RestartKind.None,
            SafeApply: dto.SafeApply is null
                ? SafeApplyPolicy.Off
                : new SafeApplyPolicy(dto.SafeApply.Enabled, dto.SafeApply.ConfirmWithinSeconds, dto.SafeApply.AutoRevert),
            Reversible: new Reversibility(
                dto.Reversible.Mode ?? throw new DataFileException(path, $"action '{dto.Id}': reversible.mode is required."),
                dto.Reversible.Strategy,
                dto.Reversible.InverseActionId),
            EstimatedSeconds: dto.EstimatedSeconds,
            Preflight: dto.Preflight ?? [],
            Compatibility: dto.Compatibility is null
                ? ActionCompatibility.Any
                : new ActionCompatibility(
                    dto.Compatibility.OsMinBuild,
                    dto.Compatibility.OsMaxBuild,
                    dto.Compatibility.Editions,
                    dto.Compatibility.Vendors,
                    dto.Compatibility.Models,
                    dto.Compatibility.CpuVendors),
            GuideId: dto.GuideId,
            Verify:
            [
                .. dto.Verify.Select(v => new VerificationStep(
                    CapabilityGraphLoader.ParseId(v.Capability, path, $"action '{dto.Id}' verify.capability"),
                    CapabilityValue.Parse(v.Expected ?? throw new DataFileException(path, $"action '{dto.Id}': verify has no expected value.")),
                    v.AfterRestart)),
            ]);
    }

    private static CapabilityAssertion ToAssertion(AssertionDto dto, string actionId, string path) =>
        new(
            CapabilityGraphLoader.ParseId(dto.Capability, path, $"action '{actionId}' capability"),
            CapabilityValue.Parse(dto.State ?? throw new DataFileException(path, $"action '{actionId}': assertion has no state.")));

    private sealed record ActionDto(
        string? Id,
        string? Version,
        string? TitleKey,
        string? Executor,
        Dictionary<string, string>? Parameters,
        List<AssertionDto>? Provides,
        List<AssertionDto>? Requires,
        WriteMode? WriteMode,
        PrivilegeLevel? Privilege,
        RiskClass? Risk,
        RestartKind? Restart,
        SafeApplyDto? SafeApply,
        ReversibleDto? Reversible,
        int EstimatedSeconds,
        List<PreflightKind>? Preflight,
        CompatibilityDto? Compatibility,
        string? GuideId,
        List<VerifyDto>? Verify);

    private sealed record AssertionDto(string? Capability, string? State);

    private sealed record SafeApplyDto(bool Enabled, int ConfirmWithinSeconds, bool AutoRevert);

    private sealed record ReversibleDto(ReversibilityMode? Mode, string? Strategy, string? InverseActionId);

    private sealed record CompatibilityDto(
        int? OsMinBuild,
        int? OsMaxBuild,
        List<string>? Editions,
        List<string>? Vendors,
        List<string>? Models,
        List<CpuVendor>? CpuVendors);

    private sealed record VerifyDto(string? Capability, string? Expected, bool AfterRestart = false);
}

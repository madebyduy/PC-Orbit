using System.Text.Json;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Serialization;

/// <summary>Reads <c>data/graph/*.graph.json</c> into a <see cref="CapabilityGraph"/>.</summary>
public static class CapabilityGraphLoader
{
    public static CapabilityGraph LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json = File.ReadAllText(path);
        return Load(json, path);
    }

    /// <summary>
    /// Loads and merges every graph file in a directory. Splitting the graph by area
    /// (core, display, network) keeps files reviewable; the merge is where duplicate node
    /// declarations get caught.
    /// </summary>
    public static CapabilityGraph LoadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        string[] files = [.. Directory.EnumerateFiles(directory, "*.graph.json").OrderBy(f => f, StringComparer.Ordinal)];

        if (files.Length == 0)
        {
            throw new DataFileException(directory, "no *.graph.json files found.");
        }

        List<CapabilityNode> nodes = [];
        List<CapabilityEdge> edges = [];
        List<string> versions = [];
        HashSet<CapabilityId> seen = [];

        foreach (string file in files)
        {
            CapabilityGraph part = LoadFile(file);
            versions.Add(part.Version);

            foreach (CapabilityNode node in part.Nodes)
            {
                if (!seen.Add(node.Id))
                {
                    throw new DataFileException(file, $"capability '{node.Id}' is already declared in another graph file.");
                }

                nodes.Add(node);
            }

            edges.AddRange(part.Edges);
        }

        return new CapabilityGraph(string.Join('+', versions), nodes, edges);
    }

    public static CapabilityGraph Load(string json, string path = "(inline)")
    {
        GraphFile file = Deserialize(json, path);

        if (string.IsNullOrWhiteSpace(file.GraphVersion))
        {
            throw new DataFileException(path, "graphVersion is required — a plan records it as provenance (spec 9.5).");
        }

        List<CapabilityNode> nodes = [];

        foreach (NodeDto dto in file.Nodes ?? [])
        {
            CapabilityId id = ParseId(dto.Id, path, "node id");

            nodes.Add(new CapabilityNode(
                Id: id,
                Kind: dto.Kind ?? throw new DataFileException(path, $"node '{id}' has no kind."),
                DisplayKey: dto.DisplayKey ?? $"cap.{id}",
                Aliases: dto.Aliases ?? [],
                ValueKind: dto.ValueKind,
                Unit: dto.Unit,
                Observable: dto.Observable));
        }

        List<CapabilityEdge> edges = [];

        foreach (EdgeDto dto in file.Edges ?? [])
        {
            edges.Add(new CapabilityEdge(
                From: ParseId(dto.From, path, "edge 'from'"),
                Kind: dto.Kind ?? throw new DataFileException(path, $"edge from '{dto.From}' has no kind."),
                To: ParseId(dto.To, path, "edge 'to'"),
                Expected: dto.Expected is null ? CapabilityValue.Enabled : CapabilityValue.Parse(dto.Expected),
                Evidence: ToEvidence(dto.Evidence, path),
                Scope: ToScope(dto.Scope)));
        }

        var graph = new CapabilityGraph(file.GraphVersion, nodes, edges);
        IReadOnlyList<string> problems = graph.Validate();

        if (problems.Count > 0)
        {
            throw new DataFileException(path, "invalid graph:" + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", problems));
        }

        return graph;
    }

    internal static CapabilityId ParseId(string? value, string path, string what)
    {
        if (!CapabilityId.TryParse(value, out CapabilityId id))
        {
            throw new DataFileException(path, $"{what} '{value}' is not a valid capability id.");
        }

        return id;
    }

    internal static Evidence ToEvidence(EvidenceDto? dto, string path)
    {
        if (dto is null)
        {
            // A relation with no stated source is exactly what spec 11.3 forbids.
            throw new DataFileException(path, "every edge needs evidence: source, kind and confidence (spec 11.3).");
        }

        if (string.IsNullOrWhiteSpace(dto.Source))
        {
            throw new DataFileException(path, "evidence.source must say where the relation came from.");
        }

        return new Evidence(dto.SourceKind, dto.Source, dto.Confidence);
    }

    internal static GraphScope ToScope(ScopeDto? dto) =>
        dto is null
            ? GraphScope.Everywhere
            : new GraphScope(dto.OsMinBuild, dto.OsMaxBuild, dto.Vendors, dto.Models, dto.CpuVendors);

    private static GraphFile Deserialize(string json, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<GraphFile>(json, JsonDefaults.DataFiles)
                ?? throw new DataFileException(path, "file is empty.");
        }
        catch (JsonException ex)
        {
            throw new DataFileException(path, $"not valid JSON: {ex.Message}");
        }
    }

    private sealed record GraphFile(string? GraphVersion, List<NodeDto>? Nodes, List<EdgeDto>? Edges);

    private sealed record NodeDto(
        string? Id,
        NodeKind? Kind,
        string? DisplayKey,
        List<string>? Aliases,
        ValueKind ValueKind = ValueKind.State,
        string? Unit = null,
        bool Observable = true);

    private sealed record EdgeDto(
        string? From,
        EdgeKind? Kind,
        string? To,
        string? Expected,
        EvidenceDto? Evidence,
        ScopeDto? Scope);
}

internal sealed record EvidenceDto(EvidenceSourceKind SourceKind, string? Source, Confidence Confidence);

internal sealed record ScopeDto(
    int? OsMinBuild,
    int? OsMaxBuild,
    List<string>? Vendors,
    List<string>? Models,
    List<CpuVendor>? CpuVendors);

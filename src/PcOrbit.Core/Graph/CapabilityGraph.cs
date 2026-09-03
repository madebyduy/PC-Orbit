using System.Collections.Immutable;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Graph;

/// <summary>
/// The dependency and evidence model the rest of the product plans from (spec 11).
/// Immutable and versioned: a plan records which graph version produced it, so a graph update
/// can never silently change a plan the user already reviewed (spec 12.3, 17.1).
/// </summary>
public sealed class CapabilityGraph
{
    private readonly ImmutableDictionary<CapabilityId, CapabilityNode> _nodes;
    private readonly ImmutableArray<CapabilityNode> _nodeList;
    private readonly ImmutableArray<CapabilityEdge> _edges;
    private readonly ImmutableDictionary<CapabilityId, ImmutableArray<CapabilityEdge>> _outgoing;
    private readonly ImmutableDictionary<CapabilityId, ImmutableArray<CapabilityEdge>> _incoming;

    public CapabilityGraph(
        string version,
        IEnumerable<CapabilityNode> nodes,
        IEnumerable<CapabilityEdge> edges)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        Version = version;
        _nodes = nodes.ToImmutableDictionary(n => n.Id, n => n);
        _nodeList = [.. _nodes.Values.OrderBy(n => n.Id)];
        _edges = [.. edges];

        _outgoing = _edges
            .GroupBy(e => e.From)
            .ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray());

        _incoming = _edges
            .GroupBy(e => e.To)
            .ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray());
    }

    public string Version { get; }

    public IReadOnlyList<CapabilityNode> Nodes => _nodeList;

    public IReadOnlyList<CapabilityEdge> Edges => _edges;

    public CapabilityNode? Node(CapabilityId id) =>
        _nodes.TryGetValue(id, out CapabilityNode? node) ? node : null;

    public bool Contains(CapabilityId id) => _nodes.ContainsKey(id);

    public IEnumerable<CapabilityEdge> From(CapabilityId id, EdgeKind? kind = null)
    {
        if (!_outgoing.TryGetValue(id, out ImmutableArray<CapabilityEdge> edges))
        {
            return [];
        }

        return kind is null ? edges : edges.Where(e => e.Kind == kind);
    }

    public IEnumerable<CapabilityEdge> To(CapabilityId id, EdgeKind? kind = null)
    {
        if (!_incoming.TryGetValue(id, out ImmutableArray<CapabilityEdge> edges))
        {
            return [];
        }

        return kind is null ? edges : edges.Where(e => e.Kind == kind);
    }

    /// <summary>
    /// Direct requirements of <paramref name="id"/> that apply to this machine.
    /// </summary>
    public IReadOnlyList<CapabilityEdge> RequirementsOf(CapabilityId id, MachineIdentity machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        return [.. From(id, EdgeKind.Requires).Where(e => e.Scope.Matches(machine))];
    }

    /// <summary>
    /// Transitive closure of <see cref="EdgeKind.Requires"/> from <paramref name="roots"/>,
    /// returned dependencies-first. Requirements that appear twice appear once in the result.
    /// </summary>
    /// <remarks>
    /// Depth-first post-order gives us "deepest dependency first", which is exactly the
    /// execution order the transaction engine needs (spec 9.1 "auto-order by dependency").
    /// A cycle is a data bug, not a runtime condition: it throws so a golden test catches it.
    /// </remarks>
    public IReadOnlyList<CapabilityId> RequirementClosure(
        IEnumerable<CapabilityId> roots,
        MachineIdentity machine)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(machine);

        List<CapabilityId> ordered = [];
        HashSet<CapabilityId> done = [];
        HashSet<CapabilityId> onStack = [];

        foreach (CapabilityId root in roots)
        {
            Visit(root);
        }

        return ordered;

        void Visit(CapabilityId id)
        {
            if (done.Contains(id))
            {
                return;
            }

            if (!onStack.Add(id))
            {
                throw new InvalidOperationException(
                    $"Requires-cycle in capability graph {Version} at '{id}'.");
            }

            // Deterministic order so the compiler is reproducible (spec 12.3).
            foreach (CapabilityEdge edge in RequirementsOf(id, machine).OrderBy(e => e.To))
            {
                Visit(edge.To);
            }

            onStack.Remove(id);
            done.Add(id);
            ordered.Add(id);
        }
    }

    /// <summary>
    /// Data-quality problems. Run in CI over shipped graph data and at load time, so a bad
    /// graph fails loudly instead of producing a plan with holes in it.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];

        foreach (CapabilityEdge edge in _edges)
        {
            if (!_nodes.ContainsKey(edge.From))
            {
                problems.Add($"Edge {edge.From} -{edge.Kind}-> {edge.To}: 'from' node is not declared.");
            }

            if (!_nodes.ContainsKey(edge.To))
            {
                problems.Add($"Edge {edge.From} -{edge.Kind}-> {edge.To}: 'to' node is not declared.");
            }

            if (edge.Kind == EdgeKind.Requires && edge.Evidence.Confidence < Confidence.High)
            {
                problems.Add(
                    $"Edge {edge.From} -requires-> {edge.To}: confidence is {edge.Evidence.Confidence}. "
                    + "The compiler plans from 'requires', so it must be high-confidence evidence (spec 11.3).");
            }
        }

        foreach (CapabilityNode node in _nodes.Values.OrderBy(n => n.Id))
        {
            if (string.IsNullOrWhiteSpace(node.DisplayKey))
            {
                problems.Add($"Node {node.Id}: no displayKey, so the UI would have to hard-code a name (spec 21.11).");
            }
        }

        // Cycle check across every requires edge, ignoring scope: a cycle that only appears on
        // Dell machines is still a data bug, and CI does not run on a Dell.
        string? cycle = FindRequiresCycle();

        if (cycle is not null)
        {
            problems.Add(cycle);
        }

        return problems;
    }

    private string? FindRequiresCycle()
    {
        HashSet<CapabilityId> done = [];
        HashSet<CapabilityId> onStack = [];

        foreach (CapabilityId id in _nodes.Keys.OrderBy(k => k))
        {
            if (Visit(id) is { } found)
            {
                return found;
            }
        }

        return null;

        string? Visit(CapabilityId id)
        {
            if (done.Contains(id))
            {
                return null;
            }

            if (!onStack.Add(id))
            {
                return $"Requires-cycle in capability graph {Version} at '{id}'.";
            }

            foreach (CapabilityEdge edge in From(id, EdgeKind.Requires).OrderBy(e => e.To))
            {
                if (Visit(edge.To) is { } found)
                {
                    return found;
                }
            }

            onStack.Remove(id);
            done.Add(id);
            return null;
        }
    }
}

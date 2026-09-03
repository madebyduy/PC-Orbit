using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Compiler;

/// <summary>
/// Resolves the <c>"max"</c> sentinel — "as high as this machine's hardware allows" — into the
/// concrete value it means on one specific machine.
/// </summary>
/// <remarks>
/// <para>
/// An outcome like "run the display at its highest refresh rate" cannot carry a number: 165 on one
/// monitor is 60 on another. It carries <c>"max"</c> instead, and the compiler translates that per
/// machine (spec 12.2 — turning intent into concrete values is exactly the compiler's job). The
/// translation is not a guess: it follows the graph's <see cref="EdgeKind.Limits"/> edge to the
/// capability that states the ceiling, and reads that ceiling from the snapshot.
/// </para>
/// <para>
/// Everything downstream then works on the real number: the plan the user reviews says
/// <c>60 → 165</c>, the plan hash locks that value, verification compares what the machine reports
/// against it, and undo knows what to restore. If the ceiling could not be read, there is no value
/// to resolve to — and per spec 6.6 that is a stated blocker, never a default.
/// </para>
/// </remarks>
public static class LimitResolver
{
    private const string MaxSentinel = "max";

    /// <summary>True when this expected value is the <c>"max"</c> sentinel rather than a literal.</summary>
    public static bool IsMax(CapabilityValue expected) =>
        expected.Status == CapabilityStatus.Value
        && string.Equals(expected.Raw, MaxSentinel, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves <c>"max"</c> for <paramref name="capability"/> by reading the capability that
    /// limits it. Returns false when no limits edge applies to this machine or the ceiling was
    /// not readable — the caller must then refuse, not substitute.
    /// </summary>
    public static bool TryResolveMax(
        CapabilityGraph graph,
        StateSnapshot snapshot,
        CapabilityId capability,
        out CapabilityValue resolved,
        out CapabilityId limitedBy)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(snapshot);

        foreach (CapabilityEdge edge in graph.To(capability, EdgeKind.Limits))
        {
            if (!edge.Scope.Matches(snapshot.Machine))
            {
                continue;
            }

            CapabilityValue ceiling = snapshot.ValueOf(edge.From);

            if (ceiling.Status == CapabilityStatus.Value)
            {
                resolved = ceiling;
                limitedBy = edge.From;
                return true;
            }
        }

        resolved = CapabilityValue.Unknown;
        limitedBy = default;
        return false;
    }
}

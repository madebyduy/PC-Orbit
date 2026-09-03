using PcOrbit.Core.Model;

namespace PcOrbit.Core.Graph;

/// <summary>
/// Answers "is this workload ready?" from the graph rather than from a hand-written check.
/// </summary>
/// <remarks>
/// <para>
/// A workload node such as <c>workload.docker-wsl2-ready</c> is not something Windows can be asked
/// about — it is true exactly when everything it requires is true. Deriving it from the same
/// <see cref="EdgeKind.Requires"/> edges the compiler plans from means the plan and the final
/// verification can never disagree about what "ready" meant.
/// </para>
/// <para>
/// Unknown propagates. If one requirement could not be read, the workload is Unknown, not
/// "not ready" — spec 6.6 and 21.3 both refuse to let a failed read masquerade as a verdict.
/// </para>
/// </remarks>
public static class WorkloadEvaluator
{
    public const string Ready = "ready";
    public const string NotReady = "notReady";

    public static CapabilityReading Evaluate(
        CapabilityId workload,
        StateSnapshot snapshot,
        CapabilityGraph graph,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(graph);

        List<string> unmet = [];
        List<string> unreadable = [];

        Walk(workload, new HashSet<CapabilityId>());

        CapabilityValue value;
        Confidence confidence;
        string reason;

        if (unmet.Count > 0)
        {
            value = CapabilityValue.Scalar(NotReady);
            confidence = Confidence.High;
            reason = "not met: " + string.Join(", ", unmet);
        }
        else if (unreadable.Count > 0)
        {
            value = CapabilityValue.Unknown;
            confidence = Confidence.None;
            reason = "could not read: " + string.Join(", ", unreadable);
        }
        else
        {
            value = CapabilityValue.Scalar(Ready);
            confidence = Confidence.High;
            reason = "every requirement in the capability graph is satisfied";
        }

        return new CapabilityReading(
            workload,
            value,
            new Evidence(
                EvidenceSourceKind.Inference,
                $"Derived from capability graph {graph.Version}: {reason}",
                confidence,
                Query: $"requires-closure({workload})"),
            observedAt);

        void Walk(CapabilityId node, HashSet<CapabilityId> visiting)
        {
            if (!visiting.Add(node))
            {
                return;
            }

            foreach (CapabilityEdge edge in graph.RequirementsOf(node, snapshot.Machine))
            {
                CapabilityValue actual = snapshot.ValueOf(edge.To);

                // A nested workload is derived, not read, so recurse into it instead of expecting
                // the snapshot to contain an answer for it.
                if (graph.Node(edge.To)?.Kind == NodeKind.Workload && !snapshot.Has(edge.To))
                {
                    Walk(edge.To, visiting);
                    continue;
                }

                if (!actual.IsKnown)
                {
                    unreadable.Add(edge.To.Value);
                }
                else if (!actual.Satisfies(edge.Expected))
                {
                    unmet.Add($"{edge.To} is {actual.Canonical}, needs {edge.Expected.Canonical}");
                }

                Walk(edge.To, visiting);
            }
        }
    }
}

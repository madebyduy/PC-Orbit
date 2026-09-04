using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Checkup;

/// <param name="Score">0-100. 100 means the checkup found nothing.</param>
/// <param name="LabelKey">
/// String-catalog key for the word shown beside the number, so the UI never invents the wording.
/// </param>
/// <param name="FindingCount">How many findings produced this number.</param>
/// <param name="UnreadableCount">
/// How many capabilities this machine was expected to answer for and did not. The score cannot
/// account for these — no rule fires on <see cref="CapabilityValue.Unknown"/> — so the number is
/// only honest when it travels with them.
/// </param>
public sealed record HealthVerdict(int Score, string LabelKey, int FindingCount, int UnreadableCount)
{
    /// <summary>
    /// True when the score is a statement about part of the machine rather than all of it, and the
    /// UI has to say so.
    /// </summary>
    public bool IsPartial => UnreadableCount > 0;
}

/// <summary>
/// Turns the checkup's findings into one number.
/// </summary>
/// <remarks>
/// <para>
/// Spec 8.1 allows a score only as a <em>way of presenting findings</em>: it may never deduct
/// points for a subjective tweak, and it may never be the reason a finding exists. So this is a
/// pure function of the findings the rules already produced — nothing here can invent a problem,
/// and a machine with no findings always scores 100.
/// </para>
/// <para>
/// A finding whose value could not be read does not exist, because the rules refuse to raise one
/// from <see cref="CapabilityValue.Unknown"/>. That is right for the rules and wrong for a score
/// on its own: a machine scanned without administrator rights would otherwise show 100 and mean
/// "we saw nothing", which is <see cref="CapabilityValue.Unknown"/> inferred into a value at the
/// presentation layer — the exact failure spec 6.6 and 27.13 forbid one layer down. So the verdict
/// carries the unreadable count with it and every surface shows both (ADR 0004).
/// </para>
/// </remarks>
public static class HealthScore
{
    private const int WarningCost = 14;
    private const int AttentionCost = 8;
    private const int InfoCost = 2;

    /// <summary>
    /// Scores a checkup against the snapshot it ran on, so the unreadable count is real rather
    /// than assumed.
    /// </summary>
    public static HealthVerdict Evaluate(CheckupContext context, IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Evaluate(findings, CountUnreadable(context.Snapshot, context.Graph));
    }

    /// <param name="unreadableCount">
    /// Required rather than optional: a caller that cannot say how much it failed to read is a
    /// caller that should not be showing a score.
    /// </param>
    public static HealthVerdict Evaluate(IReadOnlyList<Finding> findings, int unreadableCount)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentOutOfRangeException.ThrowIfNegative(unreadableCount);

        int deduction = findings.Sum(f => f.Severity switch
        {
            FindingSeverity.Warning => WarningCost,
            FindingSeverity.Attention => AttentionCost,
            _ => InfoCost,
        });

        // Floored at 40: a checkup that found things worth fixing is not the same as a broken PC,
        // and a number in the teens would say something the findings do not support.
        int score = Math.Clamp(100 - deduction, 40, 100);

        string labelKey = score switch
        {
            >= 95 => "health.excellent",
            >= 80 => "health.good",
            >= 60 => "health.fair",
            _ => "health.needsWork",
        };

        return new HealthVerdict(score, labelKey, findings.Count, unreadableCount);
    }

    /// <summary>
    /// Capabilities that read <see cref="CapabilityValue.Unknown"/> when the graph said they
    /// should be readable.
    /// </summary>
    /// <remarks>
    /// A node marked <c>observable: false</c> is expected to be Unknown on a typical machine — it
    /// is declared so the model is complete, not so the user is told something is missing. Counting
    /// those would turn an honest declaration into permanent noise.
    /// </remarks>
    public static int CountUnreadable(StateSnapshot snapshot, CapabilityGraph graph)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(graph);

        return snapshot.Readings.Count(r =>
            !r.Value.IsKnown && graph.Node(r.Capability) is not { Observable: false });
    }
}

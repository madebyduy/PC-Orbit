namespace PcOrbit.Core.Checkup;

/// <param name="Score">0-100. 100 means the checkup found nothing.</param>
/// <param name="LabelKey">
/// String-catalog key for the word shown beside the number, so the UI never invents the wording.
/// </param>
public sealed record HealthVerdict(int Score, string LabelKey);

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
/// A finding whose value could not be read does not exist (the rules refuse to raise one from
/// <c>Unknown</c>), which means an unreadable machine scores 100 rather than being punished for
/// what we could not see. That is deliberate: the score is not a guess about hidden state, and
/// the UI shows unreadable values separately.
/// </para>
/// </remarks>
public static class HealthScore
{
    private const int WarningCost = 14;
    private const int AttentionCost = 8;
    private const int InfoCost = 2;

    public static HealthVerdict Evaluate(IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

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

        return new HealthVerdict(score, labelKey);
    }
}

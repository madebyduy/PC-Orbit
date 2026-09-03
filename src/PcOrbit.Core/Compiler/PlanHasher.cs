using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PcOrbit.Core.Compiler;

/// <summary>
/// Locks a plan so the privileged side can prove it is executing what the user reviewed
/// (spec 9.1 "lock the plan with a hash before handing it to the privileged helper", 19.1).
/// </summary>
public static class PlanHasher
{
    /// <summary>
    /// The exact text that gets hashed. Exposed because a hash mismatch is otherwise impossible
    /// to debug, and because a golden test that diffs this material tells you <em>what</em>
    /// changed rather than just that something did.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes the snapshot id and the compile timestamp: two scans of an unchanged
    /// machine must produce the same hash, otherwise "the plan changed, review it again" (spec 12.3)
    /// would fire on every refresh and mean nothing.
    /// </remarks>
    public static string Material(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sb = new StringBuilder();

        sb.Append("outcome=").Append(plan.OutcomeId).Append('@').Append(plan.OutcomeVersion).Append('\n');
        sb.Append("machine=").Append(plan.Machine.Fingerprint).Append('\n');
        sb.Append("graph=").Append(plan.GraphVersion).Append('\n');
        sb.Append("rules=").Append(plan.RuleVersion).Append('\n');
        sb.Append("catalog=").Append(plan.CatalogVersion).Append('\n');

        foreach (PlanPhase phase in plan.Phases)
        {
            sb.Append("phase=")
              .Append(phase.Index.ToString(CultureInfo.InvariantCulture))
              .Append(" restartAfter=")
              .Append(phase.RestartAfter)
              .Append('\n');

            foreach (PlanStep step in phase.Steps)
            {
                sb.Append("  step=").Append(step.Ordinal.ToString(CultureInfo.InvariantCulture))
                  .Append(" capability=").Append(step.Capability)
                  .Append(" from=").Append(step.CurrentValue.Canonical)
                  .Append(" to=").Append(step.DesiredValue.Canonical)
                  .Append(" action=").Append(step.Action.Id).Append('@').Append(step.Action.Version)
                  .Append(" mode=").Append(step.Action.WriteMode)
                  .Append(" restart=").Append(step.Action.Restart)
                  .Append('\n');
            }
        }

        foreach (var check in plan.FinalVerification)
        {
            sb.Append("verify=").Append(check.Check).Append('=').Append(check.Expected.Canonical).Append('\n');
        }

        // Blockers change what Apply is allowed to do, so they belong inside the hash.
        foreach (PlanIssue issue in plan.Issues.Where(i => i.Severity == PlanIssueSeverity.Blocker))
        {
            sb.Append("blocker=").Append(issue.Code).Append(':').Append(issue.Capability?.ToString() ?? "-").Append('\n');
        }

        return sb.ToString();
    }

    public static string Hash(Plan plan)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Material(plan)));
        return Convert.ToHexStringLower(bytes);
    }
}

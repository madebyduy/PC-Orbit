using System.Globalization;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Model;
using PcOrbit.Core.Preflight;

using PcOrbit.Core.Navigation;

namespace PcOrbit.Core.Checkup;

/// <summary>
/// Whether this machine can still rescue itself.
/// </summary>
/// <remarks>
/// <para>
/// The deep research puts recovery readiness first, and the reason is worth restating: every other
/// finding in this checkup describes a machine that is currently working. This one describes what
/// happens when it stops. The cheapest moment to find out that the recovery environment is
/// unregistered is a moment when Windows still boots.
/// </para>
/// <para>
/// It reports only what it positively read. A machine scanned without administrator rights reads
/// Unknown for all three sources, and Unknown produces silence, not a warning — telling someone
/// their safety net is gone on the strength of a permission error would be worse than saying
/// nothing (spec 6.6, 27.13; ADR 0004).
/// </para>
/// </remarks>
public sealed class RecoveryReadinessRule : ICheckupRule
{
    public string Code => "recovery.environment-unavailable";

    public IEnumerable<Finding> Evaluate(CheckupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CapabilityReading? winre = context.Snapshot.ReadingOf(CoreCapabilities.RecoveryEnvironment);

        if (winre is null || !winre.Value.IsKnown || winre.Value.Status == CapabilityStatus.Enabled)
        {
            yield break;
        }

        // "staged" means a WinRE update is part-applied: registered, present, and unable to boot.
        // That is a different sentence to the user, so it is a different finding.
        bool staged = string.Equals(winre.Value.Canonical, "staged", StringComparison.Ordinal);

        string suffix = staged ? "staged" : "off";

        yield return new Finding(
            Code: staged ? "recovery.environment-staged" : Code,
            Route: Route.ToPage(PageKeys.Recovery),

            // A Warning, at the same level as System Restore being off: this is about losing
            // access to the machine, not about it running below its potential.
            Severity: FindingSeverity.Warning,
            TitleKey: $"finding.recovery.environment-{suffix}.title",
            BenefitKey: $"finding.recovery.environment-{suffix}.benefit",
            SafetyKey: $"finding.recovery.environment-{suffix}.safety",
            Arguments: RuleHelpers.Args(),
            Evidence: winre.Evidence,
            Capability: CoreCapabilities.RecoveryEnvironment);
    }
}

/// <summary>
/// System Restore is on, but the newest restore point is old enough that rolling back to it would
/// cost more than it saves.
/// </summary>
/// <remarks>
/// <para>
/// This is the gap the research calls out as "không chỉ báo lần backup cuối": a switch in the on
/// position is not a recovery path. Protection can be enabled with the disk allowance so small
/// that Windows discards points almost immediately, and the owner finds out at the worst moment.
/// </para>
/// <para>
/// Read-only, and it stays that way. The fix is disk allowance, exclusions and schedule — several
/// variables at once, none of which this rule can state the safety of, so it explains and carries
/// no button (spec 21.6).
/// </para>
/// </remarks>
public sealed class RestorePointFreshnessRule : ICheckupRule
{
    /// <summary>
    /// Ninety days. Not a preference: past roughly a quarter, a restore point predates enough
    /// Windows updates and driver changes that restoring to it is its own kind of incident.
    /// </summary>
    private const int StaleAfterDays = 90;

    public string Code => "recovery.restore-point-stale";

    public IEnumerable<Finding> Evaluate(CheckupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CapabilityReading? age = context.Snapshot.ReadingOf(CoreCapabilities.RestorePointAgeDays);
        CapabilityValue restore = context.Snapshot.ValueOf(CoreCapabilities.SystemRestore);

        // Only meaningful while protection is on. When it is off, SystemRestoreRule already says
        // the more important thing, and two findings about one subject is noise.
        if (age is null
            || restore.Status != CapabilityStatus.Enabled
            || !RuleHelpers.TryNumber(age.Value, out double days)
            || days < StaleAfterDays)
        {
            yield break;
        }

        yield return new Finding(
            Code: Code,
            Route: Route.ToOutcome("outcome.system-restore-on"),
            Severity: FindingSeverity.Attention,
            TitleKey: "finding.recovery.restore-point-stale.title",
            BenefitKey: "finding.recovery.restore-point-stale.benefit",
            SafetyKey: "finding.recovery.restore-point-stale.safety",
            Arguments: RuleHelpers.Args(
                ("days", ((int)days).ToString(CultureInfo.InvariantCulture)),
                ("threshold", StaleAfterDays.ToString(CultureInfo.InvariantCulture))),
            Evidence: age.Evidence,
            Capability: CoreCapabilities.RestorePointAgeDays);
    }
}

/// <summary>
/// What stands between this machine and Windows 11.
/// </summary>
/// <remarks>
/// <para>
/// The one finding in this checkup where the distinction the research keeps insisting on — between
/// <em>unsupported</em>, <em>disabled</em>, <em>firmware-needed</em> and <em>Unknown</em> — decides
/// what the user should do next. A TPM that reads 1.2 means buy a machine. A TPM that reads 2.0 and
/// is switched off in firmware means four minutes in a setup screen. Collapsing those into "not
/// compatible" is how people are told to replace a working PC.
/// </para>
/// <para>
/// So this rule reports only the requirements it positively read as unmet, names them, and offers
/// the outcome — which compiles a plan containing exactly the steps that can be done, and blockers
/// naming the ones that cannot.
/// </para>
/// </remarks>
public sealed class Windows11ReadinessRule : ICheckupRule
{
    /// <summary>
    /// The build Windows 11 starts at. A machine already on 11 has nothing to plan.
    /// </summary>
    private const int FirstWindows11Build = 22000;

    public string Code => "workload.windows11-not-ready";

    public IEnumerable<Finding> Evaluate(CheckupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Snapshot.Machine.OsBuild >= FirstWindows11Build)
        {
            yield break;
        }

        CapabilityReading? readiness = context.Snapshot.ReadingOf(CoreCapabilities.Windows11Ready);

        // Unknown here means at least one requirement could not be read. Saying "your PC cannot run
        // Windows 11" because Win32_Tpm needed administrator rights is the exact mistake this
        // codebase exists to avoid.
        if (readiness is null
            || !readiness.Value.IsKnown
            || readiness.Value.Canonical != Graph.WorkloadEvaluator.NotReady)
        {
            yield break;
        }

        List<CapabilityId> blocked = [.. Unmet(context)];

        if (blocked.Count == 0)
        {
            yield break;
        }

        // Whether anything can actually be done decides which sentence the user reads. A firmware
        // switch is a four-minute job; a TPM that is not there at all is not. Only the two settings
        // this build can guide someone through count as fixable — the boot mode and the TPM chip's
        // own version have no action, and the compiler will say so rather than this rule implying it.
        bool fixableInFirmware = blocked.Exists(id =>
            id == CoreCapabilities.SecureBoot || id == CoreCapabilities.TpmReady);

        yield return new Finding(
            Code: Code,
            Route: Route.ToPage(PageKeys.Security),
            Severity: FindingSeverity.Info,
            TitleKey: "finding.workload.windows11-not-ready.title",
            BenefitKey: fixableInFirmware
                ? "finding.workload.windows11-not-ready.benefit.firmware"
                : "finding.workload.windows11-not-ready.benefit.hardware",
            SafetyKey: "finding.workload.windows11-not-ready.safety",
            Arguments: RuleHelpers.Args(("count", blocked.Count.ToString(CultureInfo.InvariantCulture))),
            Evidence: readiness.Evidence,
            Capability: CoreCapabilities.Windows11Ready,
            SuggestedOutcomeId: fixableInFirmware ? "outcome.windows11-ready" : null,
            Restart: fixableInFirmware ? RestartKind.Firmware : RestartKind.None,
            EstimatedSeconds: fixableInFirmware ? 300 : 0,
            Related: blocked);
    }

    /// <summary>
    /// The requirements this machine positively fails. Unknown is not a failure and never appears
    /// here — which is the whole difference between "your TPM is off" and "we could not read it".
    /// </summary>
    private static IEnumerable<CapabilityId> Unmet(CheckupContext context)
    {
        foreach (Graph.CapabilityEdge edge in
                 context.Graph.RequirementsOf(CoreCapabilities.Windows11Ready, context.Snapshot.Machine))
        {
            CapabilityValue actual = context.Snapshot.ValueOf(edge.To);

            if (actual.IsKnown && !actual.Satisfies(edge.Expected))
            {
                yield return edge.To;
            }
        }
    }
}

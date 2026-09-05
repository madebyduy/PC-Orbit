using System.Globalization;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;
using PcOrbit.Core.Preflight;

using PcOrbit.Core.Navigation;

namespace PcOrbit.Core.Checkup;

public enum FindingSeverity
{
    /// <summary>Worth knowing, nothing to fix.</summary>
    Info = 0,

    /// <summary>Something is not set up the way the hardware allows.</summary>
    Attention,

    /// <summary>Something can cost the user data or access if left alone.</summary>
    Warning,
}

/// <summary>
/// One thing the checkup found, in the shape spec 21.6 requires.
/// </summary>
/// <remarks>
/// The four lines — what, what you gain, why it is safe, what it costs — are structural, not a
/// writing convention: a rule that cannot state the benefit and the safety of its own fix has no
/// business asking a non-technical person to press Apply. Everything here is keys plus arguments;
/// the sentences live in the string catalog and are written by native speakers (spec 21.6, 21.11).
/// </remarks>
public sealed record Finding(
    string Code,
    FindingSeverity Severity,
    string TitleKey,
    string BenefitKey,
    string SafetyKey,
    IReadOnlyDictionary<string, string> Arguments,
    Evidence Evidence,
    CapabilityId? Capability = null,
    string? SuggestedActionId = null,
    string? SuggestedOutcomeId = null,
    RestartKind Restart = RestartKind.None,
    int EstimatedSeconds = 0,
    IReadOnlyList<CapabilityId>? Related = null,
    Route? Route = null)
{
    /// <summary>
    /// Where this finding leads. Never null for a shipped rule; a test refuses a dead end.
    /// </summary>
    /// <remarks>
    /// The outcome wins when there is one, because it is the only route that ends in a change the
    /// app can verify. Otherwise the rule's own route: the page, guide, Settings screen or support
    /// site that is the honest next step when nothing can be automated safely.
    /// </remarks>
    public Route? NextRoute => SuggestedOutcomeId is { } outcome ? Route.ToOutcome(outcome) : Route;

    /// <summary>False when there is nothing to press: the finding is information only.</summary>
    public bool HasFix => SuggestedActionId is not null || SuggestedOutcomeId is not null;

    /// <summary>
    /// Other capabilities this finding is about, when naming them is the point.
    /// </summary>
    /// <remarks>
    /// Ids, not names: the display name of a capability lives in the string catalog under the
    /// node's <c>displayKey</c>, so a finding that wanted to list four of them inside one sentence
    /// would have to put translated text in an ICU argument — which is how a support report ends up
    /// half in one language (spec 21.11). The surface resolves these and renders them as a list.
    /// </remarks>
    public IReadOnlyList<CapabilityId> RelatedCapabilities { get; } = Related ?? [];
}

public sealed record CheckupContext(StateSnapshot Snapshot, CapabilityGraph Graph, ActionCatalog Catalog);

public interface ICheckupRule
{
    string Code { get; }

    IEnumerable<Finding> Evaluate(CheckupContext context);
}

/// <summary>
/// Runs the checkup rules. Spec 8.1: a score is only a way of presenting findings, and no finding
/// exists without specific evidence — we never deduct points for subjective tweaks.
/// </summary>
public sealed class CheckupEngine(IEnumerable<ICheckupRule> rules)
{
    private readonly IReadOnlyList<ICheckupRule> _rules = [.. rules];

    public static CheckupEngine Default { get; } = new(
    [
        new FirmwareVirtualizationRule(),
        new RefreshRateRule(),
        new MemorySpeedRule(),
        new SystemRestoreRule(),
        new FeatureDependencyRule(),
        new BitLockerRecoveryKeyRule(),
        new DiskSpaceRule(),
        new RecoveryReadinessRule(),
        new RestorePointFreshnessRule(),
        new Windows11ReadinessRule(),
        new FirmwareAdvisoryRule(),
    ]);

    public IReadOnlyList<Finding> Run(CheckupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<Finding> findings = [];

        foreach (ICheckupRule rule in _rules)
        {
            findings.AddRange(rule.Evaluate(context));
        }

        // Most serious first, then stable by code so the list does not shuffle between scans.
        return [.. findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Code, StringComparer.Ordinal)];
    }
}

internal static class RuleHelpers
{
    internal static IReadOnlyDictionary<string, string> Args(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);

        foreach ((string key, string value) in pairs)
        {
            result[key] = value;
        }

        return result;
    }

    internal static bool TryNumber(CapabilityValue value, out double number) =>
        double.TryParse(value.Raw, NumberStyles.Number, CultureInfo.InvariantCulture, out number);

    internal static string Round(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// The CPU can virtualise but firmware has it switched off — the single most common blocker for
/// WSL, Docker, Android emulators and Sandbox (spec 23.1.G).
/// </summary>
public sealed class FirmwareVirtualizationRule : ICheckupRule
{
    public string Code => "firmware.virtualization-disabled";

    public IEnumerable<Finding> Evaluate(CheckupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CapabilityValue supported = context.Snapshot.ValueOf(CoreCapabilities.CpuVirtualization);
        CapabilityReading? firmware = context.Snapshot.ReadingOf(CoreCapabilities.FirmwareVirtualization);

        // Unknown on either side means we say nothing. Guessing "it is off" here would send people
        // into their BIOS for no reason (spec 21.3, 27.13).
        if (!supported.Satisfies(CapabilityValue.Supported)
            || firmware is null
            || firmware.Value.Status != CapabilityStatus.Disabled)
        {
            yield break;
        }

        yield return new Finding(
            Code: Code,
            Severity: FindingSeverity.Attention,
            TitleKey: "finding.firmware.virtualization-disabled.title",
            BenefitKey: "finding.firmware.virtualization-disabled.benefit",
            SafetyKey: "finding.firmware.virtualization-disabled.safety",
            Arguments: RuleHelpers.Args(),
            Evidence: firmware.Evidence,
            Capability: CoreCapabilities.FirmwareVirtualization,
            SuggestedOutcomeId: "outcome.docker-wsl2-ready",
            Restart: RestartKind.Firmware,
            EstimatedSeconds: 240);
    }
}

/// <summary>
/// Spec 23.1.H — the finding a non-developer feels immediately, and the first place Safe Apply
/// with auto-revert gets exercised.
/// </summary>
public sealed class RefreshRateRule : ICheckupRule
{
    public string Code => "display.refresh-rate-mismatch";

    public IEnumerable<Finding> Evaluate(CheckupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CapabilityReading? current = context.Snapshot.ReadingOf(CoreCapabilities.DisplayCurrentRefreshRate);
        CapabilityValue max = context.Snapshot.ValueOf(CoreCapabilities.DisplayMaxRefreshRate);

        if (current is null
            || !RuleHelpers.TryNumber(current.Value, out double currentHz)
            || !RuleHelpers.TryNumber(max, out double maxHz)
            || currentHz >= maxHz - 0.5)
        {
            yield break;
        }

        ActionDefinition? fix = context.Catalog.ById("display.set-refresh-rate.max");
        int seconds = fix?.SafeApply.ConfirmWithinSeconds ?? 15;

        yield return new Finding(
            Code: Code,
            Severity: FindingSeverity.Attention,
            TitleKey: "finding.display.refresh-rate-mismatch.title",
            BenefitKey: "finding.display.refresh-rate-mismatch.benefit",
            SafetyKey: "finding.display.refresh-rate-mismatch.safety",
            Arguments: RuleHelpers.Args(
                ("current", RuleHelpers.Round(currentHz)),
                ("max", RuleHelpers.Round(maxHz)),
                ("seconds", seconds.ToString(CultureInfo.InvariantCulture))),
            Evidence: current.Evidence,
            Capability: CoreCapabilities.DisplayCurrentRefreshRate,
            SuggestedActionId: fix?.Id,
            SuggestedOutcomeId: "outcome.display-max-refresh",
            Restart: RestartKind.None,
            EstimatedSeconds: fix?.EstimatedSeconds ?? 20);
    }
}

/// <summary>
/// Memory running below what the modules are rated for — usually XMP/EXPO never switched on.
/// </summary>
/// <remarks>
/// Read-only in v0.1 on purpose. Enabling a memory profile is a firmware change that can make a
/// PC unstable if the kit cannot hold the speed, so the finding explains rather than offers a
/// button (spec 6.6, 8.3.2 read-only mode).
/// </remarks>
public sealed class MemorySpeedRule : ICheckupRule
{
    public string Code => "memory.speed-mismatch";

    public IEnumerable<Finding> Evaluate(CheckupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CapabilityReading? current = context.Snapshot.ReadingOf(CoreCapabilities.MemoryCurrentSpeed);
        CapabilityValue rated = context.Snapshot.ValueOf(CoreCapabilities.MemoryRatedSpeed);

        if (current is null
            || !RuleHelpers.TryNumber(current.Value, out double currentSpeed)
            || !RuleHelpers.TryNumber(rated, out double ratedSpeed)
            || ratedSpeed <= 0)
        {
            yield break;
        }

        // 5% slack: SPD tables and reported clocks disagree by a few MT/s on plenty of machines,
        // and a finding that fires on noise trains people to ignore findings.
        if (currentSpeed >= ratedSpeed * 0.95)
        {
            yield break;
        }

        yield return new Finding(
            Code: Code,
            Route: Route.ToGuide("memory XMP"),
            Severity: FindingSeverity.Attention,
            TitleKey: "finding.memory.speed-mismatch.title",
            BenefitKey: "finding.memory.speed-mismatch.benefit",
            SafetyKey: "finding.memory.speed-mismatch.safety",
            Arguments: RuleHelpers.Args(
                ("current", RuleHelpers.Round(currentSpeed)),
                ("rated", RuleHelpers.Round(ratedSpeed))),
            Evidence: current.Evidence,
            Capability: CoreCapabilities.MemoryCurrentSpeed);
    }
}

public sealed class SystemRestoreRule : ICheckupRule
{
    public string Code => "windows.system-restore-disabled";

    public IEnumerable<Finding> Evaluate(CheckupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CapabilityReading? restore = context.Snapshot.ReadingOf(CoreCapabilities.SystemRestore);

        if (restore is null || restore.Value.Status != CapabilityStatus.Disabled)
        {
            yield break;
        }

        ActionDefinition? fix = context.Catalog.ById("windows.system-restore.enable");

        yield return new Finding(
            Code: Code,
            Severity: FindingSeverity.Warning,
            TitleKey: "finding.windows.system-restore-disabled.title",
            BenefitKey: "finding.windows.system-restore-disabled.benefit",
            SafetyKey: "finding.windows.system-restore-disabled.safety",
            Arguments: RuleHelpers.Args(),
            Evidence: restore.Evidence,
            Capability: CoreCapabilities.SystemRestore,
            SuggestedActionId: fix?.Id,
            SuggestedOutcomeId: "outcome.system-restore-on",
            EstimatedSeconds: fix?.EstimatedSeconds ?? 30);
    }
}

/// <summary>
/// A Windows feature is on while something it requires is off, so it will fail the moment the
/// user tries to use it. Generic: it walks the graph rather than hard-coding pairs.
/// </summary>
public sealed class FeatureDependencyRule : ICheckupRule
{
    public string Code => "windows.feature-dependency-mismatch";

    public IEnumerable<Finding> Evaluate(CheckupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (CapabilityNode node in context.Graph.Nodes.Where(n => n.Kind == NodeKind.OsFeature))
        {
            CapabilityValue featureState = context.Snapshot.ValueOf(node.Id);

            if (featureState.Status != CapabilityStatus.Enabled)
            {
                continue;
            }

            foreach (CapabilityEdge edge in context.Graph.RequirementsOf(node.Id, context.Snapshot.Machine))
            {
                CapabilityReading? dependency = context.Snapshot.ReadingOf(edge.To);

                // Only a positively-read "off" counts. Unknown is not a mismatch.
                if (dependency is null || dependency.Value.Satisfies(edge.Expected) || !dependency.Value.IsKnown)
                {
                    continue;
                }

                CapabilityNode? dependencyNode = context.Graph.Node(edge.To);

                yield return new Finding(
                    Code: $"{Code}:{node.Id}",
                    Route: Route.ToPage(PageKeys.Readings),
                    Severity: FindingSeverity.Attention,
                    TitleKey: "finding.windows.feature-dependency-mismatch.title",
                    BenefitKey: "finding.windows.feature-dependency-mismatch.benefit",
                    SafetyKey: "finding.windows.feature-dependency-mismatch.safety",
                    Arguments: RuleHelpers.Args(
                        ("feature", node.DisplayKey),
                        ("dependency", dependencyNode?.DisplayKey ?? edge.To.Value)),
                    Evidence: dependency.Evidence,
                    Capability: edge.To,
                    Restart: RestartKind.Windows);
            }
        }
    }
}

/// <summary>
/// The Windows drive is nearly full.
/// </summary>
/// <remarks>
/// <para>
/// Thresholds are not taste: Windows feature updates need roughly 20 GB of free space, and below
/// about 10 GB Windows starts failing updates, hibernation and restore points outright. So the
/// warning level is the one where things break, and the softer level is the one where the next
/// feature update will not fit.
/// </para>
/// <para>
/// This rule states the problem and stops. Deleting files is not something v0.1 does on someone's
/// behalf, and a rule that cannot state the safety of its own fix has no business offering an
/// Apply button (spec 21.6) — so it explains, and the finding carries no action.
/// </para>
/// </remarks>
public sealed class DiskSpaceRule : ICheckupRule
{
    private const double CriticalGb = 10d;
    private const double LowGb = 25d;

    public string Code => "storage.system-drive-low";

    public IEnumerable<Finding> Evaluate(CheckupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CapabilityReading? free = context.Snapshot.ReadingOf(CoreCapabilities.SystemDriveFreeGb);

        if (free is null
            || !RuleHelpers.TryNumber(free.Value, out double freeGb)
            || freeGb >= LowGb)
        {
            yield break;
        }

        yield return new Finding(
            Code: Code,
            Route: Route.ToPage(PageKeys.Cleanup),
            Severity: freeGb < CriticalGb ? FindingSeverity.Warning : FindingSeverity.Attention,
            TitleKey: "finding.storage.system-drive-low.title",
            BenefitKey: "finding.storage.system-drive-low.benefit",
            SafetyKey: "finding.storage.system-drive-low.safety",
            Arguments: RuleHelpers.Args(
                ("free", RuleHelpers.Round(freeGb)),
                ("needed", RuleHelpers.Round(LowGb))),
            Evidence: free.Evidence,
            Capability: CoreCapabilities.SystemDriveFreeGb);
    }
}

/// <summary>
/// Spec 23.1.G explicitly wants this as a soft warning, not an error: the PC is fine, but a
/// firmware change later could lock the owner out, and now is the cheap moment to find the key.
/// </summary>
public sealed class BitLockerRecoveryKeyRule : ICheckupRule
{
    public string Code => "security.bitlocker-recovery-key-unconfirmed";

    public IEnumerable<Finding> Evaluate(CheckupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CapabilityReading? encryption = context.Snapshot.ReadingOf(CoreCapabilities.BitLockerSystemDrive);

        if (encryption is null || encryption.Value.Canonical is not ("on" or "enabled"))
        {
            yield break;
        }

        yield return new Finding(
            Code: Code,
            Route: Route.ToWeb("https://aka.ms/myrecoverykey"),
            Severity: FindingSeverity.Warning,
            TitleKey: "finding.security.bitlocker-recovery-key-unconfirmed.title",
            BenefitKey: "finding.security.bitlocker-recovery-key-unconfirmed.benefit",
            SafetyKey: "finding.security.bitlocker-recovery-key-unconfirmed.safety",
            Arguments: RuleHelpers.Args(),
            Evidence: encryption.Evidence,
            Capability: CoreCapabilities.BitLockerSystemDrive);
    }
}

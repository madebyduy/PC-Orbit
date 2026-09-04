using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Guides;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows.Executors;

/// <summary>
/// Prepares a firmware change the user performs themselves, then stands aside.
/// </summary>
/// <remarks>
/// <para>
/// Spec 8.3.3, and the reason Guided is a feature rather than an error screen: everything the app
/// can do, it does before and after the restart; the user does exactly one thing in the middle.
/// So this executor picks the right instructions for this machine, hands them back, and reports
/// <see cref="ApplyStatus.AwaitingUserAction"/>. The transaction then waits for a restart, and
/// after boot it reads the real value from Windows rather than asking "did you do it?".
/// </para>
/// <para>
/// Windows has no API for writing firmware settings, and this executor does not pretend otherwise.
/// It will never write to a UEFI <c>Setup</c> variable by offset — undocumented, different on
/// every BIOS build, and able to leave a machine unbootable (spec decision 18).
/// </para>
/// </remarks>
public sealed class GuidedFirmwareExecutor(
    IReadOnlyDictionary<string, GuideData> guides,
    IClock? clock = null) : IActionExecutor
{
    private readonly IReadOnlyDictionary<string, GuideData> _guides = guides
        ?? throw new ArgumentNullException(nameof(guides));

    private readonly IClock _clock = clock ?? SystemClock.Instance;

    public string Id => "firmware.guided-change";

    public Task<ApplyOutcome> ApplyAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        GuideEntry? entry = ResolveGuide(context.Action, context.Machine, out string? guideProblem);

        if (entry is null)
        {
            return Task.FromResult(ApplyOutcome.Failed(
                "action.executor-unavailable",
                guideProblem ?? "No guide data is available for this capability."));
        }

        string instructions = Describe(entry, context);

        if (context.Mode == ExecutionMode.DryRun)
        {
            return Task.FromResult(new ApplyOutcome(ApplyStatus.Skipped, "action.dry-run", instructions));
        }

        // Nothing is written here. The value of this step is that the plan now carries the pending
        // change, the instructions and the verification method, and survives the restart.
        return Task.FromResult(new ApplyOutcome(
            ApplyStatus.AwaitingUserAction,
            "action.guided.staged",
            instructions,
            new Evidence(
                EvidenceSourceKind.Documentation,
                $"guide {entry.Tier}: {entry.SettingName}",
                entry.Tier == GuideTier.GuidedVerified ? Confidence.High : Confidence.Medium)));
    }

    public Task<ApplyOutcome> RollbackAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        GuideEntry? entry = ResolveGuide(context.Action, context.Machine, out _);

        // Spec 21.9: when we cannot undo something ourselves, we show how — we do not hide the
        // button and say nothing. During an undo, Requested carries the value to set back to.
        string target = context.Requested.IsKnown ? context.Requested.Canonical : "its previous value";

        string how = entry is null
            ? $"Set the option back to {target} in your firmware setup screen."
            : $"Go back to {string.Join(" > ", entry.MenuPath)}, set '{entry.SettingName}' to {target}, "
              + $"and save ({string.Join(" or ", entry.SaveKeys ?? ["F10"])}).";

        return Task.FromResult(new ApplyOutcome(ApplyStatus.AwaitingUserAction, "action.guided.staged", how));
    }

    private GuideEntry? ResolveGuide(ActionDefinition action, MachineIdentity machine, out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(action.GuideId))
        {
            problem = $"Action '{action.Id}' is guided but names no guideId.";
            return null;
        }

        if (!_guides.TryGetValue(action.GuideId, out GuideData? guide))
        {
            problem = $"Guide data '{action.GuideId}' is not installed.";
            return null;
        }

        GuideEntry? entry = guide.SelectFor(machine, DateOnly.FromDateTime(_clock.Now.LocalDateTime));

        if (entry is null)
        {
            // Fail closed, and say which gate closed. An expired knowledge pack is not the same
            // problem as a missing one, and the fix is different: revalidate the pack against
            // current vendor firmware (ADR 0004).
            problem =
                $"Guide data '{action.GuideId}' expired on {guide.ExpiresOn:yyyy-MM-dd}. Menu names and "
                + "paths move between BIOS revisions, so it is no longer safe to send someone into "
                + "firmware setup on it.";
        }

        return entry;
    }

    private static string Describe(GuideEntry entry, ActionExecutionContext context)
    {
        List<string> lines =
        [
            $"Restart into firmware setup ({string.Join(" or ", entry.EnterKeys ?? ["Del", "F2"])} during startup).",
            $"Go to: {string.Join(" > ", entry.MenuPath)}",
            $"Set '{entry.SettingName}' to {context.Requested.Canonical}.",
            $"Save and exit ({string.Join(" or ", entry.SaveKeys ?? ["F10"])}).",
        ];

        if (entry.AlternateNames is { Count: > 0 } alternates)
        {
            lines.Add($"It may be called: {string.Join(", ", alternates)}.");
        }

        if (!string.IsNullOrWhiteSpace(entry.SearchKey))
        {
            lines.Add($"Your firmware has a search function ({entry.SearchKey}) if you cannot find it.");
        }

        if (entry.Tier == GuideTier.GuidedGeneric)
        {
            lines.Add("These instructions are for this brand in general; your menu may differ slightly.");
        }

        return string.Join(" ", lines);
    }
}

/// <summary>
/// The vendor write adapter contract, shaped for Dell, and deliberately not yet authorised to
/// write.
/// </summary>
/// <remarks>
/// <para>
/// Spec 8.3.5 sets the conditions for Auto mode: an officially documented interface, testing on at
/// least three models of the line with and without a BIOS password, a path for physical-presence
/// prompts, BitLocker preflight, and post-restart verification. Until those are met on real
/// hardware, this adapter probes for the interface and then refuses — because shipping an untested
/// firmware write would be precisely the "recommendation that destroys trust" risk in spec 27.5,
/// with a bricked machine attached.
/// </para>
/// <para>
/// The manifest that would use this executor lives in
/// <c>data/actions/pending-verification/</c> and is not loaded, so the compiler cannot select it.
/// Promoting a vendor adapter means moving that file after the criteria are met on real machines —
/// an auditable gate rather than a code comment.
/// </para>
/// </remarks>
public sealed class DellFirmwareExecutor(PowerShellRunner? powerShell = null) : IActionExecutor
{
    private const string ProbeScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $svc = Get-CimInstance -Namespace 'root\dcim\sysman\biosattributes' -ClassName BIOSAttributeInterface
        if ($svc) { "present" } else { "absent" }
        """;

    private readonly PowerShellRunner _powerShell = powerShell ?? PowerShellRunner.Default;

    public string Id => "firmware.vendor.dell";

    public async Task<ApplyOutcome> ApplyAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Read-only probe, so it is safe to run in a dry run too: it answers the useful question
        // of whether this machine even exposes the vendor interface.
        PowerShellResult probe = await _powerShell.RunAsync(ProbeScript, cancellationToken).ConfigureAwait(false);
        bool interfacePresent = probe.Succeeded && probe.StandardOutput.Contains("present", StringComparison.Ordinal);

        string state = interfacePresent
            ? "The Dell BIOS attribute interface is present on this machine."
            : "The Dell BIOS attribute interface was not found on this machine.";

        return ApplyOutcome.Failed(
            "action.executor-unavailable",
            $"{state} Automatic firmware writes stay disabled until this adapter has been verified on real "
            + "hardware per spec 8.3.5 (three models, with and without a BIOS password, physical-presence "
            + "handling, and post-restart verification). Use the guided route instead.");
    }

    public Task<ApplyOutcome> RollbackAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ApplyOutcome.Failed(
            "action.executor-unavailable",
            "This adapter has never written anything, so there is nothing to roll back."));
}

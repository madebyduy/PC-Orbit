using System.Globalization;
using PcOrbit.Adapters.Windows.Interop;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows.Executors;

/// <summary>
/// Raises the primary display to the highest refresh rate it supports, with Safe Apply.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole Safe Apply loop from spec 10.1, on the one change in v0.1 where it genuinely
/// earns its place: the failure mode of a wrong display mode is a screen the user cannot read, so
/// no dialog they could answer would help. Therefore: record the old mode, apply the new one, ask
/// for confirmation with a countdown, and put the old mode back if the answer does not come.
/// </para>
/// <para>
/// No elevation, no restart, and reversible in about a second — which is exactly why spec 23.1.H
/// puts it in v0.1 as the demo a non-developer can feel.
/// </para>
/// </remarks>
public sealed class DisplayRefreshRateExecutor(ISafeApplyConfirmation? confirmation = null) : IActionExecutor
{
    private readonly ISafeApplyConfirmation _confirmation = confirmation ?? NeverConfirms.Instance;

    public string Id => "display.set-refresh-rate";

    public async Task<ApplyOutcome> ApplyAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string target = ActionParameters.OneOf(context.Action, "target", ["max"]);

        DisplayMode? before = DisplayInterop.CurrentMode();
        IReadOnlyList<DisplayMode> supported = DisplayInterop.SupportedModesAtCurrentResolution();

        if (before is null || supported.Count == 0)
        {
            return ApplyOutcome.Failed(
                "action.executor-unavailable",
                "Could not enumerate display modes for the primary display.");
        }

        int wanted = supported.Max(m => m.RefreshHz);

        if (context.Mode == ExecutionMode.DryRun)
        {
            return new ApplyOutcome(
                ApplyStatus.Skipped,
                "action.dry-run",
                $"Would switch the primary display from {before.RefreshHz} Hz to {wanted} Hz (target '{target}').");
        }

        if (wanted <= before.RefreshHz)
        {
            return new ApplyOutcome(
                ApplyStatus.Skipped,
                "action.already-in-state",
                $"Already at {before.RefreshHz} Hz, which is the highest this display offers at the current resolution.");
        }

        DisplayChangeResult applied = DisplayInterop.TrySetRefreshRate(wanted);

        if (!applied.Succeeded)
        {
            return ApplyOutcome.Failed("action.failed", applied.Detail);
        }

        if (!context.Action.SafeApply.Enabled)
        {
            return ApplyOutcome.Applied(applied.Detail);
        }

        int seconds = Math.Max(5, context.Action.SafeApply.ConfirmWithinSeconds);

        bool confirmed = await _confirmation.ConfirmAsync(
            "safeApply.confirm",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["seconds"] = seconds.ToString(CultureInfo.InvariantCulture),
                ["from"] = before.RefreshHz.ToString(CultureInfo.InvariantCulture),
                ["to"] = wanted.ToString(CultureInfo.InvariantCulture),
            },
            seconds,
            cancellationToken).ConfigureAwait(false);

        if (confirmed)
        {
            return ApplyOutcome.Applied($"{applied.Detail} Confirmed by the user.");
        }

        if (!context.Action.SafeApply.AutoRevert)
        {
            return ApplyOutcome.Applied($"{applied.Detail} Not confirmed, and this action does not auto-revert.");
        }

        DisplayChangeResult reverted = DisplayInterop.TrySetRefreshRate(before.RefreshHz);

        // Reverting failed is the one genuinely bad outcome here, so it is reported as a failure
        // with the manual way out, rather than as a tidy "reverted".
        return reverted.Succeeded
            ? new ApplyOutcome(
                ApplyStatus.Skipped,
                "safeApply.reverted",
                $"No confirmation within {seconds}s, so the display went back to {before.RefreshHz} Hz.")
            : ApplyOutcome.Failed(
                "action.failed",
                $"Switched to {wanted} Hz but could not switch back to {before.RefreshHz} Hz: {reverted.Detail}. "
                + "Change it in Settings > System > Display > Advanced display.");
    }

    public Task<ApplyOutcome> RollbackAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Mode == ExecutionMode.DryRun)
        {
            return Task.FromResult(new ApplyOutcome(ApplyStatus.Skipped, "action.dry-run", "Would restore the previous refresh rate."));
        }

        if (context.Before.Status != CapabilityStatus.Value
            || !int.TryParse(context.Before.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int previous))
        {
            return Task.FromResult(ApplyOutcome.Failed(
                "action.failed",
                "The previous refresh rate was never read, so there is nothing to restore to."));
        }

        DisplayChangeResult result = DisplayInterop.TrySetRefreshRate(previous);

        return Task.FromResult(result.Succeeded
            ? ApplyOutcome.Applied(result.Detail)
            : ApplyOutcome.Failed("action.failed", result.Detail));
    }
}

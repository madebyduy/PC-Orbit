using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows.Executors;

/// <summary>Shared plumbing for executors that need PowerShell and an elevation check.</summary>
public abstract class WindowsExecutorBase(PowerShellRunner? powerShell, IElevationContext? elevation)
{
    protected PowerShellRunner PowerShell { get; } = powerShell ?? PowerShellRunner.Default;

    protected IElevationContext Elevation { get; } = elevation ?? WindowsElevationContext.Instance;

    /// <summary>
    /// The dry-run and elevation gates every write executor shares. Returns null to continue.
    /// </summary>
    /// <remarks>
    /// Elevation is checked here as well as in preflight. Preflight is about telling the user
    /// before they commit; this is about the process refusing to try. Both matter: a plan can sit
    /// pending for days, and rights can change in between.
    /// </remarks>
    protected ApplyOutcome? Gate(ActionExecutionContext context, string wouldDo)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Mode == ExecutionMode.DryRun)
        {
            return new ApplyOutcome(ApplyStatus.Skipped, "action.dry-run", wouldDo);
        }

        if (context.Action.Privilege == PrivilegeLevel.Administrator && !Elevation.IsElevated)
        {
            return ApplyOutcome.Failed("action.not-elevated", $"{context.Action.Id} needs administrator rights.");
        }

        return null;
    }

    protected async Task<ApplyOutcome> RunAsync(
        string script,
        string succeededDetail,
        bool pendingRestart,
        CancellationToken cancellationToken)
    {
        PowerShellResult result = await PowerShell.RunAsync(script, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return ApplyOutcome.Failed(
                "action.failed",
                string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"exit code {result.ExitCode}"
                    : result.StandardError);
        }

        string detail = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? succeededDetail
            : $"{succeededDetail} ({result.StandardOutput})";

        return pendingRestart ? ApplyOutcome.PendingRestart(detail) : ApplyOutcome.Applied(detail);
    }
}

/// <summary>
/// Turns a Windows optional component on or off through DISM.
/// </summary>
/// <remarks>
/// <para>
/// The feature name is not taken from the manifest and used: it is matched against the names this
/// build knows about (<see cref="WindowsCapabilities.OptionalFeatureNames"/>). A manifest naming
/// something else fails the step. That is the allowlist spec 19.1 asks for, at the one point in
/// this executor where a string reaches a command.
/// </para>
/// <para>
/// Always <c>-NoRestart</c>: when a restart happens is the transaction engine's decision, so that
/// a plan touching three components still asks for exactly one restart (spec 21.8).
/// </para>
/// </remarks>
public sealed class OptionalFeatureExecutor(
    bool enable,
    PowerShellRunner? powerShell = null,
    IElevationContext? elevation = null)
    : WindowsExecutorBase(powerShell, elevation), IActionExecutor
{
    public string Id => enable ? "windows.optional-feature.enable" : "windows.optional-feature.disable";

    public Task<ApplyOutcome> ApplyAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(context, turnOn: enable, cancellationToken);

    public Task<ApplyOutcome> RollbackAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(context, turnOn: !enable, cancellationToken);

    private async Task<ApplyOutcome> ChangeAsync(
        ActionExecutionContext context,
        bool turnOn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string featureName = ActionParameters.OneOf(
            context.Action,
            "featureName",
            WindowsCapabilities.OptionalFeatureNames.Values);

        string verb = turnOn ? "Enable" : "Disable";

        if (Gate(context, $"{verb} the Windows component '{featureName}'") is { } gated)
        {
            return gated;
        }

        // Nothing to do is a real, common answer, and reporting it beats a needless DISM run.
        if ((turnOn && context.Before.Status == CapabilityStatus.Enabled)
            || (!turnOn && context.Before.Status == CapabilityStatus.Disabled))
        {
            return new ApplyOutcome(ApplyStatus.Skipped, "action.already-in-state", $"{featureName} is already {context.Before.Canonical}.");
        }

        // featureName came from the allowlist above, so this interpolation cannot carry anything
        // the manifest author invented.
        string script = turnOn
            ? $"""
              $ErrorActionPreference = 'Stop'
              [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
              $r = Enable-WindowsOptionalFeature -Online -FeatureName '{featureName}' -All -NoRestart
              "RestartNeeded=$($r.RestartNeeded)"
              """
            : $"""
              $ErrorActionPreference = 'Stop'
              [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
              $r = Disable-WindowsOptionalFeature -Online -FeatureName '{featureName}' -NoRestart
              "RestartNeeded=$($r.RestartNeeded)"
              """;

        return await RunAsync(
            script,
            $"{verb}d '{featureName}' via DISM.",
            pendingRestart: context.Action.Restart != RestartKind.None,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Sets the WSL default version. No restart, no elevation, and idempotent.</summary>
public sealed class WslDefaultVersionExecutor(
    PowerShellRunner? powerShell = null,
    IElevationContext? elevation = null)
    : WindowsExecutorBase(powerShell, elevation), IActionExecutor
{
    public string Id => "wsl.set-default-version";

    public Task<ApplyOutcome> ApplyAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default) =>
        SetAsync(context, ActionParameters.OneOf(context.Action, "version", ["1", "2"]), cancellationToken);

    public Task<ApplyOutcome> RollbackAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Restore to what was actually there before the change (context.Requested during an undo),
        // not to a hard-coded version. If that value was never read, refuse rather than guess.
        if (context.Requested.Status != CapabilityStatus.Value || context.Requested.Raw is not ("1" or "2"))
        {
            return Task.FromResult(ApplyOutcome.Failed(
                "action.failed",
                "The previous WSL default version was never read, so there is nothing to restore to."));
        }

        return SetAsync(context, context.Requested.Raw, cancellationToken);
    }

    private async Task<ApplyOutcome> SetAsync(
        ActionExecutionContext context,
        string version,
        CancellationToken cancellationToken)
    {
        if (Gate(context, $"Set the WSL default version to {version}") is { } gated)
        {
            return gated;
        }

        if (context.Before.Canonical == version)
        {
            return new ApplyOutcome(ApplyStatus.Skipped, "action.already-in-state", $"WSL default version is already {version}.");
        }

        // version is "1" or "2" from the allowlist.
        string script = $"""
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            & "$env:SystemRoot\System32\wsl.exe" --set-default-version {version} | Out-String
            """;

        return await RunAsync(
            script,
            $"Set WSL default version to {version}.",
            pendingRestart: false,
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed class SystemRestoreExecutor(
    PowerShellRunner? powerShell = null,
    IElevationContext? elevation = null)
    : WindowsExecutorBase(powerShell, elevation), IActionExecutor
{
    private const string EnableScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        Enable-ComputerRestore -Drive "$env:SystemDrive\"
        "enabled"
        """;

    private const string DisableScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        Disable-ComputerRestore -Drive "$env:SystemDrive\"
        "disabled"
        """;

    public string Id => "windows.system-restore.enable";

    public async Task<ApplyOutcome> ApplyAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (Gate(context, "Turn System Restore on for the Windows drive") is { } gated)
        {
            return gated;
        }

        return await RunAsync(EnableScript, "Enabled System Restore.", false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplyOutcome> RollbackAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (Gate(context, "Turn System Restore back off") is { } gated)
        {
            return gated;
        }

        return await RunAsync(DisableScript, "Disabled System Restore.", false, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Suspends BitLocker for exactly one restart — the escape hatch spec 10.3 requires before a
/// firmware or boot change on an encrypted machine.
/// </summary>
/// <remarks>
/// <c>-RebootCount 1</c> matters: protection comes back by itself after the next start, so a user
/// who forgets about it is not left with an unprotected drive.
/// </remarks>
public sealed class BitLockerSuspendExecutor(
    PowerShellRunner? powerShell = null,
    IElevationContext? elevation = null)
    : WindowsExecutorBase(powerShell, elevation), IActionExecutor
{
    public string Id => "security.bitlocker.suspend";

    public async Task<ApplyOutcome> ApplyAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string rebootCount = ActionParameters.OneOf(context.Action, "rebootCount", ["1"]);

        if (Gate(context, "Suspend BitLocker for one restart") is { } gated)
        {
            return gated;
        }

        string script = $"""
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            Suspend-BitLocker -MountPoint "$env:SystemDrive" -RebootCount {rebootCount} | Out-Null
            "suspended"
            """;

        return await RunAsync(script, "Suspended BitLocker for one restart.", false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ApplyOutcome> RollbackAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (Gate(context, "Resume BitLocker protection now") is { } gated)
        {
            return gated;
        }

        const string script = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            Resume-BitLocker -MountPoint "$env:SystemDrive" | Out-Null
            "resumed"
            """;

        return await RunAsync(script, "Resumed BitLocker protection.", false, cancellationToken)
            .ConfigureAwait(false);
    }
}

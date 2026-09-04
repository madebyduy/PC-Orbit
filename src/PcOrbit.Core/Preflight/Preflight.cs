using System.Globalization;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Compiler;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Preflight;

public enum PreflightVerdict
{
    Ok = 0,
    Warning,

    /// <summary>Apply is not allowed until the user resolves this.</summary>
    Blocked,
}

/// <param name="MessageKey">i18n key. The check never produces a sentence itself (spec 21.11).</param>
/// <param name="Arguments">ICU arguments for that message.</param>
public sealed record PreflightFinding(
    PreflightKind Kind,
    PreflightVerdict Verdict,
    string MessageKey,
    IReadOnlyDictionary<string, string> Arguments,
    string? Detail = null);

public sealed record PreflightReport(IReadOnlyList<PreflightFinding> Findings)
{
    public static PreflightReport Empty { get; } = new([]);

    public bool IsBlocked => Findings.Any(f => f.Verdict == PreflightVerdict.Blocked);

    public IEnumerable<PreflightFinding> Blockers => Findings.Where(f => f.Verdict == PreflightVerdict.Blocked);

    public IEnumerable<PreflightFinding> Warnings => Findings.Where(f => f.Verdict == PreflightVerdict.Warning);
}

/// <param name="Acknowledgements">
/// Things the user has explicitly confirmed in this session, by stable key. Spec 10.3 point 5:
/// a firmware plan may not be applied until the user picks one of "I have the key" or
/// "suspend encryption for one restart" — so consent is data the engine can check, not a UI habit.
/// </param>
public sealed record PreflightContext(
    Plan Plan,
    StateSnapshot Snapshot,
    bool IsElevated,
    IReadOnlySet<string> Acknowledgements)
{
    public static class Ack
    {
        /// <summary>User confirmed they can get to their BitLocker recovery key.</summary>
        public const string BitLockerKeyConfirmed = "bitlocker.recovery-key-confirmed";
    }
}

public interface IPreflightCheck
{
    PreflightKind Kind { get; }

    PreflightFinding? Evaluate(PreflightContext context);
}

/// <summary>
/// Runs only the checks the plan's actions actually declared, and returns everything at once so
/// the user sees the full cost of the plan before Apply rather than one dialog at a time.
/// </summary>
public sealed class PreflightRunner(IEnumerable<IPreflightCheck> checks)
{
    private readonly IReadOnlyList<IPreflightCheck> _checks = [.. checks];

    /// <summary>The default set. All pure: they read the snapshot and the plan, and change nothing.</summary>
    public static PreflightRunner Default { get; } = new(
    [
        new ElevationPreflight(),
        new BitLockerPreflight(),
        new PowerSourcePreflight(),
        new UnsavedWorkPreflight(),
        new DiskSpacePreflight(),
        new BiosPasswordPreflight(),
        new RecoveryAvailablePreflight(),
    ]);

    public PreflightReport Run(PreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<PreflightKind> required = context.Plan.RequiredPreflight;
        List<PreflightFinding> findings = [];

        foreach (IPreflightCheck check in _checks.Where(c => required.Contains(c.Kind)))
        {
            if (check.Evaluate(context) is { } finding)
            {
                findings.Add(finding);
            }
        }

        return new PreflightReport(findings);
    }
}

public static class CoreCapabilities
{
    public static CapabilityId BitLockerSystemDrive { get; } = CapabilityId.Parse("security.bitlocker.system-drive");
    public static CapabilityId OnBattery { get; } = CapabilityId.Parse("power.on-battery");
    public static CapabilityId BatteryPercent { get; } = CapabilityId.Parse("power.battery-percent");
    public static CapabilityId SystemDriveFreeGb { get; } = CapabilityId.Parse("storage.system-drive.free-gb");
    public static CapabilityId SystemRestore { get; } = CapabilityId.Parse("windows.system-restore");
    public static CapabilityId RecoveryEnvironment { get; } = CapabilityId.Parse("recovery.winre");
    public static CapabilityId RecoveryPartition { get; } = CapabilityId.Parse("recovery.partition");
    public static CapabilityId RestorePointAgeDays { get; } = CapabilityId.Parse("recovery.restore-point.age-days");
    public static CapabilityId SecureBoot { get; } = CapabilityId.Parse("firmware.secure-boot");
    public static CapabilityId TpmVersion { get; } = CapabilityId.Parse("firmware.tpm.version");
    public static CapabilityId TpmReady { get; } = CapabilityId.Parse("firmware.tpm.ready");
    public static CapabilityId BootMode { get; } = CapabilityId.Parse("firmware.boot-mode");
    public static CapabilityId BiosVersion { get; } = CapabilityId.Parse("firmware.bios.version");
    public static CapabilityId BiosAgeDays { get; } = CapabilityId.Parse("firmware.bios.age-days");
    public static CapabilityId FirmwareUpdateDelivery { get; } = CapabilityId.Parse("firmware.update-delivery");
    public static CapabilityId Windows11Ready { get; } = CapabilityId.Parse("workload.windows11-ready");
    public static CapabilityId RecoveryReady { get; } = CapabilityId.Parse("workload.recovery-ready");
    public static CapabilityId FirmwareVirtualization { get; } = CapabilityId.Parse("firmware.cpu.virtualization");
    public static CapabilityId CpuVirtualization { get; } = CapabilityId.Parse("cpu.virtualization");
    public static CapabilityId MemoryRatedSpeed { get; } = CapabilityId.Parse("memory.rated-speed");
    public static CapabilityId MemoryCurrentSpeed { get; } = CapabilityId.Parse("memory.current-speed");
    public static CapabilityId DisplayCurrentRefreshRate { get; } = CapabilityId.Parse("display.current-refresh-rate");
    public static CapabilityId DisplayMaxRefreshRate { get; } = CapabilityId.Parse("display.max-refresh-rate");
}

public sealed class ElevationPreflight : IPreflightCheck
{
    public PreflightKind Kind => PreflightKind.Elevation;

    public PreflightFinding? Evaluate(PreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.IsElevated || !context.Plan.RequiresElevation)
        {
            return null;
        }

        // Spec 21.10: a standard user still gets the full scan and checkup. What they do not get
        // is a silent failure halfway through applying.
        return new PreflightFinding(
            Kind,
            PreflightVerdict.Blocked,
            "preflight.elevation.blocked",
            Empty,
            string.Join(
                ", ",
                context.Plan.Steps.Where(s => s.NeedsElevation).Select(s => s.Action.Id)));
    }

    internal static IReadOnlyDictionary<string, string> Empty { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Spec 10.3 — mandatory, no exceptions, for anything touching firmware or boot.
/// </summary>
/// <remarks>
/// This is the single biggest real-world way an ordinary user breaks their PC with a tool like
/// this: change something in firmware, reboot, and Windows asks for a 48-digit key they have
/// never seen. Most consumer Windows 11 laptops ship with Device Encryption on and the owner
/// does not know. So: blocked until the user either confirms they have the key or the plan
/// itself suspends encryption for one restart.
/// </remarks>
public sealed class BitLockerPreflight : IPreflightCheck
{
    public PreflightKind Kind => PreflightKind.Bitlocker;

    public PreflightFinding? Evaluate(PreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CapabilityValue encryption = context.Snapshot.ValueOf(CoreCapabilities.BitLockerSystemDrive);
        bool encrypted = encryption.Canonical is "on" or "enabled";
        bool unknown = !encryption.IsKnown;

        if (!encrypted && !unknown)
        {
            return null;
        }

        bool planSuspends = context.Plan.Steps.Any(s =>
            s.Action.Provides.Any(p =>
                p.Capability.Equals(CoreCapabilities.BitLockerSystemDrive)
                && p.State.Canonical == "suspended"));

        if (planSuspends || context.Acknowledgements.Contains(PreflightContext.Ack.BitLockerKeyConfirmed))
        {
            return null;
        }

        // An unknown encryption state blocks too. Spec 10.3 says "mandatory, no exceptions", and
        // "we could not read it" is not evidence that the drive is unencrypted — reading the
        // encryption state needs administrator rights, which a guided firmware step does not.
        // The user still has a way forward: confirm they have the key.
        return new PreflightFinding(
            Kind,
            PreflightVerdict.Blocked,
            unknown ? "preflight.bitlocker.unknown" : "preflight.bitlocker.blocked",
            ElevationPreflight.Empty,
            unknown ? "Encryption state could not be read." : $"Encryption reads '{encryption.Canonical}'.");
    }
}

public sealed class PowerSourcePreflight : IPreflightCheck
{
    public PreflightKind Kind => PreflightKind.PowerSource;

    public PreflightFinding? Evaluate(PreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Snapshot.ValueOf(CoreCapabilities.OnBattery).Canonical != "yes")
        {
            return null;
        }

        string percent = context.Snapshot.ValueOf(CoreCapabilities.BatteryPercent).Raw ?? "?";

        return new PreflightFinding(
            Kind,
            PreflightVerdict.Warning,
            "preflight.powerSource.warning",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["percent"] = percent });
    }
}

public sealed class UnsavedWorkPreflight : IPreflightCheck
{
    public PreflightKind Kind => PreflightKind.UnsavedWork;

    public PreflightFinding? Evaluate(PreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // v0.1 cannot enumerate documents with unsaved changes yet (that needs the Restart Manager
        // API, spec 21.8). Until then we say the honest generic thing rather than nothing.
        return context.Plan.Cost.Restarts == 0
            ? null
            : new PreflightFinding(Kind, PreflightVerdict.Warning, "preflight.unsavedWork.warning", ElevationPreflight.Empty);
    }
}

public sealed class DiskSpacePreflight : IPreflightCheck
{
    private const int MinimumFreeGb = 5;

    public PreflightKind Kind => PreflightKind.DiskSpace;

    public PreflightFinding? Evaluate(PreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CapabilityValue free = context.Snapshot.ValueOf(CoreCapabilities.SystemDriveFreeGb);

        if (!free.IsKnown
            || !double.TryParse(free.Raw, NumberStyles.Number, CultureInfo.InvariantCulture, out double gb)
            || gb >= MinimumFreeGb)
        {
            return null;
        }

        return new PreflightFinding(
            Kind,
            PreflightVerdict.Warning,
            "preflight.diskSpace.warning",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["free"] = gb.ToString("0.#", CultureInfo.InvariantCulture),
                ["needed"] = MinimumFreeGb.ToString(CultureInfo.InvariantCulture),
            });
    }
}

public sealed class BiosPasswordPreflight : IPreflightCheck
{
    public PreflightKind Kind => PreflightKind.BiosPassword;

    public PreflightFinding? Evaluate(PreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // We cannot read whether firmware setup is password protected. Saying so beats finding
        // out at the worst moment, standing in front of the BIOS screen (spec 8.3.4).
        return new PreflightFinding(Kind, PreflightVerdict.Warning, "preflight.biosPassword.warning", ElevationPreflight.Empty);
    }
}

public sealed class RecoveryAvailablePreflight : IPreflightCheck
{
    public PreflightKind Kind => PreflightKind.RecoveryAvailable;

    public PreflightFinding? Evaluate(PreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Snapshot.ValueOf(CoreCapabilities.SystemRestore).Canonical == "enabled"
            ? null
            : new PreflightFinding(Kind, PreflightVerdict.Warning, "preflight.recoveryAvailable.warning", ElevationPreflight.Empty);
    }
}

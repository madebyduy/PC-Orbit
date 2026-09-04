using Microsoft.Win32;
using PcOrbit.Core.Actions;
using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows.Executors;

/// <param name="Capability">The graph node whose state this key is.</param>
/// <param name="Hive">Machine-wide settings need administrator rights; per-user ones do not.</param>
/// <param name="Path">Key path under the hive. Never comes from a manifest.</param>
/// <param name="Name">Value name.</param>
/// <param name="On">The value that means the setting is on, in the sense the capability describes.</param>
/// <param name="Off">The value that means it is off.</param>
/// <param name="AbsentMeans">
/// What it means for the value not to exist at all. Most of these keys are absent on a default
/// Windows install, and reading that as "off" when Windows' own default is on would make the
/// checkup raise a finding about a machine that is fine.
/// </param>
public sealed record RegistryTweak(
    CapabilityId Capability,
    RegistryHive Hive,
    string Path,
    string Name,
    int On,
    int Off,
    bool AbsentMeans);

/// <summary>
/// The registry settings this product is allowed to read and write, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The allowlist spec 17.1 requires, in the only form that actually constrains anything: a
/// compile-time table. A manifest passes the <em>id</em> of a tweak and
/// <see cref="ActionParameters.OneOf"/> checks it against these keys; the hive, path and value name
/// are looked up here. A data contribution can therefore add an action for a tweak that already
/// exists, and cannot introduce a registry path.
/// </para>
/// <para>
/// Every entry had to clear the fourth test in ADR 0006: switching it on must not make the machine
/// less safe. That is why this list turns tracking <em>off</em> and protections <em>on</em>, and
/// why it contains no entry for SmartScreen, Defender or error reporting — those appear in the
/// checkup as findings when they are disabled, which is the same list read the other way round.
/// </para>
/// </remarks>
public static class RegistryTweaks
{
    public static IReadOnlyList<RegistryTweak> All { get; } =
    [
        // ---------------------------------------------------------------- privacy
        new(
            CapabilityId.Parse("privacy.advertising-id"),
            RegistryHive.CurrentUser,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
            "Enabled",
            On: 0,
            Off: 1,

            // Windows ships this on. Absent therefore means "on", i.e. not yet turned off.
            AbsentMeans: false),

        new(
            CapabilityId.Parse("privacy.tailored-experiences"),
            RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Privacy",
            "TailoredExperiencesWithDiagnosticDataEnabled",
            On: 0,
            Off: 1,
            AbsentMeans: false),

        new(
            CapabilityId.Parse("privacy.activity-history"),
            RegistryHive.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\System",
            "PublishUserActivities",
            On: 0,
            Off: 1,
            AbsentMeans: false),

        new(
            CapabilityId.Parse("privacy.telemetry"),
            RegistryHive.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\DataCollection",
            "AllowTelemetry",
            On: 0,
            Off: 1,
            AbsentMeans: false),

        // ---------------------------------------------------------------- explorer
        // Not privacy, and the single most useful thing this list does for a non-technical owner:
        // a hidden extension is how "invoice.pdf.exe" gets opened.
        new(
            CapabilityId.Parse("explorer.file-extensions"),
            RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
            "HideFileExt",
            On: 0,
            Off: 1,
            AbsentMeans: false),
    ];

    private static readonly Dictionary<string, RegistryTweak> ById =
        All.ToDictionary(t => t.Capability.Value, StringComparer.Ordinal);

    /// <summary>The ids a manifest may name. This is what <c>OneOf</c> is checked against.</summary>
    public static IReadOnlyList<string> Ids { get; } = [.. ById.Keys.Order(StringComparer.Ordinal)];

    public static RegistryTweak? Find(CapabilityId capability) =>
        ById.TryGetValue(capability.Value, out RegistryTweak? tweak) ? tweak : null;

    /// <summary>
    /// Reads one tweak's current state.
    /// </summary>
    /// <remarks>
    /// A value that exists but holds something we do not recognise reads Unknown, not "off". People
    /// and other tools write these keys too, and a third value means we do not know what state the
    /// machine is in — which is a thing to say, not a thing to overwrite (spec 6.6).
    /// </remarks>
    public static (CapabilityValue Value, Evidence Evidence) Read(RegistryTweak tweak)
    {
        ArgumentNullException.ThrowIfNull(tweak);

        string full = $"{(tweak.Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\\{tweak.Path}\\{tweak.Name}";

        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(tweak.Hive, RegistryView.Default);
            using RegistryKey? key = root.OpenSubKey(tweak.Path);

            object? raw = key?.GetValue(tweak.Name);

            if (raw is null)
            {
                return (
                    tweak.AbsentMeans ? CapabilityValue.Enabled : CapabilityValue.Disabled,
                    new Evidence(
                        EvidenceSourceKind.Registry,
                        $"{full} is not set, which on Windows means the default",
                        Confidence.High,
                        Query: full,
                        RawResult: "(absent)"));
            }

            if (raw is not int value)
            {
                return (
                    CapabilityValue.Unknown,
                    Evidence.Missing($"{full} holds a {raw.GetType().Name}, which this build does not interpret"));
            }

            if (value == tweak.On)
            {
                return (CapabilityValue.Enabled, RegistryEvidence(full, value));
            }

            return value == tweak.Off
                ? (CapabilityValue.Disabled, RegistryEvidence(full, value))
                : (CapabilityValue.Unknown, Evidence.Missing(
                    $"{full} is {value}, which is neither the on nor the off value this build knows"));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return (CapabilityValue.Unknown, Evidence.Missing(
                $"{full} could not be read: {ex.Message}. Reading a machine-wide policy key needs administrator rights"));
        }
    }

    private static Evidence RegistryEvidence(string full, int value) => new(
        EvidenceSourceKind.Registry,
        full,
        Confidence.High,
        Query: full,
        RawResult: value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>
/// Writes one of the allowlisted registry settings.
/// </summary>
/// <remarks>
/// The manifest hands over a capability id and a target state. Everything that reaches the registry
/// — hive, path, value name, the number written — comes from <see cref="RegistryTweaks"/>, so this
/// executor cannot be pointed at a key nobody reviewed (spec 19.1).
/// </remarks>
public sealed class RegistrySettingExecutor : IActionExecutor
{
    public string Id => "windows.registry-setting";

    public Task<ApplyOutcome> ApplyAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Task.FromResult(Write(context, context.Requested));
    }

    /// <summary>
    /// Puts the value back. <c>Requested</c> carries the before-value during an undo, so the
    /// forward and reverse paths are the same code and cannot drift (spec 21.9).
    /// </summary>
    public Task<ApplyOutcome> RollbackAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Task.FromResult(Write(context, context.Requested));
    }

    private static ApplyOutcome Write(ActionExecutionContext context, CapabilityValue target)
    {
        string id = ActionParameters.OneOf(context.Action, "setting", RegistryTweaks.Ids);
        RegistryTweak? tweak = RegistryTweaks.Find(CapabilityId.Parse(id));

        if (tweak is null)
        {
            return ApplyOutcome.Failed(
                "action.executor-unavailable",
                $"'{id}' is not a registry setting this build knows how to write.");
        }

        if (!target.IsKnown)
        {
            // Undoing a step whose before-value was never read would mean inventing one.
            return ApplyOutcome.Failed(
                "action.undo-unavailable",
                "The previous value of this setting was never read, so there is nothing to put back.");
        }

        int number = target.Status == CapabilityStatus.Enabled ? tweak.On : tweak.Off;

        if (context.Mode == ExecutionMode.DryRun)
        {
            return new ApplyOutcome(
                ApplyStatus.Skipped,
                "action.dry-run",
                $"Would set {tweak.Name} to {number}.");
        }

        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(tweak.Hive, RegistryView.Default);
            using RegistryKey key = root.CreateSubKey(tweak.Path, writable: true);

            key.SetValue(tweak.Name, number, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return ApplyOutcome.Failed(
                "action.needs-elevation",
                $"Writing this setting needs administrator rights: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException)
        {
            return ApplyOutcome.Failed("action.failed", ex.Message);
        }

        // Nothing here claims success. The engine re-reads the machine through ICapabilityReader
        // and decides (spec 6.4).
        return new ApplyOutcome(
            ApplyStatus.Applied,
            "action.applied",
            $"Set {tweak.Name} to {number}.",
            new Evidence(
                EvidenceSourceKind.Registry,
                $"wrote {tweak.Name}={number}",
                Confidence.High));
    }
}

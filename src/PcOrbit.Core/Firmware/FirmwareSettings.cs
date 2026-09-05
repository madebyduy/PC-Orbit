using PcOrbit.Core.Model;

namespace PcOrbit.Core.Firmware;

/// <summary>How much a firmware setting can cost you if it is wrong.</summary>
/// <remarks>
/// Not a severity for a checkup — a gate on the confirmation. The three levels decide how hard it
/// is to press the button, and <see cref="FirmwareRisk.Refused"/> decides that there is no button.
/// </remarks>
public enum FirmwareRisk
{
    /// <summary>Cosmetic or reversible from inside Windows. A normal confirmation is enough.</summary>
    Routine = 0,

    /// <summary>Changes how the machine behaves. Named consequence, then a confirmation.</summary>
    Careful,

    /// <summary>
    /// Can stop the machine from starting, or lock encrypted data behind a key the owner has to
    /// find. Consequence, preflight, and typing the setting's name to confirm.
    /// </summary>
    Serious,

    /// <summary>
    /// This product will not write it at any confirmation level.
    /// </summary>
    /// <remarks>
    /// Reserved for changes whose damage cannot be undone by changing them back: clearing the
    /// security chip destroys the keys BitLocker and Windows Hello are built on, and a firmware
    /// password set through an automated path locks out an owner who mistypes it once.
    /// </remarks>
    Refused,
}

/// <param name="Name">The vendor's own name for the setting. Never translated: it is what the
/// firmware screen will call it too, and matching those two is the point.</param>
/// <param name="Current">Null when the vendor reported the setting but not its value.</param>
/// <param name="Options">
/// The values this setting accepts, as the firmware itself declared them. This is the allowlist:
/// a value that is not in here never reaches the vendor interface (ADR 0005).
/// </param>
public sealed record FirmwareSetting(
    string Name,
    string? Current,
    IReadOnlyList<string> Options,
    FirmwareRisk Risk,
    Evidence Evidence)
{
    /// <summary>Whether this product will offer to change it, and it has somewhere to change to.</summary>
    public bool Writable => Risk != FirmwareRisk.Refused && Options.Count > 1 && !IsCompound;

    /// <summary>
    /// True when the current value is a list of several options joined with colons.
    /// </summary>
    /// <remarks>
    /// Lenovo's <c>BootOrder</c> reads <c>USBCD:USBFDD:NVMe0:NVMe1:…</c> and accepts each device
    /// name as an option. A picker that offers one option at a time would write
    /// <c>BootOrder,NVMe0</c> — which is not "make NVMe0 first", it is "the boot order is now one
    /// device". The user would think they had chosen a first boot device and would have wiped the
    /// list. So a value whose parts are all options is a list, and this build does not edit lists.
    /// The test is on the parts being options, not on the colon alone: <c>AlarmTime</c> reads
    /// <c>00:00:00</c> and its parts are not.
    /// </remarks>
    public bool IsCompound
    {
        get
        {
            if (Current is null || !Current.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }

            string[] parts = Current.Split(':', StringSplitOptions.RemoveEmptyEntries);

            return parts.Length > 1 && parts.All(Accepts);
        }
    }

    /// <summary>
    /// Whether a value is one the firmware said it would take.
    /// </summary>
    /// <remarks>
    /// For a list-valued setting the value is several options joined with colons, and it is
    /// accepted when every part is an option and no part repeats. That is the shape the firmware
    /// wrote the current value in, so it is the shape it reads back. A single option is still a
    /// valid list of one — which is exactly the write the one-value picker must not be allowed to
    /// make, and <see cref="Writable"/> is what stops it.
    /// </remarks>
    public bool Accepts(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!value.Contains(':', StringComparison.Ordinal))
        {
            return Options.Any(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase));
        }

        string[] parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length > 0
            && parts.All(part => Options.Any(o => string.Equals(o, part, StringComparison.OrdinalIgnoreCase)))
            && parts.Distinct(StringComparer.OrdinalIgnoreCase).Count() == parts.Length;
    }

    /// <summary>A list-valued setting this product offers to reorder, rather than to pick one value for.</summary>
    public bool Reorderable => Risk != FirmwareRisk.Refused && IsCompound;
}

/// <summary>
/// Whether this machine's firmware can be driven from Windows, and if not, why not.
/// </summary>
/// <remarks>
/// <para>
/// Five answers rather than a boolean, because the first version of this had two and got one of
/// them badly wrong. It reported "your model does not expose this" whenever the vendor classes
/// returned no instances — and every ACPI-WMI class under <c>root\WMI</c> returns no instances to
/// a process without administrator rights, including ones that work on every machine ever made.
/// So a standard-user run was told, definitively, that its hardware lacked a feature nobody had
/// been able to ask about.
/// </para>
/// <para>
/// That is the mistake this whole product exists to not make: a failed read is
/// <see cref="NeedsElevation"/>, never a confident no (spec 6.6).
/// </para>
/// </remarks>
public enum FirmwareAvailability
{
    /// <summary>No manufacturer interface is registered at all. Common on self-built machines.</summary>
    NoInterface = 0,

    /// <summary>
    /// The classes are there and we lacked the rights to enumerate them. Not an answer about the
    /// hardware — an answer about this process.
    /// </summary>
    NeedsElevation,

    /// <summary>
    /// Asked with the rights to get an answer, and the firmware returned nothing. This is what a
    /// consumer model does with a commercial-line interface, and it is a real finding.
    /// </summary>
    ModelDoesNotImplement,

    /// <summary>The vendor publishes the interface behind a tool that is not installed here.</summary>
    NeedsVendorTool,

    /// <summary>Settings were returned.</summary>
    Available,
}

/// <param name="Settings">Empty unless <see cref="FirmwareInterface.Availability"/> is Available.</param>
/// <param name="PasswordRequired">
/// True when the firmware has a supervisor password set, so a write will be refused without it.
/// </param>
public sealed record FirmwareInterface(
    FirmwareAvailability Availability,
    string? Vendor,
    bool PasswordRequired,
    IReadOnlyList<FirmwareSetting> Settings,
    string? Problem = null)
{
    public static FirmwareInterface None(string? vendor = null) =>
        new(FirmwareAvailability.NoInterface, vendor, false, []);

    /// <summary>The interface answered, and it had something to say.</summary>
    public bool IsUsable =>
        Availability == FirmwareAvailability.Available && Settings.Count > 0 && Problem is null;

    /// <summary>
    /// Whether the answer is about the hardware or about this process.
    /// </summary>
    /// <remarks>
    /// The caller shows a different thing for each: a machine that genuinely cannot do this gets a
    /// menu path, and a process that was not allowed to ask gets a button that relaunches with the
    /// rights to ask properly.
    /// </remarks>
    public bool AnswerIsAboutTheMachine => Availability != FirmwareAvailability.NeedsElevation;
}

/// <param name="Verified">
/// True only when the firmware was read back afterwards and agreed. A vendor method that returns
/// "Success" is a claim, not evidence (spec 6.4).
/// </param>
/// <param name="RestartRequired">
/// Almost always true. Firmware records the new value at once; it acts on it at the next start.
/// </param>
public sealed record FirmwareWriteResult(
    string Name,
    string Wanted,
    bool Applied,
    bool Verified,
    bool RestartRequired,
    string? Problem = null);

/// <summary>
/// Reads and writes firmware settings through the manufacturer's own interface.
/// </summary>
/// <remarks>
/// <para>
/// Never by writing UEFI <c>Setup</c> variables at an offset. That technique is undocumented,
/// differs between BIOS builds of the same model, and the failure mode is a machine that does not
/// start (spec decision 18). Only interfaces the manufacturer publishes and supports.
/// </para>
/// <para>
/// This is deliberately not an action manifest, and the State Compiler never selects it. The gate
/// in <c>data/actions/pending-verification/</c> is about a firmware write the compiler picks on its
/// own inside a plan; this is a person choosing one setting, being told what it costs, and saying
/// yes. Those are different risks and they get different rules (ADR 0007).
/// </para>
/// </remarks>
public interface IFirmwareSettings
{
    Task<FirmwareInterface> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one setting, then reads the firmware back to see whether it took.
    /// </summary>
    /// <param name="password">
    /// The firmware supervisor password, when one is set. Passed straight to the vendor call and
    /// never stored, logged, or put in evidence.
    /// </param>
    Task<FirmwareWriteResult> SetAsync(
        FirmwareSetting setting,
        string value,
        string? password = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What a firmware setting costs if it is wrong, worked out from its name.
/// </summary>
/// <remarks>
/// <para>
/// By name because there is nothing else. Vendors publish no risk metadata, the names differ
/// between manufacturers, and a machine can carry settings this table has never heard of. So the
/// table matches on what the names have in common across vendors, and anything it does not
/// recognise is <see cref="FirmwareRisk.Careful"/> — never Routine. Guessing that an unknown
/// setting is harmless is the guess that breaks a machine.
/// </para>
/// <para>
/// Kept in code rather than in <c>data/</c> on purpose. Shipped data is reviewed, but it is also
/// editable by anyone who can reach the folder, and this table is the difference between a
/// confirmation dialog and a machine that will not boot.
/// </para>
/// </remarks>
public static class FirmwareRiskTable
{
    /// <summary>
    /// Changes whose damage cannot be undone by putting the setting back.
    /// </summary>
    /// <remarks>
    /// Clearing the security chip destroys the keys BitLocker, Windows Hello and any stored
    /// certificate are built on — setting it back afterwards gives you a working chip with none of
    /// the old keys in it. A firmware password set through an automated path locks out an owner who
    /// mistyped it once, with no reset that does not involve the manufacturer.
    /// </remarks>
    private static readonly string[] NeverWrite =
    [
        "clearsecuritychip", "cleartpm", "tpmclear", "clearstaticpassword",
        "password", "passphrase", "physicalpresence", "resettodefault", "loadsetupdefaults",
        "wipesequence", "wipedevice", "flashwrite", "flashinglinux", "secureflash",
    ];

    /// <summary>
    /// Can stop the machine starting, or put encrypted data behind a key the owner has to find.
    /// </summary>
    private static readonly string[] Serious =
    [
        "secureboot", "securebootmode", "bootmode", "uefilegacyboot", "csmsupport", "legacyboot",
        "securitychip", "tpm", "tcgsecurity", "sataoperation", "satacontrollermode", "sataraid",
        "vmdcontroller", "raid", "bootorder", "bootpriority", "startupsequence", "devicegaurd",
        "devicegaurdvirtualization", "keyboardlayout", "diskencryption", "opalsecurity", "nvmeraid",
    ];

    /// <summary>Changes how the machine behaves, but a wrong choice is put back by choosing again.</summary>
    private static readonly string[] Careful =
    [
        "virtualization", "vtd", "vtx", "amdv", "svm", "iommu", "hyperthreading", "cpucore",
        "wakeonlan", "wakeup", "usbboot", "networkboot", "pxe", "thunderbolt", "camera",
        "microphone", "bluetooth", "wireless", "fingerprint", "fastboot", "bootup", "quickboot",
    ];

    /// <summary>Cosmetic, or a preference with no effect on whether the machine starts.</summary>
    private static readonly string[] Routine =
    [
        "bootlogo", "logodisplay", "splash", "numlock", "keyclick", "fnkey", "hotkeymode",
        "fanspeed", "fancontrol", "adaptivethermal", "displaybrightness", "poweronbeep",
    ];

    /// <summary>
    /// Classifies a setting by its vendor name.
    /// </summary>
    /// <remarks>
    /// Longest match wins, so <c>ClearSecurityChip</c> is refused rather than merely serious for
    /// containing <c>SecurityChip</c> — the two differ by exactly the word that makes one of them
    /// permanent, and a first-match-wins scan over an unordered table would decide that by luck.
    /// </remarks>
    public static FirmwareRisk For(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        string normalised = Normalise(name);

        (string Token, FirmwareRisk Risk)? best = null;

        foreach ((string[] tokens, FirmwareRisk risk) in
            new[]
            {
                (NeverWrite, FirmwareRisk.Refused),
                (Serious, FirmwareRisk.Serious),
                (Careful, FirmwareRisk.Careful),
                (Routine, FirmwareRisk.Routine),
            })
        {
            foreach (string token in tokens)
            {
                if (normalised.Contains(token, StringComparison.Ordinal)
                    && (best is null || token.Length > best.Value.Token.Length))
                {
                    best = (token, risk);
                }
            }
        }

        // Never Routine by default. A setting nobody has classified is one nobody has thought
        // about, and the safe reading of "we do not know what this does" is not "it is harmless".
        return best?.Risk ?? FirmwareRisk.Careful;
    }

    /// <summary>Lower-case, letters and digits only, so spacing and punctuation cannot hide a match.</summary>
    private static string Normalise(string name) =>
        string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}

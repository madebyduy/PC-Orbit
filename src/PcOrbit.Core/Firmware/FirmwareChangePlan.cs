using PcOrbit.Core.Model;
using PcOrbit.Core.Preflight;

namespace PcOrbit.Core.Firmware;

/// <summary>
/// What has to be true, and what has to be said, before one firmware setting is written.
/// </summary>
/// <remarks>
/// <para>
/// Kept out of the UI and out of the adapter on purpose. The adapter's job is to talk to the
/// vendor; the UI's job is to render. Deciding whether a change is allowed to be offered, what the
/// person must be told first, and how hard the confirmation has to be, is a decision — and a
/// decision that lives in a dialog handler is a decision nothing can test.
/// </para>
/// <para>
/// The rule that shapes it: this product may not put a machine somewhere its owner cannot get it
/// back from. Turning Secure Boot off with BitLocker on does not break anything, but the next start
/// asks for a 48-digit key most people have never seen. That is not a reason to forbid the change —
/// it is their machine — it is a reason they must not meet that screen by surprise.
/// </para>
/// </remarks>
public sealed record FirmwareChangePlan(
    FirmwareSetting Setting,
    string Wanted,
    FirmwareRisk Risk,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Consequences,
    bool NeedsTypedConfirmation,
    bool NeedsPassword)
{
    public bool CanProceed => Blockers.Count == 0;

    /// <summary>
    /// Works out the plan for one change against the machine as it was last read.
    /// </summary>
    /// <param name="snapshot">
    /// Null when there is no scan to check against — which is itself a blocker. A firmware change
    /// decided without knowing whether the disk is encrypted is the one this must not wave through.
    /// </param>
    public static FirmwareChangePlan For(
        FirmwareSetting setting,
        string wanted,
        StateSnapshot? snapshot,
        bool firmwarePasswordSet)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentException.ThrowIfNullOrWhiteSpace(wanted);

        List<string> blockers = [];
        List<string> consequences = [];

        if (setting.Risk == FirmwareRisk.Refused)
        {
            blockers.Add("firmware.blocked.refused");
        }

        if (!setting.Accepts(wanted))
        {
            blockers.Add("firmware.blocked.notOffered");
        }

        if (string.Equals(setting.Current, wanted, StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add("firmware.blocked.alreadySet");
        }

        if (snapshot is null)
        {
            blockers.Add("firmware.blocked.noScan");

            return new FirmwareChangePlan(
                setting, wanted, setting.Risk, blockers, consequences,
                NeedsTypedConfirmation: setting.Risk == FirmwareRisk.Serious,
                NeedsPassword: firmwarePasswordSet);
        }

        // Spec 10.3, and the README in pending-verification asks for it with no exception. Anything
        // that changes the measurements a TPM seals against — Secure Boot, the security chip, the
        // boot mode — makes an encrypted volume ask for its recovery key at the next start.
        if (TouchesMeasuredBoot(setting.Name))
        {
            CapabilityValue encryption = snapshot.ValueOf(CoreCapabilities.BitLockerSystemDrive);

            if (encryption.Status == CapabilityStatus.Enabled)
            {
                consequences.Add("firmware.consequence.bitlockerKey");
            }
            else if (!encryption.IsKnown)
            {
                // Not a blocker: unreadable encryption is common without administrator rights, and
                // refusing every firmware change on that basis would refuse most of them. It is
                // said instead, which is the honest form of "we could not rule this out".
                consequences.Add("firmware.consequence.bitlockerUnknown");
            }
        }

        if (setting.Risk is FirmwareRisk.Serious)
        {
            consequences.Add("firmware.consequence.mayNotBoot");
        }

        consequences.Add("firmware.consequence.restart");

        if (firmwarePasswordSet)
        {
            consequences.Add("firmware.consequence.password");
        }

        return new FirmwareChangePlan(
            setting,
            wanted,
            setting.Risk,
            blockers,
            consequences,
            NeedsTypedConfirmation: setting.Risk == FirmwareRisk.Serious,
            NeedsPassword: firmwarePasswordSet);
    }

    /// <summary>
    /// Whether this setting feeds the measurements a TPM seals an encrypted disk against.
    /// </summary>
    /// <remarks>
    /// Deliberately broader than the three obvious names. A setting this misses costs the user a
    /// surprise recovery-key prompt; a setting it includes needlessly costs them one extra sentence
    /// in a dialog. Those are not the same size of mistake.
    /// </remarks>
    private static bool TouchesMeasuredBoot(string name)
    {
        string normalised = string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

        string[] measured =
        [
            "secureboot", "bootmode", "csm", "legacy", "uefi", "securitychip", "tpm", "tcg",
            "sataoperation", "satacontroller", "raid", "vmd", "devicegaurd", "deviceguard",
        ];

        return measured.Any(m => normalised.Contains(m, StringComparison.Ordinal));
    }
}

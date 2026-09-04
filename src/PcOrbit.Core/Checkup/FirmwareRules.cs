using System.Globalization;
using PcOrbit.Core.Model;
using PcOrbit.Core.Preflight;

namespace PcOrbit.Core.Checkup;

/// <summary>
/// Where this machine's firmware updates come from, and how to reach them.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a download and emphatically not a flash. Spec decision 18 rules out writing
/// firmware this product has not verified on real hardware, and the reason is not caution for its
/// own sake: a firmware write that goes wrong does not produce an error message, it produces a
/// machine that will not turn on.
/// </para>
/// <para>
/// What is left is still worth having, because it is the part people get wrong on their own. Which
/// exact model this is. Whether Windows Update can deliver firmware here at all — most consumer
/// machines publish no EFI System Resource Table, so "check for updates" will never bring one, and
/// nobody tells the owner that. And where the manufacturer's page for this model actually is,
/// because searching for it is how people end up on a driver-pack site with malware in it.
/// </para>
/// </remarks>
public static class VendorSupport
{
    /// <summary>
    /// The manufacturer's own support entry point, or null when we do not know the vendor.
    /// </summary>
    /// <remarks>
    /// Vendor landing pages only, never a deep link built from a guessed serial or model path. A
    /// constructed URL that 404s sends the user to a search engine, which is exactly the journey
    /// this is meant to prevent — so we take them to the manufacturer's front door, which is stable.
    /// </remarks>
    public static string? SupportUrlFor(MachineIdentity machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        string vendor = $"{machine.SystemVendor} {machine.BaseBoardVendor}";

        return Match(vendor, "lenovo") ? "https://support.lenovo.com/"
            : Match(vendor, "dell") ? "https://www.dell.com/support/home/"
            : Match(vendor, "hp", "hewlett") ? "https://support.hp.com/"
            : Match(vendor, "asus", "asustek") ? "https://www.asus.com/support/"
            : Match(vendor, "acer") ? "https://www.acer.com/support/"
            : Match(vendor, "msi", "micro-star") ? "https://www.msi.com/support/"
            : Match(vendor, "gigabyte") ? "https://www.gigabyte.com/Support"
            : Match(vendor, "asrock") ? "https://www.asrock.com/support/"
            : Match(vendor, "microsoft") ? "https://support.microsoft.com/surface"
            : Match(vendor, "samsung") ? "https://www.samsung.com/support/"
            : Match(vendor, "lg") ? "https://www.lg.com/support"
            : null;

        static bool Match(string haystack, params string[] needles) =>
            needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The BIOS on this machine is old enough to be worth a look.
/// </summary>
/// <remarks>
/// <para>
/// Information, never a warning, and never with an Apply button. An old BIOS is not a fault: plenty
/// of machines run their shipping firmware for their whole life without trouble, and telling
/// someone their PC has a problem because of a date would be exactly the invented urgency this
/// product exists not to have.
/// </para>
/// <para>
/// What makes it worth saying at all is the second half — that on a machine with no EFI System
/// Resource Table, Windows Update will never deliver a firmware update, so an owner waiting for one
/// is waiting for something that cannot arrive.
/// </para>
/// </remarks>
public sealed class FirmwareAdvisoryRule : ICheckupRule
{
    /// <summary>
    /// Three years. Long enough that vendors have usually shipped security fixes since — the UEFI
    /// certificate expiry beginning in 2026 is the current example — and long enough not to fire on
    /// a machine bought last year.
    /// </summary>
    private const int OldAfterDays = 365 * 3;

    public string Code => "firmware.bios-old";

    public IEnumerable<Finding> Evaluate(CheckupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CapabilityReading? age = context.Snapshot.ReadingOf(CoreCapabilities.BiosAgeDays);

        if (age is null
            || !RuleHelpers.TryNumber(age.Value, out double days)
            || days < OldAfterDays)
        {
            yield break;
        }

        CapabilityValue delivery = context.Snapshot.ValueOf(CoreCapabilities.FirmwareUpdateDelivery);
        CapabilityValue version = context.Snapshot.ValueOf(CoreCapabilities.BiosVersion);

        // Whether Windows Update can even carry a firmware update here decides which of two very
        // different sentences the user reads, and only one of them ends in "so nothing will arrive
        // on its own".
        bool viaWindowsUpdate = delivery.Status == CapabilityStatus.Present;

        yield return new Finding(
            Code: Code,
            Severity: FindingSeverity.Info,
            TitleKey: "finding.firmware.bios-old.title",
            BenefitKey: viaWindowsUpdate
                ? "finding.firmware.bios-old.benefit.windowsUpdate"
                : "finding.firmware.bios-old.benefit.vendorOnly",
            SafetyKey: "finding.firmware.bios-old.safety",
            Arguments: RuleHelpers.Args(
                ("years", (days / 365d).ToString("0.#", CultureInfo.InvariantCulture)),
                ("version", version.IsKnown ? version.Canonical : "—"),
                ("model", context.Snapshot.Machine.DisplayName)),
            Evidence: age.Evidence,
            Capability: CoreCapabilities.BiosAgeDays,

            // No SuggestedActionId and no SuggestedOutcomeId, on purpose. There is nothing here for
            // this product to apply, and offering a button would be the beginning of pretending
            // otherwise (spec decision 18).
            Related: [CoreCapabilities.BiosVersion, CoreCapabilities.FirmwareUpdateDelivery]);
    }
}

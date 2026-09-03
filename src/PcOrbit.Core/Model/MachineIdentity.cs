using System.Globalization;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace PcOrbit.Core.Model;

public enum CpuVendor
{
    Unknown = 0,
    Intel,
    Amd,
    Qualcomm,
    Other,
}

/// <summary>Spec 18.4. What the app is allowed to promise for this machine.</summary>
public enum SupportTier
{
    /// <summary>Tier 0 — inventory and checkup only. No firmware writes, no guided path.</summary>
    ReadOnly = 0,

    /// <summary>Tier 3 — observe everything, guide with vendor-level instructions.</summary>
    GuidedGeneric,

    /// <summary>Tier 2 — observe everything, guide with instructions verified on this model.</summary>
    GuidedVerified,

    /// <summary>Tier 1 — observe everything and write firmware settings through a vendor API.</summary>
    Full,
}

/// <summary>
/// Enough about the machine to decide compatibility, choose guide data and correlate history
/// across reinstalls (spec 9.5 "hardware compatibility fingerprint").
/// </summary>
public sealed record MachineIdentity(
    string SystemVendor,
    string SystemModel,
    string BaseBoardVendor,
    string BaseBoardProduct,
    string BiosVersion,
    string CpuName,
    CpuVendor CpuVendor,
    int OsBuild,
    string OsEdition,
    bool IsLaptop,
    bool IsVirtualMachine)
{
    /// <summary>
    /// Stable id for "this hardware, this firmware, this OS build". Used to decide whether a
    /// stored plan, blueprint or guide-data verification still applies. Deliberately excludes
    /// anything user-identifying: no serial number, no machine name (spec 19.3).
    /// </summary>
    [JsonIgnore]
    public string Fingerprint
    {
        get
        {
            string material = string.Join(
                '|',
                SystemVendor,
                SystemModel,
                BaseBoardVendor,
                BaseBoardProduct,
                BiosVersion,
                CpuName,
                OsBuild.ToString(CultureInfo.InvariantCulture));

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            return Convert.ToHexStringLower(hash)[..16];
        }
    }

    /// <summary>Best label for the machine, for headers like "Your PC: ASUS TUF B650".</summary>
    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(SystemModel) && !IsGenericModel(SystemModel)
            ? $"{SystemVendor} {SystemModel}".Trim()
            : $"{BaseBoardVendor} {BaseBoardProduct}".Trim();

    private static bool IsGenericModel(string model) =>
        model.Contains("System Product Name", StringComparison.OrdinalIgnoreCase)
        || model.Contains("To be filled", StringComparison.OrdinalIgnoreCase)
        || model.Contains("Default string", StringComparison.OrdinalIgnoreCase);

    public static MachineIdentity Unknown { get; } = new(
        SystemVendor: "Unknown",
        SystemModel: "Unknown",
        BaseBoardVendor: "Unknown",
        BaseBoardProduct: "Unknown",
        BiosVersion: "Unknown",
        CpuName: "Unknown",
        CpuVendor: CpuVendor.Unknown,
        OsBuild: 0,
        OsEdition: "Unknown",
        IsLaptop: false,
        IsVirtualMachine: false);
}

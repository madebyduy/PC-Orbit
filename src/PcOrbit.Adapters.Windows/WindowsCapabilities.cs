using PcOrbit.Core.Model;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// The capability ids this adapter can read, parsed once.
/// </summary>
/// <remarks>
/// Kept next to the adapter rather than in the domain: which ids a given platform can observe is
/// an adapter fact. The domain only knows ids exist.
/// </remarks>
public static class WindowsCapabilities
{
    public static CapabilityId OsBuild { get; } = CapabilityId.Parse("os.build");
    public static CapabilityId OsEdition { get; } = CapabilityId.Parse("os.edition");
    public static CapabilityId Cpu { get; } = CapabilityId.Parse("hardware.cpu");
    public static CapabilityId CpuVirtualization { get; } = CapabilityId.Parse("cpu.virtualization");
    public static CapabilityId FirmwareVirtualization { get; } = CapabilityId.Parse("firmware.cpu.virtualization");
    public static CapabilityId SecureBoot { get; } = CapabilityId.Parse("firmware.secure-boot");
    public static CapabilityId TpmVersion { get; } = CapabilityId.Parse("firmware.tpm.version");
    public static CapabilityId TpmReady { get; } = CapabilityId.Parse("firmware.tpm.ready");
    public static CapabilityId BootMode { get; } = CapabilityId.Parse("firmware.boot-mode");
    public static CapabilityId Iommu { get; } = CapabilityId.Parse("firmware.iommu");
    public static CapabilityId BiosVersion { get; } = CapabilityId.Parse("firmware.bios.version");
    public static CapabilityId BiosAgeDays { get; } = CapabilityId.Parse("firmware.bios.age-days");
    public static CapabilityId FirmwareUpdateDelivery { get; } = CapabilityId.Parse("firmware.update-delivery");
    public static CapabilityId ResizableBar { get; } = CapabilityId.Parse("firmware.rebar");
    public static CapabilityId MemoryRatedSpeed { get; } = CapabilityId.Parse("memory.rated-speed");
    public static CapabilityId MemoryCurrentSpeed { get; } = CapabilityId.Parse("memory.current-speed");
    public static CapabilityId DisplayCurrentRefreshRate { get; } = CapabilityId.Parse("display.current-refresh-rate");
    public static CapabilityId DisplayMaxRefreshRate { get; } = CapabilityId.Parse("display.max-refresh-rate");
    public static CapabilityId FeatureVirtualMachinePlatform { get; } = CapabilityId.Parse("windows.feature.virtual-machine-platform");
    public static CapabilityId FeatureWsl { get; } = CapabilityId.Parse("windows.feature.wsl");
    public static CapabilityId FeatureHypervisorPlatform { get; } = CapabilityId.Parse("windows.feature.hypervisor-platform");
    public static CapabilityId FeatureHyperV { get; } = CapabilityId.Parse("windows.feature.hyper-v");
    public static CapabilityId FeatureSandbox { get; } = CapabilityId.Parse("windows.feature.sandbox");
    public static CapabilityId SystemRestore { get; } = CapabilityId.Parse("windows.system-restore");
    public static CapabilityId RecoveryEnvironment { get; } = CapabilityId.Parse("recovery.winre");
    public static CapabilityId RecoveryPartition { get; } = CapabilityId.Parse("recovery.partition");
    public static CapabilityId RestorePointAgeDays { get; } = CapabilityId.Parse("recovery.restore-point.age-days");
    public static CapabilityId WslInstalled { get; } = CapabilityId.Parse("wsl.installed");
    public static CapabilityId WslDefaultVersion { get; } = CapabilityId.Parse("wsl.default-version");
    public static CapabilityId BitLockerSystemDrive { get; } = CapabilityId.Parse("security.bitlocker.system-drive");
    public static CapabilityId OnBattery { get; } = CapabilityId.Parse("power.on-battery");
    public static CapabilityId BatteryPercent { get; } = CapabilityId.Parse("power.battery-percent");
    public static CapabilityId SystemDriveFreeGb { get; } = CapabilityId.Parse("storage.system-drive.free-gb");

    /// <summary>
    /// Capability id to the DISM/WMI feature name. One place, so the scanner and the executor can
    /// never disagree about which Windows component a capability means.
    /// </summary>
    public static IReadOnlyDictionary<CapabilityId, string> OptionalFeatureNames { get; } =
        new Dictionary<CapabilityId, string>
        {
            [FeatureVirtualMachinePlatform] = "VirtualMachinePlatform",
            [FeatureWsl] = "Microsoft-Windows-Subsystem-Linux",
            [FeatureHypervisorPlatform] = "HypervisorPlatform",
            [FeatureHyperV] = "Microsoft-Hyper-V-All",
            [FeatureSandbox] = "Containers-DisposableClientVM",
        };
}

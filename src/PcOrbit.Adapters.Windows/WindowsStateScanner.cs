using System.Globalization;
using System.Text.RegularExpressions;
using PcOrbit.Adapters.Windows.Interop;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Graph;
using PcOrbit.Core.Model;
using Sections = PcOrbit.Adapters.Windows.WindowsInventory.Sections;

namespace PcOrbit.Adapters.Windows;

/// <summary>
/// Reads this machine into a <see cref="StateSnapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// Observation only, and vendor independent — the layer spec 18.3 says must always be present.
/// Nothing here needs administrator rights except drive encryption and TPM state, and those
/// failures are reported as Unknown with the reason attached rather than guessed at.
/// </para>
/// <para>
/// Every reading carries the query that produced it. That is what lets Advanced mode answer
/// "how do you know?" and lets a support report be audited months later (spec 6.1, 6.4, 8.3.1).
/// </para>
/// </remarks>
public sealed partial class WindowsStateScanner : IStateScanner
{
    private readonly PowerShellRunner _powerShell;
    private readonly CapabilityGraph _graph;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    /// <summary>The clock reading for the scan in flight, so the static probes can use it.</summary>
    private static DateTimeOffset _lastScanAt;

    public WindowsStateScanner(
        CapabilityGraph graph,
        PowerShellRunner? powerShell = null,
        IClock? clock = null,
        IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        _graph = graph;
        _powerShell = powerShell ?? PowerShellRunner.Default;
        _clock = clock ?? SystemClock.Instance;
        _ids = ids ?? GuidIdGenerator.Instance;
    }

    public async Task<StateSnapshot> ScanAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.Now;

        PowerShellResult result = await _powerShell
            .RunAsync(WindowsInventory.Script, cancellationToken)
            .ConfigureAwait(false);

        using InventoryDocument inventory = result.Succeeded
            ? InventoryDocument.Parse(result.StandardOutput)
            : InventoryDocument.Parse(string.Empty);

        MachineIdentity machine = BuildIdentity(inventory);
        var builder = StateSnapshot.Builder(_ids.NewId("snap"), now, machine);

        AddOsReadings(builder, machine, inventory);
        _lastScanAt = now;
        AddCpuAndFirmwareReadings(builder, inventory);
        AddMemoryReadings(builder, inventory);
        AddFeatureReadings(builder, inventory);
        AddRecoveryReadings(builder, inventory, now);
        AddWslReadings(builder, inventory);
        AddSecurityReadings(builder, inventory);
        AddTweakReadings(builder);
        AddDisplayReadings(builder);
        AddPowerAndStorageReadings(builder);

        StateSnapshot observed = builder.Build();

        // Workloads are not readable facts; they are derived from everything above through the
        // same requires edges the compiler plans from (see WorkloadEvaluator).
        List<CapabilityReading> derived =
        [
            .. _graph.Nodes
                .Where(n => n.Kind == NodeKind.Workload)
                .Select(n => WorkloadEvaluator.Evaluate(n.Id, observed, _graph, now)),
        ];

        return new StateSnapshot(observed.Id, now, machine, [.. observed.Readings, .. derived]);
    }

    // ---------------------------------------------------------------- machine identity

    private static MachineIdentity BuildIdentity(InventoryDocument inventory)
    {
        (int build, string edition, _) = SystemInterop.ReadWindowsVersion();

        WindowsInventory.SystemInfo? system = inventory.Section<WindowsInventory.SystemInfo>(Sections.System);
        WindowsInventory.BoardInfo? board = inventory.Section<WindowsInventory.BoardInfo>(Sections.Board);
        WindowsInventory.BiosInfo? bios = inventory.Section<WindowsInventory.BiosInfo>(Sections.Bios);
        WindowsInventory.CpuInfo? cpu = inventory.Section<WindowsInventory.CpuInfo>(Sections.Cpu);

        string systemVendor = system?.Manufacturer ?? "Unknown";
        string systemModel = system?.Model ?? "Unknown";
        string cpuName = cpu?.Name?.Trim() ?? "Unknown";

        return new MachineIdentity(
            SystemVendor: systemVendor,
            SystemModel: systemModel,
            BaseBoardVendor: board?.Manufacturer ?? "Unknown",
            BaseBoardProduct: board?.Product ?? "Unknown",
            BiosVersion: bios?.SMBIOSBIOSVersion ?? "Unknown",
            CpuName: cpuName,
            CpuVendor: ClassifyCpu(cpu?.Manufacturer, cpuName),
            OsBuild: build,
            OsEdition: edition,

            // PCSystemType 2 is "Mobile", which is how Windows describes a laptop.
            IsLaptop: system?.PCSystemType == 2,
            IsVirtualMachine: LooksVirtual(systemVendor, systemModel));
    }

    private static CpuVendor ClassifyCpu(string? manufacturer, string name)
    {
        string haystack = $"{manufacturer} {name}";

        if (haystack.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase))
        {
            return CpuVendor.Intel;
        }

        if (haystack.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase))
        {
            return CpuVendor.Amd;
        }

        if (haystack.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("Snapdragon", StringComparison.OrdinalIgnoreCase))
        {
            return CpuVendor.Qualcomm;
        }

        return string.IsNullOrWhiteSpace(manufacturer) ? CpuVendor.Unknown : CpuVendor.Other;
    }

    private static bool LooksVirtual(string vendor, string model)
    {
        string[] markers = ["VMware", "VirtualBox", "innotek", "QEMU", "Parallels", "Xen", "Virtual Machine", "Hyper-V"];
        string haystack = $"{vendor} {model}";

        return markers.Any(m => haystack.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- OS

    private static void AddOsReadings(
        StateSnapshot.SnapshotBuilder builder,
        MachineIdentity machine,
        InventoryDocument inventory)
    {
        var registry = new Evidence(
            EvidenceSourceKind.Registry,
            @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            Confidence.High,
            Query: "CurrentBuildNumber, EditionID");

        builder.Add(
            WindowsCapabilities.OsBuild,
            machine.OsBuild == 0 ? CapabilityValue.Unknown : CapabilityValue.Scalar(machine.OsBuild),
            machine.OsBuild == 0 ? Evidence.Missing("CurrentBuildNumber could not be read") : registry);

        builder.Add(WindowsCapabilities.OsEdition, CapabilityValue.Scalar(machine.OsEdition), registry);

        builder.Add(
            WindowsCapabilities.Cpu,
            machine.CpuName == "Unknown" ? CapabilityValue.Unknown : CapabilityValue.Scalar(machine.CpuName),
            Wmi("Win32_Processor.Name", machine.CpuName == "Unknown" ? null : machine.CpuName, inventory, Sections.Cpu));
    }

    // ---------------------------------------------------------------- CPU and firmware

    private static void AddCpuAndFirmwareReadings(StateSnapshot.SnapshotBuilder builder, InventoryDocument inventory)
    {
        WindowsInventory.CpuInfo? cpu = inventory.Section<WindowsInventory.CpuInfo>(Sections.Cpu);
        WindowsInventory.SystemInfo? system = inventory.Section<WindowsInventory.SystemInfo>(Sections.System);
        bool hypervisorRunning = system?.HypervisorPresent == true;

        // A hypervisor cannot run at all without hardware virtualization enabled in firmware. When
        // one is running, Win32_Processor reports these as false because the extensions are already
        // claimed — the classic "I turned it on but the app still says off" (spec 8.3.4). Believing
        // the raw flag here would send the user into their BIOS to fix something already correct.
        if (hypervisorRunning)
        {
            var evidence = new Evidence(
                EvidenceSourceKind.Wmi,
                "Win32_ComputerSystem.HypervisorPresent is true, and no hypervisor can start without firmware virtualization",
                Confidence.High,
                Query: "Win32_ComputerSystem.HypervisorPresent",
                RawResult: "True");

            builder.Add(WindowsCapabilities.CpuVirtualization, CapabilityValue.Supported, evidence);
            builder.Add(WindowsCapabilities.FirmwareVirtualization, CapabilityValue.Enabled, evidence);
        }
        else
        {
            builder.Add(
                WindowsCapabilities.CpuVirtualization,
                cpu?.VMMonitorModeExtensions switch
                {
                    true => CapabilityValue.Supported,
                    false => CapabilityValue.NotSupported,
                    null => CapabilityValue.Unknown,
                },
                Wmi("Win32_Processor.VMMonitorModeExtensions", cpu?.VMMonitorModeExtensions, inventory, Sections.Cpu));

            builder.Add(
                WindowsCapabilities.FirmwareVirtualization,
                cpu?.VirtualizationFirmwareEnabled switch
                {
                    true => CapabilityValue.Enabled,
                    false => CapabilityValue.Disabled,
                    null => CapabilityValue.Unknown,
                },

                // Same source Task Manager uses, which matters: if we disagree with Task Manager
                // the user will trust Task Manager, and they should be able to.
                Wmi("Win32_Processor.VirtualizationFirmwareEnabled", cpu?.VirtualizationFirmwareEnabled, inventory, Sections.Cpu));
        }

        AddSecureBootReading(builder, inventory);
        AddTpmReadings(builder, inventory);
        AddBootModeReading(builder);
        AddFirmwareDeliveryReadings(builder, inventory, _lastScanAt);
        AddIommuReading(builder, inventory);

        WindowsInventory.BiosInfo? bios = inventory.Section<WindowsInventory.BiosInfo>(Sections.Bios);

        builder.Add(
            WindowsCapabilities.BiosVersion,
            string.IsNullOrWhiteSpace(bios?.SMBIOSBIOSVersion)
                ? CapabilityValue.Unknown
                : CapabilityValue.Scalar(bios.SMBIOSBIOSVersion),
            Wmi("Win32_BIOS.SMBIOSBIOSVersion", bios?.SMBIOSBIOSVersion, inventory, Sections.Bios));

        // Spec 8.3.1 lists Resizable BAR as readable only through the GPU driver API, which v0.1
        // does not talk to. Declared and Unknown, rather than quietly missing from the model.
        builder.Add(
            WindowsCapabilities.ResizableBar,
            CapabilityValue.Unknown,
            Evidence.Missing("Reading Resizable BAR needs the NVIDIA/AMD driver API or GPU PCI config space, which this version does not use"));
    }

    /// <summary>
    /// Secure Boot from the cmdlet if we can, from the registry if we cannot.
    /// </summary>
    /// <remarks>
    /// <c>Confirm-SecureBootUEFI</c> needs administrator rights;
    /// <c>Control\SecureBoot\State\UEFISecureBootEnabled</c> does not. Spec 8.3.1 lists both
    /// sources, and spec 21.10 wants a standard user to get the same scan — so we try both and
    /// record which one answered.
    /// </remarks>
    private static void AddSecureBootReading(StateSnapshot.SnapshotBuilder builder, InventoryDocument inventory)
    {
        bool? fromCmdlet = inventory.Bool(Sections.SecureBoot);
        int? fromRegistry = inventory.Int(Sections.SecureBootRegistry);

        if (fromCmdlet is { } confirmed)
        {
            builder.Add(
                WindowsCapabilities.SecureBoot,
                confirmed ? CapabilityValue.Enabled : CapabilityValue.Disabled,
                new Evidence(
                    EvidenceSourceKind.PowerShell,
                    "Confirm-SecureBootUEFI",
                    Confidence.High,
                    Query: "Confirm-SecureBootUEFI",
                    RawResult: confirmed.ToString()));
            return;
        }

        if (fromRegistry is { } value)
        {
            builder.Add(
                WindowsCapabilities.SecureBoot,
                value == 1 ? CapabilityValue.Enabled : CapabilityValue.Disabled,
                new Evidence(
                    EvidenceSourceKind.Registry,
                    @"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled",
                    Confidence.High,
                    RawResult: value.ToString(CultureInfo.InvariantCulture)));
            return;
        }

        builder.Add(
            WindowsCapabilities.SecureBoot,
            CapabilityValue.Unknown,
            Evidence.Missing(
                "Confirm-SecureBootUEFI needs administrator rights and the SecureBoot registry key is absent, "
                + "which is normal on a machine that boots in legacy BIOS mode"));
    }

    /// <summary>
    /// The TPM, from the privileged class when we have the rights and from the device tree when we
    /// do not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Win32_Tpm</c> needs administrator rights, and for a long time that meant this app told
    /// ordinary users "could not be read" about the one component Windows 11 turns on. It need not:
    /// the TPM is also an ordinary PnP device, <c>Win32_PnPEntity</c> is readable by anyone, and
    /// Windows' own friendly name for it carries the spec version verbatim — "Trusted Platform
    /// Module 2.0". <c>ConfigManagerErrorCode</c> 0 means Windows has the device started, which is
    /// what "enabled and activated" amounts to from outside the firmware.
    /// </para>
    /// <para>
    /// Confidence is graded accordingly: High from the privileged class, Medium from the device
    /// name. A reading with a stated source and an honest confidence beats an Unknown that is only
    /// Unknown because nobody looked for a second route (spec 6.1, 21.10).
    /// </para>
    /// </remarks>
    private static void AddTpmReadings(StateSnapshot.SnapshotBuilder builder, InventoryDocument inventory)
    {
        WindowsInventory.TpmInfo? tpm = inventory.Section<WindowsInventory.TpmInfo>(Sections.Tpm);
        WindowsInventory.TpmDeviceInfo? device = inventory.Section<WindowsInventory.TpmDeviceInfo>(Sections.TpmDevice);

        string? spec = tpm?.SpecVersion?.Split(',').FirstOrDefault()?.Trim();

        // "Trusted Platform Module 2.0" -> "2.0". Anchored to the end so a future name with a
        // build number in it does not silently produce a wrong version.
        string? fromName = device?.Name is { } name
            ? TpmVersionInName().Match(name) is { Success: true } m ? m.Groups[1].Value : null
            : null;

        (CapabilityValue value, Evidence evidence) version = spec is { Length: > 0 }
            ? (CapabilityValue.Scalar(spec), new Evidence(
                EvidenceSourceKind.Wmi,
                "Win32_Tpm.SpecVersion",
                Confidence.High,
                Query: @"root\CIMV2\Security\MicrosoftTpm:Win32_Tpm",
                RawResult: tpm?.SpecVersion))
            : fromName is { Length: > 0 }
                ? (CapabilityValue.Scalar(fromName), new Evidence(
                    EvidenceSourceKind.Wmi,
                    "Win32_PnPEntity — the version Windows itself prints in the device's name",
                    Confidence.Medium,
                    Query: "Win32_PnPEntity WHERE Name LIKE 'Trusted Platform Module%'",
                    RawResult: device?.Name))
                : (CapabilityValue.Unknown, Evidence.Missing(
                    inventory.ProblemFor(Sections.TpmDevice)
                    ?? "no Trusted Platform Module device is present in the device tree, and Win32_Tpm "
                       + "needs administrator rights — so this machine may simply have no TPM"));

        builder.Add(WindowsCapabilities.TpmVersion, version.value, version.evidence);

        // Both flags have to be present. Win32_Tpm can come back as an object whose properties are
        // all null when the caller lacks the rights to read them, and `x == true` on a null bool?
        // is false — which quietly turned "we could not read the TPM" into "the TPM is off", the
        // one inference this codebase is built to refuse (spec 6.6, 27.13).
        bool? ready = (tpm?.IsEnabled_InitialValue, tpm?.IsActivated_InitialValue) switch
        {
            (bool enabled, bool activated) => enabled && activated,
            _ => null,
        };

        if (ready is { } confirmed)
        {
            builder.Add(
                WindowsCapabilities.TpmReady,
                confirmed ? CapabilityValue.Enabled : CapabilityValue.Disabled,
                new Evidence(
                    EvidenceSourceKind.Wmi,
                    "Win32_Tpm.IsEnabled_InitialValue and IsActivated_InitialValue",
                    Confidence.High,
                    RawResult: confirmed.ToString()));

            return;
        }

        if (device?.ConfigManagerErrorCode is { } code)
        {
            builder.Add(
                WindowsCapabilities.TpmReady,
                code == 0 ? CapabilityValue.Enabled : CapabilityValue.Disabled,
                new Evidence(
                    EvidenceSourceKind.Wmi,
                    "Win32_PnPEntity.ConfigManagerErrorCode — Windows has the TPM device started, "
                    + "which it cannot do while the chip is switched off in firmware",
                    Confidence.Medium,
                    Query: "Win32_PnPEntity WHERE Name LIKE 'Trusted Platform Module%'",
                    RawResult: $"ConfigManagerErrorCode={code}"));

            return;
        }

        builder.Add(
            WindowsCapabilities.TpmReady,
            CapabilityValue.Unknown,
            Evidence.Missing(
                inventory.ProblemFor(Sections.TpmDevice)
                ?? "no TPM device is present in the device tree, and Win32_Tpm needs administrator rights"));
    }

    [GeneratedRegex(@"Trusted Platform Module\s+(\d+\.\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TpmVersionInName();

    /// <summary>
    /// Whether Windows can deliver firmware updates to this machine, and how old its BIOS is.
    /// </summary>
    /// <remarks>
    /// A machine with an entry under <c>Control\FirmwareResources</c> publishes an EFI System
    /// Resource Table, which is what lets Windows Update ship a UEFI capsule to it. A machine
    /// without one can only be updated from the vendor's own tool — and that is a fact worth
    /// stating plainly, because it decides whether "check for updates" will ever help.
    /// </remarks>
    private static void AddFirmwareDeliveryReadings(
        StateSnapshot.SnapshotBuilder builder,
        InventoryDocument inventory,
        DateTimeOffset now)
    {
        // Nulls filtered here as well as in the script: PowerShell's @($null) is a one-element
        // array, which briefly had this reporting "Windows Update can deliver firmware" on a
        // machine whose ESRT key does not exist at all.
        List<WindowsInventory.FirmwareResource> resources =
        [
            .. inventory.List<WindowsInventory.FirmwareResource>(Sections.Firmware)
                .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Version)),
        ];

        builder.Add(
            WindowsCapabilities.FirmwareUpdateDelivery,
            resources.Count > 0 ? CapabilityValue.Present : CapabilityValue.Absent,
            new Evidence(
                EvidenceSourceKind.Registry,
                resources.Count > 0
                    ? @"HKLM\SYSTEM\CurrentControlSet\Control\FirmwareResources — this machine publishes an "
                      + "EFI System Resource Table, so Windows Update can deliver firmware to it"
                    : @"HKLM\SYSTEM\CurrentControlSet\Control\FirmwareResources is absent, so this machine "
                      + "takes firmware only from the manufacturer's own tool",
                Confidence.High,
                RawResult: $"{resources.Count} firmware resource(s)"));

        WindowsInventory.BiosInfo? bios = inventory.Section<WindowsInventory.BiosInfo>(Sections.Bios);

        if (!DateTime.TryParse(bios?.ReleaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime released))
        {
            builder.Add(
                WindowsCapabilities.BiosAgeDays,
                CapabilityValue.Unknown,
                Evidence.Missing("Win32_BIOS.ReleaseDate was not reported by this machine"));

            return;
        }

        int ageDays = Math.Max(0, (int)(now.LocalDateTime - released).TotalDays);

        builder.Add(
            WindowsCapabilities.BiosAgeDays,
            CapabilityValue.Scalar(ageDays),
            new Evidence(
                EvidenceSourceKind.Wmi,
                "Win32_BIOS.ReleaseDate",
                Confidence.High,
                RawResult: released.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    private static void AddBootModeReading(StateSnapshot.SnapshotBuilder builder)
    {
        string? firmwareType = SystemInterop.ReadFirmwareType();

        builder.Add(
            WindowsCapabilities.BootMode,
            firmwareType is null ? CapabilityValue.Unknown : CapabilityValue.Scalar(firmwareType),
            firmwareType is null
                ? Evidence.Missing("GetFirmwareType returned nothing usable")
                : new Evidence(EvidenceSourceKind.Win32Api, "kernel32!GetFirmwareType", Confidence.High, RawResult: firmwareType));
    }

    private static void AddIommuReading(StateSnapshot.SnapshotBuilder builder, InventoryDocument inventory)
    {
        WindowsInventory.DeviceGuardInfo? guard =
            inventory.Section<WindowsInventory.DeviceGuardInfo>(Sections.DeviceGuard);

        List<int>? properties = guard?.AvailableSecurityProperties;

        // Documented mapping: element 3 is DMA protection, which cannot be offered without a
        // working IOMMU (VT-d / AMD-Vi).
        const int dmaProtection = 3;

        if (properties is not { Count: > 0 })
        {
            builder.Add(
                WindowsCapabilities.Iommu,
                CapabilityValue.Unknown,
                Evidence.Missing(
                    inventory.ProblemFor(Sections.DeviceGuard)
                    ?? "Win32_DeviceGuard reported no security properties. That namespace needs administrator rights"));
            return;
        }

        bool hasDma = properties.Contains(dmaProtection);

        builder.Add(
            WindowsCapabilities.Iommu,
            hasDma ? CapabilityValue.Enabled : CapabilityValue.Disabled,
            new Evidence(
                EvidenceSourceKind.Wmi,
                "Win32_DeviceGuard.AvailableSecurityProperties (3 = DMA protection, which needs an IOMMU)",
                hasDma ? Confidence.High : Confidence.Medium,
                Query: @"root\Microsoft\Windows\DeviceGuard:Win32_DeviceGuard",
                RawResult: string.Join(",", properties)));
    }

    // ---------------------------------------------------------------- memory

    private static void AddMemoryReadings(StateSnapshot.SnapshotBuilder builder, InventoryDocument inventory)
    {
        List<WindowsInventory.MemoryModule> modules = inventory.List<WindowsInventory.MemoryModule>(Sections.Memory);

        int rated = modules.Select(m => m.Speed ?? 0).DefaultIfEmpty(0).Max();
        int current = modules.Select(m => m.ConfiguredClockSpeed ?? 0).DefaultIfEmpty(0).Max();

        builder.Add(
            WindowsCapabilities.MemoryRatedSpeed,
            rated > 0 ? CapabilityValue.Scalar(rated) : CapabilityValue.Unknown,

            // Rated speed comes from the SPD table as reported by firmware. Some OEMs leave it
            // blank or copy the running speed into it, which is why this is High but not certain.
            rated > 0
                ? new Evidence(
                    EvidenceSourceKind.Wmi,
                    "Win32_PhysicalMemory.Speed (rated, from SPD via firmware)",
                    Confidence.High,
                    RawResult: rated.ToString(CultureInfo.InvariantCulture))
                : Evidence.Missing(inventory.ProblemFor(Sections.Memory) ?? "Win32_PhysicalMemory.Speed was not reported"));

        builder.Add(
            WindowsCapabilities.MemoryCurrentSpeed,
            current > 0 ? CapabilityValue.Scalar(current) : CapabilityValue.Unknown,
            current > 0
                ? new Evidence(
                    EvidenceSourceKind.Wmi,
                    "Win32_PhysicalMemory.ConfiguredClockSpeed (running)",
                    Confidence.High,
                    RawResult: current.ToString(CultureInfo.InvariantCulture))
                : Evidence.Missing(inventory.ProblemFor(Sections.Memory) ?? "Win32_PhysicalMemory.ConfiguredClockSpeed was not reported"));
    }

    // ---------------------------------------------------------------- Windows features

    private static void AddFeatureReadings(StateSnapshot.SnapshotBuilder builder, InventoryDocument inventory)
    {
        List<WindowsInventory.OptionalFeature> features =
            inventory.List<WindowsInventory.OptionalFeature>(Sections.Features);

        foreach ((CapabilityId capability, string featureName) in WindowsCapabilities.OptionalFeatureNames)
        {
            WindowsInventory.OptionalFeature? feature = features
                .FirstOrDefault(f => string.Equals(f.Name, featureName, StringComparison.OrdinalIgnoreCase));

            // InstallState: 1 enabled, 2 disabled, 3 absent (payload removed), 4 unknown.
            CapabilityValue value = feature?.InstallState switch
            {
                1 => CapabilityValue.Enabled,
                2 or 3 => CapabilityValue.Disabled,
                _ => CapabilityValue.Unknown,
            };

            Evidence evidence = feature is null
                ? Evidence.Missing(
                    inventory.ProblemFor(Sections.Features)
                    ?? $"'{featureName}' is not listed in Win32_OptionalFeature on this machine — some Windows editions do not offer it")
                : new Evidence(
                    EvidenceSourceKind.Wmi,
                    "Win32_OptionalFeature.InstallState",
                    Confidence.High,
                    Query: $"Win32_OptionalFeature WHERE Name='{featureName}'",
                    RawResult: feature.InstallState?.ToString(CultureInfo.InvariantCulture));

            builder.Add(capability, value, evidence);
        }

        AddSystemRestoreReading(builder, inventory);
    }

    private static void AddSystemRestoreReading(StateSnapshot.SnapshotBuilder builder, InventoryDocument inventory)
    {
        int? disabled = inventory.Int(Sections.RestoreDisabled);
        int? points = inventory.Int(Sections.RestorePointCount);

        // Explicitly disabled is solid. Otherwise, existing restore points prove it has been on.
        // Neither signal means we do not know, and we do not raise a finding from that.
        (CapabilityValue value, Evidence evidence) = (disabled, points) switch
        {
            (1, _) => (
                CapabilityValue.Disabled,
                new Evidence(
                    EvidenceSourceKind.Registry,
                    @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore\DisableSR = 1",
                    Confidence.High)),
            (_, > 0) => (
                CapabilityValue.Enabled,
                new Evidence(
                    EvidenceSourceKind.PowerShell,
                    $"Get-ComputerRestorePoint returned {points} restore point(s)",
                    Confidence.Medium)),
            _ => (
                CapabilityValue.Unknown,
                Evidence.Missing(
                    "no DisableSR value and no restore points, so System Restore state is genuinely unclear. "
                    + "Reading the protection setting directly needs administrator rights")),
        };

        builder.Add(WindowsCapabilities.SystemRestore, value, evidence);
    }

    // ---------------------------------------------------------------- recovery readiness

    /// <summary>
    /// Whether this machine can still rescue itself: a working recovery environment, a recovery
    /// partition behind it, and a restore point recent enough to be worth rolling back to.
    /// </summary>
    /// <remarks>
    /// Every source here is restricted to administrators. That is the whole reason these readings
    /// are careful: a standard-user scan that reported "no recovery environment" would be telling
    /// someone their safety net is gone on the strength of a permission error (spec 6.6, 27.13).
    /// So each one reports Unknown and names the rights it needed.
    /// </remarks>
    private static void AddRecoveryReadings(
        StateSnapshot.SnapshotBuilder builder,
        InventoryDocument inventory,
        DateTimeOffset now)
    {
        AddWinreReading(builder, inventory);

        bool? partition = inventory.Bool(Sections.RecoveryPartition);

        builder.Add(
            WindowsCapabilities.RecoveryPartition,
            partition switch
            {
                true => CapabilityValue.Present,
                false => CapabilityValue.Absent,
                null => CapabilityValue.Unknown,
            },
            partition is null
                ? Evidence.Missing(
                    inventory.ProblemFor(Sections.RecoveryPartition)
                    ?? "Get-Partition could not enumerate this machine's partitions, so whether a recovery "
                       + "partition exists is genuinely unknown")
                : new Evidence(
                    EvidenceSourceKind.PowerShell,
                    "Get-Partition, looking for a partition of type 'Recovery'",
                    Confidence.High,
                    Query: "Get-Partition | Where-Object Type -eq 'Recovery'",
                    RawResult: partition.Value.ToString()));

        AddRestorePointAgeReading(builder, inventory, now);
    }

    private static void AddWinreReading(StateSnapshot.SnapshotBuilder builder, InventoryDocument inventory)
    {
        WindowsInventory.WinreInfo? winre = inventory.Section<WindowsInventory.WinreInfo>(Sections.Winre);

        const string nullGuid = "{00000000-0000-0000-0000-000000000000}";

        if (winre is null)
        {
            builder.Add(
                WindowsCapabilities.RecoveryEnvironment,
                CapabilityValue.Unknown,
                Evidence.Missing(
                    inventory.ProblemFor(Sections.Winre)
                    ?? @"System32\Recovery\ReAgent.xml could not be read. That folder is restricted to "
                       + "administrators, so this is not evidence that the recovery environment is missing"));
            return;
        }

        bool registered = !string.IsNullOrWhiteSpace(winre.BcdId)
            && !string.Equals(winre.BcdId, nullGuid, StringComparison.OrdinalIgnoreCase);

        bool hasImage = !string.IsNullOrWhiteSpace(winre.Location);

        // A staged update leaves WinRE half-replaced. It is registered and it has a location, and
        // it still will not boot — so it is reported as its own state rather than as Enabled.
        bool staged = string.Equals(winre.Staged, "1", StringComparison.Ordinal);

        (CapabilityValue value, string raw) = (registered && hasImage, staged) switch
        {
            (true, true) => (CapabilityValue.Scalar("staged"), "registered, image present, update part-applied"),
            (true, false) => (CapabilityValue.Enabled, "registered with a WinRE image"),
            (false, _) => (CapabilityValue.Disabled, $"WinreBCD='{winre.BcdId}', WinreLocation='{winre.Location}'"),
        };

        builder.Add(
            WindowsCapabilities.RecoveryEnvironment,
            value,

            // Medium, not High: this is the configuration reagentc reports on rather than reagentc
            // itself, and the mapping from these two fields to "Enabled" is read off the file's
            // documented meaning rather than from an API that answers the question directly.
            new Evidence(
                EvidenceSourceKind.PowerShell,
                @"System32\Recovery\ReAgent.xml — WinRE is enabled when it is registered to a boot entry and has an image",
                Confidence.Medium,
                Query: @"[xml] Get-Content $env:SystemRoot\System32\Recovery\ReAgent.xml",
                RawResult: raw));
    }

    private static void AddRestorePointAgeReading(
        StateSnapshot.SnapshotBuilder builder,
        InventoryDocument inventory,
        DateTimeOffset now)
    {
        string? latest = inventory.String(Sections.RestorePointLatest);

        if (!DateTime.TryParse(latest, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime created))
        {
            builder.Add(
                WindowsCapabilities.RestorePointAgeDays,
                CapabilityValue.Unknown,
                Evidence.Missing(
                    inventory.ProblemFor(Sections.RestorePointLatest)
                    ?? "no restore point was returned. Get-ComputerRestorePoint needs administrator rights, so "
                       + "this is not evidence that no restore point exists"));
            return;
        }

        // Clamped at zero: a restore point stamped in the future is a clock problem, and a negative
        // age would make every rule downstream read as "brand new".
        int ageDays = Math.Max(0, (int)(now.LocalDateTime - created).TotalDays);

        builder.Add(
            WindowsCapabilities.RestorePointAgeDays,
            CapabilityValue.Scalar(ageDays),
            new Evidence(
                EvidenceSourceKind.PowerShell,
                "Get-ComputerRestorePoint, newest CreationTime",
                Confidence.High,
                Query: "Get-ComputerRestorePoint | Sort-Object CreationTime -Descending | Select-Object -First 1",
                RawResult: created.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
    }

    // ---------------------------------------------------------------- WSL

    private static void AddWslReadings(StateSnapshot.SnapshotBuilder builder, InventoryDocument inventory)
    {
        bool? present = inventory.Bool(Sections.WslPresent);

        builder.Add(
            WindowsCapabilities.WslInstalled,
            present switch
            {
                true => CapabilityValue.Present,
                false => CapabilityValue.Absent,
                null => CapabilityValue.Unknown,
            },
            present is null
                ? Evidence.Missing(inventory.ProblemFor(Sections.WslPresent) ?? "could not test for wsl.exe")
                : new Evidence(
                    EvidenceSourceKind.PowerShell,
                    @"Test-Path %SystemRoot%\System32\wsl.exe",
                    Confidence.High,
                    RawResult: present.ToString()));

        int? version = inventory.Int(Sections.WslDefaultVersion);

        builder.Add(
            WindowsCapabilities.WslDefaultVersion,
            version is { } v ? CapabilityValue.Scalar(v) : CapabilityValue.Unknown,

            // No registry value means the default was never chosen explicitly. That is genuinely
            // unknown rather than "1": the plan will then include the set-default-version step,
            // which is idempotent and costs nothing if it was already 2.
            version is null
                ? Evidence.Missing(
                    @"HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss\DefaultVersion is not set, "
                    + "so the WSL default version has never been chosen explicitly")
                : new Evidence(
                    EvidenceSourceKind.Registry,
                    @"HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss\DefaultVersion",
                    Confidence.High,
                    RawResult: version.Value.ToString(CultureInfo.InvariantCulture)));
    }

    // ---------------------------------------------------------------- security

    private static void AddSecurityReadings(StateSnapshot.SnapshotBuilder builder, InventoryDocument inventory)
    {
        WindowsInventory.EncryptionInfo? encryption =
            inventory.Section<WindowsInventory.EncryptionInfo>(Sections.Encryption);

        // ProtectionStatus 1 = on. 0 with ConversionStatus 1 (fully encrypted) is the documented
        // signature of a suspended volume, which matters: suspended is not the same as off, and
        // the difference decides whether a firmware change will demand a recovery key.
        (CapabilityValue value, Evidence evidence) = encryption switch
        {
            { ProtectionStatus: 1 } => (CapabilityValue.Scalar("on"), Encrypted("ProtectionStatus=1")),
            { ProtectionStatus: 0, ConversionStatus: 1 } => (
                CapabilityValue.Scalar("suspended"),
                Encrypted("ProtectionStatus=0 with ConversionStatus=1")),
            { ProtectionStatus: 0 } => (CapabilityValue.Scalar("off"), Encrypted("ProtectionStatus=0")),
            _ => (
                CapabilityValue.Unknown,
                Evidence.Missing(
                    inventory.ProblemFor(Sections.Encryption)
                    ?? "Win32_EncryptableVolume could not be read. It needs administrator rights, so this is "
                       + "not evidence that the drive is unencrypted")),
        };

        builder.Add(WindowsCapabilities.BitLockerSystemDrive, value, evidence);

        static Evidence Encrypted(string raw) => new(
            EvidenceSourceKind.Wmi,
            @"root\CIMV2\Security\MicrosoftVolumeEncryption:Win32_EncryptableVolume",
            Confidence.High,
            Query: "Win32_EncryptableVolume WHERE DriveLetter=%SystemDrive%",
            RawResult: raw);
    }

    // ---------------------------------------------------------------- tweaks

    /// <summary>
    /// The allowlisted registry settings, read straight from the registry.
    /// </summary>
    /// <remarks>
    /// No PowerShell for these: they are single values under known keys, and going through the
    /// inventory script would add a second place the path is written down. The table in
    /// <see cref="Executors.RegistryTweaks"/> is the only one.
    /// </remarks>
    private static void AddTweakReadings(StateSnapshot.SnapshotBuilder builder)
    {
        foreach (Executors.RegistryTweak tweak in Executors.RegistryTweaks.All)
        {
            (CapabilityValue value, Evidence evidence) = Executors.RegistryTweaks.Read(tweak);
            builder.Add(tweak.Capability, value, evidence);
        }
    }

    // ---------------------------------------------------------------- display, power, storage

    private static void AddDisplayReadings(StateSnapshot.SnapshotBuilder builder)
    {
        DisplayMode? current = DisplayInterop.CurrentMode();
        IReadOnlyList<DisplayMode> supported = DisplayInterop.SupportedModesAtCurrentResolution();

        builder.Add(
            WindowsCapabilities.DisplayCurrentRefreshRate,
            current is null ? CapabilityValue.Unknown : CapabilityValue.Scalar(current.RefreshHz),
            current is null
                ? Evidence.Missing("EnumDisplaySettingsEx could not read the current display mode")
                : new Evidence(
                    EvidenceSourceKind.Win32Api,
                    "user32!EnumDisplaySettingsEx(ENUM_CURRENT_SETTINGS)",
                    Confidence.High,
                    RawResult: $"{current.Width}x{current.Height} @ {current.RefreshHz} Hz"));

        int maxHz = supported.Count == 0 ? 0 : supported.Max(m => m.RefreshHz);

        builder.Add(
            WindowsCapabilities.DisplayMaxRefreshRate,
            maxHz > 0 ? CapabilityValue.Scalar(maxHz) : CapabilityValue.Unknown,
            maxHz > 0
                ? new Evidence(
                    EvidenceSourceKind.Win32Api,
                    "user32!EnumDisplaySettingsEx, modes at the current resolution and colour depth",
                    Confidence.High,
                    RawResult: string.Join(", ", supported.Select(m => $"{m.RefreshHz} Hz")))
                : Evidence.Missing("no display modes could be enumerated"));
    }

    private static void AddPowerAndStorageReadings(StateSnapshot.SnapshotBuilder builder)
    {
        PowerState power = SystemInterop.ReadPowerState();

        builder.Add(
            WindowsCapabilities.OnBattery,
            power.OnBattery switch
            {
                true => CapabilityValue.Scalar("yes"),
                false => CapabilityValue.Scalar("no"),
                null => CapabilityValue.Unknown,
            },
            power.OnBattery is null
                ? Evidence.Missing("GetSystemPowerStatus reported an unknown AC line status")
                : new Evidence(EvidenceSourceKind.Win32Api, "kernel32!GetSystemPowerStatus.ACLineStatus", Confidence.High));

        builder.Add(
            WindowsCapabilities.BatteryPercent,
            power.BatteryPercent is { } percent ? CapabilityValue.Scalar(percent) : CapabilityValue.Unknown,
            power.BatteryPercent is null
                ? Evidence.Missing("no battery, or the level is unknown")
                : new Evidence(EvidenceSourceKind.Win32Api, "kernel32!GetSystemPowerStatus.BatteryLifePercent", Confidence.High));

        double? freeGb = SystemInterop.SystemDriveFreeGb();

        builder.Add(
            WindowsCapabilities.SystemDriveFreeGb,
            freeGb is { } gb
                ? CapabilityValue.Scalar(gb.ToString("0.#", CultureInfo.InvariantCulture))
                : CapabilityValue.Unknown,
            freeGb is null
                ? Evidence.Missing("could not read free space on the Windows drive")
                : new Evidence(EvidenceSourceKind.Win32Api, "DriveInfo.AvailableFreeSpace on the Windows drive", Confidence.High));
    }

    private static Evidence Wmi(string query, object? raw, InventoryDocument inventory, string section) =>
        raw is null
            ? Evidence.Missing(inventory.ProblemFor(section) ?? $"{query} was not reported by this machine")
            : new Evidence(EvidenceSourceKind.Wmi, query, Confidence.High, Query: query, RawResult: raw.ToString());
}

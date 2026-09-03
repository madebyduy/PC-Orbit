namespace PcOrbit.Adapters.Windows;

/// <summary>
/// The single read-only inventory script, and the shape of each section it returns.
/// </summary>
/// <remarks>
/// One batch, one process. Every query is wrapped so that a class or cmdlet the machine does not
/// have produces <c>null</c> rather than killing the scan: a partial snapshot with honest Unknowns
/// is the design (spec 8.3.1, 21.3), and a machine that fails one probe still gets a checkup.
/// Sections are read independently — see <see cref="InventoryDocument"/> for why that matters.
/// </remarks>
internal static class WindowsInventory
{
    /// <summary>
    /// Everything read here is observation only: no cmdlet in this script changes machine state.
    /// Keep it that way — this is the script that runs unattended on first launch.
    /// </summary>
    internal const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        function Safe { param([scriptblock] $Block) try { & $Block } catch { $null } }

        $cpu = Safe {
            Get-CimInstance -ClassName Win32_Processor |
                Select-Object -First 1 Name, Manufacturer, VirtualizationFirmwareEnabled,
                                       VMMonitorModeExtensions, SecondLevelAddressTranslationExtensions
        }

        $system = Safe {
            Get-CimInstance -ClassName Win32_ComputerSystem |
                Select-Object -First 1 Manufacturer, Model, HypervisorPresent, PCSystemType
        }

        $board = Safe {
            Get-CimInstance -ClassName Win32_BaseBoard | Select-Object -First 1 Manufacturer, Product
        }

        # ReleaseDate is deliberately rendered as a plain string: ConvertTo-Json writes a DateTime
        # as /Date(...)/, which is not something a JSON date parser accepts.
        $bios = Safe {
            $b = Get-CimInstance -ClassName Win32_BIOS | Select-Object -First 1
            [pscustomobject]@{
                SMBIOSBIOSVersion = $b.SMBIOSBIOSVersion
                Manufacturer      = $b.Manufacturer
                ReleaseDate       = if ($b.ReleaseDate) { $b.ReleaseDate.ToString('yyyy-MM-dd') } else { $null }
            }
        }

        $memory = Safe {
            Get-CimInstance -ClassName Win32_PhysicalMemory | Select-Object Speed, ConfiguredClockSpeed
        }

        # Win32_OptionalFeature is readable without elevation, unlike Get-WindowsOptionalFeature,
        # which needs admin because it goes through DISM. Spec 21.10 wants a standard user to get
        # the full scan, so this is the source we use. InstallState: 1 enabled, 2 disabled, 3 absent.
        $features = Safe {
            Get-CimInstance -ClassName Win32_OptionalFeature | Select-Object Name, InstallState
        }

        $tpm = Safe {
            Get-CimInstance -Namespace 'root\CIMV2\Security\MicrosoftTpm' -ClassName Win32_Tpm |
                Select-Object -First 1 SpecVersion, IsEnabled_InitialValue, IsActivated_InitialValue
        }

        # AvailableSecurityProperties element 3 means DMA protection, which requires a working
        # IOMMU (VT-d / AMD-Vi). Documented mapping, so this is inference from a firm signal
        # rather than a guess.
        $deviceGuard = Safe {
            $dg = Get-CimInstance -Namespace 'root\Microsoft\Windows\DeviceGuard' -ClassName Win32_DeviceGuard |
                    Select-Object -First 1
            [pscustomobject]@{
                AvailableSecurityProperties = @($dg.AvailableSecurityProperties)
                SecurityServicesRunning     = @($dg.SecurityServicesRunning)
            }
        }

        # Confirm-SecureBootUEFI needs administrator rights. The registry value under
        # Control\SecureBoot\State does not, so a standard user still gets an answer.
        $secureBoot = Safe { [bool] (Confirm-SecureBootUEFI) }

        $secureBootRegistry = Safe {
            (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State' `
                              -Name 'UEFISecureBootEnabled').UEFISecureBootEnabled
        }

        # Needs administrator. Without it we report Unknown and say so, rather than guessing "off"
        # on a machine whose drive is in fact encrypted (spec 10.3, 27.13).
        $encryption = Safe {
            Get-CimInstance -Namespace 'root\CIMV2\Security\MicrosoftVolumeEncryption' `
                            -ClassName Win32_EncryptableVolume |
                Where-Object { $_.DriveLetter -eq $env:SystemDrive } |
                Select-Object -First 1 ProtectionStatus, ConversionStatus, EncryptionMethod
        }

        $restoreDisabled = Safe {
            (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' `
                              -Name 'DisableSR').DisableSR
        }

        $restorePointCount = Safe { @(Get-ComputerRestorePoint).Count }

        $wslDefaultVersion = Safe {
            (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss' `
                              -Name 'DefaultVersion').DefaultVersion
        }

        $wslPresent = Safe { [bool] (Test-Path (Join-Path $env:SystemRoot 'System32\wsl.exe')) }

        [pscustomobject]@{
            Cpu                = $cpu
            System             = $system
            Board              = $board
            Bios               = $bios
            Memory             = @($memory)
            Features           = @($features)
            Tpm                = $tpm
            DeviceGuard        = $deviceGuard
            SecureBoot         = $secureBoot
            SecureBootRegistry = $secureBootRegistry
            Encryption         = $encryption
            RestoreDisabled    = $restoreDisabled
            RestorePointCount  = $restorePointCount
            WslDefaultVersion  = $wslDefaultVersion
            WslPresent         = $wslPresent
        } | ConvertTo-Json -Depth 5 -Compress
        """;

    /// <summary>Section names, so the scanner and the problem lookups cannot drift apart.</summary>
    internal static class Sections
    {
        internal const string Cpu = "Cpu";
        internal const string System = "System";
        internal const string Board = "Board";
        internal const string Bios = "Bios";
        internal const string Memory = "Memory";
        internal const string Features = "Features";
        internal const string Tpm = "Tpm";
        internal const string DeviceGuard = "DeviceGuard";
        internal const string SecureBoot = "SecureBoot";
        internal const string SecureBootRegistry = "SecureBootRegistry";
        internal const string Encryption = "Encryption";
        internal const string RestoreDisabled = "RestoreDisabled";
        internal const string RestorePointCount = "RestorePointCount";
        internal const string WslDefaultVersion = "WslDefaultVersion";
        internal const string WslPresent = "WslPresent";
    }

    internal sealed record CpuInfo(
        string? Name,
        string? Manufacturer,
        bool? VirtualizationFirmwareEnabled,
        bool? VMMonitorModeExtensions,
        bool? SecondLevelAddressTranslationExtensions);

    internal sealed record SystemInfo(
        string? Manufacturer,
        string? Model,
        bool? HypervisorPresent,
        int? PCSystemType);

    internal sealed record BoardInfo(string? Manufacturer, string? Product);

    internal sealed record BiosInfo(string? SMBIOSBIOSVersion, string? Manufacturer, string? ReleaseDate);

    internal sealed record MemoryModule(int? Speed, int? ConfiguredClockSpeed);

    internal sealed record OptionalFeature(string? Name, int? InstallState);

    internal sealed record TpmInfo(string? SpecVersion, bool? IsEnabled_InitialValue, bool? IsActivated_InitialValue);

    internal sealed record DeviceGuardInfo(List<int>? AvailableSecurityProperties, List<int>? SecurityServicesRunning);

    internal sealed record EncryptionInfo(int? ProtectionStatus, int? ConversionStatus, int? EncryptionMethod);
}

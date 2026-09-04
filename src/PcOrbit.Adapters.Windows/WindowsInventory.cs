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

        # The TPM, without administrator rights. Win32_Tpm needs them; the PnP device does not, and
        # its friendly name carries the spec version verbatim ("Trusted Platform Module 2.0").
        # ConfigManagerErrorCode 0 means Windows has the device started, which is what "enabled and
        # activated" amounts to from outside.
        $tpmDevice = Safe {
            Get-CimInstance -ClassName Win32_PnPEntity |
                Where-Object { $_.Name -match 'Trusted Platform Module' } |
                Select-Object -First 1 Name, PNPDeviceID, ConfigManagerErrorCode
        }

        # Firmware update delivery. A machine with an ESRT entry can receive firmware through
        # Windows Update; one without it cannot, and that is worth saying rather than implying.
        $firmware = Safe {
            $root = 'HKLM:\SYSTEM\CurrentControlSet\Control\FirmwareResources'
            if (-not (Test-Path -LiteralPath $root)) { $null }
            else {
                Get-ChildItem -LiteralPath $root | ForEach-Object {
                    $v = Get-ItemProperty -LiteralPath $_.PSPath
                    [pscustomobject]@{
                        Version            = [string] $v.Version
                        LastAttemptVersion = [string] $v.LastAttemptVersion
                        LastAttemptStatus  = [string] $v.LastAttemptStatus
                    }
                }
            }
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

        # The newest restore point, so we can tell "System Restore is on" from "System Restore is
        # on and has actually taken a point this year". CreationTime is a WMI datetime string.
        $restorePointLatest = Safe {
            $p = @(Get-ComputerRestorePoint) | Sort-Object CreationTime -Descending | Select-Object -First 1
            if ($p) {
                $t = $null
                try { $t = $p.ConvertToDateTime($p.CreationTime) }
                catch { $t = [System.Management.ManagementDateTimeConverter]::ToDateTime($p.CreationTime) }
                if ($t) { $t.ToString('yyyy-MM-ddTHH:mm:ss') } else { $null }
            } else { $null }
        }

        # WinRE. 'reagentc /info' is the documented command, but its output is translated, so
        # parsing it would break on a Vietnamese Windows. We read the configuration file reagentc
        # itself reports on instead. It lives under System32\Recovery, which is restricted to
        # administrators, so a standard user gets Unknown with that reason rather than a guess.
        $winre = Safe {
            $xml = [xml] (Get-Content -LiteralPath (Join-Path $env:SystemRoot 'System32\Recovery\ReAgent.xml') `
                                      -Raw -ErrorAction Stop)
            [pscustomobject]@{
                BcdId    = $xml.WindowsRE.WinreBCD.id
                Location = $xml.WindowsRE.WinreLocation.path
                Staged   = $xml.WindowsRE.WinREStaged.state
            }
        }

        $recoveryPartition = Safe {
            [bool] (@(Get-Partition -ErrorAction Stop | Where-Object { $_.Type -eq 'Recovery' }).Count -gt 0)
        }

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
            TpmDevice          = $tpmDevice
            Firmware           = @($firmware | Where-Object { $_ -and $_.Version })
            DeviceGuard        = $deviceGuard
            SecureBoot         = $secureBoot
            SecureBootRegistry = $secureBootRegistry
            Encryption         = $encryption
            RestoreDisabled    = $restoreDisabled
            RestorePointCount  = $restorePointCount
            RestorePointLatest = $restorePointLatest
            Winre              = $winre
            RecoveryPartition  = $recoveryPartition
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
        internal const string TpmDevice = "TpmDevice";
        internal const string Firmware = "Firmware";
        internal const string DeviceGuard = "DeviceGuard";
        internal const string SecureBoot = "SecureBoot";
        internal const string SecureBootRegistry = "SecureBootRegistry";
        internal const string Encryption = "Encryption";
        internal const string RestoreDisabled = "RestoreDisabled";
        internal const string RestorePointCount = "RestorePointCount";
        internal const string RestorePointLatest = "RestorePointLatest";
        internal const string Winre = "Winre";
        internal const string RecoveryPartition = "RecoveryPartition";
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

    /// <param name="Name">Windows' friendly name, e.g. "Trusted Platform Module 2.0".</param>
    /// <param name="ConfigManagerErrorCode">0 means the device is started and working.</param>
    internal sealed record TpmDeviceInfo(string? Name, string? PNPDeviceID, int? ConfigManagerErrorCode);

    /// <param name="Version">The firmware version Windows currently records for this resource.</param>
    internal sealed record FirmwareResource(string? Version, string? LastAttemptVersion, string? LastAttemptStatus);

    internal sealed record DeviceGuardInfo(List<int>? AvailableSecurityProperties, List<int>? SecurityServicesRunning);

    internal sealed record EncryptionInfo(int? ProtectionStatus, int? ConversionStatus, int? EncryptionMethod);

    /// <param name="BcdId">
    /// The boot entry WinRE is registered under. The null GUID means it is not registered, which
    /// is what <c>reagentc /info</c> reports as Disabled.
    /// </param>
    /// <param name="Location">Where the WinRE image lives. Empty when there is no image to boot.</param>
    /// <param name="Staged">Set while a WinRE update is part-applied; the environment is not usable then.</param>
    internal sealed record WinreInfo(string? BcdId, string? Location, string? Staged);
}

# Capability matrix

Spec step 2. What can be observed and what can be written, per machine, **with the evidence**.
This file decides the support tier a machine is shown (spec 18.4), so a row is only added after a
real machine produced it. Vendor documentation is not evidence for a row here.

Produce a row set with:

```bash
dotnet run --project src/PcOrbit.Cli -- scan --json > machine.json
```

## Machines tested so far

| # | Machine | CPU | Windows | Notes |
|---|---|---|---|---|
| M1 | LENOVO 21SR002JVA (laptop) | Intel Core Ultra 5 225U | Pro, build 26200 | standard user, no elevation |

Spec step 2 asks for at least: an Intel desktop, an AMD desktop, a Dell/Lenovo/HP laptop, an
ASUS/MSI/Gigabyte motherboard, Windows Home **and** Pro, one machine with BitLocker or Device
Encryption on, and one standard-user account. Only the last of those is covered.

## M1 — LENOVO 21SR002JVA, Intel Core Ultra 5 225U, Windows 11 Pro 26200

Scanned as a **standard user**, which is why several rows read `no`. That is the point of running
it this way: spec 21.10 promises a standard user the full scan and checkup, and this row set shows
exactly where that promise stops.

| Capability | Observe | Evidence source | Confidence | Value read | Write mode | Verification | Rollback |
|---|---|---|---|---|---|---|---|
| `cpu.virtualization` | yes | `Win32_Processor.VMMonitorModeExtensions` | high | supported | n/a (hardware) | re-read | n/a |
| `firmware.cpu.virtualization` | yes | `Win32_Processor.VirtualizationFirmwareEnabled` | high | enabled | guided-generic | re-read after boot | guided-manual |
| `firmware.secure-boot` | yes | `HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State` | high | disabled | read-only | re-read | — |
| `firmware.tpm.version` | **no** | `Win32_Tpm` needs administrator rights | none | unknown | read-only | — | — |
| `firmware.tpm.ready` | **no** | same | none | unknown | read-only | — | — |
| `firmware.boot-mode` | yes | `kernel32!GetFirmwareType` | high | uefi | read-only | re-read | — |
| `firmware.iommu` | yes | `Win32_DeviceGuard.AvailableSecurityProperties` contains 3 | high | enabled | read-only | re-read | — |
| `firmware.bios.version` | yes | `Win32_BIOS.SMBIOSBIOSVersion` | high | `R30ET32W(1.06 )` | n/a | — | — |
| `firmware.rebar` | no | needs the GPU driver API; not used in v0.1 | none | unknown | unsupported | — | — |
| `memory.rated-speed` | yes | `Win32_PhysicalMemory.Speed` | high | 5600 MT/s | read-only | — | — |
| `memory.current-speed` | yes | `Win32_PhysicalMemory.ConfiguredClockSpeed` | high | 5600 MT/s | read-only | — | — |
| `display.current-refresh-rate` | yes | `user32!EnumDisplaySettingsEx` | high | 60 Hz | auto | re-read | automatic |
| `display.max-refresh-rate` | yes | same, modes at current resolution | high | 60 Hz | n/a | — | — |
| `windows.feature.virtual-machine-platform` | yes | `Win32_OptionalFeature.InstallState` | high | enabled | auto | re-read after boot | automatic |
| `windows.feature.wsl` | yes | same | high | disabled | auto | re-read after boot | automatic |
| `windows.feature.hypervisor-platform` | yes | same | high | disabled | auto | re-read after boot | automatic |
| `windows.feature.hyper-v` | yes | same | high | disabled | auto | re-read after boot | automatic |
| `windows.feature.sandbox` | yes | same | high | disabled | auto | re-read after boot | automatic |
| `windows.system-restore` | **partial** | no `DisableSR` value and no restore points | none | unknown | auto (needs admin) | re-read | automatic |
| `wsl.installed` | yes | `Test-Path %SystemRoot%\System32\wsl.exe` | high | present | n/a | — | — |
| `wsl.default-version` | **partial** | `HKCU\...\Lxss\DefaultVersion` not set | none | unknown | auto | re-read | automatic |
| `security.bitlocker.system-drive` | **no** | `Win32_EncryptableVolume` needs administrator rights | none | unknown | auto (suspend, needs admin) | re-read | automatic |
| `power.on-battery` | yes | `kernel32!GetSystemPowerStatus` | high | no | n/a | — | — |
| `storage.system-drive.free-gb` | yes | `DriveInfo.AvailableFreeSpace` | high | 100.8 GB | n/a | — | — |

**Guide data**: vendor-level entry for `LENOVO`, no per-model entry. → `guidedGeneric`.

**Resolved tier**: **Tier 3 — Guided Generic.** No verified vendor write adapter matches, and the
guide entry is vendor-level rather than model-verified.

### What this machine taught us

Two things worth recording, both now covered by tests:

**A single unexpected field type can lose an entire scan.** `Win32_BIOS.ReleaseDate` is rendered by
Windows PowerShell as `/Date(...)/`, which no JSON date parser accepts. Deserialising the whole
inventory into one object meant that one field turned every reading on this machine into `Unknown`.
Sections are now parsed independently, so a failure is contained to the section that failed and
carries its own reason. See `InventoryDocument`.

**Confirm-SecureBootUEFI needs administrator rights.** Falling back to
`Control\SecureBoot\State\UEFISecureBootEnabled`, which does not, is what keeps spec 21.10's promise
real on this machine. Where no such fallback exists — TPM, drive encryption — the row honestly
reads `no`, and the reason travels with the value rather than being lost.

## Rows still needed

| Machine class | Why it matters |
|---|---|
| AMD desktop, ASUS or Gigabyte board | The guided path for `SVM Mode` is written from vendor documentation, not from a real board. Spec 18.4 will not call it Tier 2 until a real machine confirms the menu path. |
| Intel desktop, MSI or ASRock board | Same, for `Intel (VMX) Virtualization Technology`. |
| Dell Latitude or OptiPlex, elevated | The first vendor write adapter (ADR 0002). Needs three models, with and without a BIOS password. |
| Any machine with Device Encryption on | The BitLocker preflight is the most consequential gate in the product and has only been tested against an unreadable state, never against a genuinely encrypted drive. |
| Windows 11 Home | `Microsoft-Hyper-V-All` is not offered on Home. The scanner reports `Unknown` with an explanation; whether `NotSupported` would be more honest needs a real Home machine to decide. |
| A machine with a high-refresh display | The refresh-rate finding and its Safe Apply countdown have never fired on real hardware, because M1 runs 60 Hz at its native resolution. This is the v0.1 demo (spec 23.1.H) and it is untested end to end. |
| A machine with XMP or EXPO available but off | Same for the memory-speed finding. |

## Promoting a tier

Per spec 18.4, and never from documentation alone:

- **Tier 3 → Tier 2** (guided verified): add a `verifiedOn` entry to the guide with model, BIOS
  version and date, after someone completed the step on that machine and the app verified the
  result after boot. The loader refuses `guidedVerified` with an empty `verifiedOn`.
- **Tier 2 → Tier 1** (auto): meet every criterion in
  [`data/actions/pending-verification/README.md`](../data/actions/pending-verification/README.md)
  and move the manifest into `data/actions/`.

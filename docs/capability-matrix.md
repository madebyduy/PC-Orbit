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
| `recovery.winre` | yes | `System32\Recovery\ReAgent.xml` — registered to a boot entry, image present | medium | enabled | read-only | re-read | — |
| `recovery.partition` | yes | `Get-Partition`, type `Recovery` | high | present | read-only | re-read | — |
| `recovery.restore-point.age-days` | **no** | `Get-ComputerRestorePoint`: access denied without administrator rights | none | unknown | read-only | — | — |
| `wsl.installed` | yes | `Test-Path %SystemRoot%\System32\wsl.exe` | high | present | n/a | — | — |
| `wsl.default-version` | **partial** | `HKCU\...\Lxss\DefaultVersion` not set | none | unknown | auto | re-read | automatic |
| `security.bitlocker.system-drive` | **no** | `Win32_EncryptableVolume` needs administrator rights | none | unknown | auto (suspend, needs admin) | re-read | automatic |
| `power.on-battery` | yes | `kernel32!GetSystemPowerStatus` | high | no | n/a | — | — |
| `storage.system-drive.free-gb` | yes | `DriveInfo.AvailableFreeSpace` | high | 100.8 GB | n/a | — | — |

### Change sources (`pco timeline`)

Read live rather than stored, so each one is listed with what it could actually see here.

| Source | Readable | Evidence | Note |
|---|---|---|---|
| Windows Update history | yes | `Microsoft.Update.Session` → `IUpdateSearcher::QueryHistory` | works unelevated; carries the result code, so a failed update is distinguishable from an installed one |
| System event log | yes | `Get-WinEvent -LogName System` | Kernel-Power, WHEA-Logger, Kernel-PnP and WER only; event ids we cannot name are left out rather than guessed at |
| Restore points | **no** | `Get-ComputerRestorePoint`: access denied | reported as a named gap in the timeline, never as a quiet machine |

### Startup inventory (`pco startup`)

| Source | Readable | Evidence | Note |
|---|---|---|---|
| `Win32_StartupCommand` | yes | WMI | 5 entries here; works unelevated |
| Logon-triggered scheduled tasks | yes | `Get-ScheduledTask` with an `MSFT_TaskLogonTrigger` | 26 entries here, mostly Windows' own |
| `Explorer\StartupApproved` | partial | first byte of the value; low bit clear = enabled | undocumented, so medium confidence with the raw byte kept. Only 2 of 5 registry entries had a record; the other 3 report `Unknown` rather than an assumed On |

**Guide data**: vendor-level entry for `LENOVO`, no per-model entry. → `guidedGeneric`. All three
shipped packs expire `2027-09-04`; `doctor` warns 60 days ahead (ADR 0004).

**Resolved tier**: **Tier 3 — Guided Generic.** No verified vendor write adapter matches, and the
guide entry is vendor-level rather than model-verified.

### What this machine taught us

Two things worth recording, both now covered by tests:

**A single unexpected field type can lose an entire scan.** `Win32_BIOS.ReleaseDate` is rendered by
Windows PowerShell as `/Date(...)/`, which no JSON date parser accepts. Deserialising the whole
inventory into one object meant that one field turned every reading on this machine into `Unknown`.
Sections are now parsed independently, so a failure is contained to the section that failed and
carries its own reason. See `InventoryDocument`.

**A partly-read WMI object is not a read.** `Win32_Tpm` came back here as an object whose
properties were all null, and `tpm.IsEnabled_InitialValue == true` on a null `bool?` is `false` — so
the scanner reported the security chip as **off** on a machine whose chip we simply could not see.
That is `Unknown` inferred into a value, the one thing spec 6.6 forbids, and it was invisible until
`pco diff` put the two readings side by side. Both flags must now be present for a verdict.

**Confirm-SecureBootUEFI needs administrator rights.** Falling back to
`Control\SecureBoot\State\UEFISecureBootEnabled`, which does not, is what keeps spec 21.10's promise
real on this machine. Where no such fallback exists — TPM, drive encryption — the row honestly
reads `no`, and the reason travels with the value rather than being lost.

## The vendor firmware interface

A separate axis from the tiers above. Those describe how a *capability* is reached; this describes
whether the machine will let Windows change a BIOS setting at all (ADR 0007).

**Elevation comes first, and the first version of this table forgot it.** Every ACPI-WMI class
under `root\WMI` returns zero instances to a process without administrator rights —
`MSAcpi_ThermalZoneTemperature` included, and that one works on every machine ever made. So an
empty result from a standard-user run says nothing at all about the firmware, and reading it as
"this model does not have the interface" was inferring a definite negative from a failed read. That
is the one thing this product is built not to do (spec 6.6).

| State | What it means | What the app says |
|---|---|---|
| No classes | The maker publishes no interface, or this machine has no driver for it. | Use the BIOS screen; here is the button that restarts you into it, and here is the menu path. |
| Classes present, **not elevated** | Nothing. Windows will not let this process enumerate them. | This cannot be answered yet — run as administrator. |
| Classes present, elevated, zero instances | The firmware does not implement the methods behind them. A real finding. | Your model does not answer it — menu path instead. |
| Classes present, settings returned | Usable. | The settings, with their risk and their accepted values. |

| Machine | Vendor | Classes | Elevated? | Settings | Verified |
|---|---|---|---|---|---|
| LENOVO 21SR002JVA, Windows Pro 26200 | Lenovo | `Lenovo_BiosSetting` + companions registered | **no** | 0 | 2026-09-04, `pco bios --verbose`, standard user. Reports `NeedsElevation`. Says nothing about the firmware — kept as the row that shows why the next one exists. |
| LENOVO 21SR002JVA, Windows Pro 26200 | Lenovo | same | **yes** | **90** | 2026-09-04, `pco bios --verbose` from an elevated shell. `availability Available`, no supervisor password. Every setting carries its accepted values from `[Optional:…]` or `Lenovo_GetBiosSelections`. **The read path works on this machine.** Two earlier claims that this model lacks the interface were wrong; this row is the correction. **No write has been performed yet** — that needs the owner's say-so on a specific setting. |

### Rows needed before a firmware write has been seen to work

| Machine class | Why it matters |
|---|---|
| **The machine above, one Routine write** | `FnKeyAsPrimary` Disable → Enable → Disable is harmless, instantly reversible, and exercises `SetBiosSetting` + `SaveBiosSettings` + the read-back. It has to be the owner's decision, not the developer's. |
| ThinkPad or ThinkCentre, elevated, no supervisor password | Lenovo's documented hardware for this. First real exercise of `SetBiosSetting` + `SaveBiosSettings`, and of the verify-by-re-reading path. |
| ThinkPad or ThinkCentre **with** a supervisor password | The `,password,ascii,us` suffix is written from Lenovo's documentation and has never been sent. |
| HP EliteBook or ProDesk | `HP_BIOSSettingInterface.SetBIOSSetting`, including the `<utf-16/>` password prefix HP requires. Written from documentation. |
| Dell with Command \| Monitor installed | Confirms the Dell branch detects rather than misreports. The Dell write remains unimplemented on purpose. |
| Any of the above with BitLocker genuinely on | The recovery-key consequence has only ever been produced from a synthesised snapshot in a test, never from a real encrypted machine changing Secure Boot. |

Until a row appears here with a write in it, the honest description of the write path is
*implemented against published vendor documentation, unexercised on hardware* — which is why the UI
refuses to report a success it has not read back.

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

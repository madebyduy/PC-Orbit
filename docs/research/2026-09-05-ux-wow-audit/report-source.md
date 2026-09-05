# PC Orbit UX and Feature Investment Memo

**Audience:** PC Orbit product and engineering team  
**Date:** 05 September 2026  
**Decision:** What to build next so PC Orbit feels materially more useful and convenient  
**Method:** Static repository audit plus current official Windows and OEM product documentation

## Executive answer

PC Orbit already has enough surface area to look substantial. The repository exposes 16 desktop pages across six navigation sections, four outcome plans, 11 checkup rules, 17 action definitions, 24 curated applications, a transactional apply and undo engine, restart resume, firmware safety gates, quarantine cleanup, timeline and snapshot comparison. Adding another page of utilities will not create the missing wow effect.

The core usability problem is orchestration. Users still have to translate a human problem into the correct page and then combine evidence from several pages themselves. The code has four outcomes but 16 pages. It has a root-cause correlation primitive, but the desktop timeline only renders a chronological list. It has a global-search string, but Ctrl F only focuses search on the Apps and BIOS pages. It has reinstall preflight, but control and verification stop after Windows Setup launches.

The recommended investment is therefore a mission layer above the existing tools:

1. A goal-first Mission Center with app-wide search and natural-language aliases.
2. A complete route from every finding to the relevant next action.
3. An Incident Mode that answers what changed before a slowdown, crash or device failure.
4. A least-disruptive Recovery Ladder that continues through restart and verification.

These four changes reuse the strongest parts of the code and turn them into a coherent experience. Build proactive watch, PC Blueprint, privacy-safe support handoff and hardware health trends after the mission layer proves itself.

## What is already implemented

### User-facing portfolio

| Area | Current capability | Assessment |
|---|---|---|
| Overview | Health score, findings, evidence, links to fixes | Strong summary, but it does not complete every finding |
| My PC | Readings, hardware inventory, live performance, processes, BIOS and firmware controls | Broad and unusually evidence-aware |
| Protection | Security state, Windows 11 readiness, WinRE, System Restore and quarantine | Good recovery awareness while Windows still boots |
| Tune-up | Cleanup with quarantine, startup control, driver inventory | Safer than typical one-click optimizers |
| Apps and Windows | Batch app install and removal, curated app-list backup, Office deployment, edition and license information, ISO repair install | High visible utility, but several flows hand off before verification |
| Changes | Outcome planning, apply, restart resume, undo, timeline and snapshot comparison | This is the product's real differentiator |

Repository evidence: the navigation table defines 16 pages in `src/PcOrbit.App/MainWindow.xaml.cs` lines 590 to 630. The four shipped outcomes are under `data/outcomes`. The checkup engine registers 11 rules in `src/PcOrbit.Core/Checkup/Checkup.cs` lines 63 to 77.

### Defensible product strengths

- Unknown remains distinct from off or failed.
- Each reading carries evidence and source information.
- Plans are machine-specific, hashed and split at restart boundaries.
- Success is verified through an independent read path.
- Undo is a reverse transaction; cleanup uses quarantine instead of irreversible deletion.
- Risky firmware actions are gated by machine support, recovery access and user confirmation.
- English and Vietnamese are first-class product surfaces.

These are more defensible than a large catalog of tweaks. They should become visible in each mission as progress, proof and a clear way back, rather than remain mainly architectural virtues.

## Why the product does not yet feel wow

### Too many destinations and too few missions

Six navigation sections and 16 pages are reasonable as an expert browsing structure, but only four user outcomes are available: Docker and WSL 2 readiness, Windows 11 readiness, maximum display refresh and System Restore. Users arriving with “my PC became slow after yesterday's update” or “I want to reinstall safely” must assemble the journey themselves.

Microsoft's Windows navigation guidance recommends tracing typical user paths, simplifying access to important destinations and avoiding back-and-forth navigation between related content. PC Orbit currently organizes by PC subsystem, while the user's problem usually crosses subsystems. [Microsoft Windows navigation basics](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics)

### Search exists only inside two expert pages

The localization catalog already contains “Tìm cài đặt, công cụ, ứng dụng…”, but there is no code reference to `app.search`. The keyboard handler maps Ctrl F only to Firmware Search or Apps Search in `MainWindow.xaml.cs` lines 364 to 389. A user cannot type “máy chậm”, “không có âm thanh” or “cài lại Windows” and be routed to a relevant mission.

Microsoft's current Command Palette demonstrates why a single typed entry point feels fast: one surface can find apps, settings, packages and commands. PC Orbit should use the interaction pattern, but return safe PC Orbit missions and evidence instead of arbitrary shell commands. [PowerToys Command Palette](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/overview)

### The timeline presents evidence but not an answer

`TimelineBuilder.Preceding` already returns events within a window before an incident, and tests cover it. No production UI calls it. `RenderTimelineAsync` builds a 14-day timeline and renders event rows, but does not ask when the symptom started, rank relevant changes, or suggest a controlled next test. This leaves the most differentiated diagnostic primitive unused.

Microsoft SetupDiag shows that Windows setup logs can be parsed into a concrete upgrade failure rule and result. WHEA events expose hardware errors in the Windows event log. PC Orbit can combine these official evidence sources with its own snapshot and transaction history without claiming correlation is causation. [Microsoft SetupDiag](https://learn.microsoft.com/en-us/windows/deployment/upgrade/setupdiag) [Microsoft WHEA hardware error events](https://learn.microsoft.com/en-us/windows-hardware/drivers/whea/whea-hardware-error-events)

### Findings can end without a useful next step

The dashboard shows a Fix button only when `SuggestedOutcomeId` is present. Several rules deliberately have no action because automatic remediation would be unsafe. That is correct at the engine level, but “no automatic action” should not mean “no route.” Low disk space can open Cleanup with safe categories preselected. A memory-speed mismatch can open the model-specific BIOS guide. A recovery warning can open a checklist for Windows Backup, the BitLocker key and a recovery drive.

### Reinstall hands control away too early

The ISO path has useful media inspection and readiness checks, but `ReinstallStart` explicitly states that Windows Setup takes over and PC Orbit cannot report what happens after. Windows now documents a least-disruptive ladder that starts with troubleshooters or reinstalling through Windows Update while preserving apps, files and settings, then moves through update removal and restore before Reset or clean install. PC Orbit should choose and explain the least disruptive path for the current symptom rather than start at ISO selection. [Microsoft Windows recovery options](https://support.microsoft.com/en-us/windows/experience/backup-recovery/recovery-options-in-windows) [Reinstall Windows through Windows Update](https://support.microsoft.com/en-US/Windows/deployment/install-upgrade/fix-issues-by-reinstalling-the-current-version-of-windows)

### The app is reactive and single-session

`AppSettings` remembers locale, elevation preference, last section and window geometry. It does not remember the user's goal, monitor preferences, incident state or device baseline. Dell SupportAssist and HP Support Assistant make their convenience visible through proactive scanning, notifications, guided diagnostics and model-specific support. PC Orbit should not copy their auto-update behavior, but it should match the expectation that an assistant notices meaningful change and tells the user what to do. [Dell SupportAssist for Home PCs](https://www.dell.com/support/contents/en-us/article/product-support/self-support-knowledgebase/software-and-downloads/support-assist/supportassist-for-home) [HP Support Assistant](https://support.hp.com/us-en/help/hp-support-assistant)

## Recommended investments

### Priority 0 Mission Center and app-wide intent search

Add one entry point at the top of Overview: “Bạn muốn làm gì với máy này?” It should match Vietnamese and English aliases to outcomes, pages, findings and guided workflows. Initial quick intents should include:

- Máy chậm hoặc treo
- Lỗi sau Windows Update
- Cài lại Windows nhưng giữ dữ liệu
- Chuẩn bị máy mới
- Không có âm thanh, mạng hoặc màn hình
- Giải phóng dung lượng
- Bật Secure Boot, TPM or virtualization
- Cài bộ ứng dụng cần thiết

The result is not a chat response. It is a deterministic mission card showing what PC Orbit will inspect, what it may change, estimated time, restarts and the recovery path. This is a low-to-medium effort change because the string catalog, search aliases, navigation, findings and outcomes already exist.

### Priority 0 Complete every finding with a next route

Define `NextRoute` separately from `SuggestedActionId`. A route can be an outcome, a filtered page, a model guide, an official Windows settings deep link, or a support handoff. Preserve the current rule that only verified actions receive Apply buttons.

Examples:

| Finding | Next route |
|---|---|
| System drive low | Open Cleanup with safe reclaimable categories selected and show expected immediate versus delayed space |
| Memory below rated speed | Open the exact BIOS guide and explain instability risk; no automatic apply |
| WinRE unavailable | Open Recovery Ladder with registration, backup and BitLocker checks |
| Old firmware | Open the verified vendor support route for the detected model |
| Driver problem code | Open Incident Mode scoped to the affected device and recent driver changes |

This change removes dead ends without weakening the safety model.

### Priority 0 Incident Mode

Ask one question: “Vấn đề bắt đầu khoảng khi nào?” Then capture a baseline and build an evidence bundle from recent updates, drivers, crashes, WHEA, PC Orbit transactions, changed capability values, top processes and device problem codes. Show three sections:

1. What changed before the symptom.
2. What evidence supports or weakens each hypothesis.
3. The smallest reversible test to run next.

Use confidence labels such as “trùng thời điểm” and “có bằng chứng trực tiếp”; never state that an update caused a crash solely because it came first. After each test, rescan and compare automatically. This turns Timeline, Compare, Checkup and the transaction engine into one answer-producing workflow.

### Priority 0 Recovery Ladder

Replace the isolated ISO entry point with a symptom-led sequence that selects the least disruptive supported option:

1. Troubleshooter or targeted repair.
2. Reinstall current Windows through Windows Update while preserving apps, files and settings when supported.
3. Uninstall the recent update or use System Restore.
4. Enter WinRE and use startup repair or restore.
5. Reset or ISO repair install.
6. Clean installation only as a clearly destructive last resort.

Before any disruptive step, confirm Windows Backup status, BitLocker key access, power, free space and recovery environment. Resume after reboot and verify the selected outcome. This is the highest-value extension of the existing preflight and durable transaction model.

### Priority 1 Proactive Watch

Offer an opt-in local monitor that records a lightweight daily baseline and only notifies on meaningful changes: new WHEA errors, rapidly declining storage or battery health, restore protection becoming unavailable, a newly broken device, or a risky drift after update. WPF apps can use current Windows app-notification APIs without requiring a cloud service, although notifications are not supported while the app runs elevated. [Windows app notifications for WPF](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet)

### Priority 1 PC Blueprint and New PC mission

Expand the current curated 24-app backup into a reconciliation artifact that records all exportable WinGet packages, selected Windows settings, Windows Backup readiness, critical drivers and user-approved application choices. On another PC, preview what is applicable, install only approved items, and verify the observed state.

WinGet officially supports export and import of installed packages, and configuration files consolidate repeatable device setup. Windows Backup can restore files, settings, installed-app references and Wi-Fi information. [WinGet install and bulk package guidance](https://learn.microsoft.com/en-us/windows/package-manager/winget/install) [WinGet overview and configuration](https://learn.microsoft.com/en-us/windows/package-manager/) [Windows Backup](https://support.microsoft.com/en-us/windows/experience/backup-recovery/back-up-and-restore-with-windows-backup)

### Priority 1 Privacy-safe support handoff

Create a one-click support bundle scoped to the active mission. Preview and redact usernames, file paths, network identifiers, serial numbers and secrets by default. Include exact device identity, evidence, recent changes, tests performed and unresolved hypotheses. Then offer a safe handoff to a trusted helper through Quick Assist with an explicit scam warning. Microsoft itself warns that Quick Assist can expose the screen or grant control and should only be used with someone the user trusts. [Microsoft Quick Assist](https://support.microsoft.com/en-US/Windows/Apps/solve-pc-problems-remotely-using-quick-assist)

### Priority 2 Hardware health trends and driver rollback

Add storage temperature, errors and wear, battery capacity trends, sleep drain and WHEA frequency. Windows exposes storage reliability counters for temperature, errors, wear and device age, while `powercfg` can produce battery and sleep reports. Present trends against the same machine's baseline rather than a universal score. [Get Storage Reliability Counter](https://learn.microsoft.com/en-us/powershell/module/storage/get-storagereliabilitycounter?view=windowsserver2025-ps) [Powercfg options](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options)

Driver work should remain inventory, export, problem diagnosis and rollback by subsystem. Do not add “update all drivers.”

## Priority matrix

Scores are expert estimates from 1 to 5. Effort is inverse: 1 is small and 5 is large.

| Rank | Investment | User impact | Reuse of current core | Differentiation | Effort | Phase |
|---:|---|---:|---:|---:|---:|---|
| 1 | Mission Center and global intent search | 5 | 5 | 4 | 2 | P0 |
| 2 | Next route for every finding | 5 | 5 | 4 | 2 | P0 |
| 3 | Incident Mode | 5 | 5 | 5 | 3 | P0 |
| 4 | Recovery Ladder | 5 | 4 | 5 | 3 | P0 |
| 5 | Proactive Watch | 4 | 4 | 4 | 3 | P1 |
| 6 | PC Blueprint and New PC mission | 5 | 3 | 4 | 4 | P1 |
| 7 | Privacy-safe support handoff | 4 | 4 | 4 | 3 | P1 |
| 8 | Hardware health trends | 4 | 3 | 4 | 4 | P2 |
| 9 | Driver export and rollback | 4 | 3 | 3 | 4 | P2 |

## Ninety day product plan

### Release 1 Find and finish

- Add the Mission Center and app-wide intent index.
- Add `NextRoute` and remove dashboard dead ends.
- Preserve the existing six-section browser for expert users.
- Measure mission selection success, time to first relevant action and abandonment.

### Release 2 Explain what changed

- Ship Incident Mode version 1 using the existing timeline, snapshot diff and checkup data.
- Add incident start-time selection, evidence grouping and confidence language.
- Run only one reversible test at a time and rescan automatically.
- Add SetupDiag results for update or reinstall failures.

### Release 3 Recover safely

- Ship the Recovery Ladder for machines that still boot.
- Add Windows Update reinstall and update-removal routes before ISO.
- Persist mission state across reboot and verify the selected result.
- Prepare interfaces for later WinRE handoff without claiming unsupported offline control.

## Success gates

| Metric | Initial gate |
|---|---:|
| Users who reach the right mission without browsing the rail | at least 80 percent in moderated tests |
| Findings with a visible and safe next route | 100 percent |
| Median time from launch to first useful recommendation | under 30 seconds after scan |
| Incident reports that label correlation separately from direct evidence | 100 percent |
| Risky steps with a recovery path shown before Apply | 100 percent |
| Supported missions that resume and verify after reboot | at least 99.5 percent in automated and hardware tests |
| Default support bundles leaking a seeded secret or identifier | zero in the test corpus |

## What not to build next

- Another top-level navigation section.
- A generic chatbot that can invent commands.
- One-click optimize, registry cleaning or driver update all.
- Automatic firmware flashing across unsupported models.
- More isolated tweak cards without an outcome and verification path.
- Social, gamification or cosmetic dashboards before mission completion improves.

## Risks and limits

This was a static code and data audit, not a moderated usability test. The existing test suite could not be executed in this session because `dotnet` was not available on PATH, so implementation claims are based on source inspection and repository documentation rather than a fresh runtime verification. The workspace contains untracked firmware change-log files; they were not modified.

The priority scores are product judgments, not market-size estimates. Validate the first four missions with five to eight target users in Vietnamese before expanding the backlog. The stop condition for this research was reached when the current capability inventory, the main orchestration gaps, and the proposed high-impact flows all had direct repository evidence or official platform support; further competitor feature lists were unlikely to change the recommendation.

## Claim to source ledger

| Claim | Source | Publisher | Date or access note |
|---|---|---|---|
| Good navigation prioritizes simple, clear user paths and avoids back-and-forth | Navigation design basics for Windows apps | Microsoft Learn | Accessed 05 Sep 2026 |
| A single palette can search apps, settings, packages and commands | PowerToys Command Palette | Microsoft Learn | Updated 13 Jun 2026 |
| Windows recovery should start with least disruptive options | Recovery options in Windows | Microsoft Support | Accessed 05 Sep 2026 |
| Windows Update reinstall preserves apps, files and settings | Fix issues by reinstalling the current version of Windows | Microsoft Support | Accessed 05 Sep 2026 |
| SetupDiag parses setup logs to identify upgrade failures | SetupDiag | Microsoft Learn | Updated 2026 |
| WHEA hardware error events are retrievable from the system event log | WHEA hardware error events | Microsoft Learn | Updated 16 Dec 2024 |
| OEM assistants emphasize proactive scans and guided troubleshooting | SupportAssist for Home PCs; HP Support Assistant | Dell; HP | Accessed 05 Sep 2026 |
| WPF can send local Windows app notifications | Use app notifications with a .NET app | Microsoft Learn | Updated 2026 |
| WinGet supports bulk package export, import and repeatable configuration | WinGet install; WinGet overview | Microsoft Learn | Updated 2026 |
| Windows Backup covers files, settings, app references and Wi-Fi information | Back up and restore with Windows Backup | Microsoft Support | Accessed 05 Sep 2026 |
| Storage and power telemetry can support health trends | Get Storage Reliability Counter; Powercfg options | Microsoft Learn | Accessed 05 Sep 2026 |
| Remote help must include trust and control warnings | Solve PC problems remotely using Quick Assist | Microsoft Support | Accessed 05 Sep 2026 |

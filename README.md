# PC Orbit

**A state and control layer for a Windows PC.** Know what your machine actually is, describe what
you want it to be, see exactly what has to change, apply it as a transaction that survives a
restart, and verify the result against the machine rather than against hope.

This repository implements the v0.1 engine described in
[`PC_CONTROL_PROJECT_SPEC_v3.md`](PC_CONTROL_PROJECT_SPEC_v3.md). It is deliberately **not** a
dashboard of hundreds of toggles — spec step 1 says build one vertical slice first, and the slice
is *get this PC ready for WSL 2 / Docker*.

```text
Discover → Understand → Compare → Plan → Stage → Apply → Restart → Verify → Observe → Restore
```

---

## What works today

Run against a real Windows 11 machine, with no elevation required for anything read-only:

| Command | What it does |
|---|---|
| `pco scan` | Reads the machine and stores a snapshot. Every value shows where it came from; every unreadable value says *why*. |
| `pco checkup` | Findings in plain language, in the four-line shape of spec 21.6: what, what you gain, why it is safe, what it costs. |
| `pco outcomes` | The outcomes you can ask for. |
| `pco plan <outcome>` | Compiles a **difference plan** for this specific machine: only what is still missing, ordered by dependency, split at restart boundaries, with a cost header and a plan hash. Changes nothing. |
| `pco apply <outcome>` | Runs that plan as a transaction: preflight → apply → verify → restart boundary → resume → verify the outcome. `--dry-run` simulates the whole thing. |
| `pco undo [tx]` | Undoes a transaction — as a **reverse transaction** through the same preview → apply → verify (spec 21.9). Restores the values read before each change, in reverse order. Whatever cannot be undone automatically is listed with the reason, never silently dropped. Defaults to the most recent transaction still in effect. |
| `pco resume` | Picks a transaction back up after the restart and reads the real values from Windows. |
| `pco history` | Normalised change events with before/after values and the boot session each one belongs to. |
| `pco timeline` | Everything that changed recently, **whoever changed it**: Windows updates, driver installs, blue screens, hardware errors, restore points and our own transactions on one axis. A source it could not read is named, so a gap never reads as a quiet machine. |
| `pco diff` | Scans, then shows what has moved since the previous scan. Values that merely became unreadable are listed apart from values that actually changed — the difference between "the BIOS update turned off your TPM" and "this scan was not elevated". |
| `pco startup` | What starts with Windows, and whether each entry is on, off, or something we could not classify. Read-only. |
| `pco clean` | Measures reclaimable space. With `--apply`, moves it to **quarantine** — files stay restorable for 30 days and nothing is deleted until that window closes. `pco restore [id]` puts a batch back. |
| `pco startup on\|off <name>` | Switches a startup entry. The name has to be one the machine reports *right now*, and security entries are refused outright (ADR 0005). |
| `pco drivers` | The driver behind every device, faulty ones first. Never sorted by age: an old driver is not a fault. |
| `pco doctor` | Validates the shipped data and the executor allowlist, and warns about knowledge packs that have expired or are about to. Meant for CI. |

Both **English and Vietnamese** ship, as spec 21.11 requires — including plural handling, so
`--lang vi` is a real translation and not English with substituted words.

### The same outcome, two machines

That is the whole point of a compiler rather than a preset. Given
`pco plan docker-wsl2-ready`:

```text
# a machine with virtualization off in firmware and nothing enabled
Stage 1  (then: restart into firmware setup)
  1. Turn on CPU virtualization in firmware (you do one step)
Stage 2  (then: restart Windows)
  2. Turn on Virtual Machine Platform
Stage 3
  3. Set WSL default version to 2

# a machine that only needs the last step
Stage 1
  1. Set WSL default version to 2
```

---

## Getting started

Needs the .NET 10 SDK and Windows 10 build 19041 or newer.

```bash
dotnet build
```

```bash
dotnet test
```

```bash
dotnet run --project src/PcOrbit.Cli -- checkup --lang vi
```

```bash
dotnet run --project src/PcOrbit.Cli -- plan docker-wsl2-ready --verbose
```

Nothing is ever changed without an explicit `apply`, and `apply` refuses to proceed while preflight
is blocked. Try `--dry-run` first; it exercises the entire plan and writes nothing, not even to
history.

Local state lives in `%LOCALAPPDATA%\PC Orbit\pcorbit.db`. Use `--db <path>` to keep it elsewhere.

---

## How it is put together

```text
data/                shipped, versioned, reviewable: graph, outcomes, action manifests, guides, strings
  ↓
PcOrbit.Core         the domain. No Windows APIs, no I/O, no clock of its own
  ↓ ports
PcOrbit.Adapters.Windows   WMI/CIM, registry, Win32, DISM, wsl.exe — everything platform-specific
PcOrbit.Store        SQLite: snapshots, transaction checkpoints, append-only event log
  ↓
PcOrbit.Cli          the composition root and today's user interface
```

`PcOrbit.Core` targets plain `net10.0` on purpose. If a Windows API ever needs to appear in there,
that is the signal a port is missing — and it keeps the compiler and the transaction state machine
unit-testable without touching a real machine.

### The seven shared cores (spec 7)

| Core | Where | State |
|---|---|---|
| PC Capability Graph | `Core/Graph` + `data/graph` | done for the vertical slice |
| PC State Compiler | `Core/Compiler` | done, with golden tests |
| Transactional Action Engine | `Core/Transactions` | done: apply, checkpoint, restart, resume, verify, partial completion |
| Change Basket | `Cli` (`plan` / `apply`) | the plan and cost header exist; the UI does not yet |
| Desired State & Drift | — | schema-ready; not implemented (Phase 2) |
| PC Change Timeline | `Core/Events` + `Store` | event schema frozen and written from v0.1; `pco timeline` merges in Windows Update, the system log and restore points; the Timeline UI comes later |
| PC Blueprint | — | not started (Phase 5) |

---

## Design decisions you will run into immediately

These are not stylistic. Each one is load-bearing, and each is enforced by a test.

**`Unknown` is a first-class value.** A read that failed never becomes "off". Every unreadable
value carries the reason, and no finding, plan step or verdict is derived from one
(spec 6.6, 21.3, 27.13).

**Verification never asks the executor.** After a change, the machine is read again through a
separate port. An executor that reports success without changing anything produces a failed
transaction — there is a test that asserts exactly that.

**One plan, one restart.** Steps are grouped into restart tiers, so three Windows components that
each need a reboot still cost one reboot. A restart is also skipped when every step in that stage
failed, because asking someone to reboot to complete a change that did not happen is a small
dishonesty that costs trust.

**No automatic firmware writes ship yet.** Windows has no API for writing firmware settings, and
this codebase will never write UEFI `Setup` variables by offset (spec decision 18). The Dell
vendor-adapter contract exists, but its manifest is parked in
[`data/actions/pending-verification/`](data/actions/pending-verification/README.md), which the
loader does not read. Promotion requires evidence from real hardware. Every machine gets the
**guided** route today — which spec 8.3.2 calls the default path anyway, and which is verified
after boot exactly like an automatic one.

**BitLocker preflight is a hard gate.** Anything touching firmware or boot is blocked until the
user confirms they can reach their recovery key, or the plan itself suspends encryption for one
restart. "We could not read the encryption state" blocks too: reading it needs administrator
rights, so a failed read is not evidence the drive is unencrypted. This is the single most common
way an ordinary person breaks their PC with a tool like this (spec 10.3).

**A manifest names an executor; it never carries a script.** Executors are a hand-written
allowlist in the composition root. An unknown executor id fails the plan before anything is
touched — there is no shell fallback and no dynamic loading (spec 17.1, 19.1).

**Undo is a reverse transaction, not a special code path.** `pco undo` compiles a reverse plan
from the manifests carried *inside* the original transaction and runs it through the same engine —
same checkpoints, same restart handling, same read-the-machine verification. A step whose
before-value was never read is excluded rather than restored to a guess, and a guided firmware
step is listed as "you change it back by hand" instead of hiding the option (spec 21.9).

**A health score never speaks for what it could not see.** The number is a pure function of the
findings, and the findings never come from an `Unknown` — so a machine scanned without
administrator rights would otherwise show 100 and mean "we saw nothing". Every verdict therefore
carries the count of capabilities that should have been readable and were not, and every surface
shows both (ADR 0004).

**Knowledge packs expire.** A per-model firmware guide carries the date its evidence stops
counting. Past it, guided steps using it refuse themselves with that reason and the machine drops
to read-only — because OEM menus move between BIOS revisions, and a pack verified eighteen months
ago is a claim about a machine that no longer exists. `doctor` warns sixty days ahead.

**A plan says when it was compiled from a stale scan.** A warning rather than a blocker: the
engine still verifies against the live machine, so an old snapshot makes the preview misleading,
not the apply unsafe.

**The plan the user reviewed is locked by hash.** Recompiling after a machine or data change
produces a different hash, and applying then refuses rather than running something nobody
approved. The hash deliberately excludes the snapshot id and the timestamp, so re-scanning an
unchanged machine does not invalidate a pending plan.

---

## Documentation

- [`docs/architecture.md`](docs/architecture.md) — the layers, the ports, and why the seams are
  where they are.
- [`docs/adr/`](docs/adr/) — the decisions that were genuinely open, and what settled them.
- [`docs/research/`](docs/research/) — the deep research this product's backlog came from.
  [ADR 0004](docs/adr/0004-research-backlog-reconciliation.md) is the record of what was accepted
  from it, what is deferred behind a missing primitive, and what was rejected and why.
- [`docs/capability-matrix.md`](docs/capability-matrix.md) — spec step 2: what can be observed and
  written per machine, and the evidence for each claim. This file decides support tiers.
- [`schemas/`](schemas/) — JSON Schema for every shipped data contract.
- [`CLAUDE.md`](CLAUDE.md) — conventions for anyone (or anything) contributing.

## What is deliberately not here

Straight from spec 23.2: no plugin store, no universal BIOS flashing, no driver updater, no fan
curves or undervolting, no cloud migration, hundreds of repair scripts, peripheral adapters for
every brand, AI features, automatic performance optimisation, full drift auto-remediation, full
regression intelligence, or cross-device blueprint restore.

Two more, from [ADR 0004](docs/adr/0004-research-backlog-reconciliation.md), which sorted the deep
research in `docs/research/` into what shipped, what is deferred and what was rejected outright:

- **No disk cleanup.** Undo here works by restoring the value a step recorded before it. A deleted
  file has no before-value, so cleanup cannot be undone by the engine that undoes everything else.
  It needs a quarantine store first, and until that exists offering it would break the one promise
  the product is built on.
- **No switching startup entries off.** `pco startup` lists them. Disabling one means passing a
  caller-chosen identifier to an executor, and `ActionParameters` validates every parameter against
  an allowlist before it reaches a command (spec 17.1) — "whichever entry the user picked" is not
  an allowlist. That needs a design decision, and it gets its own ADR.

## The desktop app

`src/PcOrbit.App` is a WPF surface over the same `PcOrbitHost` the CLI composes, so the executor
allowlist exists once. Twelve pages, each with its own content and none repeating another's:
Dashboard, Status (every reading with its evidence), Hardware (inventory, every mounted drive),
Performance (the only page with live gauges), Security, Recovery, Free up space, Starts with
Windows, Drivers, Plan &amp; Apply, Timeline and Compare. A standard-user scan offers to relaunch as
administrator, which is the honest answer to every "could not read". A staged firmware change ends
in a banner with a button that restarts straight into firmware setup.

# ADR 0006 — Tweaks, apps and repairs: what to take from tools like HyperLis, and what not to

- **Status**: accepted
- **Date**: 2026-09-04
- **Implements**: spec 8.1 (the checkup states benefit and safety), 12.2 (outcomes declare states),
  17.1 / 19.1 (a manifest names an executor and passes checked values, never a command)
- **Related**: [ADR 0004](0004-research-backlog-reconciliation.md),
  [ADR 0005](0005-quarantine-and-dynamic-allowlists.md)

## Context

HyperLis is a Vietnamese tool for the same audience this product is for: a non-technical Windows
owner, and the helpdesk person fixing their machine remotely. It ships a good deal more surface
than PC Orbit does — an optimiser, a one-click app catalogue, Office deployment, edition
conversion, USB-less Windows installation, printer and Windows repair.

That surface is worth taking seriously. It is also built on a different premise: a tweak tool
applies a list of changes and reports that it did. PC Orbit's whole engine exists to answer the
next question — *did it actually change, and can I put it back?* Copying the feature list without
that would produce a worse HyperLis.

So this ADR maps each of those features onto what the engine already does, and sorts them by
whether the safety model can carry them as they are.

## Decision

### Group A — the engine already fits, these are mostly data

A tweak is a **capability state**: a registry value or a service start type, readable, writable,
verifiable by reading back, and undoable by restoring the value read before. That is precisely what
`StateCompiler` + `TransactionEngine` + `UndoPlanner` were built for, so these arrive as graph
nodes and action manifests rather than as new machinery.

| From HyperLis | Here |
|---|---|
| Privacy toggles (telemetry, advertising id, activity history, tailored experiences) | `privacy.*` capabilities, one manifest each |
| App/vendor tracking (Office, Chrome, NVIDIA, Edge) | `privacy.vendor.*`, same shape |
| Explorer conveniences (show file extensions, startup delay) | `explorer.*` |
| Service tuning (SysMain, Print Spooler, Fax) | `service.*`, through the service-state executor |

One new executor covers all of it: a **registry setting executor with a compile-time allowlist of
keys**. The manifest passes a tweak *id*, `ActionParameters.OneOf` checks it against that list, and
the executor looks up the hive, path and value itself. A data contribution can therefore add a
tweak the compiler already knows how to plan, and cannot invent a registry path.

### Group B — worth building, needs one new adapter each

- **App catalogue through winget.** `winget` is documented, present on Windows 11, and — the part
  that matters — *verifiable*: after installing, `winget list --id` says whether the package is
  really there. Uninstall is the inverse. This is the single largest user-visible win available and
  it fits the model exactly. Chocolatey is **not** taken: it needs an install of its own and pulls
  from a feed whose packages are community-maintained, so "we installed what you asked for" would
  stop being something we could stand behind.
- **Bloatware removal.** `Get-AppxPackage` / `Remove-AppxPackage`, per user. Honest about
  reversibility: removal is `reversible: none` from our side — the Store can reinstall it, we
  cannot — and spec 21.7 already requires that to be shown before Apply.
- **Printer repair.** Spooler restart and queue clear. Bounded, reversible in practice, and one of
  the two things helpdesk actually gets called about.
- **Windows file repair.** `SFC` and `DISM /RestoreHealth`. The *scan* is read-only and belongs in
  the checkup; the repair is an action with a restart tier.
- **Firmware advisory.** Not a write. BIOS age, whether Windows Update can even deliver firmware to
  this machine, and a link to the manufacturer's page for this exact model. Built in this change.

### Group C — deferred, with the reason

- **Office deployment (ODT).** Documented and doable, but it is an installer product in its own
  right: a configuration XML, a download phase, a licensing step. It belongs behind the app
  catalogue, reusing its progress and verification, not before it.
- **Edition conversion (Convert SKUs).** `DISM /Set-Edition` and `changepk.exe` are real, and the
  result is genuinely verifiable from `EditionID`. What is not verifiable in advance is the part
  that matters to the user: whether their licence survives. Ships first as **read-only** — which
  edition this machine is, which it could become, and what each would cost in activation terms.
  The write waits for that page to have been in front of real people.

### Group D — refused

- **Installing Windows without boot media (WSAP).** Repartitioning and writing a new OS onto the
  running disk is the one operation in this whole list where a mistake is unrecoverable by
  definition. Nothing in this product's design — checkpoint, verify, undo — survives the machine
  being replaced mid-transaction. It stays out.
- **Turning off SmartScreen, Defender, or error reporting.** This is where the two products differ
  most, and deliberately. Tools in this category ship these as "optimisations"; they are not, they
  are the machine's defences, and the performance they buy back is unmeasurable. PC Orbit's
  position is the inverse: if SmartScreen is **off**, that is a *finding*, and we offer to turn it
  **on**. The same list, read the other way round.

## The line, stated once

A change ships when all four hold:

1. Its state can be **read** from the machine, with evidence.
2. It can be **verified** by reading again afterwards, not by an exit code.
3. It can be **undone**, or its irreversibility is shown before Apply.
4. Turning it on does not make the machine **less safe**.

Group A and B clear all four. Group D fails the fourth or the third.

## Consequences

- One new executor (`RegistrySettingExecutor`) unlocks the whole optimiser surface as data.
- The app catalogue needs a package port and a progress model the UI does not have yet; it is the
  next substantial piece of work after this.
- The differentiator is now written down: **every other tool's list of things to switch off, read
  in the opposite direction.** That belongs in the README, not only here.

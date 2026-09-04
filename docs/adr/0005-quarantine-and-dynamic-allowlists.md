# ADR 0005 — Two primitives that unblock cleanup and startup control

- **Status**: accepted
- **Date**: 2026-09-04
- **Implements**: deep research §8.1 (Smart Cleanup), §8.2 (Startup & Background Controller)
- **Related**: spec 17.1 (a manifest names an executor), 19.1 (the privileged side takes an id plus
  schema-valid parameters, never a command line), 21.9 (undo is a reverse transaction),
  6.4 (verification reads the machine)
- **Follows**: [ADR 0004](0004-research-backlog-reconciliation.md), which deferred both of these
  behind "a primitive we do not have"

## Context

ADR 0004 deferred two features the research puts near the top, and was right about *why* but wrong
to leave it there. "Blocked on a missing primitive" is a description of work, not a reason to stop.
Both blocks turned out to be real and both turned out to be solvable, and the solutions are
different enough to be worth writing down.

### Cleanup had no undo

`UndoPlanner` reverses a transaction by restoring the value each step recorded before it ran. That
works for a registry value, a Windows feature and a refresh rate. It does not work for a deleted
file, which has no before-value to restore — so cleanup could not have the same guarantee as
everything else in the product, and shipping it would have meant one feature whose undo button was
decorative.

### Startup control had no allowlist

`ActionParameters.OneOf` validates every parameter against an allowlist before it reaches a
command. That is the mechanism spec 17.1 relies on, and "whichever startup entry the user clicked"
cannot be enumerated in shipped data. The obvious escapes — trusting the caller, or interpolating
the name into a script — are the two things the rule exists to prevent.

## Decision

### 1. Quarantine: cleanup moves, it does not delete

`IQuarantineStore` (`Core/Cleanup`) with a Windows implementation that keeps a folder per batch
under `%LOCALAPPDATA%\PC Orbit\quarantine`, plus a manifest recording every file's original path.
Restore reads the manifest and moves each file home. A batch outlives its retention window
(30 days) and is then deleted for real — the only place this product deletes anything.

Consequences we accept, in order of how much they cost:

- **The disk does not shrink when you press the button.** It shrinks 30 days later. This is stated
  in the confirmation text and in the result, because the alternative is an undo that quietly does
  not work, and that trade is not close.
- **Files are moved, not copied then deleted.** On one volume that is a rename: it cannot half
  succeed and leave the file in two places, and it needs no free space to perform — which matters,
  since the machine is by definition short of disk. Cross-volume falls back to copy, verify length,
  then delete.
- **Restore refuses to overwrite.** If something recreated the file meanwhile, both copies survive
  and the restore reports what it did not do. Silently overwriting would be a second data loss
  performed by the undo button.

What may be cleaned is deliberately short — user and Windows temp files over a week old, the
thumbnail and icon caches, unsent error reports — and each entry had to clear the same bar: Windows
regenerates it, and putting it back restores the previous state exactly. WinSxS, DriverStore,
`Windows.old`, restore points and the recovery partition are **absent from the scanner**, not
present behind a flag, so no later edit can promote one by changing a boolean. The Recycle Bin and
the Delivery Optimization cache are measured and reported, never touched: emptying a Recycle Bin is
irreversible by definition.

### 2. Dynamic allowlists: the machine's own inventory is the allowlist

An allowlist does not have to be shipped data. It has to be **a finite set the caller cannot
extend**. For a startup entry that set already exists: it is the machine's own startup inventory,
re-read at the moment of the change. `IStartupController.SetEnabledAsync` refuses any name that is
not in it right now, before a command is built — so an invented, mistyped or stale name is
*unusable*, not merely unlikely.

Three things sit on top of it:

- **A fixed refusal list in code.** Windows Security's agents and the BitLocker maintenance tasks
  are never changed, whatever is asked. A tool that offers to disable the antivirus to shave a
  second off sign-in has misunderstood its job, so the refusal is somewhere no setting can reach.
- **Values reach PowerShell as arguments, never as text.** `PowerShellRunner.RunWithArgumentsAsync`
  passes them as process arguments into `$args`; the script stays a compile-time constant. The
  allowlist makes the operation *intended*; this makes injection *impossible*. Both, not either.
- **The result is verified by reading the machine back.** The command's own exit code is not
  evidence that anything changed (spec 6.4).

Enabling and disabling are the same call with a different argument, so the inverse always exists
and no separate rollback path can drift from the apply path.

### 3. Driver Steward, narrowed to what is safe

Shipped as **driver inventory**: device, provider, version, date, signature, and — the part that
matters — Windows' own `ConfigManagerErrorCode` per device. Sorted with faulty devices first and
**never by date**. A driver from 2019 is not a fault; a device with problem code 28 is. Tools that
sort by age and label the top of the list "outdated" are how people are talked into installing
something worse than what they had. Installing drivers remains excluded (spec 23.2).

## Why these are on the safe side of the line

Both new write paths are bounded and reversible in a way firmware writes are not. The worst
outcome of a wrong startup change is that a program starts, or does not, and the inverse call
undoes it. The worst outcome of a wrong cleanup is a file sitting in quarantine for a month, and
restore brings it back. Neither can leave a machine unbootable, and neither can cost someone their
data — which is the test that decides what this product is allowed to automate.

## Consequences

- Two new CLI commands (`clean`, `restore`) and one extended (`startup on|off <name>`), plus
  `drivers`.
- The quarantine round trip is covered by tests including the cases that matter: a file recreated
  while quarantined, a refused category, and a real purge past the window.
- `pco clean` defaults to measuring. The destructive reading of a bare command is the harmless one.
- Smart Cleanup's remaining research scope — duplicate finder, uninstall leftovers, per-app caches
  — is still out. The primitive exists now, so those are ordinary work rather than blocked work.

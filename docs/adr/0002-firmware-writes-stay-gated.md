# ADR 0002 — Firmware writes ship only with evidence from real hardware

- **Status**: accepted
- **Date**: 2026-09-03
- **Implements**: spec 8.3.5, decision 18, decision 19
- **Related**: spec 27.5 (a wrong recommendation destroys trust), 27.12 (BitLocker lockout)

## Context

Spec 23.1.C puts `Auto` firmware control for one Dell line in v0.1, to prove the vendor adapter
contract. Meeting that requires a Dell machine — three of them, in fact, per spec 8.3.5: tested on
at least three models of the line, with and without a BIOS password, with a handled path for
firmware that demands physical presence at the next boot.

None of that hardware was available while this engine was written. The choice was therefore between
shipping an untested firmware write and shipping the contract without the write.

The failure mode is not a bad user experience. It is a machine that does not boot, or one that
demands a 48-digit recovery key its owner has never seen.

## Decision

The vendor adapter **contract** ships. The **write** does not, and the gate is structural rather
than a comment.

1. `DellFirmwareExecutor` exists, probes for the Dell BIOS attribute interface, reports whether
   this machine has it, and then refuses — with an explanation naming the criteria it has not met.
2. Its manifest lives in `data/actions/pending-verification/firmware.dell.actions.json`.
   `ActionCatalogLoader.LoadDirectory` reads `*.actions.json` from `data/actions/` and does not
   recurse, so the compiler cannot see it, cannot select it, and cannot show a user `Auto`.
3. `ActionCatalogLoader` refuses any manifest claiming `writeMode: "auto"` for a `firmware.*`
   capability without a vendor restriction. A data contribution cannot quietly promise automation.
4. Promotion means moving that file up one directory, once the criteria in the folder's README are
   met, with the evidence recorded in `docs/capability-matrix.md`.
5. Every machine gets the **guided** route today. Spec 8.3.2 calls guided the default path for most
   hardware anyway, and it is verified after boot exactly like an automatic write.

Two tests hold this in place: one asserts no loaded action writes firmware automatically, and one
asserts the parked manifest still exists as a contract example but is not in the catalog.

## Why a folder rather than a flag

A `writeMode: "auto"` manifest that matches a vendor is precisely what the compiler is designed to
prefer over a guided route — lower user burden, so it sorts first. A disabled flag inside the file
would be one refactor away from being ignored. A file the loader never opens is not.

It also makes the gate legible in review. "Why is there no Dell auto path?" is answered by a
directory listing and a README, not by reading the compiler.

## What is never allowed

Writing UEFI `Setup` variables by offset (spec decision 18). Community tools do this. The offsets
are undocumented, differ between BIOS builds of the same board, and getting one wrong can leave a
machine unbootable. It contradicts spec 6.7 (official sources first) and sits at the Critical risk
level in spec 19.2, where the only permitted modes are verified vendor interfaces or guided.

There is no code path for it, and adding one requires overturning this ADR.

## Consequences

- v0.1 does not deliver the "one Dell line on Auto" item from spec 23.1.C. That is the honest
  position: the item is gated on hardware, not on code, and the contract it existed to prove is
  proven by the executor and manifest that do exist.
- Users on Dell, Lenovo and HP get the same guided experience as everyone else. Since guided is
  verified after boot the same way, the outcome is the same; only the number of steps differs.
- Promoting the first vendor adapter is now a small, reviewable change: move a file, add matrix
  rows. The uncertainty is in the hardware testing, which is where it belongs.

# ADR 0003 — A CLI is the first interface, not the first UI

- **Status**: accepted
- **Date**: 2026-09-03
- **Implements**: spec step 1, step 6b

## Context

The signature user experience of this product is the Change Basket (spec decision 6). It is also
the thing spec step 6b says must be tested on paper with three to five non-developers — including
at least one Vietnamese speaker and one English speaker — *before* the UI is built. The questions
that test has to answer are behavioural: do they understand what their machine needs, do they dare
press Apply, do they know how to undo it, can they complete the BIOS step from instructions on
their phone.

Meanwhile the engine underneath needs to run on real hardware to be worth anything. Spec step 2
wants a capability matrix built from Intel and AMD desktops, a business laptop, a consumer
motherboard, Home and Pro, a machine with encryption on, and a standard-user account.

Those two needs have different tools.

## Decision

Ship `pco`, a command-line interface, as the first way to use the engine. Do not build the desktop
UI until the paper test has been run.

`PcOrbit.Cli` contains no product logic. Every command reads the machine, asks the compiler,
prints, and hands work to the transaction engine. The desktop UI will sit on the same ports.

## Why

**The matrix needs a tool, not a UI.** `pco scan --json` on ten machines produces the capability
matrix spec step 2 asks for. Doing that through a GUI would be slower and less repeatable.

**The engine gets exercised honestly.** `pco plan` and `pco apply --dry-run` run the entire
compiler and transaction path against real hardware, including the restart-and-resume cycle, with
nothing hidden behind a screen that is still being designed.

**It is a CI gate.** `pco doctor` validates the shipped data against the shipped code and returns
a non-zero exit code. Exit codes are meaningful throughout: `0` ok, `1` error, `2` not reached or
blocked, `3` restart needed.

**It avoids building the UI twice.** The paper test exists because the Simple-mode decisions in
spec 21.4 through 21.9 are worth getting right before they are code. Building a UI first and then
running the test would either waste the work or bias the test.

## What the CLI still has to get right

A CLI is not an excuse to skip the parts of the spec that are about honesty:

- The four-line finding shape from spec 21.6 — what, what you gain, why it is safe, what it costs.
- The plan cost header from spec 21.7, including the count of changes that cannot be undone.
- Exact counts on completion, never a bare "Done" (spec 9.4).
- Full i18n. `--lang vi` is a real translation, plural rules included (spec 21.11).
- Safe Apply with a real countdown and auto-revert on the refresh-rate change (spec 10.1) —
  including the option to extend the countdown that spec 21.12 asks for.
- The "your PC looks fine" state designed properly rather than printed as an empty list
  (spec 21.5).

## Consequences

- v0.1 has no graphical interface. Anyone expecting one from the spec's screenshots will find a
  console instead.
- The Change Basket exists as data — a `Plan` with stages, costs, hashes and per-step reasons — but
  not yet as an interface someone can stage changes into. `plan` and `apply` are the current
  entry points.
- Undo (spec 21.9) is specified and modelled (`Reversibility`, `IActionExecutor.RollbackAsync`) but
  has no command yet. It needs the panel it belongs to, and the panel needs the paper test.
- The CLI stays. Spec 21.12 wants a portable support mode, and a console tool that needs no
  install is most of that already.

# ADR 0001 — C# / .NET for the stack, with a dependency-free domain

- **Status**: accepted
- **Date**: 2026-09-03
- **Closes**: spec 30, "stack desktop cuối cùng: C#/.NET native hay React/Tauri + native core"

## Context

Spec 18.2 offered two directions: native Windows (WinUI 3 or WPF on C#/.NET) or TypeScript UI on
Tauri with a Rust or C# core. It also ruled out one thing explicitly — an Electron-only process
handling all privileged system logic, because UI and privileged execution have to be separated by a
real trust boundary (spec 19.1).

What this product actually does is read and change Windows: WMI/CIM, the registry, DISM, display
configuration APIs, BitLocker cmdlets, and eventually vendor WMI interfaces from Dell, Lenovo and
HP. The UI is the smaller half of the problem, and the part most likely to be redesigned after the
paper UX test that spec step 6b asks for.

## Decision

**C# on .NET 10 (LTS), with WPF for the desktop UI when the UI is built.**

.NET 10 rather than the .NET 8 the spec mentions: .NET 8 support ends November 2026, and there is
no reason to start a multi-year project on a runtime that is nearly out of support. WPF works the
same on both.

And a layering rule that matters more than the framework choice:

- `PcOrbit.Core` targets plain `net10.0` and references **nothing**. Domain only.
- Everything platform-specific lives in `PcOrbit.Adapters.Windows`.
- The only runtime package in `src/` is `Microsoft.Data.Sqlite`, confined to `PcOrbit.Store`.

## Why

**The work is Windows integration.** Roughly everything this product does is a Windows API call or
a PowerShell cmdlet. In C# those are first-party. In Rust they are hand-written bindings; in
TypeScript they are a subprocess boundary either way.

**One language across the trust boundary.** Spec 19.1 requires a privileged helper that accepts
action ids and schema-checked parameters, not shell commands. Sharing the contract types between
UI, orchestrator and helper without a serialisation layer in between is worth a lot for something
that runs elevated.

**The UI is the replaceable part.** Putting the domain in a dependency-free project means the CLI
that exists today and the WPF app that comes later sit on identical seams — and that a UI rewrite
never touches the compiler or the transaction engine.

**Zero dependencies is a security property here.** A privileged process should carry as little
third-party code as it can (spec 19.1). It also forced two decisions that turned out well: a small
ICU MessageFormat subset instead of a full i18n library, and a hand-rolled argument parser instead
of a CLI framework. Both are under 300 lines and fully tested.

## Consequences

- The desktop UI will be WPF. It is unfashionable, but it is stable, it has real accessibility
  support through UI Automation (spec 21.12 requires that), and it does not need a browser engine
  in a privileged process.
- No web-based UI without a rethink. Acceptable: this product has no web surface in its roadmap.
- Contributors need the .NET SDK and a Windows machine. Also acceptable: the target platform is
  Windows 11, and the hardware lab in spec 28 needs real machines regardless.
- `Core` staying dependency-free requires discipline. The check is mechanical: a `using` for
  anything Windows-only in `Core` means a port is missing.

## Alternatives considered

**React + Tauri + Rust core.** Best UI velocity and the smallest binary. Rejected because every
Windows integration becomes hand-written FFI, the team would maintain two languages across the
privileged boundary, and the UI advantage applies to the part of the product most likely to change
anyway.

**TypeScript core with PowerShell subprocesses.** Fastest to a working prototype, and evidence
capture is easy because every query is already text. Rejected because the privileged helper would
still have to be written in something else, and a Node process holding the state machine for
firmware changes is not a trust boundary anyone would defend.

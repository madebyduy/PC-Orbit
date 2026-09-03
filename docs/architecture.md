# Architecture

The layers, the ports between them, and why the seams are where they are.

Spec 7 names seven shared cores and says the UI can change, the number of settings can grow, but
the state, dependency, transaction and verification logic has to be stable from the start. That is
the constraint this structure is built around.

## The shape

```text
                     ┌──────────────────────────────────────────┐
                     │  data/  (shipped, versioned, reviewable) │
                     │  graph · outcomes · actions · guides ·   │
                     │  string catalogs                         │
                     └────────────────────┬─────────────────────┘
                                          │ loaders validate on read
                                          ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│ PcOrbit.Core                                    net10.0, zero dependencies  │
│                                                                              │
│   Graph ──────► Compiler ──────► Transactions ──────► Events                 │
│     │              │                  │                                      │
│     └── Checkup    └── Preflight      └── Guides                             │
│                                                                              │
│   Abstractions/  the ports: IStateScanner, ICapabilityReader, IClock,        │
│                  IIdGenerator, IBootSession, IElevationContext,              │
│                  ISnapshotStore, ITransactionStore, IEventLog,               │
│                  ISafeApplyConfirmation, IActionExecutor                     │
└──────────────────────────────────────────────────────────────────────────────┘
            ▲                                              ▲
            │ implements                                   │ implements
┌───────────┴──────────────────────┐        ┌──────────────┴──────────────────┐
│ PcOrbit.Adapters.Windows         │        │ PcOrbit.Store                    │
│ net10.0-windows10.0.19041.0      │        │ SQLite, WAL, synchronous = FULL  │
│ WMI/CIM · registry · Win32 ·     │        │ snapshots · checkpoints ·        │
│ DISM · wsl.exe · guided firmware │        │ append-only event log            │
└──────────────────────────────────┘        └──────────────────────────────────┘
            ▲                                              ▲
            └───────────────────┬──────────────────────────┘
                                │
                     ┌──────────┴───────────┐
                     │ PcOrbit.Cli          │
                     │ composition root      │
                     └──────────────────────┘
```

## Why Core has no dependencies

`PcOrbit.Core` targets plain `net10.0` and references nothing. That is enforcement, not taste:

- The compiler and the transaction state machine are testable without a Windows machine, which is
  what makes the golden tests in spec 28 possible at all.
- A `using` for something Windows-only inside `Core` is an immediate signal that a port is
  missing, rather than a slow drift towards a domain that can only run in one place.
- Spec 19.1 wants as little third-party code as possible near privileged execution. The only
  runtime package anywhere in `src/` is `Microsoft.Data.Sqlite`, and it is confined to `Store`.

Time, ids and the current boot session are ports for the same reason: a plan hash that changed
with the clock would make "the plan changed, review it again" meaningless.

## The flow of one `apply`

```text
1  Scanner reads the machine                        → StateSnapshot (every reading has evidence)
2  Workload nodes are derived from the graph        → workload.docker-wsl2-ready = ready/notReady/unknown
3  Compiler diffs snapshot against the outcome      → Plan (stages, restart boundaries, cost, hash)
4  Preflight runs the checks the plan asked for     → PreflightReport (blockers stop here)
5  User reviews the cost header and confirms
6  Engine.Begin locks the reviewed hash             → Transaction (carries the plan with it)
7  Per stage: apply each step, then verify by re-reading the machine
8  At a restart boundary: checkpoint, state = AwaitingRestart, return
9  After the reboot: Resume verifies the afterRestart checks, then continues
10 Final verification runs the outcome's own checks → Completed | PartiallyCompleted | Failed
11 Every step and state change writes a normalised ChangeEvent
```

Steps 7 and 10 are separate on purpose. Individual steps can all pass while the outcome is still
not reached, and spec 9.4 forbids calling that "Done".

## Key design choices

### Restart tiers, not a restart per action

A step's tier is one more than its deepest dependency that only takes effect after a restart.
Everything in a tier runs before the same reboot. That is how spec 21.8's promise — one plan, one
restart whenever dependencies allow — is actually kept rather than being a UI claim.

A stage's declared restart is also skipped when nothing in it reached a state that needs one: if
every step failed, a reboot achieves nothing, and asking for one anyway is the kind of small
dishonesty that costs trust.

A firmware restart doubles as a Windows restart, because the machine boots back into Windows
either way. That is why the firmware stage and the pending component changes share one reboot.

### Verification is a separate port

`IActionExecutor` applies and rolls back. It has no `Verify`. Verification goes through
`ICapabilityReader`, which reads the machine again.

This is the difference between "the code that made the change says it worked" and evidence. Spec
8.3.2 is explicit that automatic mode does not get to skip verification, and there is a test where
a scripted executor reports success while changing nothing — the transaction fails, as it should.

`ICapabilityReader.Invalidate()` is part of the contract rather than an implementation detail:
reading a Windows machine is expensive enough that any real implementation caches, and a cache is
exactly how you end up verifying a change against the value from before it.

### The plan travels with the transaction

A `Transaction` holds its `Plan`, so a checkpoint is one self-contained record. After a reboot,
resume replays the action manifests as they were when the user approved them — even if the shipped
catalog moved on in between (spec 17.1).

### Unknown propagates

`CapabilityValue.Unknown` never satisfies anything, including itself. It flows through the workload
evaluator (a workload with an unreadable requirement is Unknown, not "not ready"), through the
checkup (no finding is raised from one), and through verification (`verify.unreadable` is a
distinct outcome from `verify.mismatch`).

The place this matters most is drive encryption: reading it needs administrator rights, so on a
standard-user run it is Unknown — and that still blocks a firmware plan, because a failed read is
not evidence the drive is unencrypted.

### One reading that lies, handled deliberately

`Win32_Processor.VirtualizationFirmwareEnabled` reports false when a hypervisor is already running,
because the extensions are claimed. Two independent mitigations:

1. The scanner checks `HypervisorPresent` first and reports firmware virtualization as enabled with
   that as the evidence, since no hypervisor can start without it.
2. The compiler has a general rule: if something that *requires* a capability is satisfied, the
   capability is treated as satisfied even when its own reading disagrees, with an `Info` issue
   explaining why.

Either alone would do. Both is right, because spec 8.3.4 lists "I turned it on but the app still
says off" as a support case that has to be handled well.

### Data is a contract, not configuration

The graph, outcomes, manifests, guides and strings are shipped JSON with JSON Schema alongside, and
the loaders validate on read rather than trusting the file:

- a `requires` edge needs high-confidence evidence, because the compiler plans from it;
- an edge with no evidence at all is refused;
- an action with no verification or no stated reversibility is refused;
- `guidedVerified` with no machine it was verified on is refused;
- `writeMode: auto` for a `firmware.*` capability with no vendor restriction is refused.

Bad data fails loudly at load. A plan built from data with holes in it is worse than no plan.

## What is not built yet, and where it will go

| From the spec | Where it lands |
|---|---|
| Desired State & Drift (13) | new `Core/DesiredState`, reading snapshots and events that already exist |
| Timeline UI (14) | reads `Store` — the event schema is already frozen |
| Regression Intelligence (14.4) | correlates events by `RelatedRestart`, which every event already carries |
| Hardware Path Intelligence (15) | new node kind `ConnectionPath` is already in the graph schema |
| PC Blueprint (16) | serialises desired state plus snapshots; needs the reconciliation rules first |
| Privileged helper (18.1, 19.1) | `IActionExecutor` becomes an IPC boundary; the action-id-plus-parameters contract is already the shape it needs |
| Desktop UI (21) | sits on the same ports the CLI uses; `Cli` holds no product logic |

The privileged helper is the one worth calling out. Today the CLI process does everything and asks
Windows for elevation the ordinary way. The split into a UI process and a signed helper is spec
19.1's trust boundary, and the reason it is not disruptive later is that `IActionExecutor` already
takes an action id and schema-checked parameters rather than a command line — which is exactly what
crosses that boundary.

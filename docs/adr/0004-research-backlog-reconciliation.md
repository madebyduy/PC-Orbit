# ADR 0004 — What the deep-research backlog adds, and what it does not

- **Status**: accepted
- **Date**: 2026-09-04
- **Implements**: spec 23.2 (non-goals), spec 30 (open questions)
- **Related**: spec 6.6 (`Unknown` is never inferred), 8.1 (a score only presents findings),
  21.6 (a finding states the benefit and the safety of its own fix), 17.1 (a manifest names an
  executor)
- **Supersedes**: nothing. It closes an open question rather than reversing a decision.

## Context

Two deep-research documents describe a much larger PC Orbit than the one spec v3 scopes:

- `docs/research/PC_Orbit_Deep_Research_Functions_2026-09-04.docx` — fourteen systems, P0 to P2.
- `docs/research/PC_Orbit_Deep_Research_Functions_Extended_2026-09-04.pdf` — the same fourteen,
  plus a new §8 listing roughly fifty additional modules.

The two overlap almost completely. The genuine delta is §8, and §8 is a brainstorm: most entries
are a title, one sentence of user value, and a proposed mode. That is a reasonable output for
research and an unreasonable input for a backlog, because a one-line entry hides whether the thing
can be read, verified, or undone — which is the only question this codebase cares about.

Worse, §8 quietly reopens four things spec 23.2 closed: a driver updater, automatic performance
optimisation, cloud migration, and a library of repair scripts. Left unresolved, the repository
would carry a spec that forbids them and a backlog that schedules them.

CLAUDE.md says that when code and spec disagree we say so rather than quietly picking one. The
same applies when research and spec disagree.

## Decision

The research is accepted as **input**, not as a plan. Each proposal is sorted by the cost of the
primitive it needs, not by its priority score, because a P0 that needs an undo primitive we do not
have is further away than a P1 that needs none.

### Accepted — implemented in this change

These need no new safety primitive. Every one is read-only, or reuses the transaction engine
exactly as it stands.

| Proposal | Why it was cheap | Where it landed |
|---|---|---|
| Security Compatibility Planner (#10) | `firmware.secure-boot`, `firmware.tpm.*` and `firmware.boot-mode` were already scanned, localised and displayed — and drove no edge, outcome or rule | `workload.windows11-ready`, `outcome.windows11-ready`, `Windows11ReadinessRule` |
| BIOS Baseline & Diff (§8.5) | Snapshots are already stored in SQLite with evidence per reading; a diff is a pure function over two of them | `Core/Compare/SnapshotDiff`, `pco diff` |
| Recovery Readiness & Emergency Kit (#1) | New capabilities, but ordinary ones: read, evidence, Unknown-with-a-reason | `recovery.*` nodes, `RecoveryReadinessRule` |
| Root-cause Timeline, external sources (#3) | The event log and `pco history` already exist; this adds a read-only port for changes PC Orbit did not make | `IChangeSource`, `WindowsChangeSources`, `pco timeline` |
| Startup inventory (§8.2) | Read-only inventory is just another observation adapter | `IStartupInventory`, `pco startup` |
| Update Regression Guard (§8.2) | Falls out of the timeline: update events and change events on one axis | folded into `pco timeline` |

### Accepted in principle, deferred — blocked on a primitive we do not have

Recorded so they are not rediscovered, and explicitly not scheduled.

- **Smart Cleanup (§8.1)**. `UndoPlanner` restores the *before-value* a step recorded. A deleted
  file has no before-value, so cleanup cannot be undone by the engine that undoes everything else.
  It needs a quarantine store with retention, a measured reclaim figure, and per-item restore.
  Until that exists, offering cleanup would break the one promise the product is built on.
- **Startup entry disable (§8.2)**. The inventory ships; the write does not. `ActionParameters`
  validates every parameter against an allowlist before it reaches a command (spec 17.1), and
  "whichever startup entry the user picked" is not an allowlist. Enabling this means designing a
  parameter kind for caller-supplied identifiers that are validated against *the machine's own
  current inventory* rather than against shipped data. That is a real design decision and gets its
  own ADR.
- **Boot & Recovery Orchestrator (#4)**. Requires running outside Windows — WinRE, or boot media.
  That is infrastructure, not a module, and nothing in the current process model reaches it.
- **Driver Steward (#5)**, narrowed. Driver *inventory* and *rollback* are readable and reversible
  and are welcome later. Installing "the newest driver" is what spec 23.2 excludes, and the
  research's own guard rails (no update-all, no aggregate driver sources) agree. If it returns, it
  returns as **Driver Inventory + Rollback**.
- **Install / Reinstall Concierge (#2)**, **Migration Blueprint (#11)**, **Privacy-safe Support
  Bundle (#7)**. Each is a product in its own right. They stay in research until the vertical
  slice has been through spec step 6b with real people.

### Rejected — removed from the backlog

Not deferred. These do not belong in this product, and carrying them as "someday" items misleads
whoever reads the backlog next.

| Proposal | Why not |
|---|---|
| Noise Source Finder (§8.5) | No independent source can verify "the noise is the pump". A conclusion PC Orbit cannot read back is a guess with a UI, which is spec 6.6 inverted |
| Dust & Maintenance Planner (§8.5) | A calendar reminder. It changes nothing, reads nothing, and verifies nothing |
| Energy Cost Tracker (§8.6) | Estimated wattage times an estimated tariff. Two guesses multiplied, presented as a number |
| File Organization Assistant (§8.3) | A general-purpose file manager. Unbounded scope, and the highest accidental-data-loss surface in the whole list, for no PC-control benefit |
| Download Reputation & Signature (§8.4) | SmartScreen and Defender already do it, in the kernel, with a reputation service. A second opinion with less data is worse than no second opinion |
| What can my PC do? Advisor (§8.7) | Resolves to a composite score. Spec 8.1 allows a score only as a *presentation of findings*; this one would be the finding |

### Not modules — requirements that apply across the product

Filed as such so they stop appearing as backlog rows:

- **Family / Senior Mode** (§8.4) is spec 21.4's simple mode. It is a property of every screen.
- **Offline Phone Companion** (§8.7) already exists: `data/guides/*.guide.json` carries the
  per-model steps and `docs/ux/mockup/PhoneGuide.dc.html` is the screen.
- **Second-opinion Mode** (§8.7) is how hypothesis ranking must behave, not a place to navigate to.

### Merged

Fifty entries collapse to roughly fifteen once the duplicates are resolved:

- *Why is my PC slow* + *Performance Root-cause Profiler* + *Freeze/Hang Recorder* +
  *Gaming & Creator Stability Lab* → one ETW capture-and-interpret subsystem.
- *App Repair* + *Broken Uninstaller Rescue* + *Application Conflict Detector* +
  *Default Apps Repair* + *Runtime & Dependency Doctor* → one application-state subsystem.
- *Post-malware Recovery* + *Suspicious Persistence Scanner* + *Ransomware Readiness* → a
  persistence **inventory** only. The rest is antivirus territory and PC Orbit is not one.

## Consequences

- Spec 23.2 stands. Nothing in this change adds a driver updater, an optimiser, a migration tool
  or a script library.
- The graph grows from 32 nodes to 40, and every node now declares `observable` explicitly rather
  than relying on the schema default — because the new `recovery.*` readings are precisely the
  kind that read Unknown without elevation, and that has to be visible in the data.
- Three things the research got right and the codebase did not have are adopted directly:
  **knowledge-pack expiry** (a manifest verified eighteen months ago is not evidence today),
  **snapshot freshness** (a stale snapshot should not silently compile a plan), and
  **counting what could not be read** next to the health score instead of scoring around it.
- Rejected items are recorded here rather than deleted silently, so the next person to read the
  PDF finds the reason and does not re-propose them.

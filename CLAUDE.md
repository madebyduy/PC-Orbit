# Working in this repository

PC Orbit is a privileged tool that changes people's machines. The conventions below are not style
preferences — most of them exist because the alternative is a user with a PC that will not boot,
or one who stops trusting anything the app says.

Read [`README.md`](README.md) for what the project is, and
[`PC_CONTROL_PROJECT_SPEC_v3.md`](PC_CONTROL_PROJECT_SPEC_v3.md) for the product it is heading
towards. The spec is the source of truth; when code and spec disagree, say so rather than quietly
picking one.

## Build and test

```bash
dotnet build
```

```bash
dotnet test
```

```bash
dotnet run --project src/PcOrbit.Cli -- doctor
```

`doctor` validates the shipped data against the shipped code. Run it after any change under
`data/`; CI does the same.

Warnings are errors. Analyzers are on at `latest-recommended`. Two rules are switched off
deliberately, with the reason recorded in [`.editorconfig`](.editorconfig) — do not disable a third
without writing down why.

## Where things go

| Put it here | When |
|---|---|
| `src/PcOrbit.Core` | Domain logic. Targets plain `net10.0`. **No Windows APIs, no file or process I/O, no `DateTimeOffset.Now`.** If you need one, you need a port. |
| `src/PcOrbit.Adapters.Windows` | Anything platform-specific: WMI/CIM, registry, Win32, DISM, `wsl.exe`. |
| `src/PcOrbit.Store` | Persistence. SQLite only. |
| `src/PcOrbit.Cli` | Composition root and console output. No product logic. |
| `data/` | Graph, outcomes, action manifests, guide data, string catalogs. Versioned and reviewable. |
| `schemas/` | JSON Schema for each of those. Update it in the same commit as the data shape. |

The rule that keeps this honest: if a change to `Core` needs a `using` for something Windows-only,
stop and add a port instead.

## Non-negotiables

**`Unknown` is never inferred into a value.** A failed read produces
`CapabilityValue.Unknown` plus an `Evidence` that says why. Never `Disabled`, never `false`, never
a default. Spec 6.6, 21.3, 27.13.

**Every reading carries evidence.** Source kind, what was queried, the raw result, and a
confidence. This is what lets the UI answer "how do you know?" and lets a support report be
audited months later.

**Verification reads the machine, not the executor's opinion.** New verification goes through
`ICapabilityReader`. If you find yourself trusting an `ApplyOutcome` to decide whether something
worked, that is the bug.

**No user-visible string in code.** Everything goes through the string catalog by key, with ICU
arguments. `--verbose` diagnostics and `doctor` are the exception: they are developer output, and
spec 21.11 wants evidence and logs to keep their original values.

Adding a key means adding it to **both** `en.json` and `vi.json`. A test enforces parity and a test
formats every pattern, so a missing key or an unbalanced brace fails the build.

**A manifest never carries a script.** It names an executor. Executors live in the allowlist in
`PcOrbitHost.Create`. Any parameter that reaches a command goes through
`ActionParameters.OneOf` against an allowlist first.

**Firmware writes stay gated.** No `writeMode: "auto"` for a `firmware.*` capability without a
vendor restriction — the loader refuses it. Promoting a vendor adapter means moving its manifest
out of `data/actions/pending-verification/` after meeting the criteria in that folder's README,
with evidence recorded in `docs/capability-matrix.md`. Never write UEFI `Setup` variables by
offset (spec decision 18).

## Adding things

**A new capability**: add a node to `data/graph/core.graph.json` with a `displayKey` and search
aliases, add the string to both locales, teach the scanner to read it (with evidence), then run
`doctor`. If it cannot be read on a typical machine, set `observable: false` so the checkup does
not raise findings from it.

**A new edge**: only `requires` edges drive planning, and the loader demands high-confidence
evidence for those. Use `affects`, `limits` or `supportedBy` for relations that explain a reading
without authorising a change (spec 11.2, 11.3).

**A new action**: write the manifest (`detect` via `provides`/`requires`, `preflight`, `verify`,
`reversible` — all mandatory), implement the executor, register it in the allowlist, and add a
golden test showing the plan it produces. An action whose reversibility is `none` has to be worth
it: the UI shows that before Apply.

**A new outcome**: declare required capability *states*, never actions. Choosing the action is the
compiler's job, per machine (spec 12.2). Include `verify`.

**A new checkup rule**: it must be able to state the benefit and the safety of its own fix. A rule
that cannot is not ready to ask a non-technical person to press Apply (spec 21.6).

## Tests

Golden tests in `StateCompilerTests` run against the **shipped** data, not fixtures. That is
intentional: a graph edit that changes what a plan does should fail there loudly. When one fails,
read the diff — it is rendered as a plan, so it usually tells you whether the change was intended.

Cover the failure modes, not just the happy path. Spec 28 asks for fault injection, and the
existing tests include: a step that fails, an executor that lies about success, an executor that
throws, a value that becomes unreadable, a reboot in the middle, and resume in the wrong boot
session. New engine behaviour needs the equivalent.

## Commit and review

- One concern per commit. Data changes and the code that depends on them belong together.
- If a change makes the app promise more than it can verify, that is the thing to flag in review.
- `docs/adr/` is for decisions that were genuinely open. If you close one of the open questions in
  spec 30, write the ADR.

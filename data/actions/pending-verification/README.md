# Actions pending hardware verification

Action manifests in this folder are **not loaded**. `ActionCatalogLoader.LoadDirectory` reads
`*.actions.json` from `data/actions/` only, and does not recurse — so nothing here can be selected
by the State Compiler, offered in a plan, or shown to a user as `Auto`.

This is the gate for spec 8.3.5 and decision 19, expressed as something a reviewer can check
rather than as a comment in code.

## What lives here

| File | Why it is held back |
|---|---|
| `firmware.dell.actions.json` | Automatic (`Auto`) firmware write for CPU virtualization on Dell systems. The executor contract exists and probes for the Dell BIOS attribute interface, but no write has been verified on real hardware. |

## Promoting a manifest

Move the file up into `data/actions/` only when **all** of the following are true, with evidence
from real machines recorded in `docs/capability-matrix.md`:

1. The change goes through an **officially documented** vendor interface — Dell WMI / Command
   Configure, Lenovo WMI, HP BCU or equivalent. Never by writing UEFI `Setup` variables at an
   offset (spec decision 18: undocumented, differs per BIOS build, can leave a machine unbootable).
2. Tested on **at least three models** of that line, in both states: **with** and **without** a
   BIOS password set.
3. There is a handled path for firmware that demands **physical presence confirmation** at the next
   boot, including what the UI says while waiting for it.
4. BitLocker / Device Encryption preflight runs before it, with no exception (spec 10.3).
5. Post-restart **verification runs exactly as it does for the guided route**. Auto does not get to
   skip verification (spec 8.3.2).

Until then, the machine gets the guided route, which is the default path for most hardware anyway
(spec 8.3.2) and is verified after boot the same way.

## Why not just ship it disabled in the main folder

Because a `writeMode: "auto"` manifest that is compatible with a vendor is exactly what the
compiler is designed to prefer over a guided route. A flag inside the file would be one refactor
away from being ignored; a file the loader never opens is not.

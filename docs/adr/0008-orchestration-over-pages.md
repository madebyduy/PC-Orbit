# ADR 0008 — Orchestration before another page

**Status:** accepted
**Date:** 2026-09-05
**Source:** the investment memo of 2026-09-05 (`PC_Orbit_UX_Wow_Feature_Investment_Memo`), a static
audit of this repository against platform documentation. Its diagnosis was checked against the code
and found to be right in every particular it cited.

## The diagnosis we accept

The app had sixteen pages across six sections and shipped four outcomes. Everything a person might
need was somewhere in it, and a person with a problem had to know where. The rail is organised by
subsystem — firmware, storage, drivers, changes — and problems are not: "my PC got slow after
yesterday's update" is a timeline question, a driver question and a recovery question at once, and
nothing in the app knew that except the user.

Three things the memo pointed at were exactly true:

- `TimelineBuilder.Preceding` — the primitive that answers "what changed in the window before the
  symptom" — had tests and no caller.
- The string `app.search` ("Tìm cài đặt, công cụ, ứng dụng…") shipped in both catalogues and was
  used by nothing.
- The dashboard hid its Fix button when a finding had no outcome. That was the right call about
  automation and the wrong call about the person reading it: a warning with nowhere to go.

## Decision

**Fund orchestration before adding another top-level page.** Four pieces, all built from
capability that already existed, none adding a new way to change the machine:

| Piece | What it is | What it reuses |
|---|---|---|
| **Mission Center** | A sentence in the user's words becomes the right page or outcome, with cost and way back stated first | pages, outcomes, the string catalogue; matching is accent-folded and deterministic |
| **A route on every finding** | `Finding.NextRoute` is never null for a shipped rule; a test refuses a dead end | outcomes, pages, guides, `ms-settings:` deep links, vendor front doors |
| **Incident Mode** | Pick when the trouble started; see the 48 hours before it; direct evidence told apart from coincidence; the smallest reversible test per row | `TimelineBuilder.Preceding`, the timeline sources, undo |
| **Recovery Ladder** | Six ways back, gentlest first, each with its readiness read from the machine | WinRE, BitLocker, restore-point and build readings; the ISO page; Windows' own recovery screens |

### The one type that holds it together

`Route` — kind and target — is what a finding, a mission card, an incident test and a ladder rung
all produce, and `FollowRoute` in the app is the one place any of them is opened. The four surfaces
cannot drift on what "open" means, and a route the app cannot open is refused by the loader and by
`doctor` before it reaches a click.

### Missions are data

`data/missions/*.missions.json`, with a schema, like outcomes and guides. A mission carries no
script and no action. It points at a page or at an outcome, and an outcome is where a change is
planned per machine, verified, and undone like any other. Adding a mission is a reviewed data change;
it cannot add a way to change the machine that the compiler does not already have.

Matching is deliberately dumb. Every typed word has to land in an alias or the title, whole-word
matches outscore prefixes which outscore substrings, ties break on catalogue order, and there is no
fuzzy distance. A search that turns "docker" into "locker" sends someone to the wrong page with
confidence, and a search that reorders itself between keystrokes is one nobody trusts.

### Evidence language in Incident Mode

Every row in the incident window is labelled either *direct evidence* — a crash, a device error, the
symptom's own trace — or *same time* — an update, a driver, one of our own transactions, which
happened first and that is all it proves. The memo asked for that line on every row and it is
there, because "the update did it" is the conclusion people reach without help, and the honest tool
is the one that says how sure it is.

## What we did not accept, or deferred

- **A generic chatbot** that turns sentences into commands. Missions are a finite, reviewed set;
  the matching is a lookup, not a model. This is a boundary, not a gap.
- **Recovery Ladder resuming across reboot and verifying the outcome.** The rungs are offered with
  readiness and consequences, and they hand off to Windows' own recovery screens. Persisting a
  mission across the reboot those screens cause is real work and is the next release, not this one.
- **Proactive Watch, PC Blueprint, support bundles, hardware trends, driver rollback.** Priority 1
  and 2 in the memo, and left there. Each is a real feature; none is orchestration.
- **Another top-level section.** The rail stays at six.

## Consequences

- The dashboard opens with the question instead of the score: *what do you want to do with this
  PC?* The score is still there, below it.
- A finding is not allowed to ship without a route. `MissionsAndRoutesTests.NoFindingIsADeadEnd`
  provokes every rule on a machine where everything is wrong and fails the build if any finding
  has nowhere to go.
- `Ctrl+K` from anywhere is the mission box.
- Adding a page means adding a `PageKeys` constant, so a mission that points at a page nobody built
  fails in `doctor`.
- The memo's success gates are recorded here so the next audit can check them: users reaching the
  right mission without browsing the rail (target ≥ 80 % in moderated tests); findings with a
  visible, safe next route (100 %, now enforced); incident rows separating correlation from direct
  evidence (100 %, by construction). The first is a usability test nobody has run yet, in
  Vietnamese, with five to eight people; that is the measurement this decision still owes.

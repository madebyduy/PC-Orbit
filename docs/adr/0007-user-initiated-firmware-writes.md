# ADR 0007 — A user-initiated firmware write is not the thing the gate was built for

**Status:** accepted
**Date:** 2026-09-04
**Supersedes nothing.** Narrows the scope of the gate described in
[`data/actions/pending-verification/README.md`](../../data/actions/pending-verification/README.md).

## The question

The product could read firmware state and could not change it. The only route offered was "go into
the BIOS screen yourself", and the owner asked, reasonably, why a tool that changes everything else
about their PC stops at the one place they find hardest to reach.

The obstacle was our own rule. `pending-verification/README.md` holds firmware writes back until a
vendor adapter is tested on three models of a line in both password states. That bar is not
reachable by anyone who owns one PC, so read literally it means firmware writes never ship.

## What was actually being gated

Re-reading the README, every criterion in it is about a write the **State Compiler picks on its
own** and puts in a plan:

> Because a `writeMode: "auto"` manifest that is compatible with a vendor is exactly what the
> compiler is designed to prefer over a guided route.

The danger there is specific and it is not "firmware is scary". It is that a machine the compiler
has never met gets a firmware write selected for it automatically, inside a plan whose other steps
the user is really reading, because a manifest declared itself compatible. The user in that story
never chose the firmware change; they chose an outcome.

A person opening a BIOS page, reading what one setting costs, and pressing a button is a different
story with a different failure mode. Treating them as the same thing was a category error on our
part, and it cost the product its most-requested feature.

## Decision

**A firmware write that a person chooses, one setting at a time, is allowed. A firmware write the
compiler chooses stays gated exactly as it was.**

Concretely, the user-initiated route:

- is **not an action manifest**. `IFirmwareSettings` is a service like `IEditionService`; the
  State Compiler cannot reach it, cannot plan it, and cannot prefer it over a guided route.
- goes only through an interface the **manufacturer publishes** — Lenovo's WMI classes, HP's
  `HP_BIOSSettingInterface`, Dell's `DCIM_BIOSEnumeration`. Never a UEFI `Setup` variable written at
  an offset (spec decision 18 stands, untouched).
- runs the **BitLocker preflight with no exception**, which was criterion 4 and is the one that
  actually protects data.
- **verifies by reading the firmware back**, which was criterion 5. A vendor method returning
  `Success` is a claim.
- **refuses** the changes whose damage is not undone by changing them back.

## What is refused, and why that list is short

`FirmwareRisk.Refused` is not for dangerous settings. Dangerous settings are the point of the
feature. It is for changes where *putting the setting back does not put the machine back*:

| Refused | Because |
|---|---|
| Clear Security Chip / TPM Clear | Destroys the keys BitLocker, Windows Hello and stored certificates are built on. Setting it back gives you a working chip with none of the old keys in it. |
| Set / clear a firmware password | An owner who mistypes once through an automated path has no reset that does not involve the manufacturer. |
| Load Setup Defaults | Reverts every setting at once, including ones this product never saw, with nothing to compare against afterwards. |
| Physical-presence and secure-flash flags | Change what the *next* boot will accept without asking again. |

Turning Secure Boot off is not on that list. It is `Serious`, it names the recovery-key consequence
before the button, and it makes you type the setting's name — and then it does what the owner asked.

## Confirmation, sized to the risk

Three levels, decided by `FirmwareRiskTable` from the vendor's own name for the setting:

- **Routine** — cosmetic. Ordinary confirmation.
- **Careful** — changes behaviour; putting it back is choosing again. Consequences, then confirm.
- **Serious** — can stop the machine starting, or put an encrypted disk behind a recovery key.
  Consequences, preflight, and **typing the setting's name** with the button disabled until it
  matches.

An unrecognised setting is **Careful, never Routine**. A machine can carry settings this table has
never heard of, and the safe reading of "we do not know what this does" is not "it is harmless".

The table lives in code rather than in `data/`. Shipped data is reviewed, but it is also editable by
anyone who can reach the folder, and this table is the difference between a dialog and a machine
that will not boot.

## The allowlist

Per ADR 0005: an allowlist does not have to be shipped data, it has to be a finite set the caller
cannot extend. Here it is the firmware's own. The settable names are the ones the vendor interface
enumerated in that same read, and the acceptable values are the ones that setting itself declared.
Nothing a caller invents reaches the vendor call.

Shipping a static list of Lenovo setting names would have been the worse choice twice over: it
would go stale per model, and it would be a second source of truth about what a machine has.

## Passwords

Where the firmware has a supervisor password, it is collected in a `PasswordBox`, handed straight to
the vendor call, and cleared. It is never stored, never logged, never written into evidence, and
never put in a plan. This product has no feature that remembers a firmware password.

## What we cannot claim

**The write path is unverified on hardware.** The machine this was built on is a consumer Lenovo:
all four Lenovo WMI classes are registered by its driver and every one returns zero instances,
because that provider only populates on the ThinkPad and ThinkCentre lines. So detection, refusal,
classification, preflight and the "no interface here" path are all exercised against a real
machine — and the vendor call itself is not.

That is stated in three places rather than hidden: here, in `docs/capability-matrix.md`, and in the
suite that covers it. What follows from it:

- The pure logic — risk table, allowlist, preflight, plan — is unit-tested and does not need
  hardware.
- The UI never claims a success it has not read back. A write that reports `Success` and does not
  verify says *"could not confirm it"* and tells the user to check the BIOS screen, because some
  firmware holds a change as pending until the restart.
- `pco bios --verbose` exists so that anyone with a supported model can report what their firmware
  says, which is the evidence `capability-matrix.md` asks for.

## Consequences

- Most consumer hardware will see the "your model does not expose this" message. That is the
  correct answer for that hardware and it is worth saying plainly, rather than showing switches
  that quietly do nothing.
- `data/actions/pending-verification/firmware.dell.actions.json` stays where it is. Nothing here
  promotes it, and the compiler still cannot select a firmware write.
- If a future model reports a setting whose name lands in the wrong risk bucket, the fix is a table
  entry and a test — not a change to how confirmation works.

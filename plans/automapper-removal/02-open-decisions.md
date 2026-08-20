# AutoMapper Removal — Open Decisions

> **Mirror.** The canonical copy of this plan lives in the `.shift` knowledge-base repo at
> `.shift/repos/shift-entity/automapper-removal/`. This copy exists so the plan is readable without
> cloning `.shift`. Keep both in sync; if they ever disagree, `.shift` wins.

**Created:** 2026-08-19 · **Rescoped:** 2026-08-20 — framework only
The judgment calls the team must make. Each changes what gets built. Recommendations are given, but none of
these has a default that is obviously right — that is why they are here rather than decided in
[`01-steps.md`](01-steps.md).

Record the answer inline (edit this file) and reflect it in [`STATUS.md`](STATUS.md).

> **Rescope note.** Q5 and Q6 were dropped when the plan narrowed to the framework — both were consumer-scope
> questions. Q9 and Q10 replace them, and both exist *because* of the narrowing. **Numbering is deliberately
> stable**: the other three docs reference these by number.

---

## Q1 — Does the generator actually run in package-mode consumers?

**Status:** ❓ unanswered · **Blocks:** everything · **Owner:** —

The evidence contradicts itself. `shiftsoftware.shiftentity.efcore.nuspec:17` carries
`exclude="Build,Analyzers"` on the Core dependency, and `dotnet build -getItem:Analyzer` in a package-mode
consumer data project returned only SDK analyzers. Yet fresh `Generated_*.g.cs` files exist in package-mode
`obj/` trees, which a later check confirmed directly.

This is not a preference — it is a fact to establish. If the generator does **not** reach package-mode
consumers, then "an explicit mapper is required" is unreachable through NuGet, Stage D enforces something
nobody can satisfy, and no consumer can ever migrate no matter how good the guide is.

**Action:** Step 0.1 — reproduce it on the `dotnet new shift` project the Builder already creates, which is a
package-mode consumer we own. **Answer this before anything else starts.**

---

## Q2 — Nullable FK: clear or preserve on a null `ShiftEntitySelectDTO`?

**Status:** ❓ unanswered · **Blocks:** Stage E divergence triage · **Recommendation: clear**

AutoMapper preserved the existing FK when the incoming select DTO was null. The generated mapper clears it.

The framing "this is data loss" is misleading: `ShiftAutocomplete` posts `null` when the user clears a
dropdown, so under AutoMapper **"clear this dropdown" was a silent no-op** — the user cleared the field, hit
save, and the old value came back. The new behavior fixes that. `SourceGeneratedMappingTests.cs:367` already
pins clear-on-null as a green test.

You cannot have both as a default.

**Recommendation:** keep clear-on-null, and add `map.PreserveForeignKeyOnNull(x => …)` as a per-member, baked
opt-out — so every nullable select member in scope becomes an explicit migration decision rather than a
silent one. Release-note it. The opt-out has to live in the **framework** even though most of the ~21 affected
members are downstream: a consumer cannot add a baked opt-out to a generator it does not own.

---

## Q3 — On the wire, is an empty `ShiftEntitySelectDTO` `null` or `{Value:""}`?

**Status:** ❓ unanswered · **Blocks:** Step B8 scope · **Recommendation: make it global**

The hash-id converter already collapses blank to `null`, so hash-id'd properties changed behavior long ago and
nobody noticed. The remaining ~10 non-hash-id'd properties still emit `{Value:""}`.

**Options:**
- **Make it global** — one converter registration, and the divergence disappears entirely.
- **Accept the split** — then audit those ~10 properties individually and pin each.

**Recommendation:** global. A single serialization rule is cheaper to hold in your head than a list of
exceptions, and it shrinks Step B8's residual cases.

---

## Q4 — Entity-side auto-deep: default-on with a diagnostic, or opt-in?

**Status:** ❓ unanswered · **Blocks:** Step A8 shape · **Recommendation: keep default-on, add the diagnostic**

This is a genuine choice between two silent failures:

- **Opt-in** re-opens the bug fixed on 2026-08-06 (`.shift/repos/shift-entity/mapping-abstraction-plan.md` §23): JSON-owned
  grandchildren read back fine and were silently emptied on save. That fix is pinned by
  `GeneratedDeepWriteTests`.
- **Default-on with no diagnostic** corrupts link tables: *(downstream)* the generated `MenuVariant` mapper
  composes four collections that `GeneralMappingProfile.cs:315-330` deliberately ignores and merges by
  business key, so saving either throws (FKs are forced to `Restrict`) or duplicates/orphans rows.

Note the asymmetry the framework-only scope creates here: the *risk* is a consumer's, the *default* is ours,
and the consumer cannot change it. That is an argument for the loud option, not the quiet one.

**Recommendation:** keep default-on and add the diagnostic (Step A8), because the failure it produces is
*loud at build time* while the opt-in failure is *silent at runtime*. But this is a real judgment call about
which risk the team would rather carry, so it should be decided deliberately rather than inherited.

---

## Q5 — ~~Is `Menu` retired in favour of `ADP.Menus`?~~ *(dropped — out of scope)*

**Status:** ➖ dropped 2026-08-20 · moot under framework-only scope

`Menu` is a consumer service. Whether it is alive changes nothing here — it migrates on its own schedule
either way, and this plan schedules no consumer migrations.

Kept for whoever picks up a consumer migration later, because the finding still holds: `Menu` and `ADP.Menus`
are **not** duplicates — 219 differing lines after normalization, and `Menu` is pinned four months behind on
the framework version, so migrating it needs a framework bump first.

---

## Q6 — ~~ADP.SyncAgent: delete or migrate?~~ *(dropped — out of scope)*

**Status:** ➖ dropped 2026-08-20 · not a framework decision

SyncAgent has no ShiftEntity coupling, so this plan neither blocks on it nor decides it. See Step F4, which
is kept as a record rather than as work.

The one thing this plan **does** decide: because SyncAgent still exists, the release notes say *"AutoMapper is
gone from the framework"*, never *"AutoMapper is gone"*.

---

## Q7 — Client-supplied `IsDeleted` / `CreateDate` / `EmailVerified` stop working. Release note, or security advisory?

**Status:** ❓ unanswered · **Blocks:** the Stage F release notes · **Owner:** —

`MapToEntity` will stop writing `ID`, `IsDeleted` and the four audit fields, which AutoMapper's unguarded
`ReverseMap()` **did** write. That is strictly a narrowing — and a security-positive one.

Today, through `PUT api/UserManager/UserData`, a user can:
- `PUT {"isDeleted": true}` and **soft-delete their own row**;
- set their own `EmailVerified` / `PhoneVerified` flags.

**The question is how to communicate it**, not whether to ship it. A release note treats it as a behavior
change; a security advisory treats it as a fixed vulnerability with a disclosure timeline. The second is
probably correct given the self-verification path.

**Also action either way:** grep consumer upsert paths for DTO-side writes to `IsDeleted` / `CreateDate` /
`CreatedByUserID` (seed and import flows are the likely users) and route those through `AuditFieldsAreSet`.

---

## Q8 — Do you accept richer list payloads?

**Status:** ❓ unanswered · **Blocks:** Stage E divergence triage · **Recommendation: accept, then measure**

Generated list projections populate `ShiftEntitySelectDTO`s — including `Text` — that `ProjectTo` left empty,
because it never ran AutoMapper's `AfterMap`. Better data; one LEFT JOIN per member.

**Recommendation:** accept globally rather than suppressing list-side select bindings to preserve what was
effectively a `ProjectTo` bug. Then measure on the widest grids and apply `IgnoreList` per member where the
join cost is real.

**Open item:** audit any remaining list DTO carrying a select DTO member. The three known cases
(`CompanyListDTO`, `CompanyBranchListDTO`, `UserListDTO`) are all in scope and already pin them with explicit
`ForList`. *(downstream: `ReplacementItemListDTO` in `ADP.Menus`/`Menu` is unchecked — note it in the
migration guide, do not audit it here.)*

---

## Q9 — Do we ship the required-delegate compile break while consumers are un-migrated?

**Status:** ❓ unanswered · **Blocks:** Step E3 · **Recommendation: yes, take the break — and pre-announce it**

Step E3 drops the `= null` default on the replication mapping delegate. In scope this is free: the template's
12 sites are ported in E2. Out of scope it is a **compile break** at 6 call sites (`ADP.ClaimableItems` ×5,
`ADP.WarrantyClaims` ×1) that this plan does not fix.

The alternative is keeping the `= null` overloads and throwing at the fallback instead. That converts a build
error into a runtime throw **inside a swallowed per-row `catch`** — permanently-dirty rows under a clean
watermark, which is the single failure shape this plan works hardest to eliminate everywhere else.

**Recommendation:** take the compile break. Say plainly in the release notes that a consumer not ready to
port stays on the previous framework version — a pinned version is a normal, reversible state; a
half-replicated Cosmos partition is not.

**This is the one place framework-only scope creates work for someone else.** Decide it deliberately, and
tell the consumer teams *before* the release rather than with it.

---

## Q10 — What ships as the default: `AutoMapperFirst` forever, or a flip?

**Status:** ❓ unanswered · **Blocks:** D1, F1, F5 · **Recommendation: `AutoMapperFirst` until F5, then it stops existing**

`MappingMode` (D1) exists so each service can move on its own schedule. With every consumer out of scope, the
live question becomes: what does the **framework** ship as the default, and for how long?

- **Keep `AutoMapperFirst` and never delete the fallback** — then the removal never actually happens; the plan produces better diagnostics and nothing else.
- **Flip the default to `GeneratedFirst` while consumers are un-migrated** — exactly the silent profile-for-convention swap D1 exists to prevent. Measured on the first triple examined, the generated `WarrantyClaim` mapper produced three regressions with zero warnings.

**Recommendation:** the default stays `AutoMapperFirst` through every release up to F5. At F5 the fallback
*moves* into the compat package rather than being deleted outright, so the shipped chain becomes
registry → compat seam (only if you installed it) → throw. A consumer opts in with one package and one line,
and nobody's mapper silently changes underneath them. `MappingMode` survives afterwards as the per-service
opt-in it was designed to be.

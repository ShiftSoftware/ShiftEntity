# AutoMapper Removal — Open Decisions

> **Mirror.** The canonical copy of this plan lives in the `.shift` knowledge-base repo at
> `.shift/repos/shift-entity/automapper-removal/`. This copy exists so the plan is readable without
> cloning `.shift`. Keep both in sync; if they ever disagree, `.shift` wins.

**Created:** 2026-08-19
Eight judgment calls the team must make. Each changes what gets built. Recommendations are given, but none of
these has a default that is obviously right — that is why they are here rather than decided in
[`01-steps.md`](01-steps.md).

Record the answer inline (edit this file) and reflect it in [`STATUS.md`](STATUS.md).

---

## Q1 — Does the generator actually run in package-mode consumers?

**Status:** ❓ unanswered · **Blocks:** everything · **Owner:** —

The evidence contradicts itself. `shiftsoftware.shiftentity.efcore.nuspec:17` carries
`exclude="Build,Analyzers"` on the Core dependency, and `dotnet build -getItem:Analyzer` in
`ADP.ClaimableItems.Data` returned only SDK analyzers. Yet fresh `Generated_*.g.cs` files exist in ADP `obj/`
trees, which a later check confirmed directly.

This is not a preference — it is a fact to establish, and it decides whether ADP's migration is *"the registry
already has a mapper for every triple, flip a switch and audit"* or *"the analyzer has never run there."*

**Action:** Step 0.1. **Answer this before anything else starts.**

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
opt-out — so each of the ~21 nullable select members across ADP and ShiftIdentity becomes an explicit
migration decision rather than a silent one. Release-note it.

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
- **Default-on with no diagnostic** corrupts link tables: the generated `MenuVariant` mapper composes four
  collections that `GeneralMappingProfile.cs:315-330` deliberately ignores and merges by business key, so
  saving either throws (FKs are forced to `Restrict`) or duplicates/orphans rows.

**Recommendation:** keep default-on and add the diagnostic (Step A8), because the failure it produces is
*loud at build time* while the opt-in failure is *silent at runtime*. But this is a real judgment call about
which risk the team would rather carry, so it should be decided deliberately rather than inherited.

---

## Q5 — Is `Menu` retired in favour of `ADP.Menus`?

**Status:** ❓ unanswered · **Blocks:** Stage E scope · **Owner:** —

They are **not** duplicates: 219 differing lines after normalization, and `Menu` is pinned four months behind
on the framework version.

If `Menu` is still alive, Stage E gains a fifth full service migration (11 triples) **plus** a framework
version bump to get there. If it is retired, that work disappears.

---

## Q6 — ADP.SyncAgent: delete or migrate?

**Status:** ❓ unanswered · **Blocks:** Step F4 · **Recommendation: lean delete**

`UseAutoMapper` is public package API with **zero** call sites anywhere in the workspace (grepped across all
14 repos), and two of its four services (`SyncService2.cs`, `CosmosCSVSyncService.cs`) are already
`<Compile Remove>`d — dead text, not surface.

**Deleting is cheapest and honest. Migrating preserves a contract nobody uses.** The only argument for
migrating is an external consumer outside this workspace — if one exists, say so, because nothing else in the
analysis can see it.

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
(`CompanyListDTO`, `CompanyBranchListDTO`, `UserListDTO`) already pin them with explicit `ForList`;
`ReplacementItemListDTO` in `ADP.Menus`/`Menu` is unchecked.

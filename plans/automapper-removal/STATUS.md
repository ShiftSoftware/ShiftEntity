# AutoMapper Removal — Status

> **Mirror.** Canonical status lives at `.shift/repos/shift-entity/automapper-removal/STATUS.md`.
> **Update that one first** — this file drifts fastest. If they disagree, `.shift` wins.

**Last updated:** 2026-08-20 — rescoped to framework only. No implementation started.

**Scope:** `ShiftEntity` + `ShiftIdentity` + `ShiftTemplates` + CI. Consumer services (`ADP.*`,
`ADP.SyncAgent`, `Menu`) are out of scope — see [`README.md`](README.md#scope--framework-only).

Update this file as steps land. Keep it factual: what shipped, what it changed, what surprised you.
Plan: [`01-steps.md`](01-steps.md) · Evidence: [`00-gap-register.md`](00-gap-register.md) · Decisions: [`02-open-decisions.md`](02-open-decisions.md)

**Legend:** ⬜ not started · 🟡 in progress · ✅ done · ⛔ blocked · ➖ dropped / out of scope (say why)

---

## Stage 0 — Prerequisite

| Step | Status | Notes |
|------|--------|-------|
| 0.1 Confirm generator reaches package-mode consumers | ⬜ | **Do first.** Reproduce on the Builder's `dotnet new shift` project. If no, nothing downstream of Stage D is reachable. See Q1. |

## Stage A — Make failure visible

| Step | Status | Notes |
|------|--------|-------|
| A1 Behavioral generator test harness | ⬜ | Gates A3–A9 verification. |
| A2 Diagnostic source locations | ⬜ | |
| A3 Reserved names gated by declaring type | ⬜ | Live data loss today. |
| A4 Inverse scalar conventions | ⬜ | Live silent write loss today. **Before A5/A6.** |
| A5 List unmapped diagnostic (SHENGEN007) | ⬜ | Expect a large one-time warning count. |
| A6 Entity asymmetry diagnostic (SHENGEN008) | ⬜ | |
| A7 Fail-closed fluent config | ⬜ | `Ignore` is currently a no-op. |
| A8 Deep-write diagnostic + `ForEntity(existing)` + `AfterEntity` | ⬜ | Escape hatch for consumers — **nothing in scope needs it. Do not cut it.** Depends on Q4. |
| A9 Soft-deleted children excluded from auto-deep | ⬜ | |
| A10 Low-severity generator cleanups | ⬜ | Batch; nothing depends on it. |

## Stage B — Close framework-owned holes

| Step | Status | Notes |
|------|--------|-------|
| B1 CI gate | ⬜ | **Do first in this stage** — nothing is verified until it exists. |
| B2 `dotnet new shiftentity` emits a mapper | ⬜ | **Broken right now**, independent of the removal. |
| B3 `ShiftTagMapper` | ⬜ | No repository change needed — ctor already exists. |
| B4 De-eagerize replication ctors | ⬜ | ~3 lines. Ship standalone. |
| B5 `AsNoTracking` into `OdataList` | ⬜ | |
| B6 Tags-in-list splice into `OdataList` | ⬜ | Also fixes the Core→EFCore layering inversion. |
| B7 `MapToList` base-member contract | ⬜ | |
| B8 `ToForeignKey` throws 400 | ⬜ | Scope depends on Q3. |
| B9 `CopyEntity` throws | ⬜ | Land `ProductRepository.CopyEntity` first. |

## Stage C — Parity harness *(window closes when AutoMapper is deleted)*

| Step | Status | Notes |
|------|--------|-------|
| C1 Triple differ | ⬜ | Deliverable = the reviewed `KnownDivergence` table. |
| C2 Replication goldens | ⬜ | Capture **before** porting each pair. |
| C3 SQL translation tests for deep lists | ⬜ | Currently zero coverage. |

## Stage D — Wiring & enforcement

| Step | Status | Notes |
|------|--------|-------|
| D1 `MappingMode` + registry resolution | ⬜ | Default stays `AutoMapperFirst`. |
| D2 Attribute-endpoint default flip | ⬜ | Needs the three-way `spec.Repository` split. |
| D3 Startup validation | ⬜ | Where "required" is actually enforced. |
| D4 Codegen ABI stamp + check | ⬜ | |
| D5 Registry conflict detection | ⬜ | |

## Stage E — Migrate framework-owned code

| Target | Size | Status | Notes |
|--------|------|--------|-------|
| E1 Migration recipe | — | ⬜ | Write it to be **published** — it is the downstream migration guide. Ships in F5's docs pass. |
| StockPlusPlus sample | 3 profiles / 83 lines, 0 `AfterMap` | ⬜ | First. Rehearses the recipe where fixing the framework is still cheap. |
| ShiftIdentity.Data | 11 profiles / 352 lines, 0 `AfterMap` | ⬜ | After **D4** — it ships as a package, so its mappers freeze into the DLL. |
| E2 Template's 12 replication sites | — | ⬜ | **Early** — stops the bleed into new services. |
| E3 Required replication delegate | — | ⬜ | Deliberate compile break for un-migrated consumers. Blocked on **Q9**. |
| ~~ADP.Surveys / WarrantyClaims / ClaimableItems / Menus / Menu~~ | 37 triples, 16 `AfterMap` | ➖ | **Out of scope.** Consumer services — they migrate on their own schedule via E1 + the compat package. |

## Stage F — Delete

| Step | Status | Notes |
|------|--------|-------|
| F1 Compat package + obsoletions | ⬜ | **Load-bearing under this scope** — the landing pad for ~37 un-migrated consumer triples. Depends on framework-owned Stage E only, *not* on any consumer. Needs its own smoke project. |
| F2 ShiftIdentity's 11 ad-hoc `Map<T>` sites | ⬜ | |
| F3 Project template detached | ⬜ | |
| F4 ADP.SyncAgent | ➖ | **Out of scope** — no ShiftEntity coupling, nothing blocked by it. Recorded so the release notes say "gone from the framework", not "gone". |
| F5 Package references + docs | ⬜ | Replication first (NU1903 reaches consumers transitively), Core last. Publish E1 as the migration guide in the same pass. |

---

## Open decisions

| # | Question | Status | Answer |
|---|----------|--------|--------|
| Q1 | Generator reaches package-mode consumers? | ❓ | |
| Q2 | Nullable FK — clear or preserve? | ❓ | *(rec: clear + per-member opt-out)* |
| Q3 | Empty select DTO — `null` or `{Value:""}`? | ❓ | *(rec: make it global)* |
| Q4 | Entity auto-deep — default-on or opt-in? | ❓ | *(rec: default-on + diagnostic)* |
| ~~Q5~~ | ~~Is `Menu` retired?~~ | ➖ | Dropped 2026-08-20 — consumer scope, moot here. |
| ~~Q6~~ | ~~SyncAgent — delete or migrate?~~ | ➖ | Dropped 2026-08-20 — not a framework decision. See F4. |
| Q7 | Audit-field narrowing — note or advisory? | ❓ | |
| Q8 | Richer list payloads — accept? | ❓ | *(rec: accept, then measure)* |
| **Q9** | Ship the required-delegate compile break? | ❓ | *(rec: yes — pre-announce it)* **New, from the rescope.** |
| **Q10** | Shipped default: `AutoMapperFirst` forever, or a flip? | ❓ | *(rec: `AutoMapperFirst` until F5, then compat-seam)* **New, from the rescope.** |

---

## Log

**2026-08-20** — **Rescoped to the framework only.** ADP (`Surveys`, `WarrantyClaims`, `ClaimableItems`,
`Menus`, `SyncAgent`) and `Menu` removed as work; they remain in the gap register as *(downstream)* evidence.
Three consequences worth remembering:

1. Framework-owned mapping is 14 profiles / 435 lines with **zero `AfterMap` blocks** — all 16 of the hard
   collection-reconciliation blocks were downstream. Stage E went from five services to two (the sample, then
   ShiftIdentity).
2. Steps A5–A8 now serve **no in-scope code**. They stay because only the framework can build them and no
   consumer can migrate without them. Flagged in-place so nobody prunes them as unused.
3. Two new decisions fall out of the narrowing — **Q9** (ship the required-delegate compile break at 6
   consumer call sites we do not own?) and **Q10** (what is the shipped default, and does the AutoMapper
   fallback get deleted or *moved* into the compat package?). F1 stopped being an end-of-plan courtesy and
   became the deliverable the whole scope rests on.

Nothing else changed: the ordering rule, the closing Stage C window, and every framework gap stand as audited.

**2026-08-19** — Plan created from a full cross-repo audit (14 repos, 68 raw findings, 59 surviving
adversarial verification). No code changed. Key correction to the earlier assumption that replication needs a
new mapper abstraction: it does not — the merge overload already exists on both pipelines, and the real
blocker is one eager `GetRequiredService<IMapper>()` in a constructor.

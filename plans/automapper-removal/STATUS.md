# AutoMapper Removal — Status

> **Mirror.** Canonical status lives at `.shift/repos/shift-entity/automapper-removal/STATUS.md`.
> **Update that one first** — this file drifts fastest. If they disagree, `.shift` wins.

**Last updated:** 2026-08-19 — plan created, no implementation started.

Update this file as steps land. Keep it factual: what shipped, what it changed, what surprised you.
Plan: [`01-steps.md`](01-steps.md) · Evidence: [`00-gap-register.md`](00-gap-register.md) · Decisions: [`02-open-decisions.md`](02-open-decisions.md)

**Legend:** ⬜ not started · 🟡 in progress · ✅ done · ⛔ blocked · ➖ dropped (say why)

---

## Stage 0 — Prerequisite

| Step | Status | Notes |
|------|--------|-------|
| 0.1 Confirm generator reaches package-mode consumers | ⬜ | **Do first.** Blocks every estimate. See Q1. |

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
| A8 Deep-write diagnostic + `ForEntity(existing)` + `AfterEntity` | ⬜ | Unblocks 16 `AfterMap` ports. Depends on Q4. |
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

## Stage E — Service migration

| Service | Triples | Status | Notes |
|---------|---------|--------|-------|
| ADP.Surveys | — | ⬜ | Cleanest — no `AfterMap`. Start here. |
| ADP.WarrantyClaims | — | ⬜ | Differ already found 3 regressions here. |
| ADP.ClaimableItems | — | ⬜ | + 5 replication sites. |
| ADP.Menus | — | ⬜ | Worst — 5 `AfterMap` blocks. |
| Menu | 11 | ⬜ | Only if still alive — see Q5. |
| E2 Template's 12 replication sites | — | ⬜ | **Early** — stops the bleed into new services. |
| E3 ADP replication ports + required delegate | — | ⬜ | |

## Stage F — Delete

| Step | Status | Notes |
|------|--------|-------|
| F1 Compat package + obsoletions | ⬜ | |
| F2 ShiftIdentity's 11 ad-hoc `Map<T>` sites | ⬜ | |
| F3 Project template detached | ⬜ | |
| F4 ADP.SyncAgent | ⬜ | Separate workstream. See Q6. |
| F5 Package references + docs | ⬜ | Replication and SyncAgent first (NU1903), Core last. |

---

## Open decisions

| # | Question | Status | Answer |
|---|----------|--------|--------|
| Q1 | Generator reaches package-mode consumers? | ❓ | |
| Q2 | Nullable FK — clear or preserve? | ❓ | *(rec: clear + per-member opt-out)* |
| Q3 | Empty select DTO — `null` or `{Value:""}`? | ❓ | *(rec: make it global)* |
| Q4 | Entity auto-deep — default-on or opt-in? | ❓ | *(rec: default-on + diagnostic)* |
| Q5 | Is `Menu` retired? | ❓ | |
| Q6 | SyncAgent — delete or migrate? | ❓ | *(rec: lean delete)* |
| Q7 | Audit-field narrowing — note or advisory? | ❓ | |
| Q8 | Richer list payloads — accept? | ❓ | *(rec: accept, then measure)* |

---

## Log

**2026-08-19** — Plan created from a full cross-repo audit (14 repos, 68 raw findings, 59 surviving
adversarial verification). No code changed. Key correction to the earlier assumption that replication needs a
new mapper abstraction: it does not — the merge overload already exists on both pipelines, and the real
blocker is one eager `GetRequiredService<IMapper>()` in a constructor.

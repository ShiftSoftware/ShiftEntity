# AutoMapper Removal — Plan

> **Mirror.** The canonical copy of this plan lives in the `.shift` knowledge-base repo at
> `.shift/repos/shift-entity/automapper-removal/`. This copy exists so the plan is readable without
> cloning `.shift`. Keep both in sync; if they ever disagree, `.shift` wins.

**Created:** 2026-08-19 · **Rescoped:** 2026-08-20 — framework only
**Goal:** delete the AutoMapper dependency from Shift Framework. Make an explicit mapper (source-generated or hand-written) **required** instead of falling back to AutoMapper. Cover Cosmos replication too.

This folder is the working plan. It supersedes the "AutoMapper Removal Path" bullet at the bottom of
the mapping abstraction plan (`.shift/repos/shift-entity/mapping-abstraction-plan.md`), which stays the source of truth for the
*mapping abstraction itself* (what shipped, how mappers work). **Removal** work is tracked here.

| Doc | What it holds |
|-----|---------------|
| [`00-gap-register.md`](00-gap-register.md) | Every verified gap, with evidence and severity. The "why" behind each step. Also: what was checked and found **clean**, so nobody re-audits it. |
| [`01-steps.md`](01-steps.md) | **The plan.** Small, individually shippable steps in dependency order. Each says what it does and what it solves. |
| [`02-open-decisions.md`](02-open-decisions.md) | The judgment calls the team must make — eight live, two dropped as consumer-scope. Several steps are blocked on them. Recommendations included. |
| [`STATUS.md`](STATUS.md) | Live tracker. Update as steps land. |

---

## Scope — framework only

**In scope.** The Shift Framework itself:

- **ShiftEntity** — Core, EFCore, Web, CosmosDbReplication, SourceGenerator
- **ShiftIdentity** — 11 profiles, 11 ad-hoc `Map<T>` sites, the shipped `UserRepository`
- **ShiftTemplates** — the `shift` project template, the `shiftentity` item template, and the StockPlusPlus sample they are built from
- The Azure DevOps pipeline that releases them

**Out of scope.** Every consumer service: `ADP.Surveys`, `ADP.WarrantyClaims`, `ADP.ClaimableItems`,
`ADP.Menus`, `ADP.SyncAgent`, `Menu`. This plan does not edit them and does not schedule their migration.
They keep building — through the compat package (Step F1) — and migrate on their own timeline using Step E1,
which is written to be published as the downstream migration guide.

They still appear in [`00-gap-register.md`](00-gap-register.md), marked *(downstream)*, and only ever as
**evidence that a framework gap is real and reachable**. Evidence, not work.

### What framework-only changes — three things

1. **Stage E collapses from six services to two.** Framework-owned mapping is 14 profiles / 435 lines,
   29 `CreateMap`, 71 `ForMember` — and **zero `AfterMap` blocks**. The 16 `AfterMap` collection-reconciliation
   blocks that made the original plan hard are *all* downstream. What is left is `CreateMap` + `ForMember`,
   which is exactly the shape the generator already covers.

2. **The compat package stops being a courtesy and becomes the load-bearing deliverable.** ~37 consumer
   triples stay on AutoMapper indefinitely. `ShiftSoftware.ShiftEntity.EFCore.AutoMapper` (F1) is therefore
   the thing that decides whether a consumer can take a framework upgrade at all. Its dependency changes from
   *"Stage E complete for all services"* — which under this scope would mean never — to *"Stage E complete for
   framework-owned code"*, and it needs its own smoke test in the framework's suite.

3. **The escape hatches still get built, even though nothing in scope uses them.** A5/A6 diagnostics, A7's
   fail-closed config, A8's `AfterEntity`. They exist so a consumer *can* migrate later, and the framework is
   the only place they can be written. Do not cut them as "unused" — that is the one way a framework-only
   scope quietly strands everyone downstream.

---

## The finding in one paragraph

Nothing about AutoMapper is *inexpressible* without it. Every shape it covers today can be produced by the
source generator, a hand-written `IShiftEntityMapper`, a declared pair mapper, or a repository override — and
the replication merge overload already exists on both pipelines. The problem is **silence**. The generated
mapper emits three directions (`MapToView`, `MapToEntity`, `MapToList`) and diagnoses **one**. `SHENGEN004`
is reported only from the view direction; `BuildListAssignments` and `BuildEntityBody` have no unmapped
channel at all, and `EntityConvention` is strictly weaker than `ViewConvention`. So a member that reads back
perfectly can stop being persisted — green build, HTTP 200, missing column.

**Therefore the ordering rule for this entire plan is: make failure visible before you make the mapper required.**

---

## Stages

Work through them in order. Steps *within* a stage are mostly independent and say so.

| Stage | Name | What it achieves |
|-------|------|------------------|
| **0** | Prerequisite | Answer one question that changes everything downstream. |
| **A** | Make failure visible | Generator diagnostics + missing conventions. No consumer flips yet. |
| **B** | Close framework-owned holes | Things a consumer cannot fix themselves: tagging, replication, templates, CI. |
| **C** | Build the parity harness | Differential testing — **must** be built while AutoMapper still exists. |
| **D** | Wiring & enforcement | `MappingMode`, registry resolution, startup validation. Nothing flips by default. |
| **E** | Migrate framework-owned code | Two migrations: the sample, then ShiftIdentity. The recipe doubles as the consumer guide. |
| **F** | Delete | Compat package, obsoletions, package references, docs. |

---

## Three rules that are easy to get wrong

1. **Build the parity harness before anything flips (Stage C).** Once AutoMapper is deleted there is no
   oracle to diff generated output against — permanently. This is the only step in the plan whose window
   closes.

2. **Fix conventions before turning diagnostics loud.** Shipping the list/entity diagnostics before the
   inverse scalar conventions (Step A4) produces a warning wall on cases the framework should simply handle,
   and the real findings get lost in it.

3. **Never let the framework silently pick a mapper.** Every resolution chain in this plan ends in `throw`,
   never in an implicit fallback. A loud 500 on a repository is findable and cannot corrupt data; a silently
   wrong list column or a corrupt Cosmos document with a clean watermark is neither.

4. **Never break a consumer without a landing pad.** Framework-only scope means the people who have to react
   to these releases are not the people making them. Every consumer-visible break in this plan ships with
   either the compat package or a documented, version-pinnable alternative — and Step E1 exists so the
   instructions outlive the person who wrote them.

---

## What "done" means

- No `PackageReference Include="AutoMapper"` anywhere in `ShiftEntity`, `ShiftIdentity` or `ShiftTemplates` — the compat package is the only place it survives.
- Every framework-owned `ShiftRepository<,,,>` triple and every endpoint attribute resolves an explicit mapper, verified at startup.
- Every replication call site **in the framework and the template** passes an explicit mapping delegate — enforced by the compiler, not convention.
- `dotnet new shift` and `dotnet new shiftentity` produce projects that build, with no AutoMapper reference.
- CI runs the mapping tests on every framework release tag.
- `ShiftSoftware.ShiftEntity.EFCore.AutoMapper` is published, and the framework test suite contains a compat smoke project proving an old-style `Profile` still resolves a mapper through the seam.
- Step E1 is published as a docs page, with the C1 differ and the C2 goldens as the tools it points at.

**Explicitly *not* part of done:** any consumer service being migrated. `grep AutoMapper` across `ADP.*` and
`Menu` will still match, by design.

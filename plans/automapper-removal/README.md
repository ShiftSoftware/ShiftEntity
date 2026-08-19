# AutoMapper Removal — Plan

> **Mirror.** The canonical copy of this plan lives in the `.shift` knowledge-base repo at
> `.shift/repos/shift-entity/automapper-removal/`. This copy exists so the plan is readable without
> cloning `.shift`. Keep both in sync; if they ever disagree, `.shift` wins.

**Created:** 2026-08-19
**Goal:** delete the AutoMapper dependency from Shift Framework. Make an explicit mapper (source-generated or hand-written) **required** instead of falling back to AutoMapper. Cover Cosmos replication too.

This folder is the working plan. It supersedes the "AutoMapper Removal Path" bullet at the bottom of
the mapping abstraction plan (`.shift/repos/shift-entity/mapping-abstraction-plan.md`), which stays the source of truth for the
*mapping abstraction itself* (what shipped, how mappers work). **Removal** work is tracked here.

| Doc | What it holds |
|-----|---------------|
| [`00-gap-register.md`](00-gap-register.md) | Every verified gap, with evidence and severity. The "why" behind each step. Also: what was checked and found **clean**, so nobody re-audits it. |
| [`01-steps.md`](01-steps.md) | **The plan.** Small, individually shippable steps in dependency order. Each says what it does and what it solves. |
| [`02-open-decisions.md`](02-open-decisions.md) | Eight judgment calls the team must make. Several steps are blocked on them. Recommendations included. |
| [`STATUS.md`](STATUS.md) | Live tracker. Update as steps land. |

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
| **E** | Migrate, one service at a time | Per-service recipe. Reviewable, reversible. |
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

---

## What "done" means

- No `PackageReference Include="AutoMapper"` in `ShiftEntity.Core`, `ShiftEntity.CosmosDbReplication`, or `ADP.SyncAgent`.
- Every `ShiftRepository<,,,>` triple and every endpoint attribute resolves an explicit mapper, verified at startup.
- Every replication call site passes an explicit mapping delegate — enforced by the compiler, not convention.
- `dotnet new shift` and `dotnet new shiftentity` produce projects that build.
- CI runs the mapping tests on every framework release tag.

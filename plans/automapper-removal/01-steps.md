# AutoMapper Removal — Step Plan

> **Mirror.** The canonical copy of this plan lives in the `.shift` knowledge-base repo at
> `.shift/repos/shift-entity/automapper-removal/`. This copy exists so the plan is readable without
> cloning `.shift`. Keep both in sync; if they ever disagree, `.shift` wins.

**Created:** 2026-08-19
Evidence for every step: [`00-gap-register.md`](00-gap-register.md). Live tracking: [`STATUS.md`](STATUS.md).

Each step is **individually shippable** — it compiles, tests pass, and it can be pushed on its own.
Nothing here requires a big-bang change. Steps state their dependencies explicitly; anything without a
dependency can be picked up in parallel by a second person.

**Step template:** *Solves* (which gap) · *Problem* (what is broken now) · *What this step does* ·
*What it solves* (the failure that disappears) · *Files* · *Depends on* · *Breaks* · *Done when*.

---

# Stage 0 — Prerequisite

## Step 0.1 — Confirm the generator actually runs in package-mode consumers

**Solves:** the premise the whole plan rests on.

**Problem.** The evidence contradicts itself. `shiftsoftware.shiftentity.efcore.nuspec:17` carries
`exclude="Build,Analyzers"` on the Core dependency, and `dotnet build -getItem:Analyzer` in
`ADP.ClaimableItems.Data` returned only SDK analyzers — which would mean the mapper generator has **never
run** in ADP. But fresh `Generated_*.g.cs` files do exist in ADP `obj/` trees, which says it does run. Both
cannot be current.

**What this step does.** Clean `obj/` in `ADP.ClaimableItems.Data`, `ADP.Menus.Data`, `ADP.Surveys.Data` and
`ADP.WarrantyClaims.Data`, rebuild, and check whether `obj/**/generated/` repopulates. Record the answer in
[`STATUS.md`](STATUS.md).

**What it solves.** Decides whether ADP's migration is *"the registry already has a mapper for every triple —
flip a switch and audit"* or *"the analyzer has never run there, and nothing exists yet."* Every effort
estimate downstream depends on this.

**Files.** None — investigation only.

**Depends on.** Nothing. **Do this first.**

**Breaks.** Nothing.

**Done when.** STATUS records a yes/no with the command output. If **no**: add an explicit analyzer pack item
to `ShiftEntity.EFCore.csproj` (safer than flipping Core's `PrivateAssets`) as Step 0.2, and add a build check
that fails when a `ShiftRepository<,,,>` subclass compiles without the generator present.

---

# Stage A — Make failure visible

> **Rule for this stage:** conventions before diagnostics. Turning the list/entity diagnostics on before
> Step A4 produces a warning wall on cases the framework should simply handle, and the real findings drown.

## Step A1 — Behavioral generator test harness

**Solves:** A-1 verification, and every step after this one.

**Problem.** `ShiftEntity.Tests/Mapping/GeneratedDeepWriteTests.cs` asserts `Assert.Contains` against
generated **source strings**. A semantically wrong mapper that happens to emit the right substring passes.
Worse, `SHENGEN004` — the diagnostic this entire plan leans on — has **zero tests**;
`GeneratorDiagnosticTests.cs` covers only `SHENGEN006`.

**What this step does.** The existing `Generate()` helper already builds a `CSharpCompilation` that includes
the generated syntax trees. Add ~10 lines: `Emit` to a `MemoryStream`, `Assembly.Load`, activate the mapper,
and call it. Then add tests for `SHENGEN004` — its firing case **and** its silent cases (custom-configured
member, `[ShiftEntityMapperIgnore]`, cycle-skipped edge), per that file's own convention.

**What it solves.** Turns every later step in this stage from "the emitted text looks right" into "the mapper
produces the right object". Without it, steps A3–A9 cannot be verified at all.

**Files.** `ShiftEntity.Tests/Mapping/` — `GeneratorDiagnosticTests.cs`, `GeneratedDeepWriteTests.cs`, and the
shared `Generate()` helper.

**Depends on.** Nothing.

**Breaks.** Nothing.

**Done when.** At least one existing deep-write assertion is expressed as "run the mapper, assert the
resulting object", and `SHENGEN004` has a firing test plus three silent tests.

---

## Step A2 — Give diagnostics a real source location

**Solves:** A-1 (usability half).

**Problem.** Both `SHENGEN004` report sites use
`userClass?.Locations.FirstOrDefault() ?? Location.None`. `userClass` is **null for every
`UseGeneratedMapper()` triple** — the dominant shape — so the most common case produces a file-less,
unnavigable warning. You cannot double-click it; you cannot suppress it locally.

**What this step does.** `TripleModel` already carries a repository location (it was added for
`SHENGEN006`). Reuse it as the fallback location for `SHENGEN004`, and for the two new diagnostics added in
A5/A6.

**What it solves.** Makes the warnings actionable. This is the difference between "1,400 warnings we can
triage" and "1,400 warnings we turn off".

**Files.** `ShiftEntity.SourceGenerator/ShiftEntityMapperGenerator.cs:1515-1518`, `:1664-1667`.

**Depends on.** A1 (so the change is testable).

**Breaks.** Nothing.

**Done when.** A `UseGeneratedMapper()` triple with an unmapped member produces a warning that points at the
repository declaration.

---

## Step A3 — Gate reserved member names by declaring type, not by name

**Solves:** A-3. **This is live data loss today.**

**Problem.** `ViewHandledMembers` (`:842`), `EntityExcludedMembers` (`:845`) and the list filter
`p.Name != "Tags"` (`:1277`) are matched as **strings**. So a domain column that happens to be called `Tags`
or `Revisions` is silently dropped from view, entity **and** list. This is not theoretical: the committed
generated output for `BankQuestion` drops a comma-separated `Tags` column that has nothing to do with the
tagging system, keeping it only in `CopyEntity`. Separately, a pair mapper omits `dto.ID` in `Map` while its
sibling `Projection` emits `ID = e.ID` — same pair, two different answers.

**What this step does.** Introduce one predicate — `IsFrameworkMember(IPropertySymbol p)` — returning true
only when `p.ContainingType` is `ShiftEntity<>`, `ShiftEntityViewAndUpsertDTO` or `ShiftEntityListDTO`.
Replace the name-set lookups at `:658`, `:807`, `:1100`, `:1200`, `:1224`, `:1402` and the list filter at
`:1277`. Add `"Revisions"` to the list filter's framework set while you are there (latent trap today).

**What it solves.** A domain property is never confused with a framework property again. Both live bugs
resolve with **zero new API**: the domain `Tags` column falls through to the ordinary convention (and, after
A5, to the unmapped diagnostic if it genuinely can't be mapped), and `dto.ID` picks up the implicit
`long → long?` conversion, matching its own projection.

**Files.** `ShiftEntityMapperGenerator.cs` — the three name sets and their six consumption sites.

**Depends on.** A1, A2.

**Breaks.** Nothing that was working. It *starts* mapping members that were previously dropped — which is the
fix. Regenerate and diff the committed `.g.cs` output for `BankQuestion` and `MenuItemPart` as the review
evidence.

**Done when.** An entity with a domain property named `Tags` (non-tagging) round-trips through all three
directions, pinned by a behavioral test.

---

## Step A4 — Add inverse scalar conventions to `EntityConvention`

**Solves:** A-2. **This is live silent data loss today.**

**Problem.** `ViewConvention` (`:913`) handles `long(?) → string` and `enum → int(?)`.
`EntityConvention` (`:1115-1143`) handles FK↔select DTO, file-list→JSON, implicit conversions and
nullable-unwrap — then `return null`. There is no `string → long`, no `int → enum`, no `string → Guid`. A
null return means **no assignment line is emitted at all**. So the read side of such a member succeeds and
the write side does nothing.

Confirmed live: `LabourDetailsDTO.ServiceIntervalGroupID` (`string`) ↔
`MenuLabourDetails.ServiceIntervalGroupID` (`long`), mapped today by a bare `ReverseMap()`
(`ADP.Menus.Data/AutoMapperProfiles/GeneralMappingProfile.cs:98`) — and it sits on an auto-deep child, so
every row is affected.

**What this step does.** Add the inverse conversions, emitting a `MappingHelpers` call that **throws** a
diagnosable exception on bad input, naming the member via `[CallerArgumentExpression]`.

**Do not** emit `TryParse ? value : default`. AutoMapper throws here too (`Convert.ToInt64`), and
ShiftEntity's own `ToLong()` is a bare `long.Parse`. A silent `0` written into a required FK is strictly
worse than today's behavior.

**What it solves.** The most dangerous single class of silent write loss, and it removes the largest source
of noise from the diagnostics added in A5/A6 — which is exactly why it ships **before** them.

**Files.** `ShiftEntityMapperGenerator.cs:1115-1143`; `ShiftEntity.Core/MappingHelpers.cs`.

**Depends on.** A1, A2.

**Breaks.** Nothing that was working; it starts writing members that previously vanished. A malformed
`string` FK that used to silently no-op will now throw — that is the point, and it is what AutoMapper did.

**Done when.** A DTO with `string` ID, `int` enum and `string` Guid members writes back correctly, and a
malformed value throws an exception that names the member.

---

## Step A5 — List-direction unmapped diagnostic (`SHENGEN007`)

**Solves:** A-1, and gives A-7 (no flattening) a usable migration path.

**Problem.** `BuildListAssignments` returns a bare `List<string>` and its skip path (`:1329`) is a bare
`continue`. A list DTO member the generator cannot map is simply absent from the projection — the column is
null on the wire, with no build output of any kind.

**What this step does.** Thread an `Unmapped` list out of `BuildListAssignments` and report it under a **new
diagnostic id**, so it can be triaged and suppressed independently of `SHENGEN004`. Suppress members that are
custom-configured for the list direction, `IgnoreList`d, the framework `Tags` member, or skipped by cycle
detection.

Make the message **synthesize the fix line** — e.g.
`ForList(d => d.CampaignName, e => e.Campaign.Name)`. That is what makes the deliberate decision not to
implement flattening (A-7) affordable: the compiler tells you the exact line to paste.

**What it solves.** The list direction stops failing silently. It also sizes the flattening exposure
precisely, instead of by grepping profiles.

**Files.** `ShiftEntityMapperGenerator.cs:1270-1340`, plus the descriptor block at `:54-84`.

**Depends on.** A1, A2, A4 (ship A4 first or the wall includes cases the framework should handle).

**Breaks.** Nothing. Expect a large one-time warning count — that is the deliverable, and it is the only
honest way to size Stage E.

**Done when.** A list DTO with an unmappable member warns, names the member, and prints a paste-ready
`ForList(...)` line; the four suppression cases stay silent, each with a test.

---

## Step A6 — Entity-direction asymmetry diagnostic (`SHENGEN008`)

**Solves:** A-1 (write half).

**Problem.** `BuildEntityBody` returns `(Lines, UsedPairs)` — no unmapped channel. A DTO member the entity
never writes back is invisible.

**What this step does.** **Do not mirror `SHENGEN004`.** `BuildEntityBody` iterates **entity** properties
(`:1222`), so a naive mirror would warn on every internal or computed column and be useless. Report the
**asymmetry** instead:

> *DTO members that `MapToView` reads but `MapToEntity` never writes back.*

That is the actionable set, and it directly encodes the framework's documented read/write symmetry invariant.

**What it solves.** Catches the exact failure mode this plan exists to prevent — a field that displays
correctly and silently fails to save — at build time instead of in production.

**Files.** `ShiftEntityMapperGenerator.cs:1145-1240`, plus the descriptor block.

**Depends on.** A1, A2, A4, A5.

**Breaks.** Nothing. Expect legitimate asymmetries (read-only computed DTO members) — those are what
`IgnoreEntity` is for, and each one becomes an explicit, reviewed decision.

**Done when.** A DTO member present in the view body and absent from the entity body warns; an
`IgnoreEntity`'d member and a genuinely read-only member stay silent.

---

## Step A7 — Make fluent config discovery fail closed

**Solves:** A-5.

**Problem.** `BuildConfigCall` returns null — silently — for open-generic receivers (`:254`), non-literal
member selectors (`:268`), non-constant `MaxDepth` (`:260-265`), and **any cross-assembly configuration**.
When it returns null the generated body just bakes the convention. The runtime objects are inert:
`ShiftMapperBuilder.MaxDepth(int)` is literally `=> this` (`ShiftMapperBuilder.cs:146`), and the
`ignoredView/Entity/List/Copy` sets have **zero readers**. So a registration the generator failed to see has
**no effect whatsoever** in all four directions. You write `map.Ignore(x => x.Secret)`, it compiles, it runs,
and the member is still mapped.

`SHENGEN005` already treats exactly this failure class — a registration the generator cannot bake — as a
build **error** for conditional registration. This step extends that stance to the rest of the class.

**What this step does.** Two layers:
1. **Build-time:** where the un-bakeable shape is statically detectable (open-generic receiver, non-literal
   selector, non-constant `MaxDepth`), report an error rather than returning null.
2. **Runtime:** emit the baked custom/ignored member sets into the generated class and add
   `ShiftMapperBuilder.VerifyBaked(...)`, which throws when a member registered at runtime was not baked at
   build time. **This is the only mechanism that can cover cross-assembly configuration**, which no
   compilation-local analysis can ever see.

**What it solves.** `Ignore` starts meaning something. Today it is decoration.

**Files.** `ShiftEntityMapperGenerator.cs:240-280`; `ShiftEntity.Core/ShiftMapperBuilder.cs`.

**Depends on.** A1.

**Breaks.** Builds that contain an un-bakeable registration — all of which are currently no-ops, so nothing
that *works* breaks. There are zero such call sites in the tree today, which makes this a migration-time
hazard to close now rather than a live bug.

**Done when.** An un-bakeable registration either fails the build or throws at first use with a message
naming the member and the reason.

---

## Step A8 — Deep-write safety: diagnostic + `existing`-aware `ForEntity` + `AfterEntity`

**Solves:** A-4, and unblocks the 16 `AfterMap` ports in Stage E.

**Problem.** Auto-deep entity write is **replace-with-new** and default-on:
`existing.X = …Select(d => pair.MapBack(d, new Child(), ctx))` (`:1200-1218`). Meanwhile
`ModelBuilderExtensions.cs:66-71` forces `Restrict` on every non-ownership FK. So for a parent whose children
are tracked `ShiftEntity` rows with required FKs, saving either throws or duplicates/orphans link rows —
silently. The generated `MenuVariant` mapper composes all four collections that `GeneralMappingProfile.cs:315-330`
deliberately ignores and merges by business key.

Keeping the default is correct: reverting it re-opens the silent-empty-on-save bug fixed on 2026-08-06
(`.shift/repos/shift-entity/mapping-abstraction-plan.md` §23, pinned by `GeneratedDeepWriteTests`). The fix is to make the dangerous
case *visible and overridable*, not to turn the feature off.

**What this step does.** Three additions:
1. A **diagnostic** when auto-deep composes into a tracked `ShiftEntity` navigation with a required FK.
2. An **`existing`-aware `ForEntity` overload** — `Func<TViewDTO, TEntity, MappingContext, TProp>`. The call
   site already passes `existing.{name}` as the fallback value, so the plumbing is there.
3. An **`AfterEntity(Action<TViewDTO, TEntity, MappingContext>)`** hook, baked as a trailing call in the
   generated `MapToEntity`.

**What it solves.** The link-table corruption case announces itself at build time, and the escape hatch to
express "merge by business key, don't replace" exists. Without (2) and (3), ADP's 16 `AfterMap` blocks —
collection reconciliation with soft-delete/revive semantics — have nowhere to go, and Stage E stalls.

Note this is **not** inexpressible today: a user-declared `MapToEntity` suppresses the generated one
(`:1592`) while `MapToEntityGenerated` stays callable (`ProductBrandMapper` proves the pattern). These
additions make the common case ergonomic instead of forcing a full method takeover.

**Files.** `ShiftEntityMapperGenerator.cs:1200-1218`; `ShiftEntity.Core/ShiftMapperBuilder.cs:57-68`.

**Depends on.** A1, A6.

**Breaks.** Nothing — all three are additive.

**Done when.** A parent with a tracked child collection and a required FK warns at build time; an
`AfterEntity` hook can reconcile that collection by business key; an EF integration test asserts child IDs
are **stable** across an update.

---

## Step A9 — Exclude soft-deleted children from auto-deep composition

**Solves:** A-6.

**Problem.** Soft delete is enforced in exactly one place in this framework — a `Where(x => !x.IsDeleted)`
appended to the **root** list DTO queryable (`ShiftEntity.Web/Extensions/IQueryableExtensions.cs:71`,
`:120-128`). There is no `HasQueryFilter` anywhere in the ecosystem. Generated deep composition therefore
pulls child rows unconditionally, in both the view and list directions, and child DTOs in the deep shape are
plain classes the root filter can never reach. No `For*Child(ren)` overload accepts a predicate.

This is latent today only because `DefaultAutoMapperProfile` declares no child pair maps, so `ProjectTo`
composes nothing deep and lists come back flat. **The moment generated deep mapping is the only path, every
parent with a soft-deletable child collection starts returning deleted children.**

**What this step does.** When the child entity carries the soft-delete surface, emit the predicate
automatically — `.Where(c => !c.IsDeleted)` in the list direction (SQL-translatable) and the equivalent in
the view direction (free, in memory). Add an opt-out for genuine audit/history cases.

**What it solves.** Prevents deleted rows appearing in every deep payload the day a service flips —
a data-exposure bug, not just a correctness one.

**Files.** `ShiftEntityMapperGenerator.cs` — the deep composition emitters in `BuildListAssignments` and the
view body; `ShiftEntity.Core/ShiftMapperBuilder.cs` for the opt-out.

**Depends on.** A1.

**Breaks.** Deep payloads stop including deleted children. Anything relying on that was relying on a bug.

**Done when.** A parent with a soft-deleted child returns it in neither the view nor the list projection, and
the generated SQL contains the predicate (assert via `ToQueryString()`).

---

## Step A10 — Low-severity generator cleanups

**Solves:** A-8, A-9, A-10, C-5.

**What this step does.** A batch of small, independent fixes, none of which gates anything:
- Read `[ShiftEntityKeyAndName].Text` instead of hardcoding `.Name` (`:919`, `:1290`, `HasStringName` at `:1734`). ~10 lines, pure future-proofing — zero live divergence.
- Add init-only members to the unmapped **scan** only (never assign them). This is the one shape hole with a live silent regression: AutoMapper sets init setters by reflection, the generator cannot, and today the diagnostic can't even see them.
- Accept `IArrayTypeSymbol` as a collection and emit `ToArray()`.
- Defensive-copy dictionaries and same-typed lists in the view direction (currently aliased by reference).
- Report an error for constructor-only DTOs rather than emitting source that fails with `CS7036`.
- Namespace-prefix the pair-mapper `AddSource` hint name (`:1520` vs `:1670` collide for same-named pairs in different namespaces).
- Match `HasUserMethod` by full signature, and treat an explicit interface implementation as a takeover.
- Delete the two dead `ForCopyChild`/`ForCopyChildren` names from the generator's method lists and fix the comment at `:1372` — they are documented but **do not exist**. Document `ForCopy`/`IgnoreCopy`/`[ShiftEntityMapperIgnore]` as the per-member mechanism instead.

**What it solves.** Removes the remaining paper cuts, and stops the docs describing an API that isn't there.

**Depends on.** A1.

**Breaks.** Nothing.

**Done when.** Each bullet has a test or a regenerated-output diff.

> **Explicitly out of scope for Stage A** — decided, not deferred:
> - **Flattening.** It re-imports exactly the invisible two-level reach this whole effort exists to remove, and in the view direction it triggers lazy loads. A5's synthesized fix line is the answer instead.
> - **A `ShiftEntityMapperStrict` MSBuild property.** `<WarningsAsErrors>SHENGEN00x</WarningsAsErrors>` already gives per-project strictness.
> - **A "repository has no mapper" diagnostic.** Structurally vacuous — a triple the generator can see is a triple it already generated for. Enforcement belongs at startup (D3), not at build time.
> - **The equatable-model incrementality rewrite.** The models carry `ITypeSymbol`, so the cache wouldn't hold. Generator build cost is pre-existing and unchanged by this work.

---

# Stage B — Close framework-owned holes

These are things a consumer **cannot fix themselves**. All are independent of Stage A and of each other, so
they can run in parallel with a second person.

## Step B1 — Wire a CI gate

**Solves:** B-8. **Do this before anything else in Stage B** — every safety net in this plan is decorative
until something runs it.

**Problem.** `ShiftTemplates/azure-pipeline.yml:114-131` runs only the two TypeAuth test projects, and both
are conditioned on `release-all`/`release-typeauth` — so a **`release-framework` tag runs zero tests**. Line 39
builds `ShiftTemplates.sln`, which is two projects, one of them content-only: **StockPlusPlus is never
compiled in CI.** No `dotnet test` exists in any ShiftEntity or ShiftIdentity pipeline.
`ShiftIdentity.Data.Tests` is an empty `bin`/`obj` folder with no csproj.

**What this step does.** Add, before the pack steps:
1. `dotnet test ../ShiftEntity/ShiftEntity.Tests` — verified DB-free (no SQL or Cosmos references).
2. `dotnet build "content/Framework Project/StockPlusPlus.Test"` — the build step alone gates every
   `SHENGEN` **error** across the whole sample app, which is most of Stage A's value.

**Restore constraint:** the sample csprojs take unconditional `PackageReference`s at `$(ShiftFrameworkVersion)`
— the version being packed — so a naive add fails with NU1102. Add the just-packed local feed, and add
`Condition="!Exists(…)"` guards mirroring `ShiftIdentity.Data.csproj:39` as the permanent fix. Target the
csproj files, never `StockPlusPlus.sln` (it references repos CI never clones).

Then `--filter "Category!=RequiresSql"`, and a SQL service container later.

**What it solves.** Makes every subsequent step verifiable by machine instead of by memory.

**Files.** `ShiftTemplates/azure-pipeline.yml`; sample `.csproj` files.

**Depends on.** Nothing.

**Breaks.** Nothing (may surface pre-existing failures — that is the point).

**Done when.** A `release-framework` tag runs `ShiftEntity.Tests` green and compiles StockPlusPlus.

---

## Step B2 — Make `dotnet new shiftentity` emit a mapper

**Solves:** B-5. **This is broken right now, independent of the removal — fix it this week.**

**Problem.** The item template ships `ProductBrandRepository.cs`, whose constructor is
`base(db, x => x.UseMapper(new ProductBrandMapper()))` and which has `using StockPlusPlus.Data.Mappers;`
(`ProductBrandRepository.cs:18`, `:28`). The template's `include`/`rename` maps list eight files and **none is
a mapper** — `grep -c Mapper content/ShiftEntity/.template.config/template.json` returns `0`. So the
sanctioned way to add an entity already produces a project that does not compile.

**What this step does.** Add `Framework Project/StockPlusPlus.Data/Mappers/ProductBrandMapper.cs` to both the
`include` and `rename` maps so scaffolding emits a working `[ShiftEntityMapper] partial class` alongside the
repository. Add a Builder smoke step that runs `dotnet new shiftentity` into the generated test project and
builds it.

**What it solves.** Unbreaks entity scaffolding — and it matters far more after this migration, because every
new entity will need a mapper. Without it, "the mapper is required" has no on-ramp.

**Files.** `ShiftTemplates/content/ShiftEntity/.template.config/template.json`;
`ShiftTemplates.Builder`.

**Depends on.** Nothing.

**Breaks.** Nothing.

**Done when.** `dotnet new shiftentity --solution Foo` produces a project that builds, verified by the Builder
smoke step.

---

## Step B3 — Hand-write `ShiftTagMapper`

**Solves:** B-2.

**Problem.** `ShiftTagRepository` gets its mapping from `ShiftTaggingAutoMapperProfile`, registered by
`AddShiftTagging`. It cannot get a generated mapper, because the generator is attached only to
`ShiftEntity.Core` with `PrivateAssets="all"` — `ShiftEntity.EFCore` has no analyzer item at all. So when the
fallback goes, framework-owned Tag CRUD 500s in **every** consumer that called `AddShiftTagging`, with no
consumer-side workaround.

**What this step does.** Write `ShiftTagMapper : IShiftEntityMapper<Tag, TagListDTO, TagDTO>` next to
`TagProjection`, and register it in `AddShiftTagging` with
`TryAddScoped<IShiftEntityMapper<Tag, TagListDTO, TagDTO>, ShiftTagMapper>()`.

**No repository change is needed** — `ShiftTagRepository.cs:14-17` already has a constructor taking that exact
interface, and the DI-registered mapper is also picked up by `ShiftRepository.cs:146`.

**Three traps:**
- `TagProjection.ToDto` omits `ID` and the four audit fields — chain `MapBaseFields`.
- `TagListDTO : ShiftEntityDTOBase`, **not** `ShiftEntityListDTO`, so `MapBaseListFields` will not compile. Bind `IsDeleted` explicitly.
- `MapToEntity` must still write `IsDeleted` — the current profile does not ignore it.

`CopyEntity` → `ShallowCopyTo`. Note this **fixes** a latent throw: no `CreateMap<Tag,Tag>` exists today.

**What it solves.** Removes the one blocker a consumer is powerless to work around.

**Files.** `ShiftEntity.EFCore/Tagging/` — new `ShiftTagMapper.cs`, plus `ShiftTaggingServiceCollectionExtensions.cs`.

**Depends on.** Nothing.

**Breaks.** Nothing.

**Done when.** `TaggingTests` (10 integration tests) pass with the AutoMapper tagging profile removed from the
container.

---

## Step B4 — De-eagerize the Cosmos replication constructors

**Solves:** D-1. **~3 lines, highest value-per-line in the plan.**

**Problem.** Both `CosmosDbReferenceOperation` constructors call
`services.GetRequiredService<IMapper>()` **eagerly** (`CosmosDBReplication.cs:133`, `:147`). It fires on the
first `.Replicate<>()` in a catch-up sweep regardless of whether the caller supplied mapping delegates — so
ShiftIdentity and ADP.Menus, which are already 100% delegate-driven and AutoMapper-free *in intent*, still
break. `ADP.Menus.Sample.Functions/Program.cs:30-34` already ships `services.AddAutoMapper(_ => { });`
purely to satisfy this, with a comment saying so.

**What this step does.** Hold the `IServiceProvider` and resolve lazily inside the three `mapping is null`
branches (`:183`, `:285`, `:377`) — exactly what the trigger path already does correctly
(`ShiftEntityCosmosDbOptions.cs:184`, `:310`, `:359`).

**What it solves.** Immediately unblocks every already-migrated replication consumer, and lets that
apologetic `AddAutoMapper(_ => { })` line be deleted.

**Files.** `ShiftEntity.CosmosDbReplication/Services/CosmosDBReplication.cs`.

**Depends on.** Nothing. **Ship standalone.**

**Breaks.** Nothing — strictly a deferral of an existing resolve.

**Done when.** A host with no AutoMapper registration can run a full delegate-driven catch-up sweep.

---

## Step B5 — Move `AsNoTracking()` into `OdataList`

**Solves:** B-13.

**Problem.** `AsNoTracking()` is applied only inside `AutoMapperShiftEntityMapper.MapToList` (`:20`). Every
hand-written or generated mapper therefore tracks every row of every list response, and the day a service
migrates, list endpoints silently start tracking.

**What this step does.** Apply it once in `ShiftRepository.OdataList` (`:206-212`), before handing the
queryable to the mapper.

**What it solves.** Makes the no-tracking guarantee a property of the **repository**, not of one mapper
implementation — so it holds for every mapper kind, forever.

**Files.** `ShiftEntity.EFCore/ShiftRepository.cs:206-212`.

**Depends on.** Nothing.

**Breaks.** Nothing. (`AutoMapperShiftEntityMapper` may keep its call harmlessly until Stage F.)

**Done when.** A list request through a hand-written mapper leaves the change tracker empty.

---

## Step B6 — Move the tags-in-list splice into `OdataList`

**Solves:** B-14, and half of B-15.

**Problem.** Whether a list projection includes tags is decided at **build time**, by the generator swapping
`Queryable.Select` for `TaggableProjectionExtensions.SelectWithTags` (`:1601-1605`). A hand-written
`MapToList` therefore returns no tags, silently — masked today because `ProjectTo` handles it. A diagnostic
cannot fix this: it can never reach a hand-written mapper in another assembly. There is also a layering
inversion — the generator ships with `ShiftEntity.Core` but emits a call into `ShiftEntity.EFCore`
(`TaggableProjectionExtensions.cs:14`, `:29`), so a Core-only project with a taggable entity gets source that
does not compile.

**What this step does.** Move the splice into `ShiftRepository.OdataList`, with an idempotency guard (no-op
when a `Tags` binding is already present). Relocate `TaggableProjectionExtensions` and the `TagProjection.ToDto`
expression down into `ShiftEntity.Core`, which already owns `Tag` and `IShiftEntityTaggable`, leaving only
the DbContext-bound registration in EFCore.

**What it solves.** Tags-in-list becomes a runtime guarantee covering every mapper kind, and the layer
inversion disappears. Note the check must cover `ShiftRepository<,,,>` subclasses, not only
`IShiftEntityMapper` implementers.

**Files.** `ShiftEntity.EFCore/ShiftRepository.cs`; `ShiftEntity.EFCore/Tagging/TaggableProjectionExtensions.cs`
→ `ShiftEntity.Core/Tagging/`; `ShiftEntityMapperGenerator.cs:1601-1605`.

**Depends on.** B5 (same method), B3 (same area).

**Breaks.** Nothing — zero live exposure today (only `Product` is a taggable list triple, and its override
already calls `SelectWithTags` correctly).

**Done when.** A hand-written `MapToList` on a taggable entity returns tags without doing anything special.

---

## Step B7 — Make the `MapToList` base-member contract explicit and checked

**Solves:** B-7.

**Problem.** `OdataList` returns the projected queryable unfiltered, and the Web layer then appends
`.Where(x => !x.IsDeleted)` to the **already-projected DTO** queryable
(`IQueryableExtensions.cs:71`, `:120-128`). So the list DTO's `IsDeleted` **must** be bound from the entity
inside every `MapToList`, or the predicate has nothing to bind to. The same applies to `ID`, which hash-id
`$filter` rewriting and `$orderby` run against on the projected DTO.

This is free today via `ProjectTo`'s name convention. It is documented nowhere —
`IShiftEntityMapper.cs:16-20` talks only about `MappingContext` — there is no diagnostic, and no test. A
mapper author who omits `IsDeleted` gets one of two failures, both found in production: the endpoint 500s on
translation, or it silently returns soft-deleted rows.

**What this step does.** Three layers:
1. Document the contract on `IShiftEntityMapper.MapToList` with a base-member checklist. (Note `MappingHelpers.MapBaseListFields` cannot help here — it is an in-memory call and useless inside a projection.)
2. A generator warning when the emitted member-init omits either member.
3. A reflection test asserting every discovered list DTO binds both.

**What it solves.** Converts an undocumented tribal invariant into something the compiler and the test suite
enforce — before hand-written mappers become mandatory.

**Files.** `ShiftEntity.Core/IShiftEntityMapper.cs`; `ShiftEntityMapperGenerator.cs`;
`ShiftEntity.Tests/Mapping/`.

**Depends on.** A5 (reuses the list diagnostic plumbing).

**Breaks.** Nothing.

**Done when.** A list DTO missing `IsDeleted` fails the build or the test suite, not production.

---

## Step B8 — Make `ToForeignKey` throw a 400 instead of a 500

**Solves:** C-1.

**Problem.** `MappingHelpers.ToForeignKey` is an unguarded `long.Parse(selectDTO.Value)` (`:120-123`), where
AutoMapper preserved on blank (`AutoMapperExtensions.cs:23`). Most of the exposure is already covered — on the
MVC path, implicit-required-for-non-nullable-reference-types plus `ModelState` 400s a missing select before
the mapper ever runs. Two residuals are real: a **blank** `{"Value":""}` payload (passes validation →
`FormatException` → 500), and **minimal-API endpoints**, whose `ShiftEntityValidationEndpointFilter.cs:32`
does DataAnnotations only → `NullReferenceException` → 500.

**What this step does.** Harden the helper to throw `ShiftEntityException(…, 400)` naming the member via
`[CallerArgumentExpression]`. `ShiftEntityCrudHandler` already routes that to a 400. No generator change
needed.

**Safe for lists:** the list projection **inlines** the select DTO member-init (`:1283-1298`) and never calls
the helper, so no throwing code enters an expression tree.

**What it solves.** A malformed FK becomes a client error naming the field, instead of a 500 with a stack trace.

**Files.** `ShiftEntity.Core/MappingHelpers.cs:120-123`.

**Depends on.** Nothing.

**Breaks.** A blank FK payload now 400s instead of 500ing. Both are failures; one is diagnosable.

**Done when.** A blank and a missing select each produce a 400 naming the member, on both the MVC and
minimal-API paths.

---

## Step B9 — Make `CopyEntity` throw like its three siblings

**Solves:** B-12.

**Problem.** `CopyEntity` is the only mapping method with a silent fallback — `ShallowCopyTo` when no mapper
is configured (`ShiftRepository.cs:196-201`) — and it is reached on **every** `ReloadAfterSave` save. So
removal would swap implementations with no signal at all.

**But measured:** the AutoMapper path is a **no-op today**. No `CreateMap<Entity,Entity>` exists, AutoMapper
14's assignable mapper returns the source and leaves the target untouched (reproduced standalone), and
`GetIQueryable` never applies `AsNoTracking`, so the reload usually returns the *same tracked instance*.
Removal therefore **fixes** this.

**What this step does.** Make it throw like `MapToView`/`MapToEntity`/`MapToList`, so "no mapper configured"
means one thing everywhere and `ShallowCopyTo` becomes the documented default **body**, not a hidden
fallback. **Land `ProductRepository.CopyEntity` first** — that repository overrides three of four methods and
has includes, so `ReloadAfterSave` is set on every insert and it would break.

Blast radius is wider than it looks: the taggable auto-include sets `ReloadAfterSave` on every taggable
insert too.

**Do not golden-pin today's behavior** — that would pin a no-op. Write the `ReloadAfterSave` test against
the **desired** state.

**Files.** `ShiftEntity.EFCore/ShiftRepository.cs:196-201`;
`StockPlusPlus.Data/Repositories/ProductRepository.cs`.

**Depends on.** Nothing.

**Breaks.** Any repository relying on the silent shallow copy — enumerate via the registry before landing.

**Done when.** `ReloadAfterSave` returns a correctly repopulated entity, asserted against the desired state.

---

# Stage C — Build the parity harness

> **This stage has a closing window.** The differ compares generated output against AutoMapper output. Once
> AutoMapper is deleted there is no oracle, permanently. Build this before Stage E flips anything.

## Step C1 — Triple differ (DB-free)

**Solves:** the risk that Stage E silently changes payloads.

**What this step does.** For every discovered `(entity, list, view)` triple, map the same input through both
the AutoMapper mapper and the generated mapper, and compare.

**Four details that decide whether it works:**
- **Build the baseline through the public entry point** — `AddControllers().AddShiftEntityWeb(o => o.AddDataAssembly(…).AddAutoMapper(…))`, then `RegisterShiftRepositories(…)`, then `GetRequiredService<IMapper>()`. Do **not** hand-roll `new MapperConfiguration(...)`: `DataAssemblies`, `AutoMapperAssemblies`, `EndpointDefaultMaps` and `GetConfiguredPairs` are all `internal` with `InternalsVisibleTo` limited to EFCore and Web, so a hand-rolled baseline silently omits the endpoint-default-map step and is **not** the production configuration.
- Wrap it in `AutoMapperShiftEntityMapper<E,L,V>` so both arms share one interface.
- **Enumerate triples from `ShiftEntityEndpointDiscovery` + repository generic arguments**, so new entities are covered automatically and nobody maintains a list.
- Use a **reflective object filler** — deterministic non-default value per scalar, in MAXIMAL and MINIMAL passes — not hand-authored fixtures. Fixtures reintroduce exactly the "whatever the author remembered" failure mode the harness exists to eliminate.

Compare `MapToView` and `MapToEntity`. **Skip `MapToList` here** — `AutoMapperShiftEntityMapper.MapToList`
calls `AsNoTracking()`, which throws on an in-memory `IQueryable`; put list parity in C3 or call `ProjectTo`
directly for the baseline leg.

Compare **by member path**, not JSON blob equality, so a failure names the member.

**What it solves.** Turns "we think the generated mapper matches" into a reviewed list of exactly where it
doesn't.

**The deliverable is the reviewed `KnownDivergence(triple, memberPath, reason)` table — not a green build.**

**Depends on.** Stage A (so the differ measures the fixed generator, not the broken one).

**Done when.** Every triple in StockPlusPlus and ShiftIdentity either matches or has a reviewed divergence entry.

---

## Step C2 — Replication goldens

**Solves:** the risk that Stage E changes live Cosmos document content.

**What this step does.** Convert the existing 22 `ReplicationMappingParityTests` facts, and
`AttributeEndpointTests.cs:132`, off `IMapper` and onto **committed JSON snapshots** — including the four
null-navigation and three apply-onto cases, which deliberately encode AutoMapper's null-propagation and
`"0"`-for-null-nav quirks.

For the six un-migrated ADP pairs, capture goldens **before** writing the manual mapper, including an
apply-onto-**populated**-destination case so "which members got overwritten" is captured rather than assumed.

**What it solves.** Makes each replication port in Stage E a diffable change instead of a leap of faith —
and these are documents in a partitioned store, where a wrong write stamps a clean watermark and never retries.

**Depends on.** B4.

**Done when.** All 22 facts pass with no AutoMapper in the test container.

---

## Step C3 — SQL translation tests for the deep list path

**Solves:** the risk that a list projection is untranslatable in production.

**Problem.** The auto-deep list path has **zero** translation coverage. `DeepListMappingTests` runs
LINQ-to-Objects over arrays, which happily executes constructs EF cannot translate — and its own doc comment
("nothing goes deep automatically in the list direction") is contradicted by committed three-level generated
output.

**What this step does.** Mirror `CompanyBranchListTranslationTests`: project → `.Where` on a scalar →
`.Where(!IsDeleted)` → `.OrderBy` → `.Count()` → `ToQueryString()`.

**What it solves.** This matters because the **entire OData pipeline** — `$filter`, soft delete, `$orderby`,
`Count`, `Skip`/`Take` — is applied **after** the projection. A collection-bearing member-init can therefore
become untranslatable the first time a user types in a filter box, in production, on a page that worked
during testing.

**Depends on.** A9 (so the emitted predicate is included in the assertion).

**Done when.** Every deep list triple has a translation test, and the generated SQL is asserted, not assumed.

---

# Stage D — Wiring & enforcement

Nothing here changes behavior by default. The mode stays `AutoMapperFirst` until a service opts in.

## Step D1 — `MappingMode` + registry resolution in `ShiftRepository`

**Solves:** B-1.

**Problem.** `ShiftRepository.InitCommon` resolves `options.Mapper` → DI → AutoMapper → nothing
(`:144-157`) and **never consults `ShiftEntityMapperRegistry`**, which is read only by `UseGeneratedMapper()`
and endpoint discovery. ADP has 26 `ShiftRepository<>` subclasses and **zero** `UseMapper`/`UseGeneratedMapper`
calls; Menu has 11 and zero. Deleting the fallback with no other change makes all 37 throw per request.

**What this step does.** Add an explicit mode and a registry step:

```
ShiftEntityOptions.MappingMode = AutoMapperFirst | GeneratedFirst | GeneratedOnly

options.Mapper
  → DI IShiftEntityMapper<E,L,V>
  → ShiftEntityMapperRegistry        (GeneratedFirst / GeneratedOnly)
  → compat seam                      (AutoMapperFirst)
  → throw
```

**Cache the activator, never the instance.** Generated mappers hold per-instance `__shiftMapperBuilder` state
that `AddConfiguration` mutates; a shared singleton would leak one repository's customization into every
consumer of the triple.

**What it solves.** Makes "use the generated mapper" a one-line, reversible, per-service decision instead of
37 repository edits — and keeps the AutoMapper path as the default until each service is ready.

**Why not the alternatives** (all evaluated, all lose):
- **A compile-time "no mapper" diagnostic.** All five discovery pipelines are syntax providers over the *current* compilation, so the only triples the generator can see are the ones it already generated for — the diagnostic could essentially never fire on a real gap. It is also blind to packaged repositories, to the open-generic `ShiftRepository<,,,>` registered at `IServiceCollectionExtensions.cs:194` and closed at runtime via `MakeGenericType`, and to reflection-only paths.
- **Registry-implicit with no mode switch.** This is exactly the change that silently swaps a hand-tuned profile for convention output. Measured on the first triple examined: the generated `WarrantyClaim` mapper drops `HasAttachment` and the flattened `ReferenceWarrantyClaimNumber` entirely, and maps `DateTime? → DateTimeOffset?` with the **server's local offset** where the profile pinned UTC. Three regressions, zero warnings.
- **Explicit opt-in everywhere.** 37 repositories plus every attribute endpoint, and it does nothing for framework-owned repositories a consumer cannot edit.

**Files.** `ShiftEntity.EFCore/ShiftRepository.cs:144-157`; `ShiftEntity.Core/ShiftEntityOptions.cs`.

**Depends on.** Stage A, Stage C.

**Breaks.** Nothing — default stays `AutoMapperFirst`.

**Done when.** Flipping one service to `GeneratedFirst` in config changes which mapper it resolves, with no code edit.

---

## Step D2 — Flip the attribute-endpoint default to the registry

**Solves:** B-3.

**Problem.** `UseGeneratedMapper` is a per-attribute `bool` defaulting to `false`
(`ShiftEntityEndpointAttributes.cs:64`); when false, `IServiceCollectionExtensions.cs:208-211` synthesizes an
AutoMapper default map. `EndpointDefaultMaps`, `AddEndpointDefaultMap`, the explicit-generic-args
`DefaultAutoMapperProfile` constructor and `GetConfiguredPairs` all have nothing to become once AutoMapper is
gone.

**What this step does.** Invert inside `BuildSpec`: when `MapperType is null && RepositoryType is null`,
always try `ShiftEntityMapperRegistry.Find`. Keep `UseGeneratedMapper` as an accepted no-op, then obsolete it.

**Critical:** this needs a **three-way split** keyed on `spec.Repository`. A null mapper **with** a custom
repository is correct — the repository configures itself — so a blanket throw would break `Company`,
`CompanyBranch` and `User`.

**What it solves.** Removes the last framework path that reaches for AutoMapper by default. Live blast radius
is small (in the sample, only `api/country`; ShiftIdentity already sets the flag on all ten entities), but the
code path must exist before Stage F can delete the profile machinery.

**Files.** `ShiftEntity.Core/Endpoints/ShiftEntityEndpointDiscovery.cs`;
`ShiftEntity.EFCore/Extensions/IServiceCollectionExtensions.cs:208-211`.

**Depends on.** D1.

**Breaks.** Nothing while the mode defaults to `AutoMapperFirst`.

**Done when.** An attribute endpoint with neither a mapper nor a repository resolves a generated mapper, and
one *with* a repository still resolves through the repository.

---

## Step D3 — Startup validation

**Solves:** B-9.

**Problem.** There is no `AssertConfigurationIsValid` anywhere in the tree, and the AutoMapper registration
uses a deferred factory — so every mapping failure is a **first-request** failure. Across ~63 repositories and
six solutions, that turns each gap in this register from "caught in CI" into "caught in production".

**What this step does.** A `ValidateShiftEntityMappers()` pass at the end of `RegisterShiftRepositories`,
walking the repository scan plus the endpoint specs, and throwing **one aggregate** exception listing every
uncovered triple. Hard-fail under `GeneratedOnly`.

Accept any of: a registry hit, a DI descriptor, a `MapTo*` override
(`DeclaringType != typeof(ShiftRepository<,,,>)` — otherwise `ProductRepository` false-positives), or a
generator-emitted "this repository self-configures" marker.

**Must call `EnsureLoaded(assemblies)` first** — reflection scans do not run module initializers, which is
exactly why `ShiftEntityEndpointDiscovery.cs:117` already does `RunModuleConstructor`. Wrap it so one bad
consumer assembly cannot kill startup. The four existing lookup sites are primed by construction and need
nothing.

**What it solves.** This is where "the mapper is required" is actually **enforced** — at startup, loudly,
once, with a complete list, instead of per-request in production.

**Files.** `ShiftEntity.EFCore/Extensions/IServiceCollectionExtensions.cs`.

**Depends on.** D1, D2.

**Breaks.** Nothing under `AutoMapperFirst`.

**Done when.** A service with one uncovered triple fails to start under `GeneratedOnly`, naming the triple.

---

## Step D4 — Stamp and check the codegen ABI

**Solves:** B-10.

**Problem.** The generator ships as an analyzer inside the ShiftEntity package, so it runs in each library's
**own** build and the mapper bodies are compiled into that library's DLL — confirmed present in the shipped
`ShiftSoftware.ShiftIdentity.Data` assemblies. That **inverts the deployment model**: an AutoMapper `Profile`
is *data* interpreted by whatever AutoMapper the host loads, so a convention fix in ShiftEntity.Core reached
every consumer on package upgrade. A generated mapper is *code* frozen at the dependency's build day,
referencing `MappingHelpers` / `ShiftMapperBuilder` / `ShiftEntityMapperRegistry` / `TaggableProjectionExtensions`
by exact signature.

Two consequences nobody has written down:
1. Framework mapping fixes now require **rebuilding and republishing every downstream package**. The 2026-08-06 deep read/write symmetry fix does not reach a consumer of an older `ShiftIdentity.Data`.
2. Any change to the emitted helper surface is a `MissingMethodException`/`TypeLoadException` **at request time**, not a compile error — because NuGet unifies the host to the newest ShiftEntity while the consumer DLL still carries the old call.

While AutoMapper still exists, a consumer on an old package at least degrades to the fallback. Remove it, and
the pre-baked mapper is the only mapping that consumer will ever have.

**What this step does.** Freeze the set of types the generator may emit calls to as a **versioned,
additive-only** public contract. Stamp an ABI version into each emitted mapper (e.g.
`[ShiftGeneratedMapper(AbiVersion = N)]`), have `ShiftEntityMapperRegistry.Register` carry it, and check it at
startup alongside D3. Document the republish requirement in the framework release process.

**What it solves.** Converts a class of production-only failures into a startup error, and makes the new
deployment coupling explicit instead of discovered.

**Files.** `ShiftEntity.Core/ShiftEntityMapperRegistry.cs`; `ShiftEntityMapperGenerator.cs`; release docs.

**Depends on.** D3.

**Breaks.** Nothing additive; a version-skewed consumer now fails at startup instead of mid-request.

**Done when.** A deliberately skewed consumer assembly fails startup with a message naming the package to rebuild.

---

## Step D5 — Registry conflict detection

**Solves:** B-11.

**Problem.** `ShiftEntityMapperRegistry.Register` is last-write-wins (`:22`). A subclass of a **packaged**
repository resolves the same triple and registers a second, convention-only mapper built from configuration
it cannot see. Zero occurrences today — all nine in-tree repositories derive directly — but the failure would
be silent and very hard to find.

**What this step does.** Make `Register` idempotent for the same type, deterministically prefer the mapper
whose assembly declares the entity, record conflicts, and throw **one readable aggregate at startup**.

**Do not throw from `Register` itself** — it runs in a `[ModuleInitializer]`, so an exception surfaces as an
unreadable `TypeInitializationException`. Optionally skip generation when the matched base came from a
referenced assembly (warn, don't error).

**Files.** `ShiftEntity.Core/ShiftEntityMapperRegistry.cs`.

**Depends on.** D3.

**Breaks.** Nothing.

**Done when.** Two mappers registering the same triple produce one clear startup error, not a coin flip.

---

# Stage E — Migrate, one service at a time

Repeat this recipe per service. Each pass is a reviewable, reversible unit of work.

## Step E1 — Per-service migration recipe

1. Flip the service to `GeneratedFirst`.
2. Build. Clear every `SHENGEN` warning — each one is a real decision, resolved with `ForList` / `ForView` / `ForEntity` / `Ignore` / `AfterEntity` / a method override.
3. Run the C1 differ for that service's triples. Resolve every divergence **explicitly** — either fix the mapper or add a reviewed `KnownDivergence` entry.
4. Run the service's own test suite.
5. Flip to `GeneratedOnly`.
6. Delete that service's AutoMapper profiles.

**Recommended order — easiest first, so the recipe is proven before it meets the hard case:**

| Order | Service | Why here |
|-------|---------|----------|
| 1 | **ADP.Surveys** | Cleanest — 151 profile lines, no `AfterMap`. |
| 2 | **ADP.WarrantyClaims** | Small; also where the differ already found three regressions, so it validates the harness. |
| 3 | **ADP.ClaimableItems** | Moderate, plus 5 replication sites (Step E3). |
| 4 | **ADP.Menus** | **Worst** — 5 `AfterMap` blocks with soft-delete/revive collection reconciliation. Do it once A8's `AfterEntity` hook is proven. |
| 5 | **Menu** | Only if it is still alive — see [`02-open-decisions.md`](02-open-decisions.md) Q5. |

**Do not drive this from a `CreateMap` codemod.** `LabourRateMappingListDTO` and `MenuVersionDTO` have no
`CreateMap` at all yet still need mappers. **Enumerate triples from repositories, not from profiles.**

---

## Step E2 — Port the template's 12 replication sites

**Solves:** D-2, template half. **Do this early in Stage E — it stops the bleed.**

**Problem.** `StockPlusPlus.API/Controllers/UtilityController.cs:181-229` has 12 `.Replicate<…>` calls with
no mapping delegate — in the file **every new microservice is scaffolded from**. Every new service inherits
the dependency. Failures here are swallowed by the per-row `catch` (`:207-211`), so they surface as
permanently-dirty rows rather than an exception.

**What this step does.** Delete the hand-rolled block and call
`IdentityCatchUpReplicationExtensions.ReplicateAllAsync`, which **already covers all 13 entities**. The
template needs no new mapping code at all.

**Files.** `StockPlusPlus.API/Controllers/UtilityController.cs`.

**Depends on.** B4.

**Done when.** `UtilityController.ReplicateAll` contains no delegate-free `.Replicate<>` call.

---

## Step E3 — Port the remaining ADP replication sites, then make the delegate required

**Solves:** D-2, D-4, D-6.

**What this step does.**
1. Port `ADP.ClaimableItems` (5 sites) and `ADP.WarrantyClaims` (1 site) as plain static `ToXModel()` / `ApplyTo()` methods — the proven in-tree pattern (`IdentityReplicationMappingExtensions.cs`, `MenuCosmosMappers.cs`).
2. **Transcribe with `?.` and `?? default` throughout.** `ClaimableItemProfile.cs` dereferences `src.Campaign!` ten times and `SetUpReplication` passes no include — so **those fields already replicate as defaults today**. A naive port NREs inside a swallowed task and looks like nothing happened. Adding the missing `Include` is a **separate commit**: it changes live document content.
3. Gate each port with a C2 golden diff **before** deleting the profile.
4. Then drop the `= null` default on the mapping parameter of all 8 overloads — every remaining offender becomes a **compile error** instead of a silent runtime hole.
5. Guard `Utility.BuildStamp` to throw when the document id is null/empty (the existing catch marks the row dirty and retries). Make non-null partition keys an opt-in `requireNonNullPartitionKey:` — nullable key columns are legitimate and test-pinned.

**What it solves.** Replication stops being able to fall back at all, and the compiler — not a code review —
guarantees it.

**Depends on.** B4, C2, E2.

**Done when.** No `Replicate` overload accepts a null mapping delegate, and the whole solution compiles.

---

# Stage F — Delete

## Step F1 — Ship the compat package and the obsoletions

**Solves:** B-4.

**What this step does.** Move `DefaultAutoMapperProfile`, `AutoMapperExtensions`, `AutoMapperShiftEntityMapper`
and `AddAutoMapper` into an opt-in `ShiftSoftware.ShiftEntity.EFCore.AutoMapper` package, **keeping the
original namespaces** so consumer `using`s still compile. No `[TypeForwardedTo]` — that would keep Core's
reference. Add a non-generic Core seam (`IShiftEntityFallbackMapperFactory`) consulted **after** the registry.

**Critical:** the compat package must **not** register `IShiftEntityMapper<,,>` open-generically. MS.DI
resolves a closed request against both descriptors and the last one wins, so
`AddShiftEntityAutoMapperCompat()` called after `AddShiftEntity` would silently replace every closed endpoint
mapper registered at `IServiceCollectionExtensions.cs:221`.

Keep `AddDataAssembly` — it has a second consumer (endpoint discovery).

Ship `AddShiftIdentityAutoMapper()` as `[Obsolete(error: true)]` for one release with a message naming the
replacement. **Never as a silent no-op** — that is the one option that lets a host compile while its
replication mapping quietly vanishes.

**Depends on.** Stage E complete for all services.

---

## Step F2 — Port ShiftIdentity's 11 ad-hoc `Map<T>` sites

**Solves:** the ShiftIdentity half of B-4.

**Problem.** 11 ad-hoc `IMapper.Map<T>` sites in the User flows, plus `IMapper` as a public constructor
parameter of the shipped `UserRepository`. `UserDataDTO` / `UserInfoDTO` are not any triple's DTOs, and
`UserEndpoints.cs:153` maps an in-memory `IEnumerable`, which `MapToList(IQueryable)` cannot serve. Hard
compile break in three files.

**What this step does.** Two `[ShiftEntityMapper] partial class : IShiftObjectMapper<User, X>` pairs cover 10
of 11 — `MapBack(dto, existing, ctx)` is an exact match for `Map(dto, user)` at `UserRepository.cs:196`.
Route `UserEndpoints.cs:153` through the repository's generated list mapper. `UserEndpoints.cs:64` is a no-op
identity map — delete it.

**Two behavior notes to release-note:**
- `UserInfoDTO : UserListDTO`, but AutoMapper does **not** inherit `ForMember` without `IncludeBase`, so four members are convention-default today (all provably empty: navigations unloaded, `AccessTrees` ctor-initialized, no `TotpEnabled` on the entity). A pair mapper will populate them — a wire change on four endpoints.
- `UserDataDTO.Signature` (`List<ShiftFileDTO>?`) ↔ `User.Signature` (`string?`) depends on a global `ITypeConverter` invisible in ShiftIdentity's own source. The generator bakes both directions (`:928-930`, `:1133-1134`) with byte-compatible helpers.

**Depends on.** F1.

---

## Step F3 — Detach the project template from AutoMapper

**Solves:** B-6.

**What this step does.** `StockPlusPlus.API/Program.cs:166` calls `AddAutoMapper` **outside every `#if`**, so
it ships in every generated project — including `includeSampleApp=false`, which strips the whole
`AutoMapperProfiles/` folder that call exists to scan. Same in `StockPlusPlus.Functions/Program.cs:74`, `:83`.
`StockPlusPlus.Functions/Functions/ProductCategories.cs:45` hand-constructs `MapperConfiguration` +
`DefaultAutoMapperProfile` and uses `opt.Items`, a per-call runtime-context feature `MappingContext` has no
equivalent for — decide whether to add one or restructure that Function. An OData controller in the template
also serves a list via `ProjectTo` outside the repository; route it through the repository instead.

**Depends on.** F1, and B2 (item template) already landed.

**Done when.** `dotnet new shift` builds in all parameter combinations with no AutoMapper reference.

---

## Step F4 — ADP.SyncAgent

**Solves:** D-5, D-7.

**What this step does.** SyncAgent has no ShiftEntity coupling, so this is a **separate workstream** — but
"AutoMapper is gone" stays false until it lands, and it carries the NU1903 advisory into consumer builds.

Immediate: make `IMapper` optional and throw at the fallback line. Then delete the two dead
`<Compile Remove>`d files (`SyncService2.cs`, `CosmosCSVSyncService.cs`), delete `UseAutoMapper` (**zero** call
sites across all 14 repos), and make the mapping delegate required.

See [`02-open-decisions.md`](02-open-decisions.md) Q6 — deleting may be the honest answer.

**Depends on.** Nothing in this plan.

---

## Step F5 — Delete the package references, and the docs pass

**What this step does.** Remove `PackageReference Include="AutoMapper"` from
`ShiftEntity.CosmosDbReplication.csproj:36` **first**, then `ADP.SyncAgent`, then
`ShiftEntity.Core.csproj:33` **last** — replication and SyncAgent carry the security advisory into consumer
builds, so they should stop doing that as early as possible.

Rewrite the 9 docs pages that reference AutoMapper, including the dedicated
`data-project/auto-mapper-profiles.md` — until that page changes, template users keep writing profiles.

**Optional, and explicitly last:** `MapOnto(source, existing, ctx)` codegen for replication pairs
(gap D-3). It is not on the critical path — the delegate form already covers every live case.

**Depends on.** Everything.

**Done when.** `grep -rn "AutoMapper" --include=*.csproj` over the workspace returns nothing outside the
compat package.

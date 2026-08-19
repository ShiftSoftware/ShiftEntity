# AutoMapper Removal — Gap Register

> **Mirror.** The canonical copy of this plan lives in the `.shift` knowledge-base repo at
> `.shift/repos/shift-entity/automapper-removal/`. This copy exists so the plan is readable without
> cloning `.shift`. Keep both in sync; if they ever disagree, `.shift` wins.

**Created:** 2026-08-19
The evidence behind [`01-steps.md`](01-steps.md). Every entry below was read in source, not inferred.
Line numbers are as of `ShiftFrameworkVersion` 2026.08.06.1.

**Severity:** `BLOCKER` = removal cannot proceed · `HIGH` = silent wrong data, must land before any flip ·
`MED` = behavior change to decide deliberately · `LOW` = cleanup.

---

## A. Silent failure in generated mapping

| # | Sev | Gap | Evidence | Step |
|---|-----|-----|----------|------|
| A-1 | BLOCKER | **Only the view direction reports unmapped members.** `ViewEmission` carries an `Unmapped` list; `BuildEntityBody` returns `(Lines, UsedPairs)` and `BuildListAssignments` returns `List<string>` — neither has an unmapped channel. | `ShiftEntityMapperGenerator.cs:1027` (`ViewEmission` record), `:1145` (`BuildEntityBody` signature), `:1270` (`BuildListAssignments` signature), `:1515` + `:1664` (the only two `SHENGEN004` report sites, both `if (view.Unmapped.Count > 0)`) | A5, A6 |
| A-2 | BLOCKER | **`EntityConvention` has no inverse scalar conversions.** It handles FK↔`ShiftEntitySelectDTO`, `List<ShiftFileDTO>`→JSON string, implicit conversions, and nullable-unwrap — then `return null`. No `string→long`, `int→enum`, `string→Guid`. A null return means **no line is emitted at all**. | `ShiftEntityMapperGenerator.cs:1115-1143` (whole method). Compare `ViewConvention` at `:913`, which *does* handle `long(?)→string` and `enum→int(?)`. | A4 |
| A-3 | BLOCKER | **Reserved member names are matched by string, not by declaring type.** A domain column named `Tags` or `Revisions` is silently dropped from view, entity and list. | `:842-843` `ViewHandledMembers` = `{ ID, IsDeleted, CreateDate, LastSaveDate, CreatedByUserID, LastSavedByUserID, Tags, Revisions }`; `:845-846` `EntityExcludedMembers` (same plus `ReloadAfterSave`, `AuditFieldsAreSet`, `IdempotencyKey`); `:1277` list filter is literally `p.Name != "Tags"`. Consumed at `:658`, `:807`, `:1100`, `:1200`, `:1224`, `:1402`. | A3 |
| A-4 | HIGH | **Auto-deep entity write is replace-with-new, default-on, undiagnosed.** Emits `existing.X = …Select(d => pair.MapBack(d, new Child(), ctx))`. Every non-ownership FK is forced to `Restrict`. | `:1200-1218`; `ModelBuilderExtensions.cs:66-71`. Real case: generated `MenuVariant` mapper composes four collections that `ADP.Menus … GeneralMappingProfile.cs:315-330` deliberately `Ignore()`s and merges by business key. | A8 |
| A-5 | HIGH | **Fluent config discovery fails open.** `BuildConfigCall` returns null for open-generic receivers, non-literal selectors, cross-assembly config, non-constant `MaxDepth` — and the runtime objects are inert, so a missed bake has **zero** effect. | `:254`, `:260-265`, `:268`. `ShiftMapperBuilder.cs:146` is literally `public … MaxDepth(int depth) => this;`. The `ignoredView/Entity/List/Copy` sets have zero readers. `SHENGEN005` already treats the same failure class (conditional registration) as a build **error**. | A7 |
| A-6 | HIGH | **Auto-deep composition returns soft-deleted children.** Soft delete is enforced in exactly one place — appended to the *root* list DTO queryable. There is no `HasQueryFilter` anywhere in the tree. No `For*Child(ren)` overload accepts a predicate. | `ShiftRepository.cs:206-212` (`OdataList` — no filter), `ShiftEntity.Web/Extensions/IQueryableExtensions.cs:71` + `:120-128` (filter applied to the already-projected DTO queryable). Latent today only because the AutoMapper profile declares no child pair maps, so `ProjectTo` composes nothing deep. | A9 |
| A-7 | MED | **No flattening; member matching is case-sensitive ordinal.** | `:1034` and `:1274` both build `ToDictionary(p => p.Name, …)`. Live dependence is ~14 members across three ADP list DTOs. `CompanyBranchRepository.cs:56-68` already hand-fixes a `CompanyID`/`CompanyId` case mismatch, with a comment. | A5 (message only) |
| A-8 | MED | **`[ShiftEntityKeyAndName].Text` is ignored** — the generator hardcodes `.Name`; AutoMapper reflects the attribute. | `:919` and `:1290` both call `HasStringName` (`:1734`), which tests for a member literally named `Name`. Compare `DefaultAutoMapperProfile.cs:142-151`. Zero live divergence — all five entity-side usages name `Name`. | A10 |
| A-9 | LOW | Shape holes: arrays never treated as collections; dictionaries and same-typed lists aliased by reference in the view direction; **init-only members invisible to both mapping and the unmapped scan**. | `IsSettable` at `:1696`. Init-only is the only live silent regression — AutoMapper sets init setters by reflection. | A10 |
| A-10 | LOW | Generator robustness: duplicate `AddSource` hint names for same-named pair mappers in different namespaces; `HasUserMethod` matches by name only, so any overload suppresses the real method. | `:1520` vs `:1670`. | A10 |
| A-11 | LOW | Zero incremental caching (`CompilationProvider` in the final `Combine`, broad predicates, unmemoized property walks). **Cost is pre-existing and unchanged by this work** — the generator already emits for every triple regardless of opt-in. | `:141`. | Out of scope |

---

## B. Wiring, discovery, enforcement

| # | Sev | Gap | Evidence | Step |
|---|-----|-----|----------|------|
| B-1 | BLOCKER | **The repository never consults `ShiftEntityMapperRegistry`.** Resolution is `options.Mapper` → DI → AutoMapper → nothing. The registry is read only by `UseGeneratedMapper()` and endpoint discovery. | `ShiftRepository.cs:144-157`. `ShiftRepositoryOptions.cs:84` and `ShiftEntityEndpointDiscovery.cs:119` are the only registry readers. **ADP: 26 `ShiftRepository<>` subclasses, 0 `UseMapper`/`UseGeneratedMapper`. Menu: 11 / 0.** Delete the fallback with no other change and all 37 throw per request. | D1 |
| B-2 | BLOCKER | **`ShiftTagRepository` has no mapper and cannot generate one.** Its mapping comes from `ShiftTaggingAutoMapperProfile`. The generator is attached only to `ShiftEntity.Core` with `PrivateAssets="all"`, so `ShiftEntity.EFCore` gets no analyzer. | `ShiftEntity.EFCore/Tagging/ShiftTagRepository.cs:10-12` (`base(db)`, no builder); `ShiftEntity.Core.csproj:58-59` (the only analyzer attachment). **Good news:** `:14-17` already has a second ctor taking `IShiftEntityMapper<Tag, TagListDTO, TagDTO>` — so a DI registration is enough, no repository change needed. | B3 |
| B-3 | BLOCKER | **Attribute endpoints default to AutoMapper.** `UseGeneratedMapper` is a `bool` defaulting to `false`; when false the framework synthesizes an AutoMapper default map. | `ShiftEntityEndpointAttributes.cs:64`; `ShiftEntity.EFCore/Extensions/IServiceCollectionExtensions.cs:208-211`. `EndpointDefaultMaps`, `AddEndpointDefaultMap`, the explicit-generic-args `DefaultAutoMapperProfile` ctor and `GetConfiguredPairs` all have nothing to become. | D2 |
| B-4 | BLOCKER | **AutoMapper is public API surface.** `ShiftEntityOptions.AddAutoMapper` (6 callers, including the framework's own `AddShiftTagging`), public `DefaultAutoMapperProfile`, extensions living in `namespace AutoMapper` (22 call sites), and `IMapper` as a ctor param of the shipped `UserRepository`. | `ShiftEntity.Core/ShiftEntityOptions.cs`, `DefaultAutoMapperProfile.cs`, `Extensions/AutoMapperExtensions.cs`, `ShiftIdentity.Data/Repositories/UserRepository.cs`. Also constructed directly at `StockPlusPlus.Functions/Functions/ProductCategories.cs:45`. | F1 |
| B-5 | BLOCKER | **`dotnet new shiftentity` already produces code that does not compile.** The item template ships `ProductBrandRepository.cs`, whose ctor is `base(db, x => x.UseMapper(new ProductBrandMapper()))` and which does `using StockPlusPlus.Data.Mappers;` — but the template never emits `Mappers/ProductBrandMapper.cs`. | `content/ShiftEntity/.template.config/template.json` — `grep -c Mapper` returns **0**; the `include`/`rename` maps list 8 files, none of them a mapper. `content/Framework Project/StockPlusPlus.Data/Repositories/ProductBrandRepository.cs:18` and `:28`. | B2 |
| B-6 | BLOCKER | **The project template is AutoMapper-bound outside every `#if`.** `AddAutoMapper` ships in every generated project — including `includeSampleApp=false`, which strips the `AutoMapperProfiles/` folder that call exists to scan. A Function hand-builds `MapperConfiguration`; an OData controller calls `ProjectTo` outside the repository. | `StockPlusPlus.API/Program.cs:166` (not inside any `#if`; the `#if (internalShiftIdentityHosting)` block starts at `:177`); `StockPlusPlus.Functions/Program.cs:74`, `:83`; `StockPlusPlus.Functions/Functions/ProductCategories.cs:45`. | F3 |
| B-7 | BLOCKER | **Nothing states or enforces that `MapToList` must bind `IsDeleted` and `ID`.** The Web layer appends `.Where(x => !x.IsDeleted)` to the **already-projected DTO** queryable. Free today because `ProjectTo` binds both by convention. | `ShiftRepository.cs:206-212` returns the projection unfiltered; `ShiftEntity.Web/Extensions/IQueryableExtensions.cs:71`, `:120-128`. `IShiftEntityMapper.cs:16-20` documents nothing about it. No diagnostic, no test. | B7 |
| B-8 | BLOCKER | **No CI gate runs any mapping test.** The pipeline runs only the two TypeAuth test projects, both conditioned on `release-all`/`release-typeauth` — so a **`release-framework` tag runs zero tests**. StockPlusPlus is never compiled in CI (line 39 builds `ShiftTemplates.sln`, which is 2 projects, one content-only). `ShiftIdentity.Data.Tests` is an empty `bin`/`obj` folder with no csproj. | `ShiftTemplates/azure-pipeline.yml:114-131`. | B1 |
| B-9 | HIGH | **No startup validation anywhere.** Zero `AssertConfigurationIsValid` in the tree; the AutoMapper registration uses a deferred factory. Every mapping failure is first-request. | Whole-tree grep. | D3 |
| B-10 | HIGH | **Generated mappers are baked into every downstream NuGet package**, freezing the codegen ABI. A `Profile` is *data* interpreted by the host's AutoMapper, so convention fixes reached consumers on package upgrade; a generated mapper is *code* frozen at the dependency's build day, calling `MappingHelpers` / `ShiftMapperBuilder` / `ShiftEntityMapperRegistry` / `TaggableProjectionExtensions` by exact signature. | Confirmed present in the shipped `ShiftSoftware.ShiftIdentity.Data` assemblies and in `~/.nuget/packages/shiftsoftware.shiftentity/*/analyzers/dotnet/cs/`. Consequence: the 2026-08-06 deep read/write symmetry fix does **not** reach a consumer of an older `ShiftIdentity.Data`; emitted-helper changes become `MissingMethodException` at request time under version skew. | D4 |
| B-11 | MED | Registry is last-write-wins; a subclass of a *packaged* repository resolves the same triple and registers a second, convention-only mapper built from config it cannot see. Zero occurrences today. | `ShiftEntityMapperRegistry.cs:22`. Note `Register` runs in a `[ModuleInitializer]`, so throwing there surfaces as an unreadable `TypeInitializationException`. | D5 |
| B-12 | MED | **`CopyEntity` is the only mapping method with a silent fallback** (`ShallowCopyTo`), reached on every `ReloadAfterSave`. **Measured: the AutoMapper path is a no-op today** — no `CreateMap<Entity,Entity>` exists, AutoMapper 14's assignable mapper returns the source and leaves the target untouched, and `GetIQueryable` never applies `AsNoTracking`, so the reload usually returns the same tracked instance. Removal *fixes* this. | `ShiftRepository.cs:196-201`. Blast radius is wider than it looks: the taggable auto-include sets `ReloadAfterSave` on every taggable insert. | B9 |
| B-13 | MED | **`AsNoTracking()` is applied only inside `AutoMapperShiftEntityMapper.MapToList`.** Every list endpoint silently starts tracking on migration. | `AutoMapperShiftEntityMapper.cs:20`. | B5 |
| B-14 | LOW | **Tags-in-list is a build-time generator decision** — a hand-written `MapToList` silently returns no tags, and `ProjectTo` masks it today. A diagnostic can never reach hand-written mappers in other assemblies. | `ShiftEntityMapperGenerator.cs:1601-1605` (the `SelectWithTags` swap). Zero live exposure: only `Product` is a taggable list triple and its override calls `SelectWithTags` correctly. | B6 |
| B-15 | LOW | **Layering inversion:** the generator ships with `ShiftEntity.Core` but emits a call to `TaggableProjectionExtensions.SelectWithTags`, which lives in `ShiftEntity.EFCore`. A Core-only project with a taggable entity gets source that does not compile. Same root cause as B-2. | `:1605`; `ShiftEntity.EFCore/Tagging/TaggableProjectionExtensions.cs:14`, `:29`. | B6 |

---

## C. Write-path semantics — behavior changes, not compile breaks

Each of these is a *different answer* from AutoMapper's. In three of five cases the new answer is better.
All five need a release note; see [`02-open-decisions.md`](02-open-decisions.md).

| # | Sev | Change | The honest version |
|---|-----|--------|--------------------|
| C-1 | HIGH | **Required FK: `ToForeignKey` is an unguarded `long.Parse`** where AutoMapper preserved on blank (`AutoMapperExtensions.cs:23`). | On the MVC path, implicit-required-for-non-nullable-reference-types plus `ModelState` already 400s a missing select before the mapper runs. Two genuine residuals: a blank `{"Value":""}` payload (passes validation → `FormatException` → 500), and minimal-API endpoints, whose `ShiftEntityValidationEndpointFilter.cs:32` does DataAnnotations only → `NullReferenceException` → 500. |
| C-2 | MED | **Nullable FK: generated mapping clears where AutoMapper preserved.** | Presented as data loss, but `ShiftAutocomplete` posts `null` on clear — so under AutoMapper "clear this dropdown" was a **silent no-op**. The new behavior fixes a UX bug, and `SourceGeneratedMappingTests.cs:367` already pins clear-on-null green. |
| C-3 | MED | **`MapToEntity` stops writing `ID`, `IsDeleted` and the four audit fields**, which an unguarded `ReverseMap()` did write. | A narrowing, and security-positive: today a client can `PUT {"isDeleted": true}` through `PUT api/UserManager/UserData` and soft-delete its own row, and can set its own `EmailVerified`/`PhoneVerified`. |
| C-4 | MED | **List payloads get richer:** generated projections populate `ShiftEntitySelectDTO`s (with `Text`) that `ProjectTo` left empty, because it never ran AutoMapper's `AfterMap`. | Better data, one LEFT JOIN per member. All six mixed DTOs in the org are `ID` + plain strings; the real exposure is list DTOs carrying *collections* of select DTOs (`CompanyListDTO`, `CompanyBranchListDTO`, `UserListDTO`) — all three already pin them with explicit `ForList`. |
| C-5 | LOW | `CopyEntity` is shallow-only and copies navigations by reference. The documented `ForCopyChild(ren)` escape hatch **does not exist** — the names appear only in the generator's own method lists. | Docs defect. Do **not** add `IsDeleted`/audit fields to `CopyExcludedMembers` — that would defeat the reload's purpose. |

---

## D. Replication

| # | Sev | Gap | Evidence | Step |
|---|-----|-----|----------|------|
| D-1 | BLOCKER | **Eager `services.GetRequiredService<IMapper>()` in both `CosmosDbReferenceOperation` constructors.** Fires on the first `.Replicate<>()` regardless of supplied delegates, so consumers that are already 100% delegate-driven still break. | `CosmosDBReplication.cs:133` and `:147`. `ADP.Menus.Sample.Functions/Program.cs:30-34` already ships `services.AddAutoMapper(_ => { });` purely to satisfy it, with a comment saying so. The trigger path already does this correctly — lazily, inside `if (mapping is null)` (`ShiftEntityCosmosDbOptions.cs:184`, `:310`, `:359`). | B4 |
| D-2 | BLOCKER | **18 delegate-free call sites still on the fallback:** `ADP.ClaimableItems` ×5, `ADP.WarrantyClaims` ×1, and **`StockPlusPlus.API/Controllers/UtilityController.cs:184-229` ×12** — in the file every new microservice is scaffolded from. Failures are swallowed by the per-row `catch` (`:207-211`), so they surface as permanently-dirty rows, not exceptions. | Verified: `UtilityController.ReplicateAll` has 12 `.Replicate<…>` calls with no mapping delegate. `IdentityCatchUpReplicationExtensions.ReplicateAllAsync` already covers all 13 entities, so the template needs **no new code** — just call it. | E2, E3 |
| D-3 | MED | Replication's mapping shapes (N targets per entity, N sources per target, merge-onto-existing) genuinely cannot live in the `(entity, list, view)` triple — **but nothing is inexpressible today.** The merge overload `Func<EntityWrapper<E>, Doc, Doc>` already exists and is what ShiftIdentity and ADP.Menus use. | `ShiftEntityCosmosDbOptions.cs:287-289`; `CosmosDBReplication.cs:316-318`. | F5 (optional) |
| D-4 | MED | Hand-porting the remaining profiles must reproduce AutoMapper's **null-navigation propagation** and its `default(long)→"0"` substitution. `ClaimableItemProfile.cs` dereferences `src.Campaign!` ten times and `SetUpReplication` passes no include — so those fields **already replicate as defaults today**. A naive port NREs inside a swallowed task. | Follow the comments at `IdentityReplicationMappingExtensions.cs:89-91`, `:125-126`. Adding the missing `Include` is a **separate commit** — it changes live document content. | E3 |
| D-5 | MED | **ADP.SyncAgent** carries its own `AutoMapper 14.0.0` and takes `IMapper` as a **required** ctor param; `UseAutoMapper` is public package API with **zero** call sites across all 14 repos. Two of its four services are already `<Compile Remove>`d. | `ADP.SyncAgent/SyncAgent/ADP.SyncAgent.csproj:24`. | F4 |
| D-6 | LOW | A settable partition-key member left unset writes the document into the JSON-null partition; upsert succeeds and the watermark stamps clean. The "orphaned old document" half is **refuted** — the delete uses the persisted `LastReplicationStamp` coordinates, never a re-map. | `CosmosDBReplication.cs:194-201`; design comment at `ShiftEntityCosmosDbOptions.cs:190-196`. | E3 |
| D-7 | LOW | AutoMapper 14.0.0 carries **NU1903 (high severity)**, already emitted in ADP builds. An argument for doing the replication and SyncAgent package references **first**. | ADP build output. | F4 |

---

## E. Consumer volume

Corrected upward after direct counting:

| Metric | Count |
|--------|-------|
| `ForMember` calls | 245 |
| `CreateMap` calls | 109 |
| **`AfterMap` blocks** | **16** ← the genuinely hard part |
| `ConvertUsing` | 3 |
| Profile classes / lines | 23 profiles, ~1,958 lines |
| ADP triples / Menu triples / ShiftIdentity remaining | 26 / 11 / ~3 |
| Ad-hoc `IMapper.Map<T>` sites in ShiftIdentity user flows | 11 |
| Docs pages referencing AutoMapper | 9 |

The 16 `AfterMap` blocks are collection reconciliation against tracked children with soft-delete/revive
semantics. `ForEntity`'s delegate cannot see `existing`, which is why Step A8 adds that overload — with it,
the blocks port near-verbatim.

**A `CreateMap`-driven codemod is not safe on its own:** `LabourRateMappingListDTO` and `MenuVersionDTO` have
no `CreateMap` at all yet still need mappers. Enumerate triples from **repositories**, not profiles.

---

## Checked and found clean — do not re-audit

- **ShiftBlazor, ShiftFrameworkTestingTools, ShiftFrameworkLocalization, UnifiedAttestation, Vehicle-Registration** — zero `AutoMapper` / `IMapper` references.
- **`ShiftEntity.Print`** (FastReportBuilder) — mapping-free.
- **HashIds and localization** — `JsonConverter` layers, untouched by the removal. AutoMapper was doing nothing here.
- **Partition-key orphan hazard in replication** — already mitigated by the persisted `LastReplicationStamp` (see D-6).
- **Generator escape hatch** — a user-declared `MapToView`/`MapToEntity`/`MapToList` suppresses the generated one (`:1582`, `:1592`) while the `*Generated` body stays callable. `ProductBrandMapper` proves the pattern. Several early reviews wrongly reported this as missing.
- **Standalone pair mappers with arbitrary two-type arguments** — already supported and generated even when no triple references them (`:341-367`, `:706-725`), and registered by `(source, target)` (`ShiftEntityMapperRegistry.cs:27-33`). Entity→POCO is a solved shape.

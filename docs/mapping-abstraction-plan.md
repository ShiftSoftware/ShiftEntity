# Mapping Abstraction Plan

**IMPORTANT:** Update this document whenever mapping-related changes are made. This is the single source of truth for the team. Check the status below before starting work to avoid duplication.

## Purpose

Decouple ShiftRepository from AutoMapper by introducing `IShiftEntityMapper<TEntity, TListDTO, TViewDTO>` as the single mapping abstraction. This allows framework consumers to choose their mapping strategy: AutoMapper (existing behavior), manual mapping, Mapperly (source-gen), or Mapster.

## Motivation

- **Implicit flattening risk**: AutoMapper silently maps `Vehicle.VIN` into `VehicleVIN` via naming convention, which can expose data unintentionally.
- **Runtime errors**: AutoMapper mapping issues surface at runtime, not compile-time.
- **Flexibility**: Different projects have different needs. A simple CRUD entity benefits from convention-based mapping; a complex entity with FK conventions (`ShiftEntitySelectDTO`), type converters (`ShiftFileDTO`), and custom fields may be better served by explicit mapping.

## What's Done

### ShiftEntity.Core

- [x] `IShiftEntityMapper<TEntity, TListDTO, TViewDTO>` interface defined with four methods:
  - `MapToView(TEntity entity)` — entity to view/upsert DTO
  - `MapToEntity(TViewDTO dto, TEntity existing)` — DTO to entity (merge into tracked entity)
  - `MapToList(IQueryable<TEntity> query)` — IQueryable projection for OData lists
  - `CopyEntity(TEntity source, TEntity target)` — entity-to-entity copy (used by ReloadAfterSave)
- [x] `IShiftEntityCopier<T>` removed — no longer needed.
- [x] `MappingHelpers.cs` — extension method helpers to reduce manual mapping boilerplate. All follow a consistent `source.ToTarget()` pattern:
  - `dto.MapBaseFields(entity)` — maps ID, IsDeleted, audit fields from entity to view/upsert DTO. Returns dto for fluent chaining with object initializer: `new ProductDTO { ... }.MapBaseFields(entity)`
  - `dto.MapBaseListFields(entity)` — maps ID, IsDeleted from entity to list DTO (in-memory only, not for IQueryable)
  - `source.CopyBaseFields(target)` — copies audit fields between entities, skips ReloadAfterSave
  - `source.ShallowCopyTo(target)` — reflection-based shallow copy of all settable properties, skips ID/ReloadAfterSave/AuditFieldsAreSet (cached per type, one-time cost)
  - `id.ToSelectDTO(text?)` — FK (`long` or `long?`) to ShiftEntitySelectDTO. Returns null for nullable when FK is null
  - `selectDTO.ToForeignKey()` / `selectDTO.ToNullableForeignKey()` — ShiftEntitySelectDTO to FK (`long` or `long?`)
  - `json.ToShiftFiles()` — deserializes JSON string to `List<ShiftFileDTO>?` (returns empty list for null/empty)
  - `files.ToJsonString()` — serializes `List<ShiftFileDTO>?` to JSON string (returns null for null)

### ShiftEntity.EFCore

- [x] `AutoMapperShiftEntityMapper<TEntity, TListDTO, TViewDTO>` — wraps `IMapper` as an `IShiftEntityMapper` implementation. Created automatically when AutoMapper is registered in DI.
- [x] `ShiftRepository` simplified:
  - Internally uses only `entityMapper` (single path, no fallback chain).
  - Constructor `ShiftRepository(DB db, ...)` auto-wraps `IMapper` into `AutoMapperShiftEntityMapper`.
  - Constructor `ShiftRepository(DB db, IShiftEntityMapper<...> mapper, ...)` accepts any custom mapper.
  - `mapper` property kept as `[Obsolete]` computed property for backwards compatibility.
- [x] `ReloadAfterSaveTrigger` removed — reload logic moved inline into `ProcessEntriesAndSave()`, after `db.SaveChangesAsync()`.
- [x] `RegisterShiftEntityEfCoreTriggers()` removed. Existing callers will get a compile error prompting removal.
- [x] `RegisterIShiftEntityCopier()` removed from DI registration.

### ShiftIdentity

- [x] `ResetUserTrigger` removed — logic moved inline into `UserRepository.UpsertAsync()`. Captures old email/phone before mutation, resets `EmailVerified`/`PhoneVerified` flags on change.
- [x] `ShiftIdentityDbContext.OnConfiguring` — removed `UseTriggers(x => x.AddTrigger<ResetUserTrigger>())`.
- [x] `SaveChangesWithoutTriggersAsync()` replaced with `SaveChangesAsync()` across `DBSeed.cs` and `IdentitySyncController.cs` (17 call sites).
- [x] Removed unused `EntityFrameworkCore.Triggered` imports.

### StockPlusPlus Sample App (ShiftTemplates)

- [x] `ProductMapper` (`StockPlusPlus.Data/Mappers/ProductMapper.cs`) — manual mapper for Product. Demonstrates:
  - `ShiftEntitySelectDTO` FK conventions via `ToSelectDTO()` / `ToForeignKey()` helpers
  - Audit field mapping via `MapBaseFieldsToView()`
  - Entity copy via `ShallowCopyTo()`
  - Navigation properties loaded via Includes (ProductBrand, CountryOfOrigin)
  - Nullable FK (CountryOfOriginID)
  - Enum property (TrackingMethod)
  - Separate List vs View DTO shapes (ProductListDTO, ProductDTO)
  - ReloadAfterSave (triggered by Includes)
- [x] `ProductCategoryMapper` (`StockPlusPlus.Data/Mappers/ProductCategoryMapper.cs`) — manual mapper for ProductCategory. Demonstrates:
  - `ShiftFileDTO` type conversion via `ToShiftFiles()` / `ToJsonString()` helpers (JSON string ↔ `List<ShiftFileDTO>`)
  - FK mapping via `ToSelectDTO()` / `ToNullableForeignKey()`
  - Nullable enum (TrackingMethod?)
- [x] `ProductRepository` and `ProductCategoryRepository` — two-constructor pattern: one accepting `IShiftEntityMapper` (DI picks this when registered), one falling back to AutoMapper.
- [x] `Program.cs` — DI registration lines for both Product and ProductCategory manual mappers (currently uncommented/active).
- [x] Mapping POC files at `StockPlusPlus.Test/Tests/MappingPOC/` — reference implementations comparing Manual, Mapperly, and Mapster (not production code).

### Backwards Compatibility

- **Zero breaking changes for existing consumers.** All repositories using `base(db)` continue to work via AutoMapper.
- Consumers only need changes if they want to opt into a different mapping strategy.

## Mapping POC Files (ShiftTemplates)

Located at `StockPlusPlus.Test/Tests/MappingPOC/`, these files are **not production code** — they are reference implementations comparing three alternatives:

| File | Approach | Key Characteristics |
|------|----------|-------------------|
| `ManualMappingPOC.cs` | Hand-written mappers | Full control, no magic, high boilerplate |
| `MapperlyPOC.cs` | Riok.Mapperly source generator | Compile-time codegen, zero reflection, compile-time diagnostics |
| `MapsterPOC.cs` | Mapster convention library | Similar API to AutoMapper, low migration effort, same flattening risk |

Each POC covers 9 test scenarios: simple mapping, FK conventions (`ShiftEntitySelectDTO`), IQueryable projection, reverse mapping, collections, custom fields, type converters, VehicleVIN flattening, and entity-to-entity copy.

## Future Iterations

### Next — Expand Manual Mapping Examples
- [ ] Add manual mapper for an entity with collection mapping (e.g., CompanyBranch with junction table services)
- [ ] Validate end-to-end with integration tests

### Evaluate Mapperly/Mapster Integration
- [ ] Based on POC findings, decide if Mapperly or Mapster deserve first-class `IShiftEntityMapper` adapters (like `AutoMapperShiftEntityMapper`)
- [ ] If yes, create adapter classes and update documentation

### Template Integration
- [ ] Add mapping strategy as a template parameter in `template.json` (e.g., `mappingStrategy`: AutoMapper | Manual | Mapperly)
- [ ] Update `shiftentity` item template to scaffold mapper alongside entity/DTO/repository
- [ ] Update documentation and migration guide

### AutoMapper Removal Path
- [ ] Once manual/alternative mappers are proven in production, evaluate making AutoMapper optional (not a default dependency)
- [ ] Provide migration guide for existing consumers

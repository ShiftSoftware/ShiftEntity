# Mapping Abstraction Plan

## Purpose

Decouple ShiftRepository from AutoMapper by introducing `IShiftEntityMapper<TEntity, TListDTO, TViewDTO>` as the single mapping abstraction. This allows framework consumers to choose their mapping strategy: AutoMapper (existing behavior), manual mapping, Mapperly (source-gen), or Mapster.

## Motivation

- **Implicit flattening risk**: AutoMapper silently maps `Vehicle.VIN` into `VehicleVIN` via naming convention, which can expose data unintentionally.
- **Runtime errors**: AutoMapper mapping issues surface at runtime, not compile-time.
- **Flexibility**: Different projects have different needs. A simple CRUD entity benefits from convention-based mapping; a complex entity with FK conventions (`ShiftEntitySelectDTO`), type converters (`ShiftFileDTO`), and custom fields may be better served by explicit mapping.

## What's Done (Iteration 1)

### ShiftEntity.Core

- `IShiftEntityMapper<TEntity, TListDTO, TViewDTO>` interface defined with four methods:
  - `MapToView(TEntity entity)` — entity to view/upsert DTO
  - `MapToEntity(TViewDTO dto, TEntity existing)` — DTO to entity (merge into tracked entity)
  - `MapToList(IQueryable<TEntity> query)` — IQueryable projection for OData lists
  - `CopyEntity(TEntity source, TEntity target)` — entity-to-entity copy (used by ReloadAfterSave)
- `IShiftEntityCopier<T>` removed — no longer needed.

### ShiftEntity.EFCore

- `AutoMapperShiftEntityMapper<TEntity, TListDTO, TViewDTO>` — wraps `IMapper` as an `IShiftEntityMapper` implementation. Created automatically when AutoMapper is registered in DI.
- `ShiftRepository` simplified:
  - Internally uses only `entityMapper` (single path, no fallback chain).
  - Constructor `ShiftRepository(DB db, ...)` auto-wraps `IMapper` into `AutoMapperShiftEntityMapper`.
  - Constructor `ShiftRepository(DB db, IShiftEntityMapper<...> mapper, ...)` accepts any custom mapper.
  - `mapper` property kept as `[Obsolete]` computed property for backwards compatibility.
- `ReloadAfterSaveTrigger` removed — reload logic moved inline into `ProcessEntriesAndSave()`, after `db.SaveChangesAsync()`.
- `RegisterShiftEntityEfCoreTriggers()` marked `[Obsolete]` (no-op). Existing callers get a compile warning but don't break.
- `RegisterIShiftEntityCopier()` removed from DI registration.

### StockPlusPlus Sample App (ShiftTemplates)

- `ProductMapper` (`StockPlusPlus.Data/Mappers/ProductMapper.cs`) — manual `IShiftEntityMapper` implementation for the `Product` entity, demonstrating hand-written mapping for a complex entity with:
  - `ShiftEntitySelectDTO` FK conventions (ProductCategory, ProductBrand, CountryOfOrigin)
  - Navigation properties loaded via Includes (ProductBrand, CountryOfOrigin)
  - Nullable FK (CountryOfOriginID)
  - Enum property (TrackingMethod)
  - Separate List vs View DTO shapes (ProductListDTO, ProductDTO)
  - ReloadAfterSave (triggered by Includes)
- `ProductRepository` — updated with two constructors: one accepting `IShiftEntityMapper` (DI picks this when a mapper is registered), one falling back to AutoMapper. Include options extracted to a shared static field to avoid duplication.
- `Program.cs` — commented-out DI registration line to swap Product to manual mapping. Uncomment to activate.

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

### Iteration 2 — Expand Manual Mapping Examples
- [x] Add manual mapper for Product (complex entity with FKs, Includes, separate List/View DTOs)
- [ ] Add manual mapper for an entity with collection mapping (e.g., CompanyBranch with junction table services)
- [ ] Add manual mapper for an entity with `ShiftFileDTO` type converter
- [ ] Validate end-to-end with integration tests

### Iteration 3 — Framework-Level Mapper Helpers
- [ ] Extract reusable helpers for common patterns (`ShiftEntitySelectDTO` FK convention, `ShiftFileDTO` serialization) so manual mappers don't repeat boilerplate
- [ ] Consider a base class or extension methods for mapping `ShiftEntity` audit fields (ID, CreateDate, LastSaveDate, etc.)

### Iteration 4 — Evaluate Mapperly/Mapster Integration
- [ ] Based on POC findings, decide if Mapperly or Mapster deserve first-class `IShiftEntityMapper` adapters (like `AutoMapperShiftEntityMapper`)
- [ ] If yes, create adapter classes and update documentation

### Iteration 5 — Template Integration
- [ ] Add mapping strategy as a template parameter in `template.json` (e.g., `mappingStrategy`: AutoMapper | Manual | Mapperly)
- [ ] Update `shiftentity` item template to scaffold mapper alongside entity/DTO/repository
- [ ] Update documentation and migration guide

### Iteration 6 — AutoMapper Removal Path
- [ ] Once manual/alternative mappers are proven in production, evaluate making AutoMapper optional (not a default dependency)
- [ ] Provide migration guide for existing consumers

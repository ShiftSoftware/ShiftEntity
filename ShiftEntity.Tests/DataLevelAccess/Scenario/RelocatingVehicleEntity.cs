using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Flags;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Flags;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;

/// <summary>
/// A vehicle whose upsert hook sets its company from the DTO before calling <c>Base()</c>, the way an entity derives
/// its scope from what was submitted (ShiftIdentity's User takes its company from the selected branch). The Write check
/// inside <c>Base()</c> then sees the new company, so only a check made before the hook runs sees the stored one.
/// </summary>
public class RelocatingVehicleEntity : ShiftEntity<RelocatingVehicleEntity>,
    IEntityHasCompany<RelocatingVehicleEntity>,
    IUpsertsShiftRepository<RelocatingVehicleEntity, VehicleListDTO, VehicleListDTO>
{
    public string Name { get; set; } = "";
    public long? CompanyID { get; set; }

    [NotMapped] public bool HookRan { get; private set; }

    public async ValueTask<RelocatingVehicleEntity> UpsertAsync(
        RelocatingVehicleEntity entity,
        VehicleListDTO dto,
        ActionTypes actionType,
        long? userId,
        Guid? idempotencyKey,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters,
        ShiftRepositoryUpsertContext<RelocatingVehicleEntity, VehicleListDTO, VehicleListDTO> context)
    {
        HookRan = true;
        entity.CompanyID = dto.CompanyID;
        return await context.Base();
    }
}

/// <summary>Maps the name only: the company is the hook's to set.</summary>
public sealed class UpsertRelocatingVehicleMapper : IShiftEntityMapper<RelocatingVehicleEntity, VehicleListDTO, VehicleListDTO>
{
    public RelocatingVehicleEntity MapToEntity(VehicleListDTO dto, RelocatingVehicleEntity existing, MappingContext context = default)
    {
        existing.Name = dto.Name;
        return existing;
    }

    public VehicleListDTO MapToView(RelocatingVehicleEntity entity, MappingContext context = default) => throw new NotSupportedException();
    public IQueryable<VehicleListDTO> MapToList(IQueryable<RelocatingVehicleEntity> query, MappingContext context = default) => throw new NotSupportedException();
    public void CopyEntity(RelocatingVehicleEntity source, RelocatingVehicleEntity target, MappingContext context = default) => throw new NotSupportedException();
}

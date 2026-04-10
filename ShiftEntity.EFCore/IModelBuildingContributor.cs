using Microsoft.EntityFrameworkCore;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// Implement this interface to contribute entity configurations to <see cref="ShiftDbContext.OnModelCreating"/>.
/// Register implementations in DI and they will be automatically invoked during model building.
/// </summary>
public interface IModelBuildingContributor
{
    void Configure(ModelBuilder modelBuilder);
}

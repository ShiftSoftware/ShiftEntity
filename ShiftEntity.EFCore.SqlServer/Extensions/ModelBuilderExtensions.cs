using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.EFCore.Extensions;

public static class ModelBuilderExtensions
{
    public static ModelBuilder ConfigureShiftEntity(this ModelBuilder modelBuilder, bool useTemporal)
    {
        if (useTemporal)
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var clrType = entityType.ClrType;

                var isTemporal = clrType.GetCustomAttributes(true).LastOrDefault(x => x as TemporalShiftEntity != null);

                if (isTemporal != null)
                {
                    //Make the tables temporal that has TemporalShiftEntyty attribute 
                    modelBuilder.Entity(entityType.ClrType).ToTable(b => b.IsTemporal());
                }
            }
        }

        ///// Disable Cascade Delete
        var cascadeFKs = modelBuilder.Model.GetEntityTypes()
                .SelectMany(t => t.GetForeignKeys())
                .Where(fk => !fk.IsOwnership && fk.DeleteBehavior == DeleteBehavior.Cascade);

        foreach (var fk in cascadeFKs)
            fk.DeleteBehavior = DeleteBehavior.Restrict;

        return modelBuilder;
    }

}

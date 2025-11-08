using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Flags;

namespace Microsoft.EntityFrameworkCore;

public static class ModelBuilderExtensions
{
    public static ModelBuilder ConfigureShiftEntity(this ModelBuilder modelBuilder, bool useTemporal)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (useTemporal)
            {
                var isTemporal = clrType.GetCustomAttributes(true).LastOrDefault(x => x as TemporalShiftEntity != null);

                if (isTemporal != null)
                {
                    //Make the tables temporal that has TemporalShiftEntyty attribute 
                    modelBuilder.Entity(entityType.ClrType).ToTable(b => b.IsTemporal());
                }
            }

            if (clrType.GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasIdempotencyKey<>))))
            {
                var idempotencyKeyName = nameof(IEntityHasIdempotencyKey.IdempotencyKey);

                modelBuilder.Entity(clrType).HasIndex(idempotencyKeyName).IsUnique().HasFilter($"{idempotencyKeyName} IS NOT NULL");
            }

            if (clrType.GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasUniqueHash<>))))
            {
                modelBuilder
                    .Entity(clrType)
                    .Property<byte[]>(IEntityHasUniqueHash.UniqueHash)
                    .HasColumnType("BINARY(32)");

                modelBuilder
                    .Entity(clrType)
                    .HasIndex(IEntityHasUniqueHash.UniqueHash)
                    .IsUnique()
                    .HasFilter($"{IEntityHasUniqueHash.UniqueHash} IS NOT NULL and IsDeleted = 0");
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

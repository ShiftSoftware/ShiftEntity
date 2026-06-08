using Microsoft.EntityFrameworkCore.Metadata;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.Core.Flags;
using ShiftSoftware.ShiftEntity.EFCore.Attention;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using ShiftSoftware.ShiftEntity.EFCore.Migrations;

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

        ConfigureAttention(modelBuilder);

        if (useTemporal)
            ConfigureTemporalHistoryIndexAnnotation(modelBuilder);

        ///// Disable Cascade Delete
        var cascadeFKs = modelBuilder.Model.GetEntityTypes()
                .SelectMany(t => t.GetForeignKeys())
                .Where(fk => !fk.IsOwnership && fk.DeleteBehavior == DeleteBehavior.Cascade);

        foreach (var fk in cascadeFKs)
            fk.DeleteBehavior = DeleteBehavior.Restrict;

        return modelBuilder;
    }

    private static void ConfigureAttention(ModelBuilder modelBuilder)
    {
        var hasIndexedAttention = false;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (!typeof(IHasAttention).IsAssignableFrom(clrType))
                continue;

            if (typeof(IHasIndexedAttention).IsAssignableFrom(clrType))
            {
                hasIndexedAttention = true;
            }
            else
            {
                modelBuilder.Entity(clrType)
                    .Property<string>(AttentionSignalJsonHelper.ShadowPropertyName);
            }
        }

        if (hasIndexedAttention)
        {
            modelBuilder.Entity<AttentionSignalEntry>(entity =>
            {
                entity.ToTable("AttentionSignals");
                entity.HasKey(e => e.ID);
                entity.Property(e => e.EntityType).HasMaxLength(256);
                entity.Property(e => e.Source).HasMaxLength(256);
                entity.Property(e => e.Category).HasMaxLength(256);
                entity.HasIndex(e => new { e.EntityType, e.EntityId, e.ClearedAt });
            });
        }
    }

    private static void ConfigureTemporalHistoryIndexAnnotation(ModelBuilder modelBuilder)
    {
        var entries = new List<string>();

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!entityType.IsTemporal())
                continue;

            var tableName = entityType.GetTableName();
            if (string.IsNullOrEmpty(tableName))
                continue;

            var historyTable = entityType.GetHistoryTableName();
            if (string.IsNullOrEmpty(historyTable))
                historyTable = tableName + "History";

            var historySchema = entityType.GetHistoryTableSchema()
                ?? entityType.GetSchema()
                ?? "dbo";

            var pk = entityType.FindPrimaryKey();
            if (pk is null)
                continue;

            var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
            var pkColumns = pk.Properties
                .Select(p => p.GetColumnName(storeObject) ?? p.GetColumnName())
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .ToArray();

            if (pkColumns.Length == 0)
                continue;

            entries.Add($"{historySchema}{ShiftEntityMigrationsModelDiffer.FieldSeparator}{historyTable}{ShiftEntityMigrationsModelDiffer.FieldSeparator}{string.Join(ShiftEntityMigrationsModelDiffer.ColumnSeparator, pkColumns)}");
        }

        if (entries.Count == 0)
            return;

        var value = string.Join(
            ShiftEntityMigrationsModelDiffer.EntrySeparator,
            entries.OrderBy(s => s, StringComparer.Ordinal));

        modelBuilder.HasAnnotation(ShiftEntityMigrationsModelDiffer.IndexedTablesAnnotation, value);
    }
}

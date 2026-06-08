using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace ShiftSoftware.ShiftEntity.EFCore.Migrations;

public class ShiftEntityMigrationsModelDiffer : MigrationsModelDiffer
{
    public const string IndexedTablesAnnotation = "ShiftEntity:HistoryIndexedTables";
    public const char EntrySeparator = ';';
    public const char FieldSeparator = '|';
    public const char ColumnSeparator = ',';

    public ShiftEntityMigrationsModelDiffer(
        IRelationalTypeMappingSource typeMappingSource,
        IMigrationsAnnotationProvider migrationsAnnotationProvider,
        IRelationalAnnotationProvider relationalAnnotationProvider,
        IRowIdentityMapFactory rowIdentityMapFactory,
        CommandBatchPreparerDependencies commandBatchPreparerDependencies)
        : base(typeMappingSource, migrationsAnnotationProvider, relationalAnnotationProvider, rowIdentityMapFactory, commandBatchPreparerDependencies)
    {
    }

    public override IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var operations = base.GetDifferences(source, target).ToList();

        var sourceTables = ParseAnnotation(source?.Model);
        var targetTables = ParseAnnotation(target?.Model);

        var sourceKeys = sourceTables.Keys.ToHashSet(StringComparer.Ordinal);
        var targetKeys = targetTables.Keys.ToHashSet(StringComparer.Ordinal);

        foreach (var key in targetKeys.Except(sourceKeys).OrderBy(s => s, StringComparer.Ordinal))
        {
            operations.Add(new SqlOperation { Sql = BuildCreateIndexSql(targetTables[key]) });
        }

        foreach (var key in sourceKeys.Except(targetKeys).OrderBy(s => s, StringComparer.Ordinal))
        {
            operations.Add(new SqlOperation { Sql = BuildDropIndexSql(sourceTables[key]) });
        }

        return operations;
    }

    private static Dictionary<string, HistoryTableInfo> ParseAnnotation(IReadOnlyModel? model)
    {
        var result = new Dictionary<string, HistoryTableInfo>(StringComparer.Ordinal);
        if (model is null)
            return result;

        var ann = model.FindAnnotation(IndexedTablesAnnotation);
        if (ann?.Value is not string value || string.IsNullOrEmpty(value))
            return result;

        foreach (var entry in value.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(FieldSeparator);
            if (parts.Length != 3) continue;

            var schema = parts[0];
            var table = parts[1];
            var columns = parts[2].Split(ColumnSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length == 0) continue;

            var key = $"{schema}.{table}";
            result[key] = new HistoryTableInfo(schema, table, columns);
        }

        return result;
    }

    private static string BuildCreateIndexSql(HistoryTableInfo table)
    {
        var indexName = $"IX_{table.Name}_{string.Join("_", table.PkColumns)}";
        var columnList = string.Join(", ", table.PkColumns.Select(c => $"[{c}]"));
        var qualifiedTable = $"[{table.Schema}].[{table.Name}]";

        string existenceCheck;
        if (table.PkColumns.Length == 1)
        {
            existenceCheck =
                $"NOT EXISTS (SELECT 1 FROM sys.indexes i " +
                $"INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
                $"INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                $"WHERE i.object_id = OBJECT_ID(N'{qualifiedTable}') AND ic.key_ordinal = 1 AND c.name = N'{table.PkColumns[0]}')";
        }
        else
        {
            existenceCheck =
                $"NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexName}' " +
                $"AND object_id = OBJECT_ID(N'{qualifiedTable}'))";
        }

        return $"IF {existenceCheck} CREATE NONCLUSTERED INDEX [{indexName}] ON {qualifiedTable} ({columnList});";
    }

    private static string BuildDropIndexSql(HistoryTableInfo table)
    {
        var indexName = $"IX_{table.Name}_{string.Join("_", table.PkColumns)}";
        var qualifiedTable = $"[{table.Schema}].[{table.Name}]";

        return $"IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexName}' AND object_id = OBJECT_ID(N'{qualifiedTable}')) DROP INDEX [{indexName}] ON {qualifiedTable};";
    }

    private sealed record HistoryTableInfo(string Schema, string Name, string[] PkColumns);
}

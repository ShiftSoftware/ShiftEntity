using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication;

/// <summary>
/// Write-side of the replication bookkeeping pair. Both <see cref="IShiftEntityReplication"/> columns are stamped
/// here and nowhere else, so the watermark and the stamp cannot drift apart: a successful sync records them
/// together, and a failed catch-up row has its watermark cleared here.
/// </summary>
public static class ShiftEntityReplicationExtensions
{
    /// <summary>
    /// Marks the currently-loaded version of a row as replicated: copies <see cref="IShiftEntityAudit.LastSaveDate"/>
    /// into <see cref="IShiftEntityReplication.LastReplicationDate"/> and records the Cosmos coordinates the row
    /// now lives under in <see cref="IShiftEntityReplication.LastReplicationStamp"/>. See the watermark property's
    /// remarks for why this must copy the loaded version's save date and never stamp the current time.
    /// </summary>
    /// <remarks>
    /// For rows the caller loaded itself, as the catch-up does, which then writes the two columns of all its rows by
    /// key in one set-based write. The save-time sync does not use it, because its entity is the saving caller's
    /// instance.
    /// </remarks>
    /// <param name="stamp">The serialized <see cref="LastReplicationStamp"/> of the document just upserted.</param>
    public static void MarkReplicated<TEntity>(this TEntity entity, string? stamp)
        where TEntity : class, IShiftEntityAudit, IShiftEntityReplication
    {
        entity.LastReplicationDate = entity.LastSaveDate;
        entity.LastReplicationStamp = stamp;
    }

    /// <summary>
    /// Marks a row that failed in a catch-up run as not replicated: clears
    /// <see cref="IShiftEntityReplication.LastReplicationDate"/>, so the next run selects the row and retries it.
    /// </summary>
    /// <remarks>
    /// A forced run (<c>updateAll</c>) also selects rows whose watermark says they are in sync. When such a row fails,
    /// it is not known what Cosmos DB holds for it, so the row must not keep that watermark: the next run selects only
    /// dirty rows and would never retry it. The stamp stays as it is. It still gives the coordinates of the document
    /// that the last successful sync wrote, so the next sync can remove that document if the row has moved.
    /// </remarks>
    internal static void MarkReplicationFailed<TEntity>(this TEntity entity)
        where TEntity : class, IShiftEntityReplication
    {
        entity.LastReplicationDate = null;
    }

    /// <summary>
    /// Records a sync of the row with key <paramref name="id"/>, and nothing else: one UPDATE of the two replication
    /// columns that is not a save, so it runs no host triggers, no audit backfill and no replication of its own, with
    /// query filters ignored. The watermark is <paramref name="replicatedVersion"/>, the
    /// <see cref="IShiftEntityAudit.LastSaveDate"/> of the version the sync replicated (as
    /// <see cref="MarkReplicated{TEntity}"/> copies it), or null for a row a catch-up run failed (see
    /// <see cref="MarkReplicationFailed{TEntity}"/>); <paramref name="stamp"/> is the serialized
    /// <see cref="LastReplicationStamp"/> of the document the sync wrote.
    /// </summary>
    /// <remarks>
    /// The save-time sync writes with it, from values it read from the entity, because the entity is the saving
    /// caller's instance: the caller's context still tracks it and may have linked new rows to it since. Setting the two
    /// columns on it would make the caller's next save write the row again and replicate it once more. Attaching it to
    /// another context would walk that graph and insert those rows a second time. A catch-up run writes all its rows
    /// with the set-based overload instead.
    /// </remarks>
    internal static Task<int> SaveReplicationBookkeepingAsync<TEntity>(this ShiftDbContext db, long id,
        DateTimeOffset? replicatedVersion, string? stamp, CancellationToken cancellationToken = default)
        where TEntity : ShiftEntity<TEntity>, IShiftEntityReplication
    {
        return db.Set<TEntity>().IgnoreQueryFilters()
            .Where(x => x.ID == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastReplicationDate, replicatedVersion)
                .SetProperty(x => x.LastReplicationStamp, stamp), cancellationToken);
    }

    /// <summary>
    /// Records the syncs of many rows, as a catch-up run does: each row's two replication columns get the values its
    /// entity holds now, by key, and nothing else changes. It is one UPDATE for many rows at a time, all in one
    /// transaction (the caller's, or one of its own): on SQL Server joined to the rows' values as JSON, a thousand rows
    /// a statement, or, on a database below compatibility level 130, which has no <c>OPENJSON</c>, to a list of values,
    /// a hundred rows a statement; on SQLite joined to a list of values, a hundred rows a statement. It is not a save,
    /// so no host trigger, no audit backfill, no replication and no marking of replication trees runs for it. Other
    /// providers get one UPDATE per row.
    /// </summary>
    internal static async Task SaveReplicationBookkeepingAsync<TEntity>(this ShiftDbContext db, IReadOnlyList<TEntity> rows,
        CancellationToken cancellationToken = default)
        where TEntity : ShiftEntity<TEntity>, IShiftEntityReplication
    {
        if (rows.Count == 0)
            return;

        var json = await OpenJsonAsync(db, cancellationToken);
        if (BookkeepingStatement.For(db, typeof(TEntity), json) is not { } statement)
        {
            foreach (var row in rows)
                await db.SaveReplicationBookkeepingAsync<TEntity>(row.ID, row.LastReplicationDate, row.LastReplicationStamp, cancellationToken);
            return;
        }

        async Task WriteAsync(CancellationToken ct)
        {
            var start = 0;
            while (start < rows.Count)
            {
                var count = Math.Min(statement.RowsPerStatement, rows.Count - start);
                try
                {
                    await statement.ExecuteAsync(db, rows, start, count, ct);
                }
                catch (SqlException ex) when (statement.Json && ex.Number is 102 or 208)
                {
                    //The database does not know OPENJSON after all (102: incorrect syntax, 208: invalid object name),
                    //for example after its level was lowered. A failed statement wrote nothing and leaves the
                    //transaction open, so the same rows go again as a list of values, and the database is remembered.
                    openJson[DatabaseKey(db)] = false;
                    statement = BookkeepingStatement.For(db, typeof(TEntity), withJson: false)!;
                    continue;
                }
                start += count;
            }
        }

        //Inside a transaction already, the rows are written in it. Otherwise in one of their own, which the context's
        //execution strategy retries as a whole when it retries at all: the write sets values by key, so a repeat is
        //harmless.
        if (db.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
        {
            await WriteAsync(cancellationToken);
            return;
        }

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await WriteAsync(ct);
            await transaction.CommitAsync(ct);
        }, cancellationToken);
    }

    private const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";

    //Whether each SQL Server database a process writes bookkeeping to knows OPENJSON, by server and database name.
    private static readonly ConcurrentDictionary<string, bool> openJson = new();

    private static string DatabaseKey(ShiftDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        return connection.DataSource + "|" + connection.Database;
    }

    //OPENJSON needs compatibility level 130 or later, and a database restored or upgraded from an older version keeps
    //its older level. The level is read once per database, before the write's transaction begins.
    private static async Task<bool> OpenJsonAsync(ShiftDbContext db, CancellationToken cancellationToken)
    {
        if (db.Database.ProviderName != SqlServerProvider)
            return false;

        var key = DatabaseKey(db);
        if (openJson.TryGetValue(key, out var known))
            return known;

        var level = await db.Database
            .SqlQueryRaw<int>("SELECT CONVERT(int, [compatibility_level]) AS [Value] FROM [sys].[databases] WHERE [database_id] = DB_ID()")
            .SingleAsync(cancellationToken);
        return openJson[key] = level >= 130;
    }

    /// <summary>
    /// The set-based bookkeeping UPDATE for one table. On SQL Server the rows come in as one JSON parameter: three
    /// parameters a row cost more to bind than the statements they save. On SQLite, on a SQL Server database without
    /// <c>OPENJSON</c>, and for columns with a value converter, they come in as parameters through the properties' own
    /// type mappings, so they are written exactly as EF writes them.
    /// </summary>
    private sealed class BookkeepingStatement
    {
        private readonly IProperty id, date, stamp;
        private readonly ISqlGenerationHelper helper;
        private readonly bool json;
        private readonly Func<int, string> sql;

        public bool Json => json;

        public int RowsPerStatement => json ? 1000 : 100;

        private BookkeepingStatement(IProperty id, IProperty date, IProperty stamp, ISqlGenerationHelper helper, bool json, Func<int, string> sql)
        {
            this.id = id;
            this.date = date;
            this.stamp = stamp;
            this.helper = helper;
            this.json = json;
            this.sql = sql;
        }

        //withJson says whether the database knows OPENJSON, and is read for SQL Server only.
        public static BookkeepingStatement? For(ShiftDbContext db, Type entityType, bool withJson)
        {
            var type = db.Model.FindEntityType(entityType);
            if (type?.GetTableName() is not { } table)
                return null;

            var store = StoreObjectIdentifier.Table(table, type.GetSchema());
            var id = type.FindPrimaryKey()!.Properties[0];
            var date = type.FindProperty(nameof(IShiftEntityReplication.LastReplicationDate))!;
            var stamp = type.FindProperty(nameof(IShiftEntityReplication.LastReplicationStamp))!;
            if (id.GetColumnName(store) is not { } idColumn || date.GetColumnName(store) is not { } dateColumn ||
                stamp.GetColumnName(store) is not { } stampColumn)
                return null;

            var helper = db.GetService<ISqlGenerationHelper>();
            var target = helper.DelimitIdentifier(table, type.GetSchema());
            string Name(string column) => helper.DelimitIdentifier(column);
            string Values(int count) => string.Join(", ", Enumerable.Range(0, count).Select(i =>
                $"({helper.GenerateParameterNamePlaceholder($"p{3 * i}")}, {helper.GenerateParameterNamePlaceholder($"p{3 * i + 1}")}, " +
                $"{helper.GenerateParameterNamePlaceholder($"p{3 * i + 2}")})"));

            var converted = new[] { id, date, stamp }.Any(x => x.GetTypeMapping().Converter is not null);

            switch (db.Database.ProviderName)
            {
                case SqlServerProvider when withJson && !converted:
                    var rows = helper.GenerateParameterNamePlaceholder("rows");
                    return new BookkeepingStatement(id, date, stamp, helper, json: true, _ =>
                        $"UPDATE {Name("t")} SET {Name("t")}.{Name(dateColumn)} = {Name("v")}.{Name("Date")}, " +
                        $"{Name("t")}.{Name(stampColumn)} = {Name("v")}.{Name("Stamp")} FROM {target} AS {Name("t")} " +
                        $"INNER JOIN OPENJSON({rows}) WITH ({Name("ID")} {id.GetColumnType(store)} '$.i', " +
                        $"{Name("Date")} {date.GetColumnType(store)} '$.d', {Name("Stamp")} {stamp.GetColumnType(store)} '$.s') AS {Name("v")} " +
                        $"ON {Name("t")}.{Name(idColumn)} = {Name("v")}.{Name("ID")}");

                case SqlServerProvider:
                    return new BookkeepingStatement(id, date, stamp, helper, json: false, count =>
                        $"UPDATE {Name("t")} SET {Name("t")}.{Name(dateColumn)} = {Name("v")}.{Name("Date")}, " +
                        $"{Name("t")}.{Name(stampColumn)} = {Name("v")}.{Name("Stamp")} FROM {target} AS {Name("t")} " +
                        $"INNER JOIN (VALUES {Values(count)}) AS {Name("v")} ({Name("ID")}, {Name("Date")}, {Name("Stamp")}) " +
                        $"ON {Name("t")}.{Name(idColumn)} = {Name("v")}.{Name("ID")}");

                //SQLite names the columns of a VALUES list column1, column2 and so on.
                case "Microsoft.EntityFrameworkCore.Sqlite":
                    return new BookkeepingStatement(id, date, stamp, helper, json: false, count =>
                        $"UPDATE {target} SET {Name(dateColumn)} = {Name("v")}.{Name("column2")}, {Name(stampColumn)} = {Name("v")}.{Name("column3")} " +
                        $"FROM (VALUES {Values(count)}) AS {Name("v")} WHERE {target}.{Name(idColumn)} = {Name("v")}.{Name("column1")}");

                default:
                    return null;
            }
        }

        public async Task ExecuteAsync<TEntity>(ShiftDbContext db, IReadOnlyList<TEntity> rows, int start, int count,
            CancellationToken cancellationToken)
            where TEntity : ShiftEntity<TEntity>, IShiftEntityReplication
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            var parameters = new List<object>(json ? 1 : 3 * count);

            if (json)
            {
                var buffer = new System.Buffers.ArrayBufferWriter<byte>();
                using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
                {
                    writer.WriteStartArray();
                    for (var i = start; i < start + count; i++)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("i", rows[i].ID);
                        if (rows[i].LastReplicationDate is { } replicated)
                            writer.WriteString("d", replicated);
                        else
                            writer.WriteNull("d");
                        writer.WriteString("s", rows[i].LastReplicationStamp);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }

                var parameter = command.CreateParameter();
                parameter.ParameterName = helper.GenerateParameterName("rows");
                parameter.DbType = System.Data.DbType.String;
                parameter.Size = -1;
                parameter.Value = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
                parameters.Add(parameter);
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var row = rows[start + i];
                    parameters.Add(id.GetRelationalTypeMapping().CreateParameter(command, helper.GenerateParameterName($"p{3 * i}"), row.ID, nullable: false));
                    parameters.Add(date.GetRelationalTypeMapping().CreateParameter(command, helper.GenerateParameterName($"p{3 * i + 1}"),
                        row.LastReplicationDate, nullable: true));
                    parameters.Add(stamp.GetRelationalTypeMapping().CreateParameter(command, helper.GenerateParameterName($"p{3 * i + 2}"),
                        row.LastReplicationStamp, nullable: true));
                }
            }

            await db.Database.ExecuteSqlRawAsync(sql(count), parameters, cancellationToken);
        }
    }
}

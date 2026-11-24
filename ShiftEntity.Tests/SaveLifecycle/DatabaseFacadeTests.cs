using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;
using static ShiftSoftware.ShiftEntity.Tests.SaveLifecycle.ScriptedHandlers;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// The <c>Database</c> of a <see cref="ShiftDbContext"/> is EF's facade to whoever uses it: the same calls, the same
/// settings, and EF's own ADO.NET transaction behind the one it hands out. These pin what code that works on the
/// connection directly relies on.
/// </summary>
public class DatabaseFacadeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Database_is_one_facade_whichever_type_the_context_is_read_through()
    {
        await using var host = SavePipelineHost.Start();

        await host.InScopeAsync(db =>
        {
            ShiftDbContext shift = db;
            DbContext plain = db;

            Assert.IsType<ShiftDatabaseFacade>(db.Database);
            Assert.Same(db.Database, shift.Database);
            Assert.Same(db.Database, plain.Database);
            Assert.Null(db.Database.CurrentTransaction);
            Assert.True(db.Database.IsSqlite());
            Assert.True(db.Database.CanConnect());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_disposed_context_refuses_its_Database_as_EF_does()
    {
        await using var host = SavePipelineHost.Start();

        var disposed = await host.InScopeAsync(db => Task.FromResult(db));

        Assert.Throws<ObjectDisposedException>(() => disposed.Database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetDbTransaction_gives_EFs_own_ADO_NET_transaction_so_a_command_on_the_connection_joins_it(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "GetDbTransaction");

        async Task InsertDirectlyAsync(Db db, string name)
        {
            var transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            Assert.Same(db.Database.GetDbConnection(), transaction.Connection);

            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO Memos (Name, CreateDate, LastSaveDate, IsDeleted) VALUES (@name, @now, @now, 0)";
            command.Parameters.Add(sqlServer ? new SqlParameter("@name", name) : new SqliteParameter("@name", name));
            command.Parameters.Add(sqlServer ? new SqlParameter("@now", DateTimeOffset.UtcNow) : new SqliteParameter("@now", DateTimeOffset.UtcNow.ToString("O")));
            await command.ExecuteNonQueryAsync(Ct);
        }

        await host.InScopeAsync(async db =>
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                await InsertDirectlyAsync(db, "rolled back");
                await transaction.RollbackAsync(Ct);
            }

            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                await InsertDirectlyAsync(db, "committed");
                await transaction.CommitAsync(Ct);
            }
        });

        Assert.Equal(["committed"], await host.InScopeAsync(db => db.Memos.Select(x => x.Name).ToListAsync(Ct)));
    }

    [Fact]
    public async Task A_bulk_copy_inside_a_followed_transaction_is_rolled_back_and_committed_with_it()
    {
        // What a bulk library does: it takes the connection and the ADO.NET transaction from the context, and writes
        // around EF's save. Its rows bypass the lifecycle, as they bypass EF, and commit with the transaction.
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("BulkCopy", services => services.AddScript(new Script<Memo>
        {
            AfterCommit = Nothing,
        }));

        async Task BulkInsertAsync(Db db, params string[] names)
        {
            var table = new DataTable();
            table.Columns.Add("Name", typeof(string));
            table.Columns.Add("CreateDate", typeof(DateTimeOffset));
            table.Columns.Add("LastSaveDate", typeof(DateTimeOffset));
            table.Columns.Add("IsDeleted", typeof(bool));

            foreach (var name in names)
                table.Rows.Add(name, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

            using var bulk = new SqlBulkCopy((SqlConnection)db.Database.GetDbConnection(), SqlBulkCopyOptions.Default,
                (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction()) { DestinationTableName = "Memos" };

            foreach (DataColumn column in table.Columns)
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);

            await bulk.WriteToServerAsync(table, Ct);
        }

        await host.InScopeAsync(async db =>
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                await BulkInsertAsync(db, "a", "b");
                await transaction.RollbackAsync(Ct);
            }

            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                await BulkInsertAsync(db, "c", "d");

                // Work can be attached to what the bulk copy wrote.
                db.AfterCommit(() => host.Log.Add("bulk rows committed"));
                await transaction.CommitAsync(Ct);
            }
        });

        Assert.Equal(["c", "d"], await host.InScopeAsync(db => db.Memos.OrderBy(x => x.Name).Select(x => x.Name).ToListAsync(Ct)));
        Assert.Equal(["bulk rows committed"], host.Log.Lines);
    }

    [Fact]
    public async Task ExecuteUpdate_and_raw_SQL_have_no_rows_so_no_handler_runs_and_they_commit_with_the_transaction()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            AfterSave = Nothing,
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a", Status = "New" });
            await db.SaveChangesAsync(Ct);
            host.Log.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await db.Orders.ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "Updated"), Ct);
            await db.Database.ExecuteSqlRawAsync("UPDATE Orders SET Name = 'renamed'", Ct);
            await db.Orders.Where(x => x.Name == "nobody").ExecuteDeleteAsync(Ct);
            await transaction.CommitAsync(Ct);
        });

        Assert.Empty(host.Log.Lines);
        Assert.Equal(("renamed", "Updated"), await host.InScopeAsync(async db =>
        {
            var order = await db.Orders.SingleAsync(Ct);
            return (order.Name, order.Status);
        }));
    }

    // ── The two settings ────────────────────────────────────────────────────────

    [Fact]
    public async Task AutoSavepointsEnabled_and_AutoTransactionBehavior_are_EFs_settings_read_and_written_through_Database()
    {
        await using var host = SavePipelineHost.Start();

        await host.InScopeAsync(async db =>
        {
            DbContext plain = db;

            Assert.True(db.Database.AutoSavepointsEnabled);
            Assert.Equal(AutoTransactionBehavior.WhenNeeded, db.Database.AutoTransactionBehavior);

            db.Database.AutoSavepointsEnabled = false;
            db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;

            Assert.False(plain.Database.AutoSavepointsEnabled);
            Assert.Equal(AutoTransactionBehavior.Always, plain.Database.AutoTransactionBehavior);

            // EF acts on them: a one-row save gets a transaction, and a save inside a transaction gets no savepoint.
            db.Add(new Memo { Name = "a" });
            await db.SaveChangesAsync(Ct);
            Assert.Equal(["BEGIN", "INSERT Memos", "COMMIT"], host.Sql.Writes);

            host.Sql.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Add(new Memo { Name = "b" });
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
            Assert.Equal(["BEGIN", "INSERT Memos", "COMMIT"], host.Sql.Writes);
        });
    }

    public sealed class PooledDb : ShiftDbContext
    {
        public PooledDb(DbContextOptions<PooledDb> options) : base(options)
        {
            // What a pool snapshots once, and gives back to every lease.
            Database.AutoSavepointsEnabled = false;
            Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        }

        public DbSet<Memo> Memos => Set<Memo>();
    }

    [Fact]
    public async Task A_pooled_context_keeps_the_two_settings_of_its_constructor_on_every_lease_and_forgets_the_last_leases_transaction()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContextPool<PooledDb>(options => options.UseSqlite(connection), poolSize: 1);
        await using var provider = services.BuildServiceProvider();

        PooledDb first;

        await using (var scope = provider.CreateAsyncScope())
        {
            first = scope.ServiceProvider.GetRequiredService<PooledDb>();
            await first.Database.EnsureCreatedAsync(Ct);

            Assert.False(first.Database.AutoSavepointsEnabled);
            Assert.Equal(AutoTransactionBehavior.Always, first.Database.AutoTransactionBehavior);

            // The lease changes both, and goes back to the pool with a transaction still open.
            first.Database.AutoSavepointsEnabled = true;
            first.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
            await first.Database.BeginTransactionAsync(Ct);
            first.Memos.Add(new Memo { Name = "left in an open transaction" });
            await first.SaveChangesAsync(Ct);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var second = scope.ServiceProvider.GetRequiredService<PooledDb>();

            Assert.Same(first, second);
            Assert.True(second.ContextId.Lease > 1);
            Assert.False(second.Database.AutoSavepointsEnabled);
            Assert.Equal(AutoTransactionBehavior.Always, second.Database.AutoTransactionBehavior);

            Assert.Null(second.Database.CurrentTransaction);
            Assert.Equal(0, await second.Memos.CountAsync(Ct));

            await using var transaction = await second.Database.BeginTransactionAsync(Ct);
            second.Memos.Add(new Memo { Name = "of the second lease" });
            await second.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        await using (var scope = provider.CreateAsyncScope())
            Assert.Equal(["of the second lease"], await scope.ServiceProvider.GetRequiredService<PooledDb>().Memos.Select(x => x.Name).ToListAsync(Ct));
    }

    // ── EF's own callers of the facade ──────────────────────────────────────────

    [Fact]
    public async Task MigrateAsync_begins_its_transaction_through_Database_so_what_its_seeder_saves_waits_for_that_commit()
    {
        var seen = new List<string>();
        var seeding = false;

        await using var host = SavePipelineHost.StartOnFile(
            services => services.AddScript(new Script<Memo>
            {
                AfterSave = (rows, _) =>
                {
                    seen.Add($"after save, transaction open: {rows.Db.Database.CurrentTransaction is not null}");
                    return Task.CompletedTask;
                },
                AfterCommit = (rows, _) =>
                {
                    seen.Add($"after commit, transaction open: {rows.Db.Database.CurrentTransaction is not null}");
                    return Task.CompletedTask;
                },
            }),
            context => context
                .UseSeeding((_, _) => { })      // the host creates its tables synchronously, and seeds nothing then
                .UseAsyncSeeding(async (context, _, ct) =>
                {
                    if (!seeding)
                        return;

                    context.Add(new Memo { Name = "seeded" });
                    await context.SaveChangesAsync(ct);
                    seen.Add("the seeder returned");
                })
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));

        seeding = true;
        await host.InScopeAsync(db => db.Database.MigrateAsync(Ct));

        Assert.Equal(
            ["after save, transaction open: True", "the seeder returned", "after commit, transaction open: False"],
            seen);
        Assert.Equal(1, await host.InScopeAsync(db => db.Memos.CountAsync(Ct)));
    }

    [Fact]
    public async Task EFs_ExecuteInTransaction_helper_begins_through_Database_so_its_transaction_is_followed()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order> { AfterCommit = Nothing }));

        await host.InScopeAsync(async db =>
        {
            await db.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(
                async ct =>
                {
                    db.Add(new Order { Name = "a" });
                    await db.SaveChangesAsync(ct);
                    host.Log.Add("the operation returned");
                },
                _ => Task.FromResult(true),
                Ct);
        });

        Assert.Equal(["the operation returned", "after commit of Order: Added Order 'a'"], host.Log.Lines);
    }
}

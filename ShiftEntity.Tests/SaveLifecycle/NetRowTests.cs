using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// Before commit and after commit get one row per record for everything that commits together, however many saves
/// wrote the record. A record is matched by its entity type and key, not by the instance. The row's record is the
/// latest instance, and its <c>Original</c> is what the record held before the first of those saves.
/// </summary>
public class NetRowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [NeedsOriginal]
    public sealed class WritesNetRows(SaveLog log) : IBeforeCommitHandler<Order>, IAfterCommitHandler<Order>
    {
        public List<Order> Records { get; } = [];

        public ValueTask BeforeCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add("before commit: " + Describe(rows));
            return ValueTask.CompletedTask;
        }

        public ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add("after commit: " + Describe(rows));
            log.Add("soft deletes: " + string.Join(", ", rows.Select(row => row.IsSoftDelete)));
            Records.AddRange(rows.Select(row => row.Entity));
            return ValueTask.CompletedTask;
        }

        private static string Describe(SavedRows<Order> rows) => string.Join("; ", rows.Select(row =>
            $"{row.Change} '{row.Original?.Name}'/'{row.Original?.Status}' -> '{row.Entity.Name}'/'{row.Entity.Status}'"));
    }

    private static SavePipelineHost Host() => SavePipelineHost.Start(services => services.AddSaveHandler<WritesNetRows>());

    // One order, saved before the test's transaction and without its handler.
    private static async Task<long> SeedAsync(SavePipelineHost host)
    {
        await using var db = host.Bare();
        var order = new Order { Name = "a", Status = "New" };
        db.Add(order);
        await db.SaveChangesAsync(Ct);
        return order.ID;
    }

    private static async Task InOneTransactionAsync(SavePipelineHost host, Func<Db, Task> saves)
    {
        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await saves(db);
            await transaction.CommitAsync(Ct);
        });
    }

    private static List<string> Rows(SavePipelineHost host) => host.Log.Lines.Where(line => !line.StartsWith("soft deletes")).ToList();

    [Fact]
    public async Task Added_and_then_modified_is_one_added_row_with_the_records_last_values()
    {
        await using var host = Host();

        await InOneTransactionAsync(host, async db =>
        {
            var order = new Order { Name = "a", Status = "First" };
            db.Add(order);
            await db.SaveChangesAsync(Ct);

            order.Status = "Second";
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["before commit: Added ''/'' -> 'a'/'Second'", "after commit: Added ''/'' -> 'a'/'Second'"], Rows(host));
    }

    [Fact]
    public async Task Added_and_then_deleted_is_no_row_so_the_handler_is_not_called()
    {
        await using var host = Host();

        await InOneTransactionAsync(host, async db =>
        {
            var order = new Order { Name = "a" };
            db.Add(order);
            await db.SaveChangesAsync(Ct);

            db.Remove(order);
            await db.SaveChangesAsync(Ct);
        });

        Assert.Empty(host.Log.Lines);
    }

    [Fact]
    public async Task Modified_and_then_modified_is_one_modified_row_with_the_first_saves_old_values()
    {
        await using var host = Host();
        var id = await SeedAsync(host);

        await InOneTransactionAsync(host, async db =>
        {
            var order = await db.Orders.SingleAsync(x => x.ID == id, Ct);

            order.Status = "Second";
            await db.SaveChangesAsync(Ct);

            order.Status = "Third";
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["before commit: Modified 'a'/'New' -> 'a'/'Third'", "after commit: Modified 'a'/'New' -> 'a'/'Third'"], Rows(host));
    }

    [Fact]
    public async Task Modified_and_then_deleted_is_one_deleted_row_with_the_first_saves_old_values()
    {
        await using var host = Host();
        var id = await SeedAsync(host);

        await InOneTransactionAsync(host, async db =>
        {
            var order = await db.Orders.SingleAsync(x => x.ID == id, Ct);

            order.Status = "Second";
            await db.SaveChangesAsync(Ct);

            db.Remove(order);
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["before commit: Deleted 'a'/'New' -> 'a'/'Second'", "after commit: Deleted 'a'/'New' -> 'a'/'Second'"], Rows(host));
    }

    [Fact]
    public async Task Deleted_and_then_added_again_with_the_same_key_is_one_modified_row_whose_record_is_the_new_instance()
    {
        await using var host = Host();
        var id = await SeedAsync(host);
        Order? again = null;
        WritesNetRows? handler = null;

        await host.InScopeAsync(async db =>
        {
            handler = db.GetService<WritesNetRows>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Remove(await db.Orders.SingleAsync(x => x.ID == id, Ct));
            await db.SaveChangesAsync(Ct);

            again = new Order { ID = id, Name = "b", Status = "Again" };
            db.Add(again);
            await db.SaveChangesAsync(Ct);

            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(["before commit: Modified 'a'/'New' -> 'b'/'Again'", "after commit: Modified 'a'/'New' -> 'b'/'Again'"], Rows(host));
        Assert.Same(again, Assert.Single(handler!.Records));
    }

    [Fact]
    public async Task A_record_saved_then_loaded_again_as_another_instance_and_saved_is_one_row()
    {
        // As an import that clears the tracker between its saves does. The keys match, the instances don't.
        await using var host = Host();
        var id = await SeedAsync(host);
        Order? first = null, second = null;
        WritesNetRows? handler = null;

        await host.InScopeAsync(async db =>
        {
            handler = db.GetService<WritesNetRows>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            first = await db.Orders.SingleAsync(x => x.ID == id, Ct);
            first.Status = "Second";
            await db.SaveChangesAsync(Ct);

            db.ChangeTracker.Clear();

            second = await db.Orders.SingleAsync(x => x.ID == id, Ct);
            second.Name = "renamed";
            await db.SaveChangesAsync(Ct);

            await transaction.CommitAsync(Ct);
        });

        Assert.NotSame(first, second);
        Assert.Equal(["before commit: Modified 'a'/'New' -> 'renamed'/'Second'", "after commit: Modified 'a'/'New' -> 'renamed'/'Second'"], Rows(host));
        Assert.Same(second, Assert.Single(handler!.Records));
    }

    [Fact]
    public async Task Each_record_has_a_row_of_its_own_in_the_order_the_transaction_first_saved_it()
    {
        await using var host = Host();
        var id = await SeedAsync(host);

        await InOneTransactionAsync(host, async db =>
        {
            var seeded = await db.Orders.SingleAsync(x => x.ID == id, Ct);
            var added = new Order { Name = "b", Status = "New" };

            db.Add(added);
            await db.SaveChangesAsync(Ct);

            seeded.Status = "Second";
            added.Status = "Second";
            await db.SaveChangesAsync(Ct);

            db.Add(new Order { Name = "c", Status = "New" });
            seeded.Status = "Third";
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
            "after commit: Added ''/'' -> 'b'/'Second'; Modified 'a'/'New' -> 'a'/'Third'; Added ''/'' -> 'c'/'New'",
            Rows(host)[^1]);
    }

    [Theory]
    [InlineData(false, "True")]
    [InlineData(true, "False")]
    public async Task A_soft_delete_in_one_save_of_the_transaction_is_a_soft_delete_of_the_row_unless_a_later_save_restores_it(bool restore, string softDelete)
    {
        await using var host = Host();
        var id = await SeedAsync(host);

        await InOneTransactionAsync(host, async db =>
        {
            var order = await db.Orders.SingleAsync(x => x.ID == id, Ct);

            order.IsDeleted = true;
            await db.SaveChangesAsync(Ct);

            order.Status = "Second";
            order.IsDeleted = !restore;
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal($"soft deletes: {softDelete}", host.Log.Lines[^1]);
    }

    [Fact]
    public async Task The_saves_of_one_transaction_share_one_bag_and_the_next_transaction_starts_with_an_empty_one()
    {
        var bags = new List<string>();

        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            AfterSave = (rows, _) =>
            {
                rows.Items["saves"] = rows.Items.TryGetValue("saves", out var saves) ? (int)saves! + 1 : 1;
                return Task.CompletedTask;
            },
            BeforeCommit = (rows, _) =>
            {
                bags.Add($"before commit: {rows.Items["saves"]} save(s)");
                return Task.CompletedTask;
            },
            AfterCommit = (rows, _) =>
            {
                bags.Add($"after commit: {rows.Items["saves"]} save(s)");
                return Task.CompletedTask;
            },
        }));

        await host.InScopeAsync(async db =>
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                foreach (var name in new[] { "a", "b", "c" })
                {
                    db.Add(new Order { Name = name });
                    await db.SaveChangesAsync(Ct);
                }

                await transaction.CommitAsync(Ct);
            }

            db.Add(new Order { Name = "d" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["before commit: 3 save(s)", "after commit: 3 save(s)", "before commit: 1 save(s)", "after commit: 1 save(s)"], bags);
    }

    [Fact]
    public async Task The_bag_is_put_back_exactly_and_a_unit_that_never_uses_it_copies_nothing()
    {
        await using var host = SavePipelineHost.Start();

        await host.InScopeAsync(db =>
        {
            var unit = new SaveUnit(db);
            var bag = typeof(SaveUnit).GetField("items", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

            // Never used: no copy at a point, nothing to put back, and the bag is never made.
            Assert.Null(unit.SnapshotItems());
            unit.RestoreItems(null);
            Assert.Null(bag.GetValue(unit));

            // Used: the keys added since the point go, and the values replaced or removed since come back.
            unit.Items["kept"] = 1;
            unit.Items["replaced"] = "before";
            unit.Items["removed"] = true;

            var point = unit.SnapshotItems();

            unit.Items["added"] = 2;
            unit.Items["replaced"] = "after";
            unit.Items.Remove("removed");
            unit.Items["kept"] = 1;

            unit.RestoreItems(point);

            Assert.Equal(["kept=1", "removed=True", "replaced=before"], unit.Items.Select(item => $"{item.Key}={item.Value}").Order());

            // A point taken while the bag was empty empties it.
            unit.RestoreItems(null);
            Assert.Empty(unit.Items);

            // The copy is shallow: an object changed in place stays changed.
            var list = new List<int> { 1 };
            unit.Items["list"] = list;
            var shallow = unit.SnapshotItems();
            list.Add(2);
            unit.RestoreItems(shallow);
            Assert.Equal([1, 2], (List<int>)unit.Items["list"]!);

            return Task.CompletedTask;
        });
    }
}

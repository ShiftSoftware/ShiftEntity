using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using NSubstitute;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.DataGrid;

/// <summary>
/// The rows an action button selected, loaded for an endpoint that changes them: at most
/// <see cref="GeneralActionTree.DataGridBulkWriteMaxTop"/> of them, counted by the database. The loads that aren't for a
/// bulk write have no limit. SQLite runs the real SQL, so a refusal can be shown to have loaded nothing.
/// </summary>
public sealed class BulkWriteSelectionTests : IAsyncLifetime
{
    private const int Limit = 3;

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly Commands commands = new();
    private Store db = default!;

    public async ValueTask InitializeAsync()
    {
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        db = new Store(new DbContextOptionsBuilder<Store>().UseSqlite(connection).AddInterceptors(commands).Options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        // Rows 1-6: four labelled "Open", two labelled "Closed". Row 7 is deleted, and row 8 is out of the user's reach.
        db.Rows.AddRange(
            new Row { ID = 1, Label = "Open" }, new Row { ID = 2, Label = "Open" }, new Row { ID = 3, Label = "Open" },
            new Row { ID = 4, Label = "Open" }, new Row { ID = 5, Label = "Closed" }, new Row { ID = 6, Label = "Closed" },
            new Row { ID = 7, Label = "Closed", Deleted = true }, new Row { ID = 8, Label = "Closed", OutOfReach = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        commands.Sql.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task A_selection_at_the_limit_is_loaded()
    {
        var rows = await ForBulkWrite(Limit, Selected(1, 2, 3));

        Assert.Equal([1, 2, 3], rows.Select(x => x.ID).Order());
    }

    [Fact]
    public async Task A_selection_over_the_limit_is_refused_and_nothing_is_loaded()
    {
        var refusal = await Refused(Limit, Selected(1, 2, 3, 4));

        AssertRefusal(refusal, rows: 4, Limit);
    }

    [Fact]
    public async Task All_rows_are_refused_over_the_limit_and_loaded_at_it()
    {
        AssertRefusal(await Refused(5, new() { All = true, Total = 6 }), rows: 6, limit: 5);

        Assert.Equal(6, (await ForBulkWrite(6, new() { All = true, Total = 6 })).Count);
    }

    [Fact]
    public async Task All_rows_of_a_filter_are_refused_over_the_limit_and_loaded_at_it()
    {
        AssertRefusal(await Refused(Limit, new() { All = true, Total = 4, Filter = "Label eq 'Open'" }), rows: 4, Limit);

        var rows = await ForBulkWrite(4, new() { All = true, Total = 4, Filter = "Label eq 'Open'" });
        Assert.Equal([1, 2, 3, 4], rows.Select(x => x.ID).Order());
    }

    // With all rows selected, SelectStateDTO.Count is the Total that the client sent.
    [Theory]
    [InlineData(null)]
    [InlineData("Label eq 'Open'")]
    public async Task The_clients_own_total_is_not_trusted(string? filter)
    {
        var understated = new SelectStateDTO<WidgetListDTO> { All = true, Total = 1, Filter = filter };
        Assert.Equal(1, understated.Count);
        AssertRefusal(await Refused(Limit, understated), rows: filter is null ? 6 : 4, Limit);

        var overstated = new SelectStateDTO<WidgetListDTO> { All = true, Total = 1_000, Filter = "Label eq 'Closed'" };
        Assert.Equal(2, (await ForBulkWrite(Limit, overstated)).Count);
    }

    [Fact]
    public async Task Rows_that_would_not_be_loaded_do_not_count()
    {
        // Deleted, out of reach, unknown and selected twice.
        var rows = await ForBulkWrite(Limit, Selected(5, 6, 7, 8, 99, 5, 6));

        Assert.Equal([5, 6], rows.Select(x => x.ID).Order());
    }

    [Fact]
    public async Task A_user_without_the_grant_can_write_5_rows()
    {
        Assert.Equal(5, (await ForBulkWrite(null, Selected(1, 2, 3, 4, 5))).Count);

        AssertRefusal(await Refused(null, Selected(1, 2, 3, 4, 5, 6)), rows: 6, limit: 5);
    }

    [Fact]
    public async Task A_limit_of_0_refuses_one_row_and_lets_an_empty_selection_through()
    {
        AssertRefusal(await Refused(0, Selected(1)), rows: 1, limit: 0);

        Assert.Empty(await ForBulkWrite(0, Selected()));
    }

    // Exports, prints and other reads of a selection use these, and endpoints that existed before the limit.
    [Theory]
    [InlineData(null)]
    [InlineData("Label eq 'Open'")]
    public async Task The_other_loads_have_no_limit(string? filter)
    {
        var all = new SelectStateDTO<WidgetListDTO> { All = true, Total = 6, Filter = filter };
        var expected = filter is null ? 6 : 4;

        Assert.Equal(expected, (await Handler.GetSelectedEntitiesAsync(Http(0), all)).Count);
        Assert.Equal(expected, (await Handler.GetSelectedListDTOsAsync(Http(0), all)).Count);
        Assert.Equal(4, (await Handler.GetSelectedEntitiesAsync(Http(0), Selected(1, 2, 3, 4))).Count);
        Assert.Equal(4, (await Handler.GetSelectedListDTOsAsync(Http(0), Selected(1, 2, 3, 4))).Count);
        Assert.DoesNotContain(commands.Sql, IsCount);
    }

    [Fact]
    public async Task The_data_level_access_switches_reach_the_repository()
    {
        var http = Http(10);

        await Handler.GetSelectedEntitiesForBulkWriteAsync(http, Selected(8), disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        await http.RequestServices.GetRequiredService<IWidgetRepository>().Received(1).GetIQueryable(null, null, true, true);
    }

    [Fact]
    public async Task The_secure_controller_has_the_same_limit()
    {
        Assert.Equal(3, (await Controller(Limit).GetSelectedEntitiesForBulkWriteAsync(Selected(1, 2, 3))).Count);

        var controller = Controller(Limit);
        commands.Sql.Clear();
        AssertRefusal(
            await Assert.ThrowsAsync<ShiftEntityException>(() => controller.GetSelectedEntitiesForBulkWriteAsync(Selected(1, 2, 3, 4))),
            rows: 4, Limit);

        Assert.Equal(4, (await Controller(Limit).GetSelectedEntitiesAsync(Selected(1, 2, 3, 4))).Count);
    }

    [Fact]
    public async Task The_secure_controller_loads_nothing_without_read_access()
    {
        Assert.Empty(await Controller(Limit, read: false).GetSelectedEntitiesForBulkWriteAsync(Selected(1, 2, 3, 4)));
        Assert.Empty(commands.Sql);
    }

    [Fact]
    public async Task Skipping_the_secure_controllers_access_check_does_not_skip_the_limit()
    {
        var controller = Controller(Limit, read: false);

        Assert.Equal(3, (await controller.GetSelectedEntitiesForBulkWriteAsync(Selected(1, 2, 3), skipAuthentication: true)).Count);
        await Assert.ThrowsAsync<ShiftEntityException>(() =>
            controller.GetSelectedEntitiesForBulkWriteAsync(Selected(1, 2, 3, 4), skipAuthentication: true));
    }

    private static readonly ShiftEntityCrudHandler<IWidgetRepository, Widget, WidgetListDTO, WidgetDTO> Handler = new();

    private static SelectStateDTO<WidgetListDTO> Selected(params int[] ids)
        => new() { Items = ids.Select(x => new WidgetListDTO { ID = x.ToString() }).ToList() };

    private Task<List<Widget>> ForBulkWrite(double? limit, SelectStateDTO<WidgetListDTO> selection)
        => Handler.GetSelectedEntitiesForBulkWriteAsync(Http(limit), selection);

    private async Task<ShiftEntityException> Refused(double? limit, SelectStateDTO<WidgetListDTO> selection)
    {
        commands.Sql.Clear();

        return await Assert.ThrowsAsync<ShiftEntityException>(() => ForBulkWrite(limit, selection));
    }

    private void AssertRefusal(ShiftEntityException refusal, int rows, int limit)
    {
        Assert.Equal(400, refusal.HttpStatusCode);
        Assert.Contains($"({rows})", refusal.Message.Body);
        Assert.Contains($"maximum of {limit} ", refusal.Message.Body);

        // One COUNT ran, and no row was read.
        Assert.True(IsCount(Assert.Single(commands.Sql)));
        commands.Sql.Clear();
    }

    private static bool IsCount(string sql) => sql.Contains("COUNT(*)", StringComparison.OrdinalIgnoreCase);

    private TestController Controller(double? limit, bool read = true)
        => new() { ControllerContext = new ControllerContext { HttpContext = Http(limit, read) } };

    private HttpContext Http(double? limit, bool read = true)
    {
        var builder = new TypeAuthContextBuilder().AddActionTree<GeneralActionTree>().AddActionTree<WidgetActions>();

        if (limit is not null)
            builder.AddAccessTree(DataGridBulkWriteMaxTopTests.Grant(limit.Value));

        if (read)
        {
            builder.AddAccessTree(JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                [nameof(WidgetActions)] = new Dictionary<string, object> { [nameof(WidgetActions.Widgets)] = new[] { Access.Read } },
            }));
        }

        var repository = Substitute.For<IWidgetRepository>();
        var entities = db.Rows.Where(x => !x.OutOfReach).Select(x => new Widget { ID = x.ID, IsDeleted = x.Deleted, Label = x.Label });
        repository.GetIQueryable(null, null, Arg.Any<bool>(), Arg.Any<bool>()).Returns(ValueTask.FromResult(entities));
        repository.OdataList(Arg.Any<IQueryable<Widget>>()).Returns(call => ValueTask.FromResult(
            call.Arg<IQueryable<Widget>>().Select(x => new WidgetListDTO { ID = x.ID.ToString(), IsDeleted = x.IsDeleted, Label = x.Label })));

        var services = new ServiceCollection();
        services.AddControllers().AddShiftEntityWeb();
        services.AddSingleton<ITypeAuthService>(builder.Build()).AddSingleton(repository);

        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    public interface IWidgetRepository : IShiftRepositoryAsync<Widget, WidgetListDTO, WidgetDTO>,
        IShiftRepositoryWithOptions<Widget, WidgetListDTO, WidgetDTO>;

    [ActionTree("Widgets", "Widgets")]
    public class WidgetActions
    {
        public readonly static ReadWriteDeleteAction Widgets = new("Widgets");
    }

    public sealed class Widget : ShiftEntity<Widget>
    {
        public string? Label { get; set; }
    }

    public sealed class WidgetListDTO : ShiftEntityListDTO
    {
        public override string? ID { get; set; }
        public string? Label { get; set; }
    }

    public sealed class WidgetDTO : ShiftEntityViewAndUpsertDTO
    {
        public override string? ID { get; set; }
    }

    [NonController]
    public sealed class TestController()
        : ShiftEntitySecureControllerAsync<IWidgetRepository, Widget, WidgetListDTO, WidgetDTO>(WidgetActions.Widgets);

    private sealed class Store(DbContextOptions<Store> options) : DbContext(options)
    {
        public DbSet<Row> Rows => Set<Row>();
    }

    private sealed class Row
    {
        public long ID { get; set; }
        public string Label { get; set; } = "";
        public bool Deleted { get; set; }
        public bool OutOfReach { get; set; }
    }

    private sealed class Commands : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}

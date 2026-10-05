using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Core.Phones;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web;
using ShiftSoftware.TypeAuth.Core;
using Xunit;
using ListDTO = ShiftSoftware.ShiftEntity.Tests.Pii.PiiODataGuardTests.ContactListDTO;

namespace ShiftSoftware.ShiftEntity.Tests.Pii;

public class PiiQueryProviderTests
{
    [Theory]
    [InlineData(false, "contains(Phone/Display,'07500000088')", 1)]
    [InlineData(false, "contains(Name/Display,'Ada')", 0)]
    [InlineData(true, "contains(Name/Display,'Ada')", 2)]
    [InlineData(true, "contains(Phone/Display,'0088')", 2)]
    [InlineData(true, "contains(Phone/Display,'0000088')", 0)]
    [InlineData(false, "Phone/Display eq '07500000099'", 0)]
    [InlineData(false, "Child/Name/Display eq 'Nested example'", 1)]
    [InlineData(false, "contains(Phone/Display,@p)&@p='07500000088'", 1)]
    public async Task Sql_filter_count_page_and_selection_share_policy_and_mask_results(bool partial, string filter, int count)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var commands = new Commands();
        using var db = new Store(new DbContextOptionsBuilder<Store>().UseSqlite(connection).AddInterceptors(commands).Options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        db.Rows.AddRange(
            new Row { ID = 1, Allowed = true, Label = "A", Phone = "+964 750 000 0088", Name = "Ada Example", NestedName = "Nested example" },
            new Row { ID = 2, Allowed = true, Label = "B", Phone = "+1 202-555-0088", Name = "Ada Other" },
            new Row { ID = 3, Allowed = false, Label = "C", Phone = "+964 750 000 0088", Name = "Ada Blocked" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        commands.Sql.Clear();
        var auth = Substitute.For<ITypeAuthService>();
        auth.CanAccess(PiiActionTree.PartialSearch).Returns(partial);
        auth.CanAccess(GeneralActionTree.DataGridExport).Returns(true);
        var repository = Substitute.For<IShiftRepositoryAsync<Contact, ListDTO, ViewDTO>>();
        var entities = db.Rows.Where(r => r.Allowed).Select(r => new Contact
        { ID = r.ID, IsDeleted = false, Label = r.Label, Phone = r.Phone, Name = r.Name, NestedName = r.NestedName });
        repository.GetIQueryable(null, null, RepositoryBypass.None).Returns(ValueTask.FromResult(entities));
        repository.GetIQueryable(null, null, false, false).Returns(ValueTask.FromResult(entities));
        repository.OdataList(Arg.Any<IQueryable<Contact>>()).Returns(call => ValueTask.FromResult(call.Arg<IQueryable<Contact>>().Select(r => new ListDTO
        {
            ID = r.ID.ToString(), Label = r.Label, IsDeleted = r.IsDeleted,
            Name = new() { Value = r.Name }, Phone = new() { Value = r.Phone },
            Child = new() { Name = new() { Value = r.NestedName } }
        })));
        repository.ApplyPostODataProcessing(Arg.Any<IQueryable<ListDTO>>()).Returns(call => ValueTask.FromResult(call.Arg<IQueryable<ListDTO>>()));
        var registrations = new ServiceCollection();
        registrations.AddControllers().AddShiftEntityWeb();
        registrations.AddShiftEntityPii().AddShiftPhoneNumbers(o => o.DefaultRegion = "IQ");
        var phones = new CountingPhones(new PhoneNumberService(Options.Create(new PhoneNumberOptions { DefaultRegion = "IQ" })));
        registrations.AddSingleton<IPhoneNumberService>(phones);
        registrations.AddSingleton(auth).AddSingleton(repository);
        using var services = registrations.BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var handler = new ShiftEntityCrudHandler<IShiftRepositoryAsync<Contact, ListDTO, ViewDTO>, Contact, ListDTO, ViewDTO>();
        var query = PiiODataGuardTests.Options("?$top=1&$skip=0&$orderby=Label&$filter=" + filter);
        var result = await handler.GetListAsync(http, query);
        Assert.Equal(count, result.Count);
        Assert.Equal(Math.Min(count, 1), result.Value.Count());
        Assert.DoesNotContain("+964", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("Ada Example", JsonSerializer.Serialize(result));
        var normalizationsPerQuery = filter.Contains("Phone/") && !partial ? 1 : 0;
        Assert.Equal(normalizationsPerQuery, phones.Calls);
        var selection = await handler.GetSelectedListDTOsAsync(http, query);
        Assert.Equal(normalizationsPerQuery * 2, phones.Calls);
        Assert.Equal(count, selection.Count);
        Assert.DoesNotContain("+964", JsonSerializer.Serialize(selection));
        Assert.DoesNotContain("Nested example", JsonSerializer.Serialize(selection));
        Assert.DoesNotContain(selection, r => r.Label == "C");
        Assert.Contains(commands.Sql, sql => sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) && sql.Contains("WHERE"));
        Assert.Contains(commands.Sql, sql => sql.Contains("LIMIT"));
        Assert.All(commands.Sql, sql => Assert.Contains("WHERE", sql));
    }

    private sealed class Store(DbContextOptions<Store> options) : DbContext(options)
    {
        public DbSet<Row> Rows => Set<Row>();
    }
    private sealed class Row
    {
        public int ID { get; set; }
        public bool Allowed { get; set; }
        public string Label { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Name { get; set; } = "";
        public string? NestedName { get; set; }
    }
    public sealed class Contact : ShiftEntity<Contact>
    {
        public string? Label { get; set; }
        public string? Phone { get; set; }
        public string? Name { get; set; }
        public string? NestedName { get; set; }
    }
    public sealed class ViewDTO : ShiftEntityViewAndUpsertDTO { public override string? ID { get; set; } }
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
    private sealed class CountingPhones(IPhoneNumberService inner) : IPhoneNumberService
    {
        public int Calls { get; private set; }
        public bool TryNormalize(string input, out string normalized, out string error)
        {
            Calls++;
            return inner.TryNormalize(input, out normalized, out error);
        }
    }
}

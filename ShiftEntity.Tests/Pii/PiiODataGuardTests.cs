using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.ModelBuilder;
using NSubstitute;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Web;
using ShiftSoftware.ShiftEntity.Web.Pii;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Pii;

public class PiiODataGuardTests
{
    [Fact]
    public void Non_pii_filter_and_order_remain_available()
    {
        PiiODataGuard.Check(Options("?$filter=Label eq 'x'&$orderby=Label"));
    }

    [Fact]
    public void Nested_protected_name_does_not_block_an_unrelated_root_name()
    {
        PiiODataGuard.Check(Options<NameCollisionListDTO>("?$filter=Name eq 'Group A'&$orderby=Name"));

        var error = Assert.Throws<ShiftEntityException>(() =>
            PiiODataGuard.Check(Options<NameCollisionListDTO>("?$filter=Child/Name/Value eq 'raw'")));
        Assert.Equal(400, error.HttpStatusCode);
    }

    [Theory]
    [InlineData("?$filter=Phone/Value eq '0088'")]
    [InlineData("?$filter=contains(Phone/Display,'88')")]
    [InlineData("?$orderby=Phone/Display")]
    public void Protected_field_queries_are_rejected(string query)
    {
        var error = Assert.Throws<ShiftEntityException>(() => PiiODataGuard.Check(Options(query)));
        Assert.Equal(400, error.HttpStatusCode);
    }

    [Fact]
    public void Renamed_edm_pii_wrapper_still_rejects_protected_queries()
    {
        var error = Assert.Throws<ShiftEntityException>(() =>
            PiiODataGuard.Check(Options<ContactListDTO>(
                "?$filter=Phone/Value eq 'secret'", renamePiiWrapper: true)));
        Assert.Equal(400, error.HttpStatusCode);
    }

    [Fact]
    public async Task Shared_list_and_selection_paths_refuse_protected_queries()
    {
        var repository = Substitute.For<IShiftRepositoryAsync<Contact, ContactListDTO, ContactDTO>>();
        IQueryable<Contact> entities = new[] { new Contact() }.AsQueryable();
        IQueryable<ContactListDTO> list = new[] { new ContactListDTO() }.AsQueryable();
        repository.GetIQueryable(null, null, RepositoryBypass.None)
            .Returns(ValueTask.FromResult(entities));
        repository.GetIQueryable(null, null, false, false)
            .Returns(ValueTask.FromResult(entities));
        repository.OdataList(entities).Returns(ValueTask.FromResult(list));
        using var provider = new ServiceCollection().AddSingleton(repository).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = provider };
        var query = Options("?$filter=Phone/Value eq '0088'");
        var handler = new ShiftEntityCrudHandler<IShiftRepositoryAsync<Contact, ContactListDTO, ContactDTO>,
            Contact, ContactListDTO, ContactDTO>();

        var listError = await Assert.ThrowsAsync<ShiftEntityException>(() =>
            handler.GetListAsync(http, query));
        var selectionError = await Assert.ThrowsAsync<ShiftEntityException>(() =>
            handler.GetSelectedListDTOsAsync(http, query));

        Assert.Equal(400, listError.HttpStatusCode);
        Assert.Equal(400, selectionError.HttpStatusCode);
    }

    private static ODataQueryOptions<ContactListDTO> Options(string query) => Options<ContactListDTO>(query);

    private static ODataQueryOptions<T> Options<T>(string query, bool renamePiiWrapper = false) where T : class
    {
        var builder = new ODataConventionModelBuilder();
        builder.EntitySet<T>("Contacts");
        if (renamePiiWrapper)
        {
            var protectedField = builder.ComplexType<PiiFieldDTO>();
            protectedField.Name = "ProtectedField";
            protectedField.Namespace = "API";
        }
        var context = new ODataQueryContext(builder.GetEdmModel(), typeof(T), new());
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(query);
        return new ODataQueryOptions<T>(context, http.Request);
    }

    public sealed class ContactListDTO : ShiftEntityListDTO
    {
        public override string? ID { get; set; }
        public string? Label { get; set; }
        [Pii(PiiKind.Phone)] public PiiFieldDTO? Phone { get; set; }
    }

    public sealed class NameCollisionListDTO : ShiftEntityListDTO
    {
        public override string? ID { get; set; }
        public string? Name { get; set; }
        public NameCollisionChildDTO? Child { get; set; }
    }

    public sealed class NameCollisionChildDTO
    {
        [Pii(PiiKind.Name)] public PiiFieldDTO? Name { get; set; }
    }

    public sealed class Contact : ShiftEntity<Contact> { }

    public sealed class ContactDTO : ShiftEntityViewAndUpsertDTO
    {
        public override string? ID { get; set; }
    }
}

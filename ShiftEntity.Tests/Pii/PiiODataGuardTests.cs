using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using Microsoft.OData.UriParser;
using NSubstitute;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Core.Phones;
using ShiftSoftware.ShiftEntity.Web;
using ShiftSoftware.ShiftEntity.Web.Pii;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Pii;

public class PiiODataGuardTests
{
    private static readonly Lazy<IEdmModel> Model = new(() => BuildModel());

    [Theory]
    [InlineData("Phone/Value eq '07500000088'")]
    [InlineData("'0750-000-0088' eq Phone/Display")]
    [InlineData("contains(Phone/Display,'07500000088')")]
    [InlineData("startswith(Phone/Value,'+964 750 000 0088')")]
    [InlineData("endswith(Phone/Display,'07500000088')")]
    public void Exact_only_rewrites_positive_queries_and_normalizes_complete_phone(string filter)
    {
        using var services = Services();
        var query = Options("?$filter=" + Uri.EscapeDataString(filter));
        var node = Assert.IsType<BinaryOperatorNode>(new PiiODataGuard(services).Apply(query, query.Filter.FilterClause.Expression));
        Assert.Equal(BinaryOperatorKind.Equal, node.OperatorKind);
        Assert.Equal("Value", Assert.IsType<SingleValuePropertyAccessNode>(node.Left).Property.Name);
        Assert.Equal("+964 750 000 0088", Assert.IsType<ConstantNode>(node.Right).Value);
    }

    [Theory]
    [InlineData("contains(Phone/Value,'0088')")]
    [InlineData("startswith(Phone/Display,'+964 750')")]
    [InlineData("endswith(Phone/Display,'000 0088')")]
    public void Partial_grant_accepts_fragments_without_full_number_validation(string filter)
    {
        using var services = Services(partial: true);
        var query = Options("?$filter=" + Uri.EscapeDataString(filter));
        var node = Assert.IsType<SingleValueFunctionCallNode>(new PiiODataGuard(services).Apply(query, query.Filter.FilterClause.Expression));
        Assert.Equal("Value", Assert.IsType<SingleValuePropertyAccessNode>(node.Parameters.First()).Property.Name);
    }

    [Theory]
    [InlineData("not contains(Name/Value,'Ada')")]
    [InlineData("contains(Name/Value,'Ada') eq false")]
    [InlineData("Name/Value ne 'Ada'")]
    [InlineData("Name/Value eq null")]
    [InlineData("Name/Value eq ''")]
    [InlineData("Name/Value gt 'Ada'")]
    [InlineData("substring(Name/Value,1) eq 'da'")]
    [InlineData("tolower(Name/Value) eq 'ada'")]
    [InlineData("contains(tolower(Name/Value),'ada')")]
    [InlineData("Name/Value eq Label")]
    [InlineData("Name/Write eq 'keep'")]
    [InlineData("Secret/Value eq 'secret'")]
    [InlineData("Children/any(c:c/Name/Value eq 'Ada')")]
    [InlineData("Children/all(c:c/Name/Value eq 'Ada')")]
    [InlineData("Name/Value in ('Ada','Bea')")]
    [InlineData("Label eq 'x' or not (Name/Value eq 'Ada')")]
    [InlineData("Name/Value ne @value&@value='Ada'")]
    [InlineData("not contains(Name/Value,@value)&@value='Ada'")]
    public void Unsupported_protected_expressions_cannot_bypass_lookup(string filter)
    {
        foreach (var partial in new[] { false, true })
        {
            using var services = Services(partial);
            var query = Options("?$filter=" + filter);
            Assert.Equal(400, Assert.Throws<ShiftEntityException>(() =>
                new PiiODataGuard(services).Apply(query, query.Filter.FilterClause.Expression)).HttpStatusCode);
        }
    }

    [Fact]
    public void Mixed_nested_inherited_and_renamed_metadata_are_supported()
    {
        using var services = Services();
        var query = Options("?$filter=Label eq 'x' and (Child/Name/Display eq 'Ada' or contains(Name/Display,'Bea'))&$orderby=Label", true);
        var node = Assert.IsType<BinaryOperatorNode>(new PiiODataGuard(services).Apply(query, query.Filter.FilterClause.Expression));
        Assert.Equal(BinaryOperatorKind.And, node.OperatorKind);
        Assert.Equal(BinaryOperatorKind.Or, Assert.IsType<BinaryOperatorNode>(node.Right).OperatorKind);
    }

    [Fact]
    public void App_action_replaces_default_action_and_ordering_stays_denied()
    {
        var app = new BooleanAction("Custom protected access");
        foreach (var appGrant in new[] { false, true })
        {
            using var services = Services(true, app, appGrant);
            var query = Options("?$filter=contains(Name/Display,'Ada')");
            var result = new PiiODataGuard(services).Apply(query, query.Filter.FilterClause.Expression);
            Assert.Equal(appGrant, result is SingleValueFunctionCallNode);
            foreach (var order in new[] { "Name/Display", "Phone/Value", "Label,Child/Name/Display", "tolower(Name/Value)" })
            {
                var sorted = Options("?$orderby=" + order);
                Assert.Throws<ShiftEntityException>(() => new PiiODataGuard(services).Apply(sorted, null));
            }
        }
    }

    [Fact]
    public void Non_pii_queries_are_unchanged_and_phone_fragments_are_not_suffix_lookups()
    {
        using var services = Services();
        var query = Options("?$filter=contains(Label,'x')&$orderby=Label");
        Assert.Same(query.Filter.FilterClause.Expression, new PiiODataGuard(services).Apply(query, query.Filter.FilterClause.Expression));
        query = Options("?$filter=contains(Phone/Display,'0088')");
        Assert.Throws<ShiftEntityException>(() => new PiiODataGuard(services).Apply(query, query.Filter.FilterClause.Expression));
    }

    internal static ServiceProvider Services(bool partial = false, BooleanAction? appAction = null, bool appGrant = false)
    {
        var auth = Substitute.For<ITypeAuthService>();
        auth.CanAccess(PiiActionTree.PartialSearch).Returns(partial);
        auth.CanAccess(GeneralActionTree.DataGridExport).Returns(true);
        if (appAction is not null) auth.CanAccess(appAction).Returns(appGrant);
        return new ServiceCollection().AddSingleton(auth).AddSingleton(new ShiftEntityOptions())
            .AddShiftEntityPii(o => { if (appAction is not null) o.PartialSearchAction = appAction; })
            .AddShiftPhoneNumbers(o => o.DefaultRegion = "IQ")
            .AddScoped<IODataQueryPolicy, PiiODataGuard>().BuildServiceProvider();
    }

    internal static ODataQueryOptions<ContactListDTO> Options(string query, bool rename = false)
    {
        var context = new ODataQueryContext(rename ? BuildModel(true) : Model.Value, typeof(ContactListDTO), new());
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(query);
        return new ODataQueryOptions<ContactListDTO>(context, http.Request);
    }

    private static IEdmModel BuildModel(bool rename = false)
    {
        var builder = new ODataConventionModelBuilder();
        builder.EntitySet<ContactListDTO>("Contacts");
        if (rename)
        {
            var wrapper = builder.ComplexType<PiiFieldDTO>();
            wrapper.Name = "ProtectedField";
            wrapper.Namespace = "API";
        }
        return builder.GetEdmModel();
    }


    public abstract class ContactBaseDTO : ShiftEntityListDTO
    {
        [Pii(PiiKind.Name)] public virtual PiiFieldDTO? Name { get; set; }
    }
    public sealed class ContactListDTO : ContactBaseDTO
    {
        public override string? ID { get; set; }
        public override PiiFieldDTO? Name { get; set; }
        public string? Label { get; set; }
        [Pii(PiiKind.Phone)] public PiiFieldDTO? Phone { get; set; }
        [Pii(PiiKind.Identifier, Revealable = false)] public PiiFieldDTO? Secret { get; set; }
        public ChildDTO? Child { get; set; }
        public List<ChildDTO> Children { get; set; } = [];
    }
    public sealed class ChildDTO
    {
        [Pii(PiiKind.Name)] public PiiFieldDTO? Name { get; set; }
    }
}

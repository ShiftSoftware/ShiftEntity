using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.UriParser;
using NSubstitute;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web;
using ShiftSoftware.ShiftEntity.Web.Pii;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using Xunit;
using Contact = ShiftSoftware.ShiftEntity.Tests.Pii.PiiRevealHandlerTests.Contact;
using ContactDTO = ShiftSoftware.ShiftEntity.Tests.Pii.PiiRevealHandlerTests.ContactDTO;
using ContactListDTO = ShiftSoftware.ShiftEntity.Tests.Pii.PiiRevealHandlerTests.ContactListDTO;

namespace ShiftSoftware.ShiftEntity.Tests.Pii;

public class PiiPermissionTests
{
    public static TheoryData<bool, bool, bool, bool, bool, bool> PermissionCases
    {
        get
        {
            var cases = new TheoryData<bool, bool, bool, bool, bool, bool>();
            foreach (var replaceReveal in new[] { false, true })
            foreach (var replaceSearch in new[] { false, true })
            foreach (var defaultReveal in new[] { false, true })
            foreach (var defaultSearch in new[] { false, true })
            foreach (var appReveal in new[] { false, true })
            foreach (var appSearch in new[] { false, true })
                cases.Add(replaceReveal, replaceSearch, defaultReveal, defaultSearch, appReveal, appSearch);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(PermissionCases))]
    public async Task Search_reveal_and_writes_use_only_their_independently_configured_action(
        bool replaceReveal, bool replaceSearch, bool defaultReveal, bool defaultSearch, bool appReveal, bool appSearch)
    {
        var grants = new Dictionary<string, object>
        {
            [nameof(PiiActionTree)] = new Dictionary<string, Access[]>
            {
                [nameof(PiiActionTree.Reveal)] = defaultReveal ? [Access.Maximum] : [],
                [nameof(PiiActionTree.PartialSearch)] = defaultSearch ? [Access.Maximum] : []
            },
            [nameof(HostActions)] = new Dictionary<string, Access[]>
            {
                [nameof(HostActions.Reveal)] = appReveal ? [Access.Maximum] : [],
                [nameof(HostActions.PartialSearch)] = appSearch ? [Access.Maximum] : []
            }
        };
        var auth = new TypeAuthContextBuilder().AddActionTree<PiiActionTree>().AddActionTree<HostActions>()
            .AddAccessTree(JsonSerializer.Serialize(grants)).Build();
        var hashes = Substitute.For<IHashIdService>();
        hashes.Decode<ContactDTO>("contact-7").Returns(7);
        var repo = Substitute.For<IShiftRepositoryAsync<Contact, ContactListDTO, ContactDTO>>();
        var stored = new Contact { Phone = "synthetic-phone-0088" };
        repo.FindAsync(7, null, RepositoryBypass.None).Returns(Task.FromResult<Contact?>(stored));
        using var services = new ServiceCollection().AddSingleton<ITypeAuthService>(auth)
            .AddSingleton(hashes).AddSingleton(repo).AddShiftEntityPii(options =>
            {
                if (replaceReveal) options.Action = HostActions.Reveal;
                if (replaceSearch) options.PartialSearchAction = HostActions.PartialSearch;
            }).BuildServiceProvider();
        var canReveal = replaceReveal ? appReveal : defaultReveal;
        var canSearch = replaceSearch ? appSearch : defaultSearch;

        var query = PiiODataGuardTests.Options("?$filter=contains(Name/Display,'Ada')");
        var filter = new PiiODataGuard(services).Apply(query, query.Filter.FilterClause.Expression);
        Assert.Equal(canSearch, filter is SingleValueFunctionCallNode);
        if (!canSearch) Assert.Equal(BinaryOperatorKind.Equal, Assert.IsType<BinaryOperatorNode>(filter).OperatorKind);
        var exact = PiiODataGuardTests.Options("?$filter=Name/Display eq 'Ada Example'");
        Assert.IsType<BinaryOperatorNode>(new PiiODataGuard(services).Apply(exact, exact.Filter.FilterClause.Expression));

        var context = new DefaultHttpContext { RequestServices = services };
        var handler = new ShiftEntityCrudHandler<IShiftRepositoryAsync<Contact, ContactListDTO, ContactDTO>, Contact, ContactListDTO, ContactDTO>();
        var reveal = await handler.RevealPiiAsync(context, "contact-7", "Phone");
        Assert.Equal(canReveal ? 200 : 403, reveal.StatusCode);
        if (canReveal)
        {
            Assert.Equal(stored.Phone, Assert.IsType<PiiRevealDTO>(reveal.Body).Value);
            Assert.Equal("no-store", context.Response.Headers.CacheControl);
        }

        foreach (var isCreate in new[] { false, true })
        foreach (var value in new string?[] { "replacement", null })
        {
            var dto = new ContactDTO { Phone = new() { Value = value, Write = "replace" } };
            var entity = new Contact { Phone = stored.Phone };
            if (canReveal)
            {
                var policy = PiiSavePolicy.Prepare(dto, entity, services, isCreate);
                entity.Phone = dto.Phone.Value;
                policy.ValidateMapped(entity);
                Assert.Equal(value, entity.Phone);
            }
            else
            {
                Assert.Equal(403, Assert.Throws<ShiftEntityException>(() =>
                    PiiSavePolicy.Prepare(dto, entity, services, isCreate)).HttpStatusCode);
                Assert.Equal(stored.Phone, entity.Phone);
            }
        }
        var keep = new ContactDTO { Phone = new() { Value = "ignored", Write = "keep" } };
        PiiSavePolicy.Prepare(keep, stored, services, false).ValidateMapped(stored);
        Assert.Equal(stored.Phone, keep.Phone.Value);
    }

    [ActionTree("Host protected information", "Synthetic independent permission overrides")]
    public class HostActions
    {
        public static readonly BooleanAction Reveal = new("Reveal or change protected information");
        public static readonly BooleanAction PartialSearch = new("Search protected information using partial values");
    }
}

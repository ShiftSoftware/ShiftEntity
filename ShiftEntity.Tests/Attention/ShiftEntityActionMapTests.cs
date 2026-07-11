using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Tests.Attention.Scenario;
using ShiftSoftware.ShiftEntity.Web.Endpoints;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Attention;

/// <summary>Action tree for the action-map tests — plain static ReadWriteDeleteAction nodes.</summary>
[ActionTree("Attention Test Actions", "Static actions for the action-map tests.")]
public class AttentionTestActions
{
    public readonly static ReadWriteDeleteAction Widgets = new("Widgets");
    public readonly static ReadWriteDeleteAction Portals = new("Portals");
}

/// <summary>Minimal DTOs — the endpoint type parameters demand them; these tests never map.</summary>
public class PortalListDTO : ShiftEntityDTOBase
{
    public override string? ID { get; set; }
}

/// <inheritdoc cref="PortalListDTO"/>
public class PortalViewDTO : ShiftEntityViewAndUpsertDTO
{
    public override string? ID { get; set; }
}

/// <summary>
/// A secure attribute endpoint entity. RegisterShiftRepositories discovers the attribute and
/// must feed the <see cref="ShiftEntityActionMap"/> from it.
/// </summary>
[ShiftEntitySecureEndpoint<PortalListDTO, PortalViewDTO, AttentionTestActions>(
    "api/action-map-portals", nameof(AttentionTestActions.Portals))]
public class PortalEntity : ShiftEntity<PortalEntity>
{
    public string Name { get; set; } = "";
}

/// <summary>
/// The <see cref="ShiftEntityActionMap"/> registry: the map contract itself, and the three
/// surfaces that feed it — secure attribute endpoints (via <c>RegisterShiftRepositories</c>),
/// <c>MapShiftEntitySecureCrud</c> (at map time), and the explicit
/// <c>AddShiftEntityAction</c> registration for classic secure-controller apps.
/// </summary>
public class ShiftEntityActionMapTests
{
    // ── The map contract ────────────────────────────────────────────────────────

    [Fact]
    public void Register_MakesTheAction_AvailableToTryGetAction()
    {
        var map = new ShiftEntityActionMap();

        map.Register("WidgetEntity", AttentionTestActions.Widgets);

        Assert.True(map.TryGetAction("WidgetEntity", out var action));
        Assert.Same(AttentionTestActions.Widgets, action);
    }

    [Fact]
    public void TryGetAction_UnknownEntityType_ReturnsFalse()
    {
        var map = new ShiftEntityActionMap();

        Assert.False(map.TryGetAction("Mystery", out _));
    }

    [Fact]
    public void Register_SameEntityTypeAgain_OverwritesThePreviousAction()
    {
        // The last registration overwrites the previous one. This keeps re-registration (for
        // example mapping the same entity twice during tests or startup retries) harmless.
        var map = new ShiftEntityActionMap();
        map.Register("WidgetEntity", AttentionTestActions.Widgets);

        map.Register("WidgetEntity", AttentionTestActions.Portals);

        Assert.True(map.TryGetAction("WidgetEntity", out var action));
        Assert.Same(AttentionTestActions.Portals, action);
    }

    [Fact]
    public void EntityTypeMatching_IsOrdinal()
    {
        // Entity types are CLR short names — exact, case-sensitive matching, same as the
        // ShiftEntityDtoMap keys.
        var map = new ShiftEntityActionMap();

        map.Register("WidgetEntity", AttentionTestActions.Widgets);

        Assert.False(map.TryGetAction("widgetentity", out _));
    }

    // ── Feed: secure attribute endpoints via RegisterShiftRepositories ─────────

    private static ShiftEntityActionMap? RegisteredActionMap(IServiceCollection services)
        => services.SingleOrDefault(d => d.ServiceType == typeof(ShiftEntityActionMap))
            ?.ImplementationInstance as ShiftEntityActionMap;

    [Fact]
    public void RegisterShiftRepositories_FeedsTheMap_FromSecureEndpointAttributes()
    {
        var services = new ServiceCollection();

        services.RegisterShiftRepositories(typeof(PortalEntity).Assembly);

        var actionMap = RegisteredActionMap(services);
        Assert.NotNull(actionMap);

        // The attribute names its action (action tree + field name); registration resolved it
        // to the actual static action instance.
        Assert.True(actionMap.TryGetAction(nameof(PortalEntity), out var action));
        Assert.Same(AttentionTestActions.Portals, action);
    }

    [Fact]
    public void RegisterShiftRepositories_RegistersTheMapSingleton_EvenWithoutSecureEndpoints()
    {
        // Consumers resolve the map optionally. Registering it even when it stays empty means
        // "no entry" and "no secure endpoints at all" behave the same.
        var services = new ServiceCollection();

        services.RegisterShiftRepositories(typeof(ShiftEntityActionMap).Assembly);   // Core has no endpoint attributes

        Assert.NotNull(RegisteredActionMap(services));
    }

    // ── Feed: explicit AddShiftEntityAction (classic secure-controller apps) ───

    [Fact]
    public void AddShiftEntityAction_RegistersTheSingleton_AndTheEntry()
    {
        var services = new ServiceCollection();

        services.AddShiftEntityAction<WidgetEntity>(AttentionTestActions.Widgets);

        var actionMap = RegisteredActionMap(services);
        Assert.NotNull(actionMap);
        Assert.True(actionMap.TryGetAction(nameof(WidgetEntity), out var action));
        Assert.Same(AttentionTestActions.Widgets, action);
    }

    [Fact]
    public void AddShiftEntityAction_BeforeOrAfterRegisterShiftRepositories_WritesIntoTheSameSingleton()
    {
        // Ordering must not matter: both paths find (or create) the same singleton instance.
        var before = new ServiceCollection();
        before.AddShiftEntityAction<WidgetEntity>(AttentionTestActions.Widgets);
        before.RegisterShiftRepositories(typeof(PortalEntity).Assembly);

        var after = new ServiceCollection();
        after.RegisterShiftRepositories(typeof(PortalEntity).Assembly);
        after.AddShiftEntityAction<WidgetEntity>(AttentionTestActions.Widgets);

        foreach (var services in new[] { before, after })
        {
            var actionMap = RegisteredActionMap(services);   // SingleOrDefault: also asserts a single registration
            Assert.NotNull(actionMap);
            Assert.True(actionMap.TryGetAction(nameof(WidgetEntity), out _));    // explicit feed
            Assert.True(actionMap.TryGetAction(nameof(PortalEntity), out _));    // attribute feed
        }
    }

    // ── Feed: MapShiftEntitySecureCrud at map time ──────────────────────────────

    [Fact]
    public void MapShiftEntitySecureCrud_FeedsTheMap_AtMapTime()
    {
        var actionMap = new ShiftEntityActionMap();

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(actionMap)
            .BuildServiceProvider();

        var endpoints = new TestEndpointRouteBuilder(provider);

        endpoints.MapShiftEntitySecureCrud<
            ShiftRepository<AttentionTestDbContext, PortalEntity, PortalListDTO, PortalViewDTO>,
            PortalEntity, PortalListDTO, PortalViewDTO>(
            "api/portal-crud", AttentionTestActions.Portals);

        Assert.True(actionMap.TryGetAction(nameof(PortalEntity), out var action));
        Assert.Same(AttentionTestActions.Portals, action);
    }

    [Fact]
    public void MapShiftEntitySecureCrud_WithANullAction_DoesNotFeedTheMap()
    {
        // A null action means authenticated-only endpoints without a TypeAuth permission.
        // There is no action to hand to cross-entity surfaces.
        var actionMap = new ShiftEntityActionMap();

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(actionMap)
            .BuildServiceProvider();

        var endpoints = new TestEndpointRouteBuilder(provider);

        endpoints.MapShiftEntitySecureCrud<
            ShiftRepository<AttentionTestDbContext, PortalEntity, PortalListDTO, PortalViewDTO>,
            PortalEntity, PortalListDTO, PortalViewDTO>(
            "api/portal-crud-null-action", action: null);

        Assert.False(actionMap.TryGetAction(nameof(PortalEntity), out _));
    }

    [Fact]
    public void MapShiftEntitySecureCrud_WithoutARegisteredMap_StillMaps()
    {
        // GetService feed: an app that never registered the map gets no registry entries,
        // and mapping must work as normal.
        using var provider = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();

        var endpoints = new TestEndpointRouteBuilder(provider);

        var group = endpoints.MapShiftEntitySecureCrud<
            ShiftRepository<AttentionTestDbContext, PortalEntity, PortalListDTO, PortalViewDTO>,
            PortalEntity, PortalListDTO, PortalViewDTO>(
            "api/portal-crud-no-map", AttentionTestActions.Portals);

        Assert.NotNull(group);
    }
}

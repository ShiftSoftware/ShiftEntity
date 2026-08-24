using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.Auditing.Scenario;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Repository;

/// <summary>
/// Step D1's acceptance criterion: flipping the mode changes which mapper a repository resolves, with no code
/// edit anywhere.
/// <para>
/// The gap this closes (B-1) is that <c>ShiftRepository</c> never consulted
/// <see cref="ShiftEntityMapperRegistry"/>. A source-generated mapper could exist, be correct, be registered,
/// and the repository would still use AutoMapper — which the Stage C parity inventory found live on one
/// triple. The mode exists rather than wiring the registry in unconditionally because doing it silently is
/// exactly the change that swaps a hand-tuned profile for convention output without telling anyone.
/// </para>
/// </summary>
public class MappingModeResolutionTests
{
    /// <summary>Stands in for a source-generated mapper: parameterless, registry-resolvable, unmistakable.</summary>
    private sealed class RegistryOrderMapper : IShiftEntityMapper<OrderEntity, OrderListDTO, OrderListDTO>
    {
        public OrderEntity MapToEntity(OrderListDTO dto, OrderEntity existing, MappingContext context = default)
        {
            existing.Number = dto.Number;
            return existing;
        }

        public OrderListDTO MapToView(OrderEntity entity, MappingContext context = default) => throw new NotSupportedException();
        public IQueryable<OrderListDTO> MapToList(IQueryable<OrderEntity> query, MappingContext context = default) => throw new NotSupportedException();
        public void CopyEntity(OrderEntity source, OrderEntity target, MappingContext context = default) => throw new NotSupportedException();
    }

    private static ServiceProvider Host(ShiftEntityMappingMode? mode)
    {
        var services = new ServiceCollection();

        services.AddDbContext<OrderingDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<ICurrentUserProvider>(_ => FakeUserProvider.Anonymous());
        services.AddScoped<IdentityClaimProvider>();
        services.AddSingleton<IHashIdService>(new IdentityHashIdService());
        services.AddSingleton<IDefaultDataLevelAccess>(new RecordingDefaultDataLevelAccess());

        // Absent entirely when mode is null — the shape of a host that never configured ShiftEntityOptions,
        // which must behave as AutoMapperFirst rather than throwing.
        if (mode is { } m)
            services.AddSingleton(new ShiftEntityOptions { MappingMode = m });

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static ShiftRepository<OrderingDbContext, OrderEntity, OrderListDTO, OrderListDTO> Repo(IServiceScope scope)
        => new(scope.ServiceProvider.GetRequiredService<OrderingDbContext>());

    private static void RegisterGeneratedMapper() =>
        ShiftEntityMapperRegistry.Register(
            typeof(OrderEntity), typeof(OrderListDTO), typeof(OrderListDTO), typeof(RegistryOrderMapper));

    [Fact]
    public void GeneratedFirst_ResolvesTheRegistryMapper()
    {
        RegisterGeneratedMapper();

        using var provider = Host(ShiftEntityMappingMode.GeneratedFirst);
        using var scope = provider.CreateScope();

        Assert.IsType<RegistryOrderMapper>(Repo(scope).ShiftRepositoryOptions.Mapper);
    }

    [Fact]
    public void GeneratedOnly_ResolvesTheRegistryMapper()
    {
        RegisterGeneratedMapper();

        using var provider = Host(ShiftEntityMappingMode.GeneratedOnly);
        using var scope = provider.CreateScope();

        Assert.IsType<RegistryOrderMapper>(Repo(scope).ShiftRepositoryOptions.Mapper);
    }

    /// <summary>
    /// The safety property that makes D1 shippable on its own: the registry is NOT consulted under the default
    /// mode, so upgrading to a framework that has this step changes nothing until someone opts in.
    /// </summary>
    [Fact]
    public void AutoMapperFirst_DoesNotConsultTheRegistry()
    {
        RegisterGeneratedMapper();

        using var provider = Host(ShiftEntityMappingMode.AutoMapperFirst);
        using var scope = provider.CreateScope();

        // No AutoMapper in this host either, so the correct outcome is NO mapper at all — the mapping methods
        // then throw. What matters is that the registry mapper was not silently picked up.
        Assert.Null(Repo(scope).ShiftRepositoryOptions.Mapper);
    }

    [Fact]
    public void NoOptionsConfigured_BehavesAsAutoMapperFirst()
    {
        RegisterGeneratedMapper();

        using var provider = Host(mode: null);
        using var scope = provider.CreateScope();

        // A host that never called AddShiftEntity at all must not start resolving generated mappers because
        // the framework was upgraded underneath it.
        Assert.Null(Repo(scope).ShiftRepositoryOptions.Mapper);
    }

    /// <summary>
    /// Generated mappers carry per-instance builder state that <c>AddConfiguration</c> mutates, so the
    /// activator is cached and the instance never is. A shared singleton would leak one repository's
    /// customization into every other consumer of the triple — a cross-request bug that would present as
    /// intermittent mis-mapping.
    /// </summary>
    [Fact]
    public void EachRepository_GetsItsOwnMapperInstance()
    {
        RegisterGeneratedMapper();

        using var provider = Host(ShiftEntityMappingMode.GeneratedFirst);
        using var scope = provider.CreateScope();

        var first = Repo(scope).ShiftRepositoryOptions.Mapper;
        var second = Repo(scope).ShiftRepositoryOptions.Mapper;

        Assert.NotNull(first);
        Assert.NotSame(first, second);
    }

    /// <summary>An explicitly configured mapper still wins — the mode only decides the DEFAULT.</summary>
    [Fact]
    public void AnExplicitMapper_StillBeatsTheRegistry()
    {
        RegisterGeneratedMapper();

        using var provider = Host(ShiftEntityMappingMode.GeneratedFirst);
        using var scope = provider.CreateScope();

        var explicitMapper = new RegistryOrderMapper();
        var repo = new ShiftRepository<OrderingDbContext, OrderEntity, OrderListDTO, OrderListDTO>(
            scope.ServiceProvider.GetRequiredService<OrderingDbContext>(),
            o => o.UseMapper(explicitMapper));

        Assert.Same(explicitMapper, repo.ShiftRepositoryOptions.Mapper);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftMapper;
using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// How ShiftMapper reaches a repository's <c>Mapping(...)</c> configuration when a customized map is used
/// BEFORE any repository ran — a service mapping an invoice in a scope where no <c>InvoiceRepository</c> was
/// constructed yet.
///
/// <para>The generated map looks the customized member's value up by the type the lambda was written in. On a
/// miss ShiftMapper asks this resolver to make that type run its configuration; constructing the repository
/// does exactly that (<c>InitCommon</c> runs the lambda and calls <see cref="IMapper.Configure"/>), so the
/// retry that follows finds the value. Two shapes of configurator:</para>
/// <list type="bullet">
///   <item>A REPOSITORY class — the lambda sits in its base-constructor builder. It is registered scoped by
///   <c>RegisterShiftRepositories</c>; resolving it is enough.</item>
///   <item>An ENTITY implementing <see cref="IConfiguresShiftRepository{TEntity, TListDTO, TViewDTO}"/> — the
///   lambda sits in <c>ConfigureRepository</c>, which the built-in repository runs. That repository is closed
///   over the host's <c>DbContext</c>, found through the <see cref="DbContextOptions"/> the host registered
///   (the one <c>AddDbContext</c> adds; a host with several contexts gets the first, which is the framework's
///   convention for the built-in repository too).</item>
/// </list>
/// </summary>
internal sealed class ShiftEntityConfiguratorResolver : IShiftMapperConfiguratorResolver
{
    public bool TryApply(Type configurator, IServiceProvider services)
    {
        if (typeof(ShiftRepositoryBase).IsAssignableFrom(configurator))
            return services.GetService(configurator) is not null;

        var configures = configurator.GetInterfaces().FirstOrDefault(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConfiguresShiftRepository<,,>));

        if (configures is null)
            return false;

        if (services.GetService<DbContextOptions>()?.ContextType is not { } db || !typeof(ShiftDbContext).IsAssignableFrom(db))
            return false;

        var triple = configures.GetGenericArguments();
        var repository = typeof(ShiftRepository<,,,>).MakeGenericType(db, triple[0], triple[1], triple[2]);

        return services.GetService(repository) is not null;
    }
}

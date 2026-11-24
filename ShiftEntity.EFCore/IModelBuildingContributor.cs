using Microsoft.EntityFrameworkCore;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// Implement this interface to contribute entity configurations to <see cref="ShiftDbContext.OnModelCreating"/>.
/// Register implementations in DI and they will be automatically invoked during model building.
/// </summary>
public interface IModelBuildingContributor
{
    void Configure(ModelBuilder modelBuilder);

    /// <summary>
    /// The save handlers that belong to the entity types this module contributes. They run in every host whose
    /// context the module contributes to, with nothing for the host to register. Called once per context type and
    /// host, like <see cref="Configure"/>.
    /// </summary>
    /// <remarks>
    /// Among handlers with the same order, a context's own run first, then the modules', then the host's. A handler
    /// declared here is created as one the context declares: from the services of the saving context, or with
    /// <c>new</c> when its only constructor takes nothing, which it then needs no services for.
    /// </remarks>
    void ConfigureSaveHandlers(SaveHandlerSet handlers) { }
}

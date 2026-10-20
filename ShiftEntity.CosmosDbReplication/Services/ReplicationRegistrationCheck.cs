using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;

/// <summary>
/// Builds the save-time replication registrations once when the host starts, in a scope of its own, so that a
/// registration the framework refuses (<see cref="ReplicationRegistrationException"/>, for example <c>Replicate</c>
/// together with <c>ReplicateTree</c>) stops the host. The registrations are otherwise built at the first replicated
/// save, after that save is committed, and a refused one then fails every replicated save of the process.
/// </summary>
/// <remarks>
/// <para>
/// Any other failure only logs a warning and lets the host start: a registration can depend on its environment, such
/// as a Cosmos DB setting a development or test environment lacks, or on something only a request provides. Such a
/// registration is built again at the first replicated save and fails there, as it did before this check.
/// </para>
/// <para>
/// Both replication registrations add it, once. It runs wherever a generic host starts its hosted services: ASP.NET
/// Core and the isolated Azure Functions worker. Without a host, as with a service provider a test builds itself, the
/// refusal still comes at the first replicated save.
/// </para>
/// </remarks>
internal sealed class ReplicationRegistrationCheck(IServiceScopeFactory scopes, ILogger<ReplicationRegistrationCheck> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            _ = scope.ServiceProvider.GetServices<ShiftEntityCosmosDbOptions>().ToList();
        }
        catch (ReplicationRegistrationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The Cosmos DB replication registrations could not be built when the host started. They are " +
                "built again at the first replicated save.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

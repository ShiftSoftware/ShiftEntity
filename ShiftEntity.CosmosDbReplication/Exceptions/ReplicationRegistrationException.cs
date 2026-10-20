namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;

/// <summary>
/// A replication registration the framework refuses, such as one that registers both <c>Replicate</c> and
/// <c>ReplicateTree</c> for an entity. It is thrown where the registration is set up, and when the host builds the
/// registrations at its start, it stops the host. It derives from <see cref="InvalidOperationException"/>, which the
/// refusals threw before, so code that catches that still catches it.
/// </summary>
public sealed class ReplicationRegistrationException : InvalidOperationException
{
    public ReplicationRegistrationException(string message) : base(message)
    {
    }
}

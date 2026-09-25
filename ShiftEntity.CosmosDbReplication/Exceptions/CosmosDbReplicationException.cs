using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;

/// <summary>A catch-up run in which rows failed. <see cref="Result"/> lists them.</summary>
public class CosmosDbReplicationException : Exception
{
    public CosmosDbReplicationException(CosmosDbReplicationResult result) : base(result.ToString())
    {
        Result = result;
    }

    public CosmosDbReplicationResult Result { get; }
}

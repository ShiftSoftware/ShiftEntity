
namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;

public class PartitionKeyChangedException : Exception
{
    public PartitionKeyChangedException(string? message = null) : base(message)
    {
    }
}

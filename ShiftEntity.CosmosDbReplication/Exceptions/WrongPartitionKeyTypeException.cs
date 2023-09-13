
namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;

public class WrongPartitionKeyTypeException : Exception
{
    public WrongPartitionKeyTypeException(string? message = null) : base(message)
    {
    }
}


namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;

public class WrongPartitionKeyNameException : Exception
{
    public WrongPartitionKeyNameException(string? message = null) : base(message)
    {
    }
}

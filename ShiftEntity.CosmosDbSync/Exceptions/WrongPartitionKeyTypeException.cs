using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.CosmosDbSync.Exceptions;

public class WrongPartitionKeyTypeException : Exception
{
    public WrongPartitionKeyTypeException(string? message = null) : base(message)
    {
    }
}

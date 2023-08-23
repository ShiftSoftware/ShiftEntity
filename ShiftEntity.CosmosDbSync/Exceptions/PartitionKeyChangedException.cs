using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.CosmosDbSync.Exceptions;

public class PartitionKeyChangedException: Exception
{
    public PartitionKeyChangedException(string? message=null): base(message)
    {
    }
}

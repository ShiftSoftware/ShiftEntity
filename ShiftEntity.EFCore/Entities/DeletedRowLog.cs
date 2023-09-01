using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.EFCore.Entities;

[Index(nameof(LastSyncDate))]
public class DeletedRowLog
{
    public long ID { get; set; }
    public long RowID { get; set; }
    public string? PartitionKeyValue { get; set; } = default!;
    public PartitionKeyTypes PartitionKeyType { get; set; }
    public string EntityName { get; set; } = default!;
    public DateTime? LastSyncDate { get; set; }
}

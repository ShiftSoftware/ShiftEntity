using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore.Entities;

[Index(nameof(LastReplicationDate))]
public class DeletedRowLog
{
    public long ID { get; set; }
    public long RowID { get; set; }
    public string? PartitionKeyValue { get; set; } = default!;
    public PartitionKeyTypes PartitionKeyType { get; set; }
    public string EntityName { get; set; } = default!;
    public DateTime? LastReplicationDate { get; set; }
}

using ShiftSoftware.ShiftEntity.Model.Replication;
using System;
using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Core.Tagging;

public class Tag : ShiftEntity<Tag>, IShiftEntityReplication
{
    [Required]
    [MaxLength(128)]
    public string Name { get; set; } = default!;

    [MaxLength(32)]
    public string? Color { get; set; }

    [MaxLength(512)]
    public string? Description { get; set; }

    [MaxLength(128)]
    public string? IntegrationID { get; set; }

    // Replication bookkeeping columns (written only by the replication pipeline's MarkReplicated).
    // Implementing IShiftEntityReplication lets a Tag be set up for Cosmos replication so tag edits
    // can refresh their denormalized copies embedded in other documents.
    public DateTimeOffset? LastReplicationDate { get; set; }
    public string? LastReplicationStamp { get; set; }
}

using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.Text;

namespace ShiftSoftware.ShiftEntity.Model.Replication;

public abstract class ReplicationModel : ShiftEntityDTO
{
    public string id { get; set; }
}

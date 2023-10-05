using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.Text;

namespace ShiftSoftware.ShiftEntity.Model.Replication;

public abstract class ReplicationBaseModel : ShiftEntityDTO
{
    public string id { get; set; }
}

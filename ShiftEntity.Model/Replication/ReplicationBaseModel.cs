﻿using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.Text;

namespace ShiftSoftware.ShiftEntity.Model.Replication;

public abstract class ReplicationBaseModel
{
    [System.Text.Json.Serialization.JsonPropertyName(nameof(id))]
    [Newtonsoft.Json.JsonProperty(nameof(id))]
    public string id { get; set; }

    public bool IsDeleted { get; set; }
}

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public class ShiftEntityBase
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [System.Text.Json.Serialization.JsonPropertyName(nameof(ID))]
    [Newtonsoft.Json.JsonProperty(nameof(ID))]
    public long ID { get; set; }
}

public class ShiftEntityBase<EntityType> : ShiftEntityBase where EntityType : class
{
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Dtos;

public class ODataDTO<TValue>
{
    public Int64 Count { get; set; }
    public TValue Value { get; set; }
}

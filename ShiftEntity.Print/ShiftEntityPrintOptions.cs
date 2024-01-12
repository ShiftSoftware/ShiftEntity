using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Print;

public class ShiftEntityPrintOptions
{
    public string SASTokenKey { get; set; } = default!;
    public int TokenExpirationInSeconds { get; set; } = 60;
}

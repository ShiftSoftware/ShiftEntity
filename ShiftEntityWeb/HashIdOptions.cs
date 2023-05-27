using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web;

public class HashIdOptions
{
    public bool AcceptUnencodedIds { get; set; }

    public string Salt { set; get; }
    public int MinHashLength { set; get; }
    public string? Alphabet { set; get; }
}

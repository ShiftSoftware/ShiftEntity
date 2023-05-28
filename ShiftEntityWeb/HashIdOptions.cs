using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web;

public class HashIdOptions
{
    public bool AcceptUnencodedIds { get; set; }

    public string UserIdsSalt { set; get; }
    public int UserIdsMinHashLength { set; get; }
    public string? UserIdsAlphabet { set; get; }
}

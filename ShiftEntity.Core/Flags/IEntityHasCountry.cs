using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Flags;

public interface IEntityHasCountry<Entity>
{
    long? CountryID { get; set; }
}

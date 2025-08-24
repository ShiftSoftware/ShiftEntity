using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Flags;

public interface IEntityHasCity<Entity>
    
{
    long? CityID { get; set; }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Dtos
{
    public class ShiftEntityListDto
    {
        public Guid ID { get; set; }
        public ICollection<RevisionDTO>? Revisions { get; set; }
    }
}

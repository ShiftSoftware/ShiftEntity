using Microsoft.EntityFrameworkCore;
using ShiftSoftware.EFCore.SqlServer;

namespace $rootnamespace$
{
    public class $fileinputname$Repository : 
    ShiftRepository<DB, $fileinputname$, $fileinputname$ListDTO, $fileinputname$DTO>,
    IShiftRepositoryAsync<$fileinputname$, $fileinputname$ListDTO, $fileinputname$DTO>
    {
       public $fileinputname$Repository()
       {
       }
    }
}
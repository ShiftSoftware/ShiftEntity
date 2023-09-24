using AutoMapper;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;

namespace $rootnamespace$
{
    public class $fileinputname$Repository : 
    ShiftRepository<DB, $fileinputname$, $fileinputname$ListDTO, $fileinputname$DTO, $fileinputname$DTO>,
    IShiftRepositoryAsync<$fileinputname$, $fileinputname$ListDTO, $fileinputname$DTO>
    {
       public $fileinputname$Repository(DB dB, IMapper mapper) : base(dB, dB.$fileinputname$s, mapper)
       {
       }
    }
}
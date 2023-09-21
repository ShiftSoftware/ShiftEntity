using ShiftSoftware.ShiftEntity.Web;
using Microsoft.AspNetCore.Mvc;

namespace $rootnamespace$
{
    [Route("api/[controller]")]
    public class $fileinputname$Controller : ShiftEntitySecureControllerAsync<$fileinputname$Repository, $fileinputname$, $fileinputname$ListDTO, $fileinputname$DTO>
    {
        private readonly $fileinputname$Repository $fileinputname$Repository;
        public $fileinputname$Controller($fileinputname$Repository $fileinputname$Repository) 
        {
            this.$fileinputname$Repository = $fileinputname$Repository;
        }
    }
}
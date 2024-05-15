using EntityFrameworkCore.Triggered;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Core;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Triggers;

internal class SetUserAndCompanyInfoTrigger<Entity> : IBeforeSaveTrigger<Entity> where Entity : ShiftEntity<Entity>
{
    private readonly IHttpContextAccessor? http;

    public SetUserAndCompanyInfoTrigger(IHttpContextAccessor? http)
    {
        this.http = http;
    }

    //private long? GetUserId()
    //{
    //    //Services.ShiftEntityHashIds.Decode<ShiftIdentity.Core.DTOs.User.UserDTO>(id);

    //    var userIdClaim = http?.HttpContext?.User.Claims?.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier);
    //    long userId = 0;
    //    if (userIdClaim == null || !long.TryParse(userIdClaim?.Value, out userId))
    //        return null;
    //    else
    //        return userId;
    //}

    public Task BeforeSave(ITriggerContext<Entity> context, CancellationToken cancellationToken)
    {
        long? userId = http!.HttpContext!.GetUserID();

        if (context.ChangeType == ChangeType.Added)
        {
            context.Entity.CreatedByUserID = userId;
            context.Entity.LastSavedByUserID = userId;

            if (context.Entity.GetType().GetCustomAttributes<ShiftIdentity.Core.DontSetCompanyInfoOnThisEntityWithAutoTrigger>().Count() == 0)
            {
                long? regionId = http!.HttpContext!.GetRegionID();
                long? companyId = http!.HttpContext!.GetCompanyID();
                long? companyBranchId = http!.HttpContext!.GetCompanyBranchID();

                //if (context.Entity.RegionID is null)
                //    context.Entity.RegionID = regionId;

                //if (context.Entity.CompanyID is null)
                //    context.Entity.CompanyID = companyId;

                //if (context.Entity.CompanyBranchID is null)
                //    context.Entity.CompanyBranchID = companyBranchId;
            }
        }

        if (context.ChangeType == ChangeType.Modified)
        {
            context.Entity.LastSavedByUserID = userId;
        }

        return Task.CompletedTask;
    }
}

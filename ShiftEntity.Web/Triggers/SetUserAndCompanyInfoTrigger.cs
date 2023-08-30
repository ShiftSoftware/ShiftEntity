using EntityFrameworkCore.Triggered;
using Microsoft.AspNetCore.Http;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Web.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Triggers;

internal class SetUserAndCompanyInfoTrigger<Entity> : IBeforeSaveTrigger<Entity>
    where Entity : ShiftEntity<Entity>
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
        long? regionId = http!.HttpContext!.GetRegionID();
        long? companyId = http!.HttpContext!.GetCompanyID();
        long? companyBranchId = http!.HttpContext!.GetCompanyBranchID();
        long? userId = http!.HttpContext!.GetUserID();

        if (context.ChangeType == ChangeType.Added)
        {
            context.Entity.CreatedByUserID = userId;
            context.Entity.LastSavedByUserID = userId;

            context.Entity.RegionID = regionId;
            context.Entity.CompanyID = companyId;
            context.Entity.CompanyBranchID = companyBranchId;
        }

        if (context.ChangeType == ChangeType.Modified)
        {
            context.Entity.LastSavedByUserID = userId;
        }

        return Task.CompletedTask;
    }
}

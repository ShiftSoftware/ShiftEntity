using EntityFrameworkCore.Triggered;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Flags;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Triggers;

internal class SetUserAndCompanyInfoTrigger<Entity> : IBeforeSaveTrigger<Entity> where Entity : ShiftEntity<Entity>, new()
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
                long? cityId = http!.HttpContext!.GetCityID();

                if (typeof(Entity).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasRegion<Entity>))))
                {
                    var entityWithRegion = (IEntityHasRegion<Entity>)context.Entity;

                    if (entityWithRegion.RegionID is null)
                        entityWithRegion.RegionID = regionId;
                }

                if (typeof(Entity).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasCompany<Entity>))))
                {
                    var entityWithCompany = (IEntityHasCompany<Entity>)context.Entity;

                    if (entityWithCompany.CompanyID is null)
                        entityWithCompany.CompanyID = companyId;
                }

                if (typeof(Entity).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasCity<Entity>))))
                {
                    var entityWithCity = (IEntityHasCity<Entity>)context.Entity;

                    if (entityWithCity.CityID is null)
                        entityWithCity.CityID = cityId;
                }

                if (typeof(Entity).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasCompanyBranch<Entity>))))
                {
                    var entityWithCompanyBranch = (IEntityHasCompanyBranch<Entity>)context.Entity;

                    if (entityWithCompanyBranch.CompanyBranchID is null)
                        entityWithCompanyBranch.CompanyBranchID = companyBranchId;
                }
            }
        }

        if (context.ChangeType == ChangeType.Modified)
        {
            context.Entity.LastSavedByUserID = userId;
        }

        return Task.CompletedTask;
    }
}

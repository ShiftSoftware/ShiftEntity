using AutoMapper;
using EntityFrameworkCore.Triggered;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Flags;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System.Text;

namespace ShiftSoftware.ShiftEntity.EFCore;

public class ShiftRepository<DB, EntityType, ListDTO, ViewAndUpsertDTO> :
    ShiftRepositoryBase,
    IShiftRepositoryAsync<EntityType, ListDTO, ViewAndUpsertDTO>
    where DB : ShiftDbContext
    where EntityType : ShiftEntity<EntityType>, new()
    where ListDTO : ShiftEntityDTOBase
{
    public readonly DB db;
    internal DbSet<EntityType> dbSet;
    public readonly IMapper mapper;
    private readonly IDefaultDataLevelAccess defaultDataLevelAccess;

    public ShiftRepository(DB db, Action<ShiftRepositoryOptions<EntityType>>? shiftRepositoryBuilder = null)
    {
        this.db = db;
        this.dbSet = db.Set<EntityType>();
        this.mapper = db.GetService<IMapper>();
        this.defaultDataLevelAccess = db.GetService<IDefaultDataLevelAccess>();

        if (shiftRepositoryBuilder is not null)
        {
            this.ShiftRepositoryOptions = new ShiftRepositoryOptions<EntityType>();

            shiftRepositoryBuilder.Invoke(this.ShiftRepositoryOptions);
        }
    }

    public virtual IQueryable<ListDTO> OdataList(IQueryable<EntityType>? queryable = null)
    {
        if (queryable is null)
            queryable = GetIQueryable();

        return mapper.ProjectTo<ListDTO>(queryable.AsNoTracking());
    }

    public virtual ValueTask<ViewAndUpsertDTO> ViewAsync(EntityType entity)
    {
        return new ValueTask<ViewAndUpsertDTO>(mapper.Map<ViewAndUpsertDTO>(entity));
    }

    public virtual ValueTask<EntityType> UpsertAsync(EntityType entity, ViewAndUpsertDTO dto, ActionTypes actionType, long? userId = null, Guid? idempotencyKey = null)
    {
        entity = mapper.Map(dto, entity);

        if (idempotencyKey != null)
        {
            (entity as IEntityHasIdempotencyKey<EntityType>)!.IdempotencyKey = idempotencyKey;
        }

        return new ValueTask<EntityType>(entity);
    }
    
    public Message? ResponseMessage { get; set; }
    public Dictionary<string, object>? AdditionalResponseData { get; set; }

    public ShiftRepositoryOptions<EntityType>? ShiftRepositoryOptions { get; set; }

    //public virtual EntityType Find(long id, DateTime? asOf = null, List<string> includes = null)
    //{
    //    return GetIQueryable(asOf, includes)
    //        .FirstOrDefault(x =>
    //            EF.Property<long>(x, nameof(ShiftEntity<EntityType>.ID)) == id
    //        );
    //}

    //public virtual EntityType Find(long id, DateTime? asOf = null, params Action<IncludeOperations<EntityType>>[] includeOperations)
    //{
    //    List<string> includes = new();

    //    foreach (var i in includeOperations)
    //    {
    //        IncludeOperations<EntityType> operation = new();
    //        i.Invoke(operation);
    //        includes.Add(operation.Includes);
    //    }

    //    return Find(id, asOf, includes);
    //}

    private async Task<EntityType?> BaseFindAsync(long id, DateTimeOffset? asOf = null, Guid? idempotencyKey = null)
    {
        List<string>? includes = null;

        if (ShiftRepositoryOptions is not null)
        {
            includes = new();

            foreach (var i in ShiftRepositoryOptions.IncludeOperations)
            {
                IncludeOperations<EntityType> operation = new();
                i.Invoke(operation);
                includes.Add(operation.Includes);
            }
        }

        var q = GetIQueryable(asOf, includes);

        EntityType? entity = null;

        if (id != 0)
        {
            entity = await q.FirstOrDefaultAsync(x =>
                EF.Property<long>(x, nameof(ShiftEntity<EntityType>.ID)) == id
            );
        }
        else if (idempotencyKey != null)
        {
            entity = await q.FirstOrDefaultAsync(x =>
                EF.Property<Guid?>(x, nameof(IEntityHasIdempotencyKey<EntityType>.IdempotencyKey)) == idempotencyKey
            );
        }

        if (entity is not null && includes?.Count > 0)
            entity.ReloadAfterSave = true;

        return entity;
    }

    public virtual async Task<EntityType?> FindAsync(long id, DateTimeOffset? asOf = null)
    {
        return await BaseFindAsync(id, asOf);
    }

    public virtual async Task<EntityType?> FindByIdempotencyKeyAsync(Guid idempotencyKey)
    {
        return await BaseFindAsync(0, null, idempotencyKey);
    }

    public virtual IQueryable<EntityType> GetIQueryable(DateTimeOffset? asOf = null, List<string>? includes = null)
    {
        var query = asOf is null ? dbSet.AsQueryable() : dbSet.TemporalAsOf(asOf.Value.UtcDateTime);

        if (includes is not null)
        {
            foreach (var include in includes)
                query = query.Include(include);
        }

        query = this.ApplyDefaultDataLevelFilters(query);

        query = this.ApplyGloballFilters(query);
        
        return query;
    }

    private IQueryable<EntityType> ApplyGloballFilters(IQueryable<EntityType> query)
    {
        return query;

        if (this.ShiftRepositoryOptions is null || this.ShiftRepositoryOptions.GlobalFilters.Count == 0)
            return query;

        foreach (var filter in this.ShiftRepositoryOptions.GlobalFilters)
        {
            var expression = filter.GetFilterExpression<EntityType>();

            if (expression != null)
                query = query.Where(expression);
        }

        return query;
    }

    private IQueryable<EntityType> ApplyDefaultDataLevelFilters(IQueryable<EntityType> query)
    {
        return query;

        //var accessibleCountriesTypeAuth = typeAuthService.GetAccessibleItems(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Countries, x => x == TypeAuth.Core.Access.Read, this.HttpContext.GetHashedCountryID()!);
        //var accessibleRegionsTypeAuth = typeAuthService.GetAccessibleItems(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Regions, x => x == TypeAuth.Core.Access.Read, this.HttpContext.GetHashedRegionID()!);
        //var accessibleCompaniesTypeAuth = typeAuthService.GetAccessibleItems(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Companies, x => x == TypeAuth.Core.Access.Read, this.HttpContext.GetHashedCompanyID()!);
        //var accessibleBranchesTypeAuth = typeAuthService.GetAccessibleItems(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Branches, x => x == TypeAuth.Core.Access.Read, this.HttpContext.GetHashedCompanyBranchID()!);
        //var accessibleBrandsTypeAuth = typeAuthService.GetAccessibleItems(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Brands, x => x == TypeAuth.Core.Access.Read);
        //var accessibleCitiesTypeAuth = typeAuthService.GetAccessibleItems(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Cities, x => x == TypeAuth.Core.Access.Read, this.HttpContext.GetHashedCityID()!);
        //var accessibleTeamsTypeAuth = typeAuthService.GetAccessibleItems(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Teams, x => x == TypeAuth.Core.Access.Read, this.HttpContext.GetHashedTeamIDs()?.ToArray());

        //List<long?>? accessibleCountries = accessibleCountriesTypeAuth.WildCard ? null : accessibleCountriesTypeAuth.AccessibleIds.Select(x => x == TypeAuthContext.EmptyOrNullKey ? null : (long?)ShiftEntityHashIdService.Decode<CountryDTO>(x)).ToList();
        //List<long?>? accessibleRegions = accessibleRegionsTypeAuth.WildCard ? null : accessibleRegionsTypeAuth.AccessibleIds.Select(x => x == TypeAuthContext.EmptyOrNullKey ? null : (long?)ShiftEntityHashIdService.Decode<RegionDTO>(x)).ToList();
        //List<long?>? accessibleCompanies = accessibleCompaniesTypeAuth.WildCard ? null : accessibleCompaniesTypeAuth.AccessibleIds.Select(x => x == TypeAuthContext.EmptyOrNullKey ? null : (long?)ShiftEntityHashIdService.Decode<CompanyDTO>(x)).ToList();
        //List<long?>? accessibleBranches = accessibleBranchesTypeAuth.WildCard ? null : accessibleBranchesTypeAuth.AccessibleIds.Select(x => x == TypeAuthContext.EmptyOrNullKey ? null : (long?)ShiftEntityHashIdService.Decode<CompanyBranchDTO>(x)).ToList();
        //List<long?>? accessibleBrands = accessibleBrandsTypeAuth.WildCard ? null : accessibleBrandsTypeAuth.AccessibleIds.Select(x => x == TypeAuthContext.EmptyOrNullKey ? null : (long?)ShiftEntityHashIdService.Decode<BrandDTO>(x)).ToList();
        //List<long?>? accessibleCities = accessibleCitiesTypeAuth.WildCard ? null : accessibleCitiesTypeAuth.AccessibleIds.Select(x => x == TypeAuthContext.EmptyOrNullKey ? null : (long?)ShiftEntityHashIdService.Decode<CityDTO>(x)).ToList();
        //List<long?>? accessibleTeams = accessibleTeamsTypeAuth.WildCard ? null : accessibleTeamsTypeAuth.AccessibleIds.Select(x => x == TypeAuthContext.EmptyOrNullKey ? null : (long?)ShiftEntityHashIdService.Decode<TeamDTO>(x)).ToList();

        List<long?>? accessibleCountries = this.defaultDataLevelAccess.GetAccessibleCountries();
        List<long?>? accessibleRegions = this.defaultDataLevelAccess.GetAccessibleRegions();
        List<long?>? accessibleCompanies = this.defaultDataLevelAccess.GetAccessibleCompanies();
        List<long?>? accessibleBranches = this.defaultDataLevelAccess.GetAccessibleBranches();
        List<long?>? accessibleBrands = this.defaultDataLevelAccess.GetAccessibleBrands();
        List<long?>? accessibleCities = this.defaultDataLevelAccess.GetAccessibleCities();
        List<long?>? accessibleTeams = this.defaultDataLevelAccess.GetAccessibleTeams();


        if (!(this.ShiftRepositoryOptions is not null && this.ShiftRepositoryOptions.DisableDefaultCountryFilter))
        {
            if (typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasCountry<EntityType>))))
                query = query.Where(x => accessibleCountries == null ? true : accessibleCountries.Contains((x as IEntityHasCountry<EntityType>)!.CountryID));
        }

        if (!(this.ShiftRepositoryOptions is not null && this.ShiftRepositoryOptions.DisableDefaultRegionFilter))
        {
            if (typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasRegion<EntityType>))))
                query = query.Where(x => accessibleRegions == null ? true : accessibleRegions.Contains((x as IEntityHasRegion<EntityType>)!.RegionID));
        }

        if (!(this.ShiftRepositoryOptions is not null && this.ShiftRepositoryOptions.DisableDefaultCompanyFilter))
        {
            if (typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasCompany<EntityType>))))
                query = query.Where(x => accessibleCompanies == null ? true : accessibleCompanies.Contains((x as IEntityHasCompany<EntityType>)!.CompanyID));
        }

        if (!(this.ShiftRepositoryOptions is not null && this.ShiftRepositoryOptions.DisableDefaultCompanyBranchFilter))
        {
            if (typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasCompanyBranch<EntityType>))))
                query = query.Where(x => accessibleBranches == null ? true : accessibleBranches.Contains((x as IEntityHasCompanyBranch<EntityType>)!.CompanyBranchID));
        }

        if (!(this.ShiftRepositoryOptions is not null && this.ShiftRepositoryOptions.DisableDefaultBrandFilter))
        {
            if (typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasBrand<EntityType>))))
                query = query.Where(x => accessibleBrands == null ? true : accessibleBrands.Contains((x as IEntityHasBrand<EntityType>)!.BrandID));
        }

        if (!(this.ShiftRepositoryOptions is not null && this.ShiftRepositoryOptions.DisableDefaultCityFilter))
        {
            if (typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasCity<EntityType>))))
                query = query.Where(x => accessibleCities == null ? true : accessibleCities.Contains((x as IEntityHasCity<EntityType>)!.CityID));
        }

        if (!(this.ShiftRepositoryOptions is not null && this.ShiftRepositoryOptions.DisableDefaultTeamFilter))
        {
            if (typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasTeam<EntityType>))))
                query = query.Where(x => accessibleTeams == null ? true : accessibleTeams.Contains((x as IEntityHasTeam<EntityType>)!.TeamID));
        }

        return query;
    }

    public virtual IQueryable<RevisionDTO> GetRevisionsAsync(long id)
    {
        return dbSet
                .TemporalAll()
                .AsNoTracking()
                .Where(x => EF.Property<long>(x, nameof(ShiftEntity<EntityType>.ID)) == id)
                .Select(x => new
                {
                    ID = EF.Property<long>(x, nameof(ShiftEntity<EntityType>.ID)),
                    ValidFrom = EF.Property<DateTime>(x, "PeriodStart"),
                    ValidTo = EF.Property<DateTime>(x, "PeriodEnd"),
                    SavedByUserID = EF.Property<long?>(x, nameof(ShiftEntity<EntityType>.LastSavedByUserID)),
                })
                .Select(x => new RevisionDTO
                {
                    ID = x.ID.ToString(),
                    ValidFrom = x.ValidFrom,
                    ValidTo = x.ValidTo,
                    SavedByUserID = x.SavedByUserID == null ? null : x.SavedByUserID.ToString(),
                });
    }

    public virtual void Add(EntityType entity)
    {
        if (this.ShiftRepositoryOptions is not null && this.ShiftRepositoryOptions.IncludeOperations.Count > 0)
        {
            entity.ReloadAfterSave = true;
        }

        dbSet.Add(entity);
    }

    public virtual async Task SaveChangesAsync(bool raiseBeforeCommitTriggers = false)
    {
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                if (typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasUniqueHash<EntityType>))))
                {
                    var entryWithUniqueHash = entry.Entity as IEntityHasUniqueHash<EntityType>;

                    if (entryWithUniqueHash is null)
                        continue;

                    var uniqueHash = entryWithUniqueHash.CalculateUniqueHash();

                    if (uniqueHash != null)
                    {
                        using var sha256 = System.Security.Cryptography.SHA256.Create();
                        
                        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(uniqueHash));

                        entry.Property("UniqueHash").CurrentValue = hashBytes;
                    }
                }
            }
        }

        if (raiseBeforeCommitTriggers)
        {
            using var tx = db.Database.BeginTransaction();
            var triggerService = db.GetService<ITriggerService>(); // ITriggerService is responsible for creating now trigger sessions (see below)
            var triggerSession = triggerService.CreateSession(db); // A trigger session keeps track of all changes that are relevant within that session. e.g. RaiseAfterSaveTriggers will only raise triggers on changes it discovered within this session (through RaiseBeforeSaveTriggers)

            try
            {
                await db.SaveChangesAsync();
                await triggerSession.RaiseBeforeCommitTriggers();
                await tx.CommitAsync();
                await triggerSession.RaiseAfterCommitTriggers();
            }
            catch
            {
                await triggerSession.RaiseBeforeRollbackTriggers();
                await tx.RollbackAsync();
                await triggerSession.RaiseAfterRollbackTriggers();
                throw;
            }

        }
        else
            await db.SaveChangesAsync();
    }

    public virtual ValueTask<EntityType> DeleteAsync(EntityType entity, bool isHardDelete = false, long? userId = null)
    {
        if (isHardDelete)
            dbSet.Remove(entity);
        else
            entity.MarkAsDeleted();


        return new ValueTask<EntityType>(entity);
    }

    public virtual Task<Stream> PrintAsync(string id)
    {
        throw new NotImplementedException();
    }
}

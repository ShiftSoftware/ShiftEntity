using Newtonsoft.Json;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.ShiftIdentity.Core;
using ShiftSoftware.TypeAuth.Core;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;

/// <summary>
/// The Phase 4 (standard-profile parity) entity: carries the real <see cref="IEntityHasCompany{Entity}"/> marker the
/// legacy <c>DefaultDataLevelAccess</c> keys off — distinct from the engine-level POCO <see cref="Vehicle"/> (which
/// deliberately has no markers) and the repository-level <see cref="VehicleEntity"/>. Uses the same generic company
/// cast (DealerA=1 / DealerB=2 / Intermediary=4) so parity tests read like the rest of the scenario.
/// </summary>
public class CompanyScopedEntity : ShiftEntity<CompanyScopedEntity>, IEntityHasCompany<CompanyScopedEntity>
{
    public string Name { get; set; } = "";
    public long? CompanyID { get; set; }

    /// <summary>One row per interesting company value: two dealers, the Intermediary, and a null FK.</summary>
    public static List<CompanyScopedEntity> Samples() => new()
    {
        new() { ID = 1, Name = "DealerA's row", CompanyID = Companies.DealerA },
        new() { ID = 2, Name = "DealerB's row", CompanyID = Companies.DealerB },
        new() { ID = 3, Name = "Intermediary's row", CompanyID = Companies.Intermediary },
        new() { ID = 4, Name = "Row with no company", CompanyID = null },
    };
}

/// <summary>
/// Builds a real <see cref="TypeAuthContext"/> scoped on the <b>real</b>
/// <see cref="ShiftIdentityActions.DataLevelAccess.Companies"/> action — the one both the legacy
/// <c>DefaultDataLevelAccess</c> and the v2 standard profile authorize against — so the Phase 4 parity tests run
/// both arms over the identical grants. The twin of <see cref="ScopedTypeAuth"/> (which scopes the
/// ShiftIdentity-agnostic <see cref="VehicleDataLevel"/> tree); the access tree's JSON keys are the class/field
/// names: <c>ShiftIdentityActions → DataLevelAccess → Companies</c>.
/// </summary>
public static class IdentityScopedTypeAuth
{
    private static readonly Access[] DefaultAccesses = { Access.Read, Access.Write, Access.Delete };

    /// <summary>No access to any company (and no wildcard) — the caller sees nothing.</summary>
    public static TypeAuthContext None() => Build("{}");

    /// <summary>Wildcard: access to every company at the given levels (default Read+Write+Delete).</summary>
    public static TypeAuthContext Wildcard(params Access[] accesses)
        => BuildCompanies(Defaulted(accesses));

    /// <summary>Access scoped to the given company ids at the given levels (default Read+Write+Delete).</summary>
    public static TypeAuthContext ToCompanies(IEnumerable<long> companyIds, params Access[] accesses)
    {
        var levels = Defaulted(accesses);
        var byId = new Dictionary<string, object>();
        foreach (var id in companyIds)
            byId[id.ToString()] = levels;
        return BuildCompanies(byId);
    }

    /// <summary>Convenience overload for a single company id.</summary>
    public static TypeAuthContext ToCompany(long companyId, params Access[] accesses)
        => ToCompanies(new[] { companyId }, accesses);

    /// <summary>
    /// Access scoped to the given raw accessible-id <em>strings</em>, granted verbatim — so a test can grant a
    /// hashid-<em>encoded</em> id (e.g. <c>"C4"</c>) and verify both arms route it through the company type-key.
    /// </summary>
    public static TypeAuthContext ToCompanyKeys(IEnumerable<string> keys, params Access[] accesses)
    {
        var levels = Defaulted(accesses);
        var byKey = new Dictionary<string, object>();
        foreach (var key in keys)
            byKey[key] = levels;
        return BuildCompanies(byKey);
    }

    /// <summary>"Own company" access: grants the self-reference key, resolved from the caller's company claim.</summary>
    public static TypeAuthContext Self(params Access[] accesses)
        => BuildCompanies(new Dictionary<string, object> { [TypeAuthContext.SelfReferenceKey] = Defaulted(accesses) });

    /// <summary>Access to rows whose company FK is null (the empty/null sentinel).</summary>
    public static TypeAuthContext NullCompany(params Access[] accesses)
        => BuildCompanies(new Dictionary<string, object> { [TypeAuthContext.EmptyOrNullKey] = Defaulted(accesses) });

    private static Access[] Defaulted(Access[] accesses) => accesses.Length == 0 ? DefaultAccesses : accesses;

    // companiesNode is either an Access[] (→ wildcard list) or a Dictionary<id, Access[]> (→ scoped ids).
    private static TypeAuthContext BuildCompanies(object companiesNode)
    {
        var tree = new Dictionary<string, object>
        {
            [nameof(ShiftIdentityActions)] = new Dictionary<string, object>
            {
                [nameof(ShiftIdentityActions.DataLevelAccess)] = new Dictionary<string, object>
                {
                    [nameof(ShiftIdentityActions.DataLevelAccess.Companies)] = companiesNode,
                },
            },
        };
        return Build(JsonConvert.SerializeObject(tree));
    }

    private static TypeAuthContext Build(string accessTreeJson)
        => new TypeAuthContextBuilder()
            .AddAccessTree(accessTreeJson)
            .AddActionTree<ShiftIdentityActions>()
            .Build();
}

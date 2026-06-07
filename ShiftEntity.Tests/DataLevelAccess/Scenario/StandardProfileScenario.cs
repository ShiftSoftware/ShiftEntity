using Newtonsoft.Json;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.ShiftIdentity.Core;
using ShiftSoftware.TypeAuth.Core;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;

/// <summary>
/// The Phase 4 (standard-profile parity) entity: carries the real marker interfaces the legacy
/// <c>DefaultDataLevelAccess</c> keys off — one column per migrated dimension, growing a marker per slice
/// (4.1 Company, 4.2 Country, …) — distinct from the engine-level POCO <see cref="Vehicle"/> (deliberately
/// marker-free) and the repository-level <see cref="VehicleEntity"/>. The parity tests isolate one dimension at a
/// time by disabling every other dimension's legacy flag, so the unused columns staying null never interferes.
/// </summary>
public class StandardScopedEntity : ShiftEntity<StandardScopedEntity>,
    IEntityHasCountry<StandardScopedEntity>,
    IEntityHasRegion<StandardScopedEntity>,
    IEntityHasCompany<StandardScopedEntity>,
    IEntityHasCompanyBranch<StandardScopedEntity>,
    IEntityHasCity<StandardScopedEntity>
{
    public string Name { get; set; } = "";
    public long? CountryID { get; set; }
    public long? RegionID { get; set; }
    public long? CompanyID { get; set; }
    public long? CompanyBranchID { get; set; }
    public long? CityID { get; set; }

    /// <summary>
    /// One row per interesting value of <em>one</em> dimension's column (set via <paramref name="setKey"/>):
    /// ids 1 and 2, the canonical in-scope id 4, and a null FK. The other dimensions' columns stay null.
    /// </summary>
    public static List<StandardScopedEntity> Samples(Action<StandardScopedEntity, long?> setKey)
    {
        var rows = new List<StandardScopedEntity>
        {
            new() { ID = 1, Name = "id-1 row" },
            new() { ID = 2, Name = "id-2 row" },
            new() { ID = 3, Name = "id-4 row (canonical in-scope)" },
            new() { ID = 4, Name = "null-FK row" },
        };
        setKey(rows[0], 1);
        setKey(rows[1], 2);
        setKey(rows[2], 4);
        setKey(rows[3], null);
        return rows;
    }
}

/// <summary>
/// Builds a real <see cref="TypeAuthContext"/> scoped on a <b>real</b>
/// <c>ShiftIdentityActions.DataLevelAccess.*</c> action — the ones both the legacy <c>DefaultDataLevelAccess</c>
/// and the v2 standard profile authorize against — so the Phase 4 parity tests run both arms over identical
/// grants. The dimension-generalized twin of <see cref="ScopedTypeAuth"/> (which scopes the ShiftIdentity-agnostic
/// <see cref="VehicleDataLevel"/> tree): every method takes the action's <em>field name</em> (e.g.
/// <c>nameof(ShiftIdentityActions.DataLevelAccess.Companies)</c>), because the access tree's JSON keys are the
/// class/field names — <c>ShiftIdentityActions → DataLevelAccess → {action}</c>.
/// </summary>
public static class IdentityScopedTypeAuth
{
    private static readonly Access[] DefaultAccesses = { Access.Read, Access.Write, Access.Delete };

    /// <summary>No access to anything (and no wildcard) — the caller sees nothing.</summary>
    public static TypeAuthContext None() => Build("{}");

    /// <summary>Wildcard on the action: access to every id at the given levels (default Read+Write+Delete).</summary>
    public static TypeAuthContext Wildcard(string action, params Access[] accesses)
        => BuildAction(action, Defaulted(accesses));

    /// <summary>Access scoped to the given ids at the given levels (default Read+Write+Delete).</summary>
    public static TypeAuthContext ToIds(string action, IEnumerable<long> ids, params Access[] accesses)
    {
        var levels = Defaulted(accesses);
        var byId = new Dictionary<string, object>();
        foreach (var id in ids)
            byId[id.ToString()] = levels;
        return BuildAction(action, byId);
    }

    /// <summary>Convenience overload for a single id.</summary>
    public static TypeAuthContext ToId(string action, long id, params Access[] accesses)
        => ToIds(action, new[] { id }, accesses);

    /// <summary>
    /// Access scoped to the given raw accessible-id <em>strings</em>, granted verbatim — so a test can grant a
    /// hashid-<em>encoded</em> id (e.g. <c>"K4"</c>) and verify both arms route it through the dimension's type-key.
    /// </summary>
    public static TypeAuthContext ToKeys(string action, IEnumerable<string> keys, params Access[] accesses)
    {
        var levels = Defaulted(accesses);
        var byKey = new Dictionary<string, object>();
        foreach (var key in keys)
            byKey[key] = levels;
        return BuildAction(action, byKey);
    }

    /// <summary>"Own id" access: grants the self-reference key, resolved from the caller's dimension claim.</summary>
    public static TypeAuthContext Self(string action, params Access[] accesses)
        => BuildAction(action, new Dictionary<string, object> { [TypeAuthContext.SelfReferenceKey] = Defaulted(accesses) });

    /// <summary>Access to rows whose dimension FK is null (the empty/null sentinel).</summary>
    public static TypeAuthContext NullKey(string action, params Access[] accesses)
        => BuildAction(action, new Dictionary<string, object> { [TypeAuthContext.EmptyOrNullKey] = Defaulted(accesses) });

    /// <summary>Grants on several actions at once (for the AND-composition tests) — each value built like <see cref="ToIds"/>.</summary>
    public static TypeAuthContext ToIdsOnActions(IReadOnlyDictionary<string, IEnumerable<long>> idsByAction, params Access[] accesses)
    {
        var levels = Defaulted(accesses);
        var actions = new Dictionary<string, object>();
        foreach (var (action, ids) in idsByAction)
        {
            var byId = new Dictionary<string, object>();
            foreach (var id in ids)
                byId[id.ToString()] = levels;
            actions[action] = byId;
        }
        return BuildTree(actions);
    }

    private static Access[] Defaulted(Access[] accesses) => accesses.Length == 0 ? DefaultAccesses : accesses;

    // actionNode is either an Access[] (→ wildcard list) or a Dictionary<id, Access[]> (→ scoped ids).
    private static TypeAuthContext BuildAction(string action, object actionNode)
        => BuildTree(new Dictionary<string, object> { [action] = actionNode });

    private static TypeAuthContext BuildTree(Dictionary<string, object> actionNodes)
    {
        var tree = new Dictionary<string, object>
        {
            [nameof(ShiftIdentityActions)] = new Dictionary<string, object>
            {
                [nameof(ShiftIdentityActions.DataLevelAccess)] = actionNodes,
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

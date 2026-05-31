using Newtonsoft.Json;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;

/// <summary>
/// Builds a real <see cref="TypeAuthContext"/> scoped on the <c>Companies</c> dimension from a small
/// spec — none / wildcard / id-set / self / null-company. No DB; this mirrors the TypeAuth
/// QueryableFilterTests idiom (an access tree as JSON fed into a real context) and is reused across
/// every data-level-access slice so the scenario reads consistently.
/// <para>
/// Access values default to Read+Write+Delete so the same scoped user works on both the query path
/// (Read) and the row path (Write/Delete); pass explicit <see cref="Access"/> values to narrow it.
/// </para>
/// </summary>
public static class ScopedTypeAuth
{
    /// <summary>The data-level action the whole scenario scopes on.</summary>
    public static DynamicReadWriteDeleteAction CompaniesAction => VehicleDataLevel.Companies;

    /// <summary>
    /// Converts an accessible-id string back to a company id. The null-FK sentinel is mapped to
    /// <see langword="null"/> upstream by <c>ConvertIds</c>, so this only ever sees real ids.
    /// </summary>
    public static long ToCompanyId(string id) => long.Parse(id);

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
    /// "Own data" access: grants the self-reference key, which resolves to whatever self id is supplied
    /// to <c>GetAccessibleItemsByAccess</c> / <c>GetReadableItems</c> / <c>Can</c> at evaluation time.
    /// </summary>
    public static TypeAuthContext Self(params Access[] accesses)
        => BuildCompanies(SingleKey(TypeAuthContext.SelfReferenceKey, Defaulted(accesses)));

    /// <summary>Access to rows whose company FK is null (the empty/null sentinel).</summary>
    public static TypeAuthContext NullCompany(params Access[] accesses)
        => BuildCompanies(SingleKey(TypeAuthContext.EmptyOrNullKey, Defaulted(accesses)));

    private static Access[] Defaulted(Access[] accesses) => accesses.Length == 0 ? DefaultAccesses : accesses;

    private static Dictionary<string, object> SingleKey(string key, object value) => new() { [key] = value };

    // companiesNode is either an Access[] (→ wildcard list) or a Dictionary<id, Access[]> (→ scoped ids).
    private static TypeAuthContext BuildCompanies(object companiesNode)
    {
        var tree = new Dictionary<string, object>
        {
            [nameof(VehicleDataLevel)] = new Dictionary<string, object>
            {
                [nameof(VehicleDataLevel.Companies)] = companiesNode,
            },
        };
        return Build(JsonConvert.SerializeObject(tree));
    }

    private static TypeAuthContext Build(string accessTreeJson)
        => new TypeAuthContextBuilder()
            .AddAccessTree(accessTreeJson)
            .AddActionTree<VehicleDataLevel>()
            .Build();
}

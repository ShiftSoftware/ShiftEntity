using System.Security.Claims;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.HashIds;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;

/// <summary>
/// <see cref="ICurrentUserProvider"/> test double — supplies a principal carrying chosen claims (for <c>Self</c> /
/// <c>OnOwner</c> resolution), or no signed-in user at all (to exercise the fail-closed absent-claim path).
/// </summary>
public sealed class FakeCurrentUserProvider : ICurrentUserProvider
{
    private readonly ClaimsPrincipal? user;

    private FakeCurrentUserProvider(ClaimsPrincipal? user) => this.user = user;

    public ClaimsPrincipal? GetUser() => user;

    /// <summary>A signed-in caller whose principal carries the given (claim-type → value) claims.</summary>
    public static FakeCurrentUserProvider WithClaims(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(authenticationType: "Test");
        foreach (var (type, value) in claims)
            identity.AddClaim(new Claim(type, value));
        return new FakeCurrentUserProvider(new ClaimsPrincipal(identity));
    }

    /// <summary>
    /// A principal that CARRIES the given claims but is <b>not authenticated</b> (no authentication type). Claims on
    /// such a principal must never grant data access — both the legacy claim reads and v2's
    /// <c>DataLevelAccessContext.GetClaim</c> resolve them to null (the 4.1 parity alignment).
    /// </summary>
    public static FakeCurrentUserProvider WithUnauthenticatedClaims(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(); // no authentication type ⇒ IsAuthenticated == false
        foreach (var (type, value) in claims)
            identity.AddClaim(new Claim(type, value));
        return new FakeCurrentUserProvider(new ClaimsPrincipal(identity));
    }

    /// <summary>No signed-in user (<see cref="GetUser"/> ⇒ <see langword="null"/>): every claim resolves to null.</summary>
    public static FakeCurrentUserProvider Anonymous() => new(null);
}

/// <summary>
/// <see cref="IHashIdService"/> test double. <see cref="Decode(string, System.Type)"/> /
/// <see cref="Encode(long, System.Type)"/> run an injectable function (default: identity — <c>long.Parse</c> /
/// <c>ToString</c>) and record each (value, DTO type) call, so a test can assert the engine routed a
/// <c>HashId&lt;TDto&gt;</c> dimension through the hashid converter (with the right DTO) rather than parsing raw.
/// The generic <see cref="Decode{TDTO}(string)"/>/<see cref="Encode{TDTO}(long)"/> forms — what the <em>legacy</em>
/// <c>DefaultDataLevelAccess</c> calls — run the same functions and record into the same lists, so the Phase 4
/// parity tests see both arms resolve the identical id-space (and can assert the DTO type-key either way). Members
/// neither arm uses throw, to fail loud if a path unexpectedly depends on them.
/// </summary>
public sealed class RecordingHashIdService : IHashIdService
{
    private readonly Func<string, Type, long> decode;
    private readonly Func<long, Type, string> encode;

    public List<(string Key, Type DtoType)> DecodeCalls { get; } = new();
    public List<(long Id, Type DtoType)> EncodeCalls { get; } = new();

    public RecordingHashIdService(Func<string, Type, long>? decode = null, Func<long, Type, string>? encode = null)
    {
        this.decode = decode ?? ((key, _) => long.Parse(key));
        this.encode = encode ?? ((id, _) => id.ToString());
    }

    public long Decode(string key, Type dtoType)
    {
        DecodeCalls.Add((key, dtoType));
        return decode(key, dtoType);
    }

    public string Encode(long id, Type dtoType)
    {
        EncodeCalls.Add((id, dtoType));
        return encode(id, dtoType);
    }

    // The legacy DefaultDataLevelAccess uses the generic forms — same functions, same recording.
    public long Decode<TDTO>(string key) => Decode(key, typeof(TDTO));
    public string Encode<TDTO>(long id) => Encode(id, typeof(TDTO));

    // Not used by either arm — throw rather than guess at behavior.
    public bool IsConfigurationRegistered(string configurationName) => throw new NotImplementedException();
    public bool IsAcceptUnencodedIds(string? configurationName) => throw new NotImplementedException();
    public long Decode(string key, JsonHashIdConverterAttribute attr) => throw new NotImplementedException();
    public string Encode(long id, JsonHashIdConverterAttribute attr) => throw new NotImplementedException();
    public ShiftEntityHashId? GetHasherFor(JsonHashIdConverterAttribute attr) => throw new NotImplementedException();
}

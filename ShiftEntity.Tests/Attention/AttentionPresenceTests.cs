using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.Tests.Attention.Scenario;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.ShiftEntity.Web.Attention;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Attention;

/// <summary>
/// Entity-viewer presence tests. They cover: the <see cref="InMemoryEntityViewerTracker"/>
/// contract (a connection may hold many entries at once, entries carry an optional scope,
/// scope and entity-type matching are exact and case-sensitive, RemoveViewer removes exactly
/// one entry, RemoveConnection removes them all); the <see cref="AttentionHub"/> viewing
/// methods that update it (hash-encoded ids are decoded when they arrive, anything that cannot
/// be decoded is silently ignored, cleanup happens on disconnect); the
/// <see cref="AttentionViewerExtensions.HasActiveViewers{TEntity}"/> evaluator helper, which
/// returns <c>false</c> whenever presence cannot be checked so signals are raised as normal;
/// and the <c>AddAttentionHub</c> tracker registration.
/// </summary>
public class AttentionPresenceTests
{
    // ── InMemoryEntityViewerTracker: entries and removal ────────────────────────

    [Fact]
    public void Tracker_AddViewer_MakesEntityVisible_ToHasViewers()
    {
        var tracker = new InMemoryEntityViewerTracker();

        tracker.AddViewer("conn-1", "Invoice", 42);

        Assert.True(tracker.HasViewers("Invoice", 42));
        Assert.False(tracker.HasViewers("Invoice", 43));      // different row
        Assert.False(tracker.HasViewers("Order", 42));        // different type, same id
    }

    [Fact]
    public void Tracker_OneConnection_CanHoldManyEntriesAtOnce()
    {
        // A connection is not limited to one record. Adding an entry keeps the existing ones.
        var tracker = new InMemoryEntityViewerTracker();

        tracker.AddViewer("conn-1", "Invoice", 42);
        tracker.AddViewer("conn-1", "Invoice", 43);
        tracker.AddViewer("conn-1", "Order", 7);

        Assert.True(tracker.HasViewers("Invoice", 42));
        Assert.True(tracker.HasViewers("Invoice", 43));
        Assert.True(tracker.HasViewers("Order", 7));
    }

    [Fact]
    public void Tracker_AddViewer_SameEntryTwice_KeepsASingleEntry()
    {
        // Adding an entry that already exists does nothing. One RemoveViewer call is enough
        // to take it away again — no duplicate is left behind.
        var tracker = new InMemoryEntityViewerTracker();
        tracker.AddViewer("conn-1", "Invoice", 42);
        tracker.AddViewer("conn-1", "Invoice", 42);

        tracker.RemoveViewer("conn-1", "Invoice", 42);

        Assert.False(tracker.HasViewers("Invoice", 42));
    }

    [Fact]
    public void Tracker_RemoveViewer_RemovesExactlyThatEntry()
    {
        var tracker = new InMemoryEntityViewerTracker();
        tracker.AddViewer("conn-1", "Invoice", 42);
        tracker.AddViewer("conn-1", "Invoice", 43);

        tracker.RemoveViewer("conn-1", "Invoice", 42);

        Assert.False(tracker.HasViewers("Invoice", 42));
        Assert.True(tracker.HasViewers("Invoice", 43));       // the other entry is kept
    }

    [Fact]
    public void Tracker_RemoveViewer_UnknownEntryOrConnection_IsANoOp()
    {
        var tracker = new InMemoryEntityViewerTracker();
        tracker.AddViewer("conn-1", "Invoice", 42);

        tracker.RemoveViewer("conn-1", "Invoice", 99);        // entry never added
        tracker.RemoveViewer("never-seen", "Invoice", 42);    // unknown connection — must not throw

        Assert.True(tracker.HasViewers("Invoice", 42));
    }

    [Fact]
    public void Tracker_RemoveConnection_ForgetsEveryEntryOfThatConnection()
    {
        // Disconnect cleanup: all of the connection's entries go at once. Other connections
        // are not affected. Unknown connection ids are a no-op.
        var tracker = new InMemoryEntityViewerTracker();
        tracker.AddViewer("conn-1", "Invoice", 42);
        tracker.AddViewer("conn-1", "Order", 7, scope: "tab-1");
        tracker.AddViewer("conn-2", "Invoice", 42);

        tracker.RemoveConnection("conn-1");
        tracker.RemoveConnection("never-seen");   // must not throw

        Assert.False(tracker.HasViewers("Order", 7));
        Assert.True(tracker.HasViewers("Invoice", 42));       // conn-2 still viewing
    }

    [Fact]
    public void Tracker_MultipleConnectionsOnOneEntity_StayVisibleUntilTheLastLeaves()
    {
        var tracker = new InMemoryEntityViewerTracker();
        tracker.AddViewer("conn-1", "Invoice", 42);
        tracker.AddViewer("conn-2", "Invoice", 42);

        tracker.RemoveConnection("conn-1");
        Assert.True(tracker.HasViewers("Invoice", 42));       // conn-2 still viewing

        tracker.RemoveConnection("conn-2");
        Assert.False(tracker.HasViewers("Invoice", 42));
    }

    [Fact]
    public void Tracker_EntityTypeMatching_IsOrdinal()
    {
        // Entity types are CLR short names — exact, case-sensitive matching.
        var tracker = new InMemoryEntityViewerTracker();

        tracker.AddViewer("conn-1", "Invoice", 42);

        Assert.False(tracker.HasViewers("invoice", 42));
    }

    // ── InMemoryEntityViewerTracker: scope matching ─────────────────────────────

    [Fact]
    public void Tracker_ScopedEntry_MatchesTheNullScopeQuery_AndItsExactScope()
    {
        // A null-scope query asks "is anyone viewing this record at all?" — any entry counts,
        // whatever its scope. A scoped query only counts entries added with that exact scope.
        var tracker = new InMemoryEntityViewerTracker();

        tracker.AddViewer("conn-1", "Invoice", 42, scope: "payments-tab");

        Assert.True(tracker.HasViewers("Invoice", 42));                          // any scope
        Assert.True(tracker.HasViewers("Invoice", 42, scope: "payments-tab"));   // exact scope
        Assert.False(tracker.HasViewers("Invoice", 42, scope: "items-tab"));     // other scope
    }

    [Fact]
    public void Tracker_NullScopeEntry_IsNotFound_ByAScopedQuery()
    {
        // A null-scope entry means "the record as a whole". It matches the null-scope query,
        // but it does not count as a viewer of any named scope.
        var tracker = new InMemoryEntityViewerTracker();

        tracker.AddViewer("conn-1", "Invoice", 42);

        Assert.True(tracker.HasViewers("Invoice", 42));
        Assert.False(tracker.HasViewers("Invoice", 42, scope: "payments-tab"));
    }

    [Fact]
    public void Tracker_ScopeMatching_IsOrdinal()
    {
        var tracker = new InMemoryEntityViewerTracker();

        tracker.AddViewer("conn-1", "Invoice", 42, scope: "payments-tab");

        Assert.False(tracker.HasViewers("Invoice", 42, scope: "PAYMENTS-TAB"));
    }

    [Fact]
    public void Tracker_SameRecordWithDifferentScopes_AreIndependentEntries()
    {
        // One connection can view two parts of the same record. Removing one scope's entry
        // keeps the other, and the record still counts as viewed.
        var tracker = new InMemoryEntityViewerTracker();
        tracker.AddViewer("conn-1", "Invoice", 42, scope: "payments-tab");
        tracker.AddViewer("conn-1", "Invoice", 42, scope: "items-tab");

        tracker.RemoveViewer("conn-1", "Invoice", 42, scope: "payments-tab");

        Assert.False(tracker.HasViewers("Invoice", 42, scope: "payments-tab"));
        Assert.True(tracker.HasViewers("Invoice", 42, scope: "items-tab"));
        Assert.True(tracker.HasViewers("Invoice", 42));
    }

    [Fact]
    public void Tracker_RemoveViewer_RequiresTheScopeTheEntryWasAddedWith()
    {
        // The scope is part of the entry's identity. A null-scope remove does not take away
        // a scoped entry, and a scoped remove does not take away a null-scope entry.
        var tracker = new InMemoryEntityViewerTracker();
        tracker.AddViewer("conn-1", "Invoice", 42, scope: "payments-tab");
        tracker.AddViewer("conn-1", "Order", 7);

        tracker.RemoveViewer("conn-1", "Invoice", 42);                       // wrong: null scope
        tracker.RemoveViewer("conn-1", "Order", 7, scope: "payments-tab");   // wrong: scoped

        Assert.True(tracker.HasViewers("Invoice", 42, scope: "payments-tab"));
        Assert.True(tracker.HasViewers("Order", 7));
    }

    [Fact]
    public void Tracker_AddViewer_CapsEntriesPerConnection()
    {
        // The cap stops one connection from growing the tracker without limit (scopes are
        // free-form client strings, so a hostile client could send ever-new ones). Dropped
        // adds are safe: the record then counts as not viewed, and signals raise as normal.
        var tracker = new InMemoryEntityViewerTracker();

        for (var i = 0; i < 200; i++)
            tracker.AddViewer("conn-1", "Invoice", 42, scope: $"scope-{i}");

        Assert.True(tracker.HasViewers("Invoice", 42, scope: "scope-0"));
        Assert.False(tracker.HasViewers("Invoice", 42, scope: "scope-199"));

        // The cap is per connection: a full connection does not block another one.
        tracker.AddViewer("conn-2", "Invoice", 42, scope: "scope-199");
        Assert.True(tracker.HasViewers("Invoice", 42, scope: "scope-199"));

        // Removing an entry frees room for a new one on the capped connection.
        tracker.RemoveViewer("conn-1", "Invoice", 42, scope: "scope-0");
        tracker.AddViewer("conn-1", "Invoice", 42, scope: "scope-fresh");
        Assert.True(tracker.HasViewers("Invoice", 42, scope: "scope-fresh"));
    }

    // ── AttentionHub viewing methods ────────────────────────────────────────────

    /// <summary>
    /// Builds a hub for the presence tests. The default decoder drops the first character of
    /// the key: the tests pass hand-written ids like <c>"H42"</c>, and the decoder turns them
    /// into <c>42</c>. (The fake's own <c>Encode</c> does not add this prefix — do not feed an
    /// encoded value back into a viewing method.)
    /// </summary>
    private static AttentionHub CreateHub(
        string connectionId,
        InMemoryEntityViewerTracker tracker,
        RecordingHashIdService? hashIds = null,
        ShiftEntityDtoMap? dtoMap = null)
    {
        if (dtoMap is null)
        {
            dtoMap = new ShiftEntityDtoMap();
            dtoMap.Register(nameof(GadgetEntity), typeof(GadgetDTO));
        }

        return new AttentionHub(
            tracker,
            dtoMap,
            hashIds ?? new RecordingHashIdService(decode: (key, _) => long.Parse(key[1..])))
        {
            Groups = new RecordingGroupManager(),
            Context = new FakeHubCallerContext(connectionId),
        };
    }

    [Fact]
    public async Task StartViewingEntity_DecodesTheHashEncodedId_ViaDtoType_AndAddsTheViewer()
    {
        var tracker = new InMemoryEntityViewerTracker();
        var hashIds = new RecordingHashIdService(decode: (key, _) => long.Parse(key[1..]));
        using var hub = CreateHub("conn-1", tracker, hashIds);

        await hub.StartViewingEntity(nameof(GadgetEntity), "H42", scope: null);

        Assert.True(tracker.HasViewers(nameof(GadgetEntity), 42));

        // The decode used the entity's DTO type. This is the reverse of the notifier's encode.
        Assert.Contains(hashIds.DecodeCalls, c => c.Key == "H42" && c.DtoType == typeof(GadgetDTO));
    }

    [Fact]
    public async Task StartViewingEntity_RecordsTheScope_ThatTheClientSent()
    {
        var tracker = new InMemoryEntityViewerTracker();
        using var hub = CreateHub("conn-1", tracker);

        await hub.StartViewingEntity(nameof(GadgetEntity), "H42", scope: "stock-tab");

        Assert.True(tracker.HasViewers(nameof(GadgetEntity), 42, scope: "stock-tab"));
        Assert.False(tracker.HasViewers(nameof(GadgetEntity), 42, scope: "other-tab"));
    }

    [Fact]
    public async Task StartViewingEntity_AllowsManyEntries_OnOneConnection()
    {
        // A form can report the record it displays and, at the same time, a specific part of
        // it. Both entries live side by side on the same connection.
        var tracker = new InMemoryEntityViewerTracker();
        using var hub = CreateHub("conn-1", tracker);

        await hub.StartViewingEntity(nameof(GadgetEntity), "H42", scope: null);
        await hub.StartViewingEntity(nameof(GadgetEntity), "H43", scope: "stock-tab");

        Assert.True(tracker.HasViewers(nameof(GadgetEntity), 42));
        Assert.True(tracker.HasViewers(nameof(GadgetEntity), 43, scope: "stock-tab"));
    }

    [Fact]
    public async Task StartViewingEntity_UnknownEntityType_IsASilentNoOp_WithoutDecoding()
    {
        var tracker = new InMemoryEntityViewerTracker();
        var hashIds = new RecordingHashIdService(decode: (key, _) => long.Parse(key[1..]));
        using var hub = CreateHub("conn-1", tracker, hashIds, dtoMap: new ShiftEntityDtoMap());

        await hub.StartViewingEntity("Mystery", "H42", scope: null);   // must not throw

        Assert.False(tracker.HasViewers("Mystery", 42));
        Assert.Empty(hashIds.DecodeCalls);                 // decode was never called
    }

    [Fact]
    public async Task StartViewingEntity_UndecodableId_IsASilentNoOp()
    {
        // Presence is best-effort: invalid input from a client only means no viewer is
        // recorded. The hub never sends an error back for it.
        var tracker = new InMemoryEntityViewerTracker();
        var hashIds = new RecordingHashIdService(decode: (_, _) => throw new FormatException());
        using var hub = CreateHub("conn-1", tracker, hashIds);

        await hub.StartViewingEntity(nameof(GadgetEntity), "not-a-hash", scope: null);

        Assert.False(tracker.HasViewers(nameof(GadgetEntity), 42));
    }

    [Fact]
    public async Task StopViewingEntity_RemovesOnlyThatEntry()
    {
        // The client navigated away from one record. The connection's other entries are kept.
        var tracker = new InMemoryEntityViewerTracker();
        using var hub = CreateHub("conn-1", tracker);
        await hub.StartViewingEntity(nameof(GadgetEntity), "H42", scope: null);
        await hub.StartViewingEntity(nameof(GadgetEntity), "H43", scope: null);

        await hub.StopViewingEntity(nameof(GadgetEntity), "H42", scope: null);

        Assert.False(tracker.HasViewers(nameof(GadgetEntity), 42));
        Assert.True(tracker.HasViewers(nameof(GadgetEntity), 43));
    }

    [Fact]
    public async Task StopViewingEntity_MatchesTheScope_TheEntryWasAddedWith()
    {
        var tracker = new InMemoryEntityViewerTracker();
        using var hub = CreateHub("conn-1", tracker);
        await hub.StartViewingEntity(nameof(GadgetEntity), "H42", scope: "stock-tab");

        // Wrong scope: the entry is kept.
        await hub.StopViewingEntity(nameof(GadgetEntity), "H42", scope: null);
        Assert.True(tracker.HasViewers(nameof(GadgetEntity), 42, scope: "stock-tab"));

        // Matching scope: the entry is removed.
        await hub.StopViewingEntity(nameof(GadgetEntity), "H42", scope: "stock-tab");
        Assert.False(tracker.HasViewers(nameof(GadgetEntity), 42));
    }

    [Fact]
    public async Task StopViewingEntity_InvalidInput_IsASilentNoOp()
    {
        // Same best-effort behavior as StartViewingEntity: nulls and undecodable ids are
        // ignored, never returned to the caller as a hub error.
        var tracker = new InMemoryEntityViewerTracker();
        using var hub = CreateHub("conn-1", tracker);
        await hub.StartViewingEntity(nameof(GadgetEntity), "H42", scope: null);

        await hub.StopViewingEntity("", "H42", scope: null);
        await hub.StopViewingEntity(nameof(GadgetEntity), "", scope: null);
        await hub.StopViewingEntity("Mystery", "H42", scope: null);

        Assert.True(tracker.HasViewers(nameof(GadgetEntity), 42));
    }

    [Fact]
    public async Task OnDisconnected_RemovesEveryEntryOfTheCaller()
    {
        // A closed browser tab never calls StopViewingEntity. The disconnect event is where
        // the cleanup happens — for all of the connection's entries at once.
        var tracker = new InMemoryEntityViewerTracker();
        using var hub = CreateHub("conn-1", tracker);
        await hub.StartViewingEntity(nameof(GadgetEntity), "H42", scope: null);
        await hub.StartViewingEntity(nameof(GadgetEntity), "H43", scope: "stock-tab");

        await hub.OnDisconnectedAsync(exception: null);

        Assert.False(tracker.HasViewers(nameof(GadgetEntity), 42));
        Assert.False(tracker.HasViewers(nameof(GadgetEntity), 43));
    }

    // ── HasActiveViewers evaluator convenience ──────────────────────────────────

    private static AttentionContext<GadgetEntity> ContextFor(GadgetEntity gadget, IServiceProvider services)
        => new()
        {
            Action = ActionTypes.Update,
            Entity = gadget,
            Original = null,
            Services = services,
        };

    [Fact]
    public void HasActiveViewers_TrueWhileAConnectionViewsTheEntity()
    {
        var tracker = new InMemoryEntityViewerTracker();
        tracker.AddViewer("conn-1", nameof(GadgetEntity), 42);
        using var provider = new ServiceCollection()
            .AddSingleton<IEntityViewerTracker>(tracker)
            .BuildServiceProvider();

        var context = ContextFor(new GadgetEntity { ID = 42 }, provider);

        Assert.True(context.HasActiveViewers());
    }

    [Fact]
    public void HasActiveViewers_PassesTheScopeThrough()
    {
        var tracker = new InMemoryEntityViewerTracker();
        tracker.AddViewer("conn-1", nameof(GadgetEntity), 42, scope: "stock-tab");
        using var provider = new ServiceCollection()
            .AddSingleton<IEntityViewerTracker>(tracker)
            .BuildServiceProvider();

        var context = ContextFor(new GadgetEntity { ID = 42 }, provider);

        Assert.True(context.HasActiveViewers());                     // any scope
        Assert.True(context.HasActiveViewers(scope: "stock-tab"));   // exact scope
        Assert.False(context.HasActiveViewers(scope: "other-tab"));  // other scope
    }

    [Fact]
    public void HasActiveViewers_FalseWhenNoTrackerIsRegistered_SoSignalsAreRaisedAsNormal()
    {
        // There is no tracker in the DI graph (the host never enabled presence). The result
        // is false, so the evaluator raises normally.
        using var provider = new ServiceCollection().BuildServiceProvider();

        var context = ContextFor(new GadgetEntity { ID = 42 }, provider);

        Assert.False(context.HasActiveViewers());
    }

    [Fact]
    public void HasActiveViewers_FalseForAnUnsavedEntity_SoSignalsAreRaisedAsNormal()
    {
        // Insert path: there is no database ID yet, so nobody can be viewing the record.
        // The tracker must not be asked about the placeholder id 0.
        var tracker = new InMemoryEntityViewerTracker();
        tracker.AddViewer("conn-1", nameof(GadgetEntity), 0);
        using var provider = new ServiceCollection()
            .AddSingleton<IEntityViewerTracker>(tracker)
            .BuildServiceProvider();

        var context = ContextFor(new GadgetEntity { ID = 0 }, provider);

        Assert.False(context.HasActiveViewers());
    }

    [Fact]
    public void HasActiveViewers_FalseWhenNobodyViewsTheEntity()
    {
        var tracker = new InMemoryEntityViewerTracker();
        tracker.AddViewer("conn-1", nameof(GadgetEntity), 7);   // someone views a different row
        using var provider = new ServiceCollection()
            .AddSingleton<IEntityViewerTracker>(tracker)
            .BuildServiceProvider();

        var context = ContextFor(new GadgetEntity { ID = 42 }, provider);

        Assert.False(context.HasActiveViewers());
    }

    // ── AddAttentionHub registration ────────────────────────────────────────────

    [Fact]
    public void AddAttentionHub_RegistersTheInMemoryTracker_AsASingleton()
    {
        var services = new ServiceCollection();

        services.AddAttentionHub();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IEntityViewerTracker)
            && d.ImplementationType == typeof(InMemoryEntityViewerTracker)
            && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddAttentionHub_KeepsAHostRegisteredTracker()
    {
        // TryAdd: a host with its own real-time hubs registers its own tracker first and
        // updates it from those hubs. AddAttentionHub must not replace it.
        var services = new ServiceCollection();
        var hostTracker = new InMemoryEntityViewerTracker();
        services.AddSingleton<IEntityViewerTracker>(hostTracker);

        services.AddAttentionHub();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IEntityViewerTracker));
        Assert.Same(hostTracker, descriptor.ImplementationInstance);
    }
}

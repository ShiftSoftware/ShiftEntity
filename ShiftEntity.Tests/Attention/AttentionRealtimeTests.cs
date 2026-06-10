using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.Attention.Scenario;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.ShiftEntity.Web.Attention;
using System.Reflection;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Attention;

/// <summary>
/// Iteration 8 — real-time hub (server side). A raised signal fans out as a SignalR refresh
/// hint to the <see cref="AttentionHub"/> group for its entity type, with the raw entity ID
/// hash-encoded at the process boundary. Covers the notifier (composed through the real Iter 7
/// emission pipeline and in isolation), the hub's subscribe/unsubscribe group naming, hub auth,
/// the <c>AddAttentionHub</c> registration, and the "payload is a refresh hint, not data"
/// property.
/// </summary>
public class AttentionRealtimeTests
{
    private static ShiftRepository<AttentionTestDbContext, GadgetEntity, GadgetDTO, GadgetDTO> GadgetRepository(
        AttentionTestDbContext db)
        => new(db, new ThrowingGadgetMapper());

    [Fact]
    public async Task RaisedSignal_PublishesHintToEntityTypeGroup_WithHashEncodedId()
    {
        // encode != identity, so the assertion can prove the raw id did NOT leak.
        var hashIds = new RecordingHashIdService(encode: (id, _) => $"H{id}");
        var dtoMap = new ShiftEntityDtoMap();
        dtoMap.Register(nameof(GadgetEntity), typeof(GadgetDTO));
        var hub = new RecordingHubContext();

        await using var app = await AttentionApp.StartAsync(s => s
            .AddSingleton<IHashIdService>(hashIds)
            .AddSingleton(dtoMap)
            .AddSingleton<IHubContext<AttentionHub>>(hub)
            .AddAttentionConsumer<AttentionRealtimeNotifier>());
        using var scope = app.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>();
        var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 2 };
        db.Gadgets.Add(gadget);

        await GadgetRepository(db).SaveChangesAsync();

        var messages = await hub.WaitUntilAsync(m => m.Count >= 1);

        var message = Assert.Single(messages);
        Assert.Equal(AttentionRealtime.GroupFor(nameof(GadgetEntity)), message.Group);
        Assert.Equal(AttentionRealtime.MessageName, message.Method);
        Assert.Empty(message.ExcludedConnectionIds);   // no origin header on this save → whole group

        var payload = Assert.IsType<AttentionRealtimePayload>(message.Payload);
        Assert.Equal(nameof(GadgetEntity), payload.EntityType);
        Assert.Equal(AttentionRealtimeKind.Raised, payload.Kind);
        Assert.True(gadget.ID > 0, "the insert should have assigned a database ID");
        Assert.Equal($"H{gadget.ID}", payload.EntityId);                 // hash-encoded
        Assert.NotEqual(gadget.ID.ToString(), payload.EntityId);         // raw id did not leak
        Assert.Equal(AttentionSeverity.Warning, payload.Severity);
        Assert.NotEqual(default, payload.RaisedAt);

        // The encode routed through the entity's DTO type, per the HashID convention.
        Assert.Contains(hashIds.EncodeCalls, c => c.Id == gadget.ID && c.DtoType == typeof(GadgetDTO));
    }

    [Fact]
    public async Task HandleAsync_EncodesEntityId_ViaDtoType()
    {
        var hashIds = new RecordingHashIdService(encode: (id, _) => $"H{id}");
        var dtoMap = new ShiftEntityDtoMap();
        dtoMap.Register(nameof(GadgetEntity), typeof(GadgetDTO));
        var hub = new RecordingHubContext();
        var notifier = new AttentionRealtimeNotifier(hub, hashIds, dtoMap, NullLogger<AttentionRealtimeNotifier>.Instance);

        var raisedAt = DateTimeOffset.UtcNow;
        await notifier.HandleAsync(new AttentionRaised
        {
            EntityType = nameof(GadgetEntity),
            EntityId = 42,
            Signal = new StoredAttentionSignal
            {
                Source = "S",
                Category = "C",
                Severity = AttentionSeverity.Critical,
                RaisedAt = raisedAt,
            },
        }, TestContext.Current.CancellationToken);

        var message = Assert.Single(hub.Snapshot());
        Assert.Equal(AttentionRealtime.GroupFor(nameof(GadgetEntity)), message.Group);
        Assert.Equal(AttentionRealtime.MessageName, message.Method);
        Assert.Empty(message.ExcludedConnectionIds);   // event carried no OriginConnectionId

        var payload = Assert.IsType<AttentionRealtimePayload>(message.Payload);
        Assert.Equal("H42", payload.EntityId);
        Assert.Equal(AttentionRealtimeKind.Raised, payload.Kind);
        Assert.Equal(AttentionSeverity.Critical, payload.Severity);
        Assert.Equal(raisedAt, payload.RaisedAt);
        Assert.Contains(hashIds.EncodeCalls, c => c.Id == 42 && c.DtoType == typeof(GadgetDTO));
    }

    [Fact]
    public async Task HandleAsync_WhenEntityTypeNotMapped_FallsBackToRawId_WithoutEncoding()
    {
        var hashIds = new RecordingHashIdService(encode: (id, _) => $"H{id}");
        var dtoMap = new ShiftEntityDtoMap();   // empty — no mapping for "Mystery"
        var hub = new RecordingHubContext();
        var notifier = new AttentionRealtimeNotifier(hub, hashIds, dtoMap, NullLogger<AttentionRealtimeNotifier>.Instance);

        await notifier.HandleAsync(new AttentionRaised
        {
            EntityType = "Mystery",
            EntityId = 7,
            Signal = new StoredAttentionSignal
            {
                Source = "S",
                Category = "C",
                Severity = AttentionSeverity.Info,
                RaisedAt = DateTimeOffset.UtcNow,
            },
        }, TestContext.Current.CancellationToken);

        var message = Assert.Single(hub.Snapshot());
        var payload = Assert.IsType<AttentionRealtimePayload>(message.Payload);
        Assert.Equal("7", payload.EntityId);     // raw id as an invariant string
        Assert.Empty(hashIds.EncodeCalls);        // the encode path was not taken
    }

    [Fact]
    public async Task BroadcastClearedAsync_SendsClearedHintToEntityTypeGroup_WithHashEncodedId()
    {
        // The clear path uses this (clearing raises no AttentionRaised), so other sessions drop
        // the indicator. Same group + encoded-id contract as the raise path, but Kind=Cleared.
        var hashIds = new RecordingHashIdService(encode: (id, _) => $"H{id}");
        var dtoMap = new ShiftEntityDtoMap();
        dtoMap.Register(nameof(GadgetEntity), typeof(GadgetDTO));
        var hub = new RecordingHubContext();
        var notifier = new AttentionRealtimeNotifier(hub, hashIds, dtoMap, NullLogger<AttentionRealtimeNotifier>.Instance);

        await notifier.BroadcastClearedAsync(nameof(GadgetEntity), 7,
            cancellationToken: TestContext.Current.CancellationToken);

        var message = Assert.Single(hub.Snapshot());
        Assert.Equal(AttentionRealtime.GroupFor(nameof(GadgetEntity)), message.Group);
        Assert.Equal(AttentionRealtime.MessageName, message.Method);
        Assert.Empty(message.ExcludedConnectionIds);   // no origin supplied → whole group
        var payload = Assert.IsType<AttentionRealtimePayload>(message.Payload);
        Assert.Equal(nameof(GadgetEntity), payload.EntityType);
        Assert.Equal("H7", payload.EntityId);
        Assert.Equal(AttentionRealtimeKind.Cleared, payload.Kind);
    }

    [Fact]
    public async Task HandleAsync_WithOriginConnectionId_ExcludesOriginatingConnectionFromGroup()
    {
        // A raise carrying the originating window's connection id fans out to the group EXCEPT
        // that connection — the window that caused the raise isn't notified about its own change.
        var hashIds = new RecordingHashIdService(encode: (id, _) => $"H{id}");
        var dtoMap = new ShiftEntityDtoMap();
        dtoMap.Register(nameof(GadgetEntity), typeof(GadgetDTO));
        var hub = new RecordingHubContext();
        var notifier = new AttentionRealtimeNotifier(hub, hashIds, dtoMap, NullLogger<AttentionRealtimeNotifier>.Instance);

        await notifier.HandleAsync(new AttentionRaised
        {
            EntityType = nameof(GadgetEntity),
            EntityId = 42,
            OriginConnectionId = "conn-1",
            Signal = new StoredAttentionSignal
            {
                Source = "S",
                Category = "C",
                Severity = AttentionSeverity.Warning,
                RaisedAt = DateTimeOffset.UtcNow,
            },
        }, TestContext.Current.CancellationToken);

        var message = Assert.Single(hub.Snapshot());
        Assert.Equal(AttentionRealtime.GroupFor(nameof(GadgetEntity)), message.Group);
        Assert.Equal(new[] { "conn-1" }, message.ExcludedConnectionIds);     // origin excluded
        var payload = Assert.IsType<AttentionRealtimePayload>(message.Payload);
        Assert.Equal(AttentionRealtimeKind.Raised, payload.Kind);
    }

    [Fact]
    public async Task BroadcastClearedAsync_WithOriginConnectionId_ExcludesOriginatingConnection()
    {
        // The window that performed the clear isn't notified about the clear it just did.
        var hashIds = new RecordingHashIdService(encode: (id, _) => $"H{id}");
        var dtoMap = new ShiftEntityDtoMap();
        dtoMap.Register(nameof(GadgetEntity), typeof(GadgetDTO));
        var hub = new RecordingHubContext();
        var notifier = new AttentionRealtimeNotifier(hub, hashIds, dtoMap, NullLogger<AttentionRealtimeNotifier>.Instance);

        await notifier.BroadcastClearedAsync(nameof(GadgetEntity), 7, "conn-9",
            TestContext.Current.CancellationToken);

        var message = Assert.Single(hub.Snapshot());
        Assert.Equal(AttentionRealtime.GroupFor(nameof(GadgetEntity)), message.Group);
        Assert.Equal(new[] { "conn-9" }, message.ExcludedConnectionIds);     // origin excluded
        var payload = Assert.IsType<AttentionRealtimePayload>(message.Payload);
        Assert.Equal(AttentionRealtimeKind.Cleared, payload.Kind);
    }

    [Fact]
    public async Task SubscribeToEntityType_AddsConnectionToEntityTypeGroup()
    {
        var groups = new RecordingGroupManager();
        using var hub = new AttentionHub { Groups = groups, Context = new FakeHubCallerContext("conn-1") };

        await hub.SubscribeToEntityType("Invoice");

        var added = Assert.Single(groups.Added);
        Assert.Equal("conn-1", added.ConnectionId);
        Assert.Equal(AttentionRealtime.GroupFor("Invoice"), added.GroupName);
        Assert.Equal("attention:Invoice", added.GroupName);
        Assert.Empty(groups.Removed);
    }

    [Fact]
    public async Task UnsubscribeFromEntityType_RemovesConnectionFromGroup()
    {
        var groups = new RecordingGroupManager();
        using var hub = new AttentionHub { Groups = groups, Context = new FakeHubCallerContext("conn-2") };

        await hub.UnsubscribeFromEntityType("Invoice");

        var removed = Assert.Single(groups.Removed);
        Assert.Equal("conn-2", removed.ConnectionId);
        Assert.Equal(AttentionRealtime.GroupFor("Invoice"), removed.GroupName);
        Assert.Empty(groups.Added);
    }

    [Fact]
    public void AttentionHub_RequiresAuthorization()
    {
        // Standard ASP.NET Core SignalR auth — the hub is [Authorize]; per-row access is
        // enforced when the client re-reads on reload, not in the hub (groups are type-only).
        Assert.NotEmpty(typeof(AttentionHub).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true));
    }

    [Fact]
    public void AddAttentionHub_RegistersNotifierConsumer_AndEmissionDispatcher()
    {
        var services = new ServiceCollection();

        services.AddAttentionHub();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IAttentionConsumer)
            && d.ImplementationType == typeof(AttentionRealtimeNotifier)
            && d.Lifetime == ServiceLifetime.Scoped);

        // AddAttentionConsumer pulls in emission (AddAttentionEmission) transitively.
        Assert.Contains(services, d => d.ServiceType == typeof(IAttentionDispatcher));

        // The clear-path broadcaster is registered too (same notifier type).
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IAttentionRealtimeBroadcaster)
            && d.ImplementationType == typeof(AttentionRealtimeNotifier));
    }

    [Fact]
    public void AttentionRealtimePayload_CarriesOnlyRefreshHintFields_NoRowData()
    {
        // The "refresh hint, not data" guarantee, pinned as a test: the payload exposes only the
        // hint fields (identity + raise/clear kind + severity + time), never row content the
        // receiving user might not be allowed to see.
        var properties = typeof(AttentionRealtimePayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                nameof(AttentionRealtimePayload.EntityId),
                nameof(AttentionRealtimePayload.EntityType),
                nameof(AttentionRealtimePayload.Kind),
                nameof(AttentionRealtimePayload.RaisedAt),
                nameof(AttentionRealtimePayload.Severity),
            },
            properties);
    }
}

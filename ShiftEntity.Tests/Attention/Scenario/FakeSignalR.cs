using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.Web.Attention;
using System.Security.Claims;

namespace ShiftSoftware.ShiftEntity.Tests.Attention.Scenario;

/// <summary>One <c>SendAsync</c> the notifier made, captured by <see cref="RecordingHubContext"/>.</summary>
public sealed record RecordedHubMessage(
    string Group,
    string Method,
    AttentionRealtimePayload? Payload,
    IReadOnlyList<string> ExcludedConnectionIds);

/// <summary>
/// <see cref="IHubContext{AttentionHub}"/> test double recording every group send. Mirrors the
/// emission tests' sink: callers <see cref="WaitUntilAsync"/> on the recorded messages rather
/// than sleeping. Only the <c>Clients.Group(name).SendAsync(...)</c> path the notifier uses is
/// implemented — every other member throws, to fail loud if a path unexpectedly depends on it.
/// </summary>
public sealed class RecordingHubContext : IHubContext<AttentionHub>
{
    private readonly object gate = new();
    private readonly List<RecordedHubMessage> messages = [];

    public IHubClients Clients { get; }
    public IGroupManager Groups => throw new NotImplementedException("The notifier never touches IHubContext.Groups.");

    public RecordingHubContext() => Clients = new RecordingHubClients(this);

    internal void Record(string group, string method, object?[] args, IReadOnlyList<string> excludedConnectionIds)
    {
        lock (gate)
            messages.Add(new RecordedHubMessage(
                group,
                method,
                args.Length > 0 ? args[0] as AttentionRealtimePayload : null,
                excludedConnectionIds));
    }

    public List<RecordedHubMessage> Snapshot()
    {
        lock (gate)
            return [.. messages];
    }

    public async Task<List<RecordedHubMessage>> WaitUntilAsync(
        Func<List<RecordedHubMessage>, bool> condition,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (true)
        {
            var snapshot = Snapshot();

            if (condition(snapshot) || DateTimeOffset.UtcNow > deadline)
                return snapshot;

            await Task.Delay(25);
        }
    }
}

/// <summary>
/// <see cref="IHubClients"/> double — only the notifier's two send targets are supported:
/// <see cref="Group"/> (no origin) and <see cref="GroupExcept"/> (origin excluded).
/// </summary>
public sealed class RecordingHubClients : IHubClients
{
    private readonly RecordingHubContext context;

    public RecordingHubClients(RecordingHubContext context) => this.context = context;

    public IClientProxy Group(string groupName) => new RecordingClientProxy(context, groupName, Array.Empty<string>());
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new RecordingClientProxy(context, groupName, excludedConnectionIds);

    public IClientProxy All => throw new NotImplementedException();
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy Client(string connectionId) => throw new NotImplementedException();
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotImplementedException();
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotImplementedException();
    public IClientProxy User(string userId) => throw new NotImplementedException();
    public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotImplementedException();
}

/// <summary>
/// <see cref="IClientProxy"/> bound to one group; records each send into the parent context.
/// The <c>SendAsync(method, arg, ct)</c> extension the notifier calls routes through
/// <see cref="SendCoreAsync"/>.
/// </summary>
public sealed class RecordingClientProxy : IClientProxy
{
    private readonly RecordingHubContext context;
    private readonly string group;
    private readonly IReadOnlyList<string> excludedConnectionIds;

    public RecordingClientProxy(RecordingHubContext context, string group, IReadOnlyList<string> excludedConnectionIds)
    {
        this.context = context;
        this.group = group;
        this.excludedConnectionIds = excludedConnectionIds;
    }

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        context.Record(group, method, args, excludedConnectionIds);
        return Task.CompletedTask;
    }
}

/// <summary><see cref="IGroupManager"/> double recording add/remove calls for the hub method tests.</summary>
public sealed class RecordingGroupManager : IGroupManager
{
    public List<(string ConnectionId, string GroupName)> Added { get; } = [];
    public List<(string ConnectionId, string GroupName)> Removed { get; } = [];

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Added.Add((connectionId, groupName));
        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Removed.Add((connectionId, groupName));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Minimal <see cref="HubCallerContext"/> — supplies a connection id for the hub's group
/// methods. Everything the methods don't read throws, to fail loud on unexpected use.
/// </summary>
public sealed class FakeHubCallerContext : HubCallerContext
{
    public FakeHubCallerContext(string connectionId) => ConnectionId = connectionId;

    public override string ConnectionId { get; }
    public override string? UserIdentifier => throw new NotImplementedException();
    public override ClaimsPrincipal? User => throw new NotImplementedException();
    public override IDictionary<object, object?> Items => throw new NotImplementedException();
    public override IFeatureCollection Features => throw new NotImplementedException();
    public override CancellationToken ConnectionAborted => throw new NotImplementedException();
    public override void Abort() => throw new NotImplementedException();
}

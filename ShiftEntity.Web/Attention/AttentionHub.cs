using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ShiftSoftware.ShiftEntity.Core.Attention;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Attention;

/// <summary>
/// Real-time fan-out hub for attention signals. Connected clients join a group per entity type
/// they care about (<see cref="SubscribeToEntityType"/>); when a signal is raised, the
/// framework's <c>AttentionRealtimeNotifier</c> pushes a hint to that group.
/// </summary>
/// <remarks>
/// <para>
/// Requires authentication (<see cref="AuthorizeAttribute"/>) — standard ASP.NET Core SignalR
/// auth. Groups are keyed on entity type only (<see cref="AttentionRealtime.GroupFor"/>), never
/// per row, so subscribing reveals nothing about individual records; per-row access is enforced
/// when the client re-reads on reload, not here. The pushed
/// <see cref="AttentionRealtimePayload"/> is a refresh hint with no row data, so it cannot leak a
/// row the connection's user can't see.
/// </para>
/// <para>
/// Register and map the hub with <c>services.AddAttentionHub()</c> +
/// <c>app.MapAttentionHub()</c>. Apps that call neither expose no hub endpoint and get no
/// notifier in their DI graph.
/// </para>
/// </remarks>
[Authorize]
public sealed class AttentionHub : Hub
{
    /// <summary>
    /// Joins the caller's connection to the group for <paramref name="entityType"/>, so it
    /// receives <see cref="AttentionRealtime.MessageName"/> hints for that type. A list or form
    /// calls this for the entity type it displays.
    /// </summary>
    public Task SubscribeToEntityType(string entityType) =>
        Groups.AddToGroupAsync(Context.ConnectionId, AttentionRealtime.GroupFor(entityType));

    /// <summary>
    /// Removes the caller's connection from the group for <paramref name="entityType"/>. The
    /// client calls this when a list/form is disposed or stops listening.
    /// </summary>
    public Task UnsubscribeFromEntityType(string entityType) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, AttentionRealtime.GroupFor(entityType));
}

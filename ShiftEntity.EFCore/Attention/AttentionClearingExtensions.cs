using System;
using System.Threading.Tasks;
using ShiftSoftware.ShiftEntity.Core.Attention;

namespace ShiftSoftware.ShiftEntity.EFCore.Attention;

/// <summary>
/// Public, in-process entry point for clearing attention signals without going through the HTTP
/// clear endpoint — for server-side callers that acknowledge attention as a side effect of a domain
/// action (e.g. a chat handler clearing the <c>"Chat"</c> scope when an operator takes the
/// conversation, or a background job resolving a flag). The clear endpoints in
/// <c>ShiftEntity.Web</c> are thin wrappers over the same underlying operation; this is the
/// equivalent for code that already holds a <see cref="ShiftDbContext"/>.
/// </summary>
/// <remarks>
/// Updates the persisted signal state only. It does <em>not</em> emit a real-time clear hint — that
/// is wired into the Web-layer endpoints via <c>IAttentionRealtimeBroadcaster</c>. A caller that
/// needs other open sessions to drop the indicator should invoke that broadcaster separately.
/// </remarks>
public static class AttentionClearingExtensions
{
    /// <summary>
    /// Clears the active attention signals selected by <paramref name="filter"/> (a <c>null</c>
    /// filter clears all of them). Returns the entity's post-clear <c>LastSaveDate</c> (the
    /// optimistic-concurrency stamp), or <c>null</c> when the entity carries no audit fields.
    /// </summary>
    /// <param name="db">The entity's database context.</param>
    /// <param name="entityType">CLR type name of the entity (e.g. <c>nameof(GeneralTicket)</c>).</param>
    /// <param name="entityId">Raw database ID of the entity.</param>
    /// <param name="filter">Which signals to clear; <c>null</c> = all active signals.</param>
    /// <param name="clearedByUserId">User credited with the clear, if known.</param>
    public static Task<DateTimeOffset?> ClearAttentionAsync(
        this ShiftDbContext db,
        string entityType,
        long entityId,
        AttentionClearFilter? filter = null,
        long? clearedByUserId = null)
        => AttentionPipeline.ClearSignals(db, entityType, entityId, clearedByUserId, filter);

    /// <summary>
    /// Strongly-typed overload of
    /// <see cref="ClearAttentionAsync(ShiftDbContext, string, long, AttentionClearFilter?, long?)"/>
    /// that derives the entity type name from <typeparamref name="TEntity"/>.
    /// </summary>
    /// <typeparam name="TEntity">The entity type whose signals are cleared; its name is the discriminator.</typeparam>
    public static Task<DateTimeOffset?> ClearAttentionAsync<TEntity>(
        this ShiftDbContext db,
        long entityId,
        AttentionClearFilter? filter = null,
        long? clearedByUserId = null)
        where TEntity : class
        => AttentionPipeline.ClearSignals(db, typeof(TEntity).Name, entityId, clearedByUserId, filter);
}

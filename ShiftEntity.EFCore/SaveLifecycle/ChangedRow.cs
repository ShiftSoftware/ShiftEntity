using Microsoft.EntityFrameworkCore.ChangeTracking;
using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>How a save changed a row.</summary>
public enum RowChange
{
    Added,
    Modified,
    Deleted,
}

/// <summary>One row that a save changes, as a handler sees it.</summary>
/// <typeparam name="TEntity">The type the handler is written for.</typeparam>
public readonly struct ChangedRow<TEntity> where TEntity : class
{
    private readonly TEntity? original;

    // Before the write: EF's own entry of the row, which still holds the values from before the save. They are read
    // from it only when the handler asks.
    private readonly EntityEntry? pending;

    // The handler class that got this row without the values from before the save: it is not marked [NeedsOriginal].
    private readonly Type? withheldFrom;

    private readonly bool isSoftDelete;

    /// <summary>Builds a row, for a unit test of a handler.</summary>
    public ChangedRow(TEntity entity, RowChange change, TEntity? original = null)
    {
        ArgumentNullException.ThrowIfNull(entity);

        Entity = entity;
        Change = change;
        this.original = original;
        isSoftDelete = change == RowChange.Modified
            && entity is IShiftEntityAudit { IsDeleted: true }
            && original is IShiftEntityAudit { IsDeleted: false };
    }

    internal ChangedRow(TEntity entity, RowChange change, TEntity? original, bool isSoftDelete, Type? withheldFrom)
    {
        Entity = entity;
        Change = change;
        this.original = original;
        this.isSoftDelete = isSoftDelete;
        this.withheldFrom = withheldFrom;
    }

    internal ChangedRow(TEntity entity, RowChange change, EntityEntry pending)
    {
        Entity = entity;
        Change = change;
        this.pending = pending;
    }

    /// <summary>
    /// The same row as a stage after the write gives it to a handler class that is not marked
    /// <see cref="NeedsOriginalAttribute"/>: reading <see cref="Original"/> of a modified or a deleted row throws.
    /// </summary>
    internal ChangedRow<TEntity> WithheldFrom(Type handlerType) => new(Entity, Change, original, isSoftDelete, handlerType);

    /// <summary>
    /// The record: the tracked instance, with its new key after the write. After a hard delete, the instance the
    /// context no longer tracks.
    /// </summary>
    public TEntity Entity { get; }

    /// <summary>Whether the row is added, modified or deleted.</summary>
    public RowChange Change { get; }

    /// <summary>
    /// The row's values from before the save, as a detached copy. Null for an added row, in every stage and for every
    /// handler. For a modified or a deleted row: before the write it is always there, and it is read from EF when it is
    /// asked for. After the write it is there only for a handler class marked <see cref="NeedsOriginalAttribute"/>: any
    /// other handler gets an exception that says so, not a null that reads as "nothing changed".
    /// </summary>
    /// <remarks>
    /// The copy is EF's own picture of the row before the save, so it holds the mapped columns only: no navigations, no
    /// owned types and no unmapped members. It is what the database held only when this context loaded the row. For a
    /// record attached with <c>Update</c> and never loaded, it is what the caller passed in.
    /// </remarks>
    public TEntity? Original
    {
        get
        {
            // An added row had no values before the save, so there is nothing to withhold.
            if (Change == RowChange.Added)
                return null;

            if (withheldFrom is not null)
                throw new InvalidOperationException(
                    $"{withheldFrom.Name} reads ChangedRow<{typeof(TEntity).Name}>.Original after the write, but the context kept no copy " +
                    $"of the values from before the save for it. Mark the class with [NeedsOriginal].");

            if (pending is null)
                return original;

            return Change == RowChange.Added ? null : (TEntity)pending.OriginalValues.ToObject();
        }
    }

    /// <summary>Whether this is a modified row whose <c>IsDeleted</c> the save turns from false to true.</summary>
    public bool IsSoftDelete
    {
        get
        {
            if (pending is null)
                return isSoftDelete;

            if (Change != RowChange.Modified || pending.Metadata.FindProperty(nameof(IShiftEntityAudit.IsDeleted)) is not { } isDeleted)
                return false;

            var property = pending.Property(isDeleted);
            return property.OriginalValue is false && property.CurrentValue is true;
        }
    }

    /// <summary>
    /// Whether the condition holds now and did not before the save. On an added row, whether it holds. Never on a
    /// deleted row. On a modified row it reads <see cref="Original"/>.
    /// </summary>
    public bool Became(Func<TEntity, bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        return Change switch
        {
            RowChange.Added => condition(Entity),
            RowChange.Modified => condition(Entity) && Original is { } before && !condition(before),
            _ => false,
        };
    }
}

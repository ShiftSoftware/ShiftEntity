namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>Builds the row sets that handlers are called with, for unit tests of handlers.</summary>
public static class HandlerRows
{
    /// <summary>The rows a before-save handler is called with.</summary>
    /// <remarks>
    /// <see cref="PendingRows{TEntity}.IsRepositorySave"/> is false on the set, and
    /// <see cref="PendingRows{TEntity}.RequireTransactionAsync"/> and <see cref="RowSet{TEntity}.TakeLockAsync"/> do nothing:
    /// no save is in progress.
    /// </remarks>
    /// <param name="db">The context the handler finds in <see cref="RowSet{TEntity}.Db"/>.</param>
    /// <param name="rows">The rows, each built with <see cref="ChangedRow{TEntity}"/>'s constructor.</param>
    public static PendingRows<TEntity> Pending<TEntity>(ShiftDbContext db, params ChangedRow<TEntity>[] rows) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);

        return new PendingRows<TEntity>(db, [.. rows]);
    }

    /// <summary>
    /// The rows a before-save handler is called with, as a repository's save or as any other save gives them: for the
    /// test of a handler that looks at <see cref="PendingRows{TEntity}.IsRepositorySave"/>, such as a guard that acts
    /// on repository saves only.
    /// </summary>
    /// <remarks>
    /// <see cref="PendingRows{TEntity}.RequireTransactionAsync"/> and <see cref="RowSet{TEntity}.TakeLockAsync"/> do nothing on
    /// the set: no save is in progress.
    /// </remarks>
    /// <param name="db">The context the handler finds in <see cref="RowSet{TEntity}.Db"/>.</param>
    /// <param name="isRepositorySave">What the handler finds in <see cref="PendingRows{TEntity}.IsRepositorySave"/>.</param>
    /// <param name="rows">The rows, each built with <see cref="ChangedRow{TEntity}"/>'s constructor.</param>
    public static PendingRows<TEntity> Pending<TEntity>(ShiftDbContext db, bool isRepositorySave, params ChangedRow<TEntity>[] rows) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);

        return new PendingRows<TEntity>(db, [.. rows], isRepositorySave);
    }

    /// <summary>The rows an after-save, before-commit or after-commit handler is called with.</summary>
    /// <remarks>
    /// Every row of the set gives its <see cref="ChangedRow{TEntity}.Original"/>, whatever the handler is marked with.
    /// To test that a handler is marked <see cref="NeedsOriginalAttribute"/> where it reads it, build the set with
    /// <see cref="Saved{THandler, TEntity}"/>. <see cref="RowSet{TEntity}.TakeLockAsync"/> does nothing on the set.
    /// </remarks>
    /// <param name="db">The context the handler finds in <see cref="RowSet{TEntity}.Db"/>.</param>
    /// <param name="rows">The rows, each built with <see cref="ChangedRow{TEntity}"/>'s constructor.</param>
    public static SavedRows<TEntity> Saved<TEntity>(ShiftDbContext db, params ChangedRow<TEntity>[] rows) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);

        return new SavedRows<TEntity>(db, [.. rows]);
    }

    /// <summary>
    /// The rows an after-save, before-commit or after-commit handler of the class <typeparamref name="THandler"/> is
    /// called with, as a real save gives them to that class. When the class is not marked
    /// <see cref="NeedsOriginalAttribute"/>, reading <see cref="ChangedRow{TEntity}.Original"/> of a modified or a
    /// deleted row throws the exception a real save throws, so a unit test catches a missing mark. An added row gives
    /// null either way.
    /// </summary>
    /// <typeparam name="THandler">The handler class under test.</typeparam>
    /// <typeparam name="TEntity">The type the handler is written for.</typeparam>
    /// <param name="db">The context the handler finds in <see cref="RowSet{TEntity}.Db"/>.</param>
    /// <param name="rows">The rows, each built with <see cref="ChangedRow{TEntity}"/>'s constructor.</param>
    public static SavedRows<TEntity> Saved<THandler, TEntity>(ShiftDbContext db, params ChangedRow<TEntity>[] rows)
        where THandler : class
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);

        var handlerType = typeof(THandler);

        if (!SaveHandlerRegistration.StagesOf(handlerType).Exists(stage => stage.Stage != SaveStage.BeforeSave))
            throw new ArgumentException(
                $"{handlerType.Name} has no stage after the write: it implements none of IAfterSaveHandler<T>, IBeforeCommitHandler<T> and " +
                "IAfterCommitHandler<T>. Name the handler class under test.", nameof(THandler));

        if (handlerType.IsDefined(typeof(NeedsOriginalAttribute), inherit: true))
            return new SavedRows<TEntity>(db, [.. rows]);

        var withheld = new ChangedRow<TEntity>[rows.Length];

        for (var i = 0; i < rows.Length; i++)
            withheld[i] = rows[i].WithheldFrom(handlerType);

        return new SavedRows<TEntity>(db, withheld);
    }
}

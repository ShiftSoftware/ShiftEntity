using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// What a test tells the handlers of one entity type to do in each stage. A test adds it to the host with
/// <see cref="ScriptedHandlers.AddScript{TEntity}"/>, which registers one handler for each stage it gives.
/// </summary>
public sealed class Script<TEntity> where TEntity : class
{
    public Func<PendingRows<TEntity>, CancellationToken, Task>? BeforeSave { get; init; }
    public Func<SavedRows<TEntity>, CancellationToken, Task>? AfterSave { get; init; }
    public Func<SavedRows<TEntity>, CancellationToken, Task>? BeforeCommit { get; init; }
    public Func<SavedRows<TEntity>, CancellationToken, Task>? AfterCommit { get; init; }
}

public static class ScriptedHandlers
{
    /// <summary>
    /// Registers a handler for each stage the script gives, and for no other, so that a script without an after-save
    /// and a before-commit step makes no save need a transaction. Each handler writes a line when it is called, then
    /// runs its step.
    /// </summary>
    public static IServiceCollection AddScript<TEntity>(this IServiceCollection services, Script<TEntity> script) where TEntity : class
    {
        services.AddSingleton(script);

        if (script.BeforeSave is not null)
            services.AddSaveHandler<OnBeforeSave<TEntity>>();
        if (script.AfterSave is not null)
            services.AddSaveHandler<OnAfterSave<TEntity>>();
        if (script.BeforeCommit is not null)
            services.AddSaveHandler<OnBeforeCommit<TEntity>>();
        if (script.AfterCommit is not null)
            services.AddSaveHandler<OnAfterCommit<TEntity>>();

        return services;
    }

    /// <summary>Does nothing in a stage, for a script that only needs the stage's line in the log.</summary>
    public static Task Nothing<TEntity>(SavedRows<TEntity> rows, CancellationToken cancellationToken) where TEntity : class => Task.CompletedTask;

    /// <summary>Does nothing before the save, for a script that only needs the stage's line in the log.</summary>
    public static Task NothingBefore<TEntity>(PendingRows<TEntity> rows, CancellationToken cancellationToken) where TEntity : class => Task.CompletedTask;
}

public sealed class OnBeforeSave<TEntity>(Script<TEntity> script, SaveLog log) : IBeforeSaveHandler<TEntity> where TEntity : class
{
    public async ValueTask BeforeSaveAsync(PendingRows<TEntity> rows, CancellationToken cancellationToken)
    {
        log.Add($"before save of {typeof(TEntity).Name}: {StageLog<TEntity>.Describe(rows)}");
        await script.BeforeSave!(rows, cancellationToken);
    }
}

public sealed class OnAfterSave<TEntity>(Script<TEntity> script, SaveLog log) : IAfterSaveHandler<TEntity> where TEntity : class
{
    public async ValueTask AfterSaveAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken)
    {
        log.Add($"after save of {typeof(TEntity).Name}: {StageLog<TEntity>.Describe(rows)}");
        await script.AfterSave!(rows, cancellationToken);
    }
}

public sealed class OnBeforeCommit<TEntity>(Script<TEntity> script, SaveLog log) : IBeforeCommitHandler<TEntity> where TEntity : class
{
    public async ValueTask BeforeCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken)
    {
        log.Add($"before commit of {typeof(TEntity).Name}: {StageLog<TEntity>.Describe(rows)}");
        await script.BeforeCommit!(rows, cancellationToken);
    }
}

public sealed class OnAfterCommit<TEntity>(Script<TEntity> script, SaveLog log) : IAfterCommitHandler<TEntity> where TEntity : class
{
    public async ValueTask AfterCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken)
    {
        log.Add($"after commit of {typeof(TEntity).Name}: {StageLog<TEntity>.Describe(rows)}");
        await script.AfterCommit!(rows, cancellationToken);
    }
}

using ShiftSoftware.ShiftEntity.Core;
using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// Creates the source-generated mapper for a triple, if the registry holds one.
/// <para>
/// <b>The activator is cached; the instance never is.</b> Generated mappers carry per-instance
/// <c>ShiftMapperBuilder</c> state that <c>AddConfiguration</c> mutates, so a shared singleton would leak one
/// repository's customization into every other consumer of the same triple — a cross-request data bug that
/// would look like intermittent mis-mapping and be extremely hard to trace. Caching the compiled constructor
/// keeps the per-resolve cost to a delegate call while every repository still gets its own mapper.
/// </para>
/// </summary>
internal static class GeneratedMapperFactory
{
    private static readonly ConcurrentDictionary<Type, Func<object>> activators = new();

    public static IShiftEntityMapper<TEntity, TListDTO, TViewDTO>? Create<TEntity, TListDTO, TViewDTO>()
        where TEntity : ShiftEntity<TEntity>, new()
    {
        // Reflection scans do not run module initializers, and the registry is populated by one the generator
        // emits. Without this, a repository resolved before anything else touched the entity's assembly finds
        // an empty registry — intermittently, depending on what ran first.
        RuntimeHelpers.RunModuleConstructor(typeof(TEntity).Module.ModuleHandle);

        var mapperType = ShiftEntityMapperRegistry.Find(typeof(TEntity), typeof(TListDTO), typeof(TViewDTO));
        if (mapperType is null)
            return null;

        var activator = activators.GetOrAdd(mapperType, static t =>
            Expression.Lambda<Func<object>>(Expression.New(t)).Compile());

        return (IShiftEntityMapper<TEntity, TListDTO, TViewDTO>)activator();
    }
}

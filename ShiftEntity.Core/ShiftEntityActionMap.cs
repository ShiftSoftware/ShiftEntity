using ShiftSoftware.TypeAuth.Core.Actions;
using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Registry mapping entity CLR type names to the <see cref="ReadWriteDeleteAction"/> that secures
/// the entity's own CRUD endpoints. Cross-entity surfaces (for example the standalone attention
/// endpoints) read it to apply the same TypeAuth checks that the entity's own endpoints apply.
/// </summary>
/// <remarks>
/// <para>Three sources feed the map:</para>
/// <list type="bullet">
///   <item>Attribute endpoints (<c>[ShiftEntitySecureEndpoint&lt;…&gt;]</c>) are registered
///   automatically by <c>RegisterShiftRepositories</c>, which resolves the action from the
///   attribute's action tree.</item>
///   <item><c>MapShiftEntitySecureCrud</c> registers automatically at map time when it is called
///   with a non-null action.</item>
///   <item>Classic <c>ShiftEntitySecureControllerAsync</c> apps must call
///   <c>services.AddShiftEntityAction&lt;TEntity&gt;(action)</c> explicitly. The controller
///   receives its action through its constructor, so the framework cannot see the action at
///   startup.</item>
/// </list>
/// <para>
/// Thread-safe. Entity type names use the CLR short name with exact, case-sensitive matching —
/// the same keys as <see cref="ShiftEntityDtoMap"/>.
/// </para>
/// </remarks>
public sealed class ShiftEntityActionMap
{
    private readonly ConcurrentDictionary<string, ReadWriteDeleteAction> _entityToAction = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a mapping from an entity type name to its action. Registering the same entity
    /// type name again overwrites the previous action — the last registration is the one kept.
    /// </summary>
    public void Register(string entityTypeName, ReadWriteDeleteAction action)
    {
        _entityToAction[entityTypeName] = action;
    }

    /// <summary>Tries to get the action for the given entity type name.</summary>
    public bool TryGetAction(string entityTypeName, [MaybeNullWhen(false)] out ReadWriteDeleteAction action)
    {
        return _entityToAction.TryGetValue(entityTypeName, out action);
    }
}

using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ShiftSoftware.ShiftEntity.EFCore.Replication;

/// <summary>
/// Where each entity type sits in a context's replication trees, read once from the model: the root, and the
/// navigations from the root down to the type. A type in no tree is not in the index, so a save of it costs one
/// dictionary lookup.
/// </summary>
internal sealed class ReplicationTreeIndex
{
    /// <summary>One place of a type in a tree. <see cref="Path"/> is empty for the root itself.</summary>
    internal sealed record Place(IEntityType Root, IReadOnlyList<INavigation> Path)
    {
        public bool IsRoot => Path.Count == 0;

        /// <summary>A row that points at the root itself through its own foreign key, such as a link row.</summary>
        public bool IsChildOfRoot => Path is [{ IsCollection: true }];
    }

    private static readonly ConcurrentDictionary<Type, ReplicationTreeIndex> indexes = new();

    private readonly Dictionary<Type, List<Place>> places = new();

    public IReadOnlyDictionary<Type, ReplicationTree> Trees { get; }

    private ReplicationTreeIndex(IModel model, IReadOnlyDictionary<Type, ReplicationTree> trees)
    {
        Trees = trees;

        foreach (var tree in trees.Values)
            Walk(model, tree, Add);
    }

    /// <summary>
    /// Checks every tree of a model, so a tree of a shape a save cannot follow fails when the model is built rather
    /// than at a later save.
    /// </summary>
    public static void Validate(IModel model, IReadOnlyDictionary<Type, ReplicationTree> trees)
    {
        foreach (var tree in trees.Values)
            Walk(model, tree, (_, _) => { });
    }

    // Finds each type of a tree in the model and gives its place, and the same place to its derived types (a subtype
    // mapped to the same table sits where its base type does).
    //
    // A save finds the roots of a changed row after the write, walking down from the roots to the row's key. That finds
    // every root that loses a row only for these shapes: references at any depth, toward the row each foreign key points
    // at, and a collection directly under the root whose rows point at the root through a foreign key of their own
    // (link rows, whose key names the root they leave), followed by references. Below any other collection, or through a
    // reference to a dependent row, a deleted or moved row could no longer be reached from the roots that lose it.
    private static void Walk(IModel model, ReplicationTree tree, Action<Type, Place> add)
    {
        var root = model.FindEntityType(tree.RootType)
            ?? throw new InvalidOperationException($"The replication tree of '{tree.RootType.Name}' is declared on a type the model does not have.");

        CheckKey(tree, root);
        if (root.FindProperty("LastReplicationDate") is null)
            throw new InvalidOperationException($"The root of a replication tree must implement IShiftEntityReplication, and '{root.ClrType.Name}' does not.");

        AddWithDerived(root, new Place(root, []));

        foreach (var names in tree.Paths)
        {
            var navigations = new List<INavigation>();
            var current = root;
            foreach (var name in names)
            {
                var navigation = current.FindNavigation(name)
                    ?? throw new InvalidOperationException(
                        $"The replication tree of '{root.ClrType.Name}' names '{name}', which is not a navigation of '{current.ClrType.Name}'. " +
                        "Skip navigations (a many-to-many without its join entity) are not supported.");
                if (navigation.PropertyInfo is null)
                    throw new InvalidOperationException($"The navigation '{current.ClrType.Name}.{name}' of a replication tree must be a CLR property.");

                CheckStep(tree, navigations, navigation);
                navigations.Add(navigation);
                current = navigation.TargetEntityType;
            }

            CheckKey(tree, current);
            AddWithDerived(current, new Place(root, navigations));
        }

        void AddWithDerived(IEntityType type, Place place)
        {
            add(type.ClrType, place);
            foreach (var derived in type.GetDerivedTypes())
                add(derived.ClrType, place);
        }
    }

    private static void CheckStep(ReplicationTree tree, List<INavigation> before, INavigation navigation)
    {
        var path = string.Join(".", before.Select(x => x.Name).Append(navigation.Name));

        if (navigation.IsCollection)
        {
            if (before.Count > 0)
                throw Unsupported(tree, path, "a collection can only be the first step from the root");

            var foreignKey = navigation.ForeignKey;
            if (foreignKey.Properties is not [{ PropertyInfo: not null }] || !foreignKey.PrincipalKey.IsPrimaryKey())
                throw Unsupported(tree, path,
                    $"its rows must point at the root's key through a foreign key property of '{navigation.TargetEntityType.ClrType.Name}', " +
                    $"and they point at it through {string.Join(", ", foreignKey.Properties.Select(x => x.IsShadowProperty() ? $"the shadow property '{x.Name}'" : $"'{x.Name}'"))}");
        }
        else if (!navigation.IsOnDependent)
            throw Unsupported(tree, path,
                $"'{navigation.TargetEntityType.ClrType.Name}' depends on '{navigation.DeclaringEntityType.ClrType.Name}' (its foreign key points back), " +
                "and a reference can only lead to the row its own foreign key points at");
    }

    private static InvalidOperationException Unsupported(ReplicationTree tree, string path, string reason) => new(
        $"The replication tree of '{tree.RootType.Name}' cannot include '{path}': {reason}. A tree follows references toward the row " +
        "each foreign key points at, at any depth, and collections only directly under the root, whose rows point at the root through a " +
        "foreign key property of their own (such as link rows), followed by references. A save could not find the roots that lose a " +
        "row deleted or moved below any other step.");

    // The save and the mark find rows by a key property named ID.
    private static void CheckKey(ReplicationTree tree, IEntityType type)
    {
        var key = type.FindPrimaryKey()?.Properties;
        if (key is not [{ Name: "ID", PropertyInfo: not null } id] || id.ClrType != typeof(long))
            throw new InvalidOperationException(
                $"Every row type of the replication tree of '{tree.RootType.Name}', the root included, needs a single long key property named ID, " +
                $"and the key of '{type.ClrType.Name}' is {(key is null ? "missing" : string.Join(", ", key.Select(x => $"{x.Name} ({x.ClrType.Name})")))}.");
    }

    private void Add(Type type, Place place)
    {
        if (!places.TryGetValue(type, out var list))
            places[type] = list = new();
        list.Add(place);
    }

    /// <summary>The index of a context's trees, or null when its model declares none.</summary>
    public static ReplicationTreeIndex? For(ShiftDbContext context)
    {
        var trees = ReplicationTrees.For(context);
        if (trees.Count == 0)
            return null;

        return indexes.GetOrAdd(context.GetType(), _ => new ReplicationTreeIndex(context.Model, trees));
    }

    public IReadOnlyList<Place> PlacesOf(Type type) => places.TryGetValue(type, out var list) ? list : [];

    // ---- The SQL a save issues for a tree, inside its transaction ----

    // Holds the keys a statement filters on. EF reads a member of a constant as a parameter, so the statement's text
    // stays the same whatever the keys are.
    private sealed class Keys(List<long> values)
    {
        public readonly List<long> Values = values;
    }

    private delegate Task<List<long>> FindRoots(ShiftDbContext context, LambdaExpression predicate, bool async, CancellationToken cancellationToken);
    private delegate Task<int> MarkRoots(ShiftDbContext context, List<long> ids, bool async, CancellationToken cancellationToken);

    private static readonly ConcurrentDictionary<Type, (FindRoots Find, MarkRoots Mark)> statements = new();

    private static (FindRoots Find, MarkRoots Mark) StatementsFor(Type root) => statements.GetOrAdd(root, type =>
    (
        typeof(ReplicationTreeIndex).GetMethod(nameof(FindRootsAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type).CreateDelegate<FindRoots>(),
        typeof(ReplicationTreeIndex).GetMethod(nameof(MarkRootsAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type).CreateDelegate<MarkRoots>()
    ));

    /// <summary>
    /// The keys of the roots whose tree reaches a row of <paramref name="place"/>'s type with one of
    /// <paramref name="keys"/>: one query, walking down from the root along the place's navigations.
    /// </summary>
    public static Task<List<long>> FindRootsAsync(ShiftDbContext context, Place place, List<long> keys, bool async,
        CancellationToken cancellationToken) =>
        StatementsFor(place.Root.ClrType).Find(context, Predicate(place, new Keys(keys)), async, cancellationToken);

    /// <summary>
    /// Marks the roots as not replicated: one UPDATE of their watermark, and of nothing else, for the roots that are not
    /// marked already.
    /// </summary>
    /// <remarks>
    /// <para>
    /// LOUD: the mark has no version. It sets <c>LastReplicationDate</c> to null, and every tree sync and catch-up writes
    /// back the root's <c>LastSaveDate</c>, which a change of a part never moves. So a sync that loaded the root before
    /// this change, and records its watermark after this mark, clears the mark although its document predates the change.
    /// If the sync of this change then fails, or lands first, the document keeps the older tree while the root looks
    /// replicated, until the root's next save or a forced catch-up. It takes two changes of one root's tree within one
    /// sync's duration, as scripted or concurrent edits make. Scripts and imports that edit tree parts must run a forced
    /// catch-up afterwards.
    /// </para>
    /// <para>
    /// LOUD: root tables are often system-versioned, and every UPDATE of a root adds a version to its history. Marking
    /// only the roots that are not marked already keeps a burst of part changes from adding one each time; the revisions
    /// list hides versions that change no saved data. The mark also runs inside the part's save, after its own writes, so
    /// on a system-versioned table SQL Server refuses it with error 13535 when another transaction updated one of these
    /// roots and committed since this save's transaction began, for example a sync recording its watermark. The save then
    /// fails and rolls back as a whole, and saving it again on a new context succeeds.
    /// </para>
    /// </remarks>
    public static Task<int> MarkRootsAsync(ShiftDbContext context, Type root, List<long> ids, bool async, CancellationToken cancellationToken) =>
        StatementsFor(root).Mark(context, ids, async, cancellationToken);

    private static Task<List<long>> FindRootsAsync<TRoot>(ShiftDbContext context, LambdaExpression predicate, bool async,
        CancellationToken cancellationToken)
        where TRoot : class
    {
        var query = context.Set<TRoot>().IgnoreQueryFilters()
            .Where((Expression<Func<TRoot, bool>>)predicate)
            .Select(x => EF.Property<long>(x, "ID"));

        return async ? query.ToListAsync(cancellationToken) : Task.FromResult(query.ToList());
    }

    private static Task<int> MarkRootsAsync<TRoot>(ShiftDbContext context, List<long> ids, bool async, CancellationToken cancellationToken)
        where TRoot : class
    {
        var keys = new Keys(ids);
        var roots = context.Set<TRoot>().IgnoreQueryFilters()
            .Where(x => keys.Values.Contains(EF.Property<long>(x, "ID")) && EF.Property<DateTimeOffset?>(x, "LastReplicationDate") != null);

        return async
            ? roots.ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<DateTimeOffset?>(x, "LastReplicationDate"), (DateTimeOffset?)null),
                cancellationToken)
            : Task.FromResult(roots.ExecuteUpdate(s => s.SetProperty(x => EF.Property<DateTimeOffset?>(x, "LastReplicationDate"), (DateTimeOffset?)null)));
    }

    // root => root.A.B.Any(c => keys.Values.Contains(c.ID)), built from the place's navigations.
    private static LambdaExpression Predicate(Place place, Keys keys)
    {
        var root = Expression.Parameter(place.Root.ClrType, "root");
        var values = Expression.Field(Expression.Constant(keys), nameof(Keys.Values));
        return Expression.Lambda(Build(root, place.Root, 0), root);

        Expression Build(Expression current, IEntityType type, int step)
        {
            if (step == place.Path.Count)
            {
                var key = Expression.Property(current, type.FindPrimaryKey()!.Properties[0].PropertyInfo!);
                return Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(long)], values, key);
            }

            var navigation = place.Path[step];
            var target = navigation.TargetEntityType;
            var member = Expression.Property(current, navigation.PropertyInfo!);

            if (!navigation.IsCollection)
                return Build(member, target, step + 1);

            var element = Expression.Parameter(target.ClrType, "x" + step);
            var inner = Expression.Lambda(Build(element, target, step + 1), element);
            return Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), [target.ClrType], member, inner);
        }
    }
}

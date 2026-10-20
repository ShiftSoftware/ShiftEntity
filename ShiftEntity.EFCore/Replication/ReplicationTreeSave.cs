using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ShiftSoftware.ShiftEntity.EFCore.Replication;

/// <summary>
/// What one save means for the context's replication trees. Before the save it looks at the changed rows in memory:
/// which ones belong to a tree, and whether finding or marking their roots will take SQL, in which case the save runs
/// in a transaction. After the save it finds the roots of those rows and marks the ones that are not already dirty,
/// inside that transaction.
/// </summary>
/// <remarks>
/// <para>
/// It works from the rows the save had changed when it began, whether or not the save is replicated or accepts its
/// changes. Rows that a before-save step (a host trigger or interceptor) changes during the save are not followed.
/// </para>
/// <para>
/// A root the save wrote itself is dirty already when the save advanced its <c>LastSaveDate</c>, as a repository
/// save does, or when it is new; otherwise it is marked too. A row that points at its root through its own foreign
/// key (a link row) gives the root without a query. An edited row further down costs one query for each place of its
/// type in a tree. A new row there has no roots but the ones the save writes itself, and a deleted one has none left,
/// so neither costs anything. A change of nothing but a row's two replication columns is a sync recording what it
/// wrote, and changes no document: it is not followed either.
/// </para>
/// </remarks>
internal sealed class ReplicationTreeSave
{
    // The roots the save writes itself. A new root's key is known only after the save, so the entry is kept.
    private readonly List<(Type Root, EntityEntry Entry)> written = new();

    // The roots that link rows name by their own foreign key, including the root a moved link row leaves.
    private readonly List<(Type Root, long Id)> linked = new();

    // The edited rows further down, by place: their roots are found after the save, from the roots' side.
    private readonly Dictionary<ReplicationTreeIndex.Place, List<long>> edited = new();

    // Roots the save modifies without advancing LastSaveDate: they need marking although the save writes them.
    private readonly List<(Type Root, long Id)> rootsToMark = new();

    public bool NeedsTransaction { get; private set; }

    private ReplicationTreeSave() { }

    /// <summary>The tree work of the save about to run, or null when it changes no row of any tree.</summary>
    /// <param name="context">The context about to save.</param>
    /// <param name="detectChanges">
    /// Whether to detect changes while looking at the entries. The save's audit stamping has detected them already when
    /// it ran, and a second pass over every tracked row would cost the same again.
    /// </param>
    public static ReplicationTreeSave? Prepare(ShiftDbContext context, bool detectChanges)
    {
        if (ReplicationTreeIndex.For(context) is not { } index)
            return null;

        List<(EntityEntry Entry, IReadOnlyList<ReplicationTreeIndex.Place> Places)>? changed = null;
        var tracker = context.ChangeTracker;
        var autoDetect = tracker.AutoDetectChangesEnabled;
        try
        {
            if (!detectChanges)
                tracker.AutoDetectChangesEnabled = false;

            foreach (var entry in tracker.Entries())
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
                    index.PlacesOf(entry.Metadata.ClrType) is { Count: > 0 } places && !OnlyReplicationColumns(entry))
                    (changed ??= new()).Add((entry, places));
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = autoDetect;
        }

        if (changed is null)
            return null;

        // Roots the save writes and leaves dirty, or removes: edited ones whose LastSaveDate it advances, new ones and
        // deleted ones. (A link row of a new root still holds a temporary key, which says so.)
        var settledBySave = new HashSet<(Type, long)>();
        foreach (var (entry, places) in changed)
            foreach (var place in places)
                if (place.IsRoot && !entry.Property("ID").IsTemporary &&
                    (entry.State is EntityState.Added or EntityState.Deleted || entry.Property("LastSaveDate").IsModified))
                    settledBySave.Add((place.Root.ClrType, (long)entry.Property("ID").CurrentValue!));

        var save = new ReplicationTreeSave();

        foreach (var (entry, places) in changed)
            foreach (var place in places)
            {
                var root = place.Root.ClrType;

                if (place.IsRoot)
                {
                    save.written.Add((root, entry));
                    if (entry.State == EntityState.Modified && !entry.Property("LastSaveDate").IsModified)
                    {
                        save.rootsToMark.Add((root, (long)entry.Property("ID").CurrentValue!));
                        save.NeedsTransaction = true;
                    }
                    continue;
                }

                if (place.IsChildOfRoot)
                {
                    // A link row: its own foreign key names the root, also for a deleted row.
                    var foreignKey = entry.Property(place.Path[0].ForeignKey.Properties[0].Name);
                    if (!foreignKey.IsTemporary && foreignKey.CurrentValue is long current)
                        save.Link(root, current, settledBySave);

                    // A link row moved to another root: the root it leaves loses it.
                    if (entry.State == EntityState.Modified && foreignKey.IsModified && foreignKey.OriginalValue is long left)
                        save.Link(root, left, settledBySave);
                    continue;
                }

                if (entry.State == EntityState.Modified)
                {
                    if (!save.edited.TryGetValue(place, out var keys))
                        save.edited[place] = keys = new();
                    keys.Add((long)entry.Property("ID").CurrentValue!);
                    save.NeedsTransaction = true;
                }
            }

        return save;
    }

    private void Link(Type root, long id, HashSet<(Type, long)> settledBySave)
    {
        linked.Add((root, id));
        if (!settledBySave.Contains((root, id)))
            NeedsTransaction = true;
    }

    // A sync records what it wrote in these two columns, and a save of nothing else changes no document.
    private static bool OnlyReplicationColumns(EntityEntry entry) =>
        entry.State == EntityState.Modified &&
        entry.Properties.All(x => !x.IsModified || x.Metadata.Name is "LastReplicationDate" or "LastReplicationStamp");

    /// <summary>
    /// After the save: the roots, other than the ones the save wrote, whose tree reaches a row it changed, by root type.
    /// They and the roots in <see cref="rootsToMark"/> are marked as not replicated before this returns.
    /// </summary>
    public async Task<IReadOnlyDictionary<Type, IReadOnlyCollection<long>>> FindAndMarkRootsAsync(ShiftDbContext context, bool async,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<Type, HashSet<long>>();

        foreach (var (root, id) in linked)
            Add(found, root, id);

        foreach (var (place, keys) in edited)
            foreach (var root in await ReplicationTreeIndex.FindRootsAsync(context, place, keys, async, cancellationToken))
                Add(found, place.Root.ClrType, root);

        // The roots the save wrote are replicated as written rows, not as roots it changed.
        foreach (var (root, entry) in written)
            if (found.TryGetValue(root, out var ids) && entry.Property("ID").CurrentValue is long id)
                ids.Remove(id);

        var mark = found.ToDictionary(x => x.Key, x => new HashSet<long>(x.Value));
        foreach (var (root, id) in rootsToMark)
            Add(mark, root, id);

        foreach (var (root, ids) in mark)
            if (ids.Count > 0)
                await ReplicationTreeIndex.MarkRootsAsync(context, root, ids.ToList(), async, cancellationToken);

        return found.Where(x => x.Value.Count > 0)
            .ToDictionary(x => x.Key, x => (IReadOnlyCollection<long>)x.Value.Order().ToList());
    }

    private static void Add(Dictionary<Type, HashSet<long>> sets, Type root, long id)
    {
        if (!sets.TryGetValue(root, out var set))
            sets[root] = set = new();
        set.Add(id);
    }
}

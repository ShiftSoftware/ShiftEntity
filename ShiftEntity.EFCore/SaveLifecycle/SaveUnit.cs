namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>Work that waits for a unit's commit.</summary>
internal sealed class QueuedWork
{
    /// <summary>Work on its own: an <c>AfterCommit</c> call.</summary>
    public QueuedWork(Func<CancellationToken, Task> run) => Run = run;

    /// <summary>
    /// Work that the saves of one unit share: every piece queued under the same key runs as one, at the place of the
    /// first, with the payloads that are still in the log then.
    /// </summary>
    public QueuedWork(object key, object payload, Action<IReadOnlyList<object>> runTogether)
    {
        Key = key;
        Payload = payload;
        RunTogether = runTogether;
    }

    public Func<CancellationToken, Task>? Run { get; }
    public object? Key { get; }
    public object? Payload { get; }
    public Action<IReadOnlyList<object>>? RunTogether { get; }
    public bool Done { get; set; }
}

/// <summary>
/// One added or modified row that a save wrote and EF accepted: what the context remembers when the save is undone.
/// </summary>
/// <param name="Row">The tracked instance.</param>
/// <param name="Shared">
/// Whether its entity type has a shared CLR type, such as the join row of a many-to-many. Such a row is remembered only
/// when the save wrote no other row, because a row of the app's own type names a record the app knows.
/// </param>
internal readonly record struct WrittenRow(object Row, bool Shared)
{
    /// <summary>The one of the two to remember: the first, unless it is a shared-type row and the second is not.</summary>
    public static WrittenRow? Prefer(WrittenRow? first, WrittenRow? second) =>
        first is { Shared: true } && second is { Shared: false } ? second : first ?? second;
}

/// <summary>One entry of a unit's log: a save, or work queued for after the commit.</summary>
internal sealed class SaveLogItem
{
    public QueuedWork? Work { get; init; }

    /// <summary>The rows of a save that the handlers get when the unit commits. Null when no such handler has them.</summary>
    public List<SavedRow>? Rows { get; init; }

    /// <summary>Whether the save wrote a record more than once, so that even its own rows must be combined.</summary>
    public bool RowsRepeat { get; init; }

    /// <summary>One added or modified row of the save: what the context remembers when the save is undone.</summary>
    public WrittenRow? Written { get; init; }

    /// <summary>The stage methods that were running when the save was made. None of them gets its own records back.</summary>
    public RunningHandler[]? MadeBy { get; init; }

    /// <summary>
    /// The handlers that were switched off for the save, as the switches stood when it started. Its rows stay away from
    /// them through to the commit.
    /// </summary>
    public SaveSwitches? Switches { get; init; }
}

internal enum SaveUnitState
{
    Open,
    BeforeCommit,
    AfterCommit,
    Done,
}

/// <summary>
/// What commits together: one explicit transaction with every save made in it, or one save on its own. It keeps an
/// ordered log of its saves and of the work queued for after the commit, and runs the stages at the commit: before
/// commit, then after commit.
/// </summary>
/// <remarks>
/// A savepoint is a position in the log. A rollback to it, and a save that fails, cut the log back to where they
/// began, so the work that waited is dropped for exactly the saves that were undone. A rollback drops the log.
/// </remarks>
internal sealed class SaveUnit(ShiftDbContext db)
{
    /// <summary>How many rounds of before-commit handlers one commit runs, and how deep saves nest.</summary>
    public const int Limit = 16;

    private static readonly List<SavedRow> NoRows = [];

    private List<SaveLogItem>? log;

    // The app's savepoints: where the log stood at each, and a shallow copy of the bag as it was then.
    private List<(string Name, int Position, Dictionary<object, object?>? Items)>? savepoints;

    private Dictionary<object, object?>? items;
    private List<Exception>? failures;

    // How far the after-commit stage has run the log's work.
    private int drained;

    public ShiftDbContextTransaction? Transaction { get; set; }

    public SaveUnitState State { get; private set; }

    /// <summary>Why the unit can only be rolled back, or null while it can commit.</summary>
    public string? RollbackOnly { get; private set; }

    public IDictionary<object, object?> Items => items ??= [];

    /// <summary>
    /// A shallow copy of the bag as it is now, for a point that a failed save or a rollback to a savepoint goes back
    /// to. Null when the bag is empty, so that a unit whose handlers never use it copies nothing.
    /// </summary>
    public Dictionary<object, object?>? SnapshotItems() => items is { Count: > 0 } ? new(items, items.Comparer) : null;

    /// <summary>
    /// Puts the bag back as it was at a snapshot: the keys added since are removed, and the values replaced or removed
    /// since come back. An object that a handler changed in place stays as it is now.
    /// </summary>
    public void RestoreItems(Dictionary<object, object?>? snapshot)
    {
        if (items is not { Count: > 0 } && snapshot is null)
            return;

        var bag = items ??= [];
        bag.Clear();

        if (snapshot is not null)
            foreach (var (key, value) in snapshot)
                bag[key] = value;
    }

    public int Position => log?.Count ?? 0;

    /// <summary>Whether the unit has not committed yet, so what is queued in it waits.</summary>
    public bool IsOpen => State is SaveUnitState.Open or SaveUnitState.BeforeCommit;

    public void Queue(QueuedWork work) => (log ??= []).Add(new SaveLogItem { Work = work });

    public void Append(SaveLogItem save) => (log ??= []).Add(save);

    public void MarkRollbackOnly(string reason) => RollbackOnly ??= reason;

    public void Fail(Exception failure) => (failures ??= []).Add(failure);

    /// <summary>How many rows and how many pieces of work wait in the log, for a message.</summary>
    public (int Rows, int Work) Waiting()
    {
        int rows = 0, work = 0;

        if (log is not null)
        {
            foreach (var item in log)
            {
                rows += item.Rows?.Count ?? 0;
                if (item.Work is { Done: false })
                    work++;
            }
        }

        return (rows, work);
    }

    /// <summary>
    /// Cuts the log back to a position. Returns one added or modified row of the saves that were cut, or null when
    /// they held none: a row of a type with its own CLR type when one of them wrote one, and a shared-type row, such as
    /// a join row, only when none did.
    /// </summary>
    /// <param name="position">Where the log stood at the point that is rolled back to.</param>
    /// <param name="savepoints">How many savepoints of the app's own existed at that point. The later ones are gone.</param>
    public WrittenRow? CutTo(int position, int? savepoints = null)
    {
        if (savepoints is { } kept && this.savepoints is { } set && set.Count > kept)
            set.RemoveRange(kept, set.Count - kept);

        if (log is null || position >= log.Count)
            return null;

        WrittenRow? written = null;

        for (var i = position; i < log.Count && written is not { Shared: false }; i++)
            written = WrittenRow.Prefer(written, log[i].Written);

        log.RemoveRange(position, log.Count - position);
        this.savepoints?.RemoveAll(savepoint => savepoint.Position > position);

        return written;
    }

    /// <summary>The unit was rolled back: everything that waited is dropped.</summary>
    public WrittenRow? Drop()
    {
        var written = CutTo(0);
        savepoints = null;
        State = SaveUnitState.Done;
        return written;
    }

    public void MarkSavepoint(string name) => (savepoints ??= []).Add((name, Position, SnapshotItems()));

    /// <summary>How many savepoints of the app's own the unit has, and which of them a name means: the last one set.</summary>
    public int Savepoints => savepoints?.Count ?? 0;

    public int IndexOfSavepoint(string name) => savepoints?.FindLastIndex(savepoint => savepoint.Name == name) ?? -1;

    /// <summary>The savepoints the unit had when its before-commit stage began.</summary>
    public int SavepointsBeforeCommit { get; private set; }

    public void ReleaseSavepoint(string name)
    {
        if (savepoints is null)
            return;

        var at = savepoints.FindLastIndex(savepoint => savepoint.Name == name);

        // Releasing a savepoint releases the ones set after it too.
        if (at >= 0)
            savepoints.RemoveRange(at, savepoints.Count - at);
    }

    /// <summary>
    /// The app rolled back to a savepoint of its own: the saves made after it are undone, and the bag is as it was at
    /// the savepoint.
    /// </summary>
    public WrittenRow? RollbackToSavepoint(string name)
    {
        if (savepoints is null)
            return null;

        var at = savepoints.FindLastIndex(savepoint => savepoint.Name == name);

        if (at < 0)
            return null;

        // The savepoint itself stays, as it does in the database, and the app may roll back to it again.
        var (_, position, bag) = savepoints[at];
        savepoints.RemoveRange(at + 1, savepoints.Count - at - 1);
        RestoreItems(bag);

        return CutTo(position);
    }

    // ── After save ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Calls the after-save handlers of the rows one save wrote, except the ones that are switched off for the save.
    /// A failure leaves through here and fails the save.
    /// </summary>
    public async ValueTask RunAfterSaveAsync(SaveHandlerRegistry handlers, List<SavedRow> rows, bool repeat, SaveSwitches? switches,
        CancellationToken cancellationToken)
    {
        var written = repeat ? Combine([new SaveLogItem { Rows = rows, RowsRepeat = true }], 0, 1, null, exceptOwn: false) : rows;

        if (CallsOf(handlers, SaveStage.AfterSave, written) is not { } calls)
            return;

        foreach (var call in calls)
            if (!SaveSwitches.IsOff(switches, call))
                await InvokeAsync(handlers, call, written, cancellationToken);
    }

    // ── Before commit ───────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the before-commit handlers over the saves of the unit. A save a handler makes joins the unit, and its rows
    /// get a round of their own. A failure leaves through here: the commit must not happen.
    /// </summary>
    public async Task RunBeforeCommitAsync(CancellationToken cancellationToken)
    {
        if (db.SaveHandlers is not { } handlers || !HoldsRows(0))
            return;

        State = SaveUnitState.BeforeCommit;
        SavepointsBeforeCommit = Savepoints;
        var outer = db.EnterStage(this);

        try
        {
            var from = 0;

            for (var round = 1; ; round++)
            {
                var to = log!.Count;
                var rows = Combine(log, from, to, null, exceptOwn: false);

                if (CallsOf(handlers, SaveStage.BeforeCommit, rows) is not { Count: > 0 } calls || !AnyOn(calls, from, to))
                    break;

                if (round > Limit)
                    throw new InvalidOperationException(
                        $"The before-commit handlers of {db.GetType().Name} are still saving rows with before-commit handlers after {Limit} rounds: " +
                        $"{string.Join(", ", calls.Select(call => call.Name))}. A handler that saves in every round never lets the transaction commit.");

                await DispatchAsync(handlers, SaveStage.BeforeCommit, rows, from, to, exceptOwn: true, cancellationToken, calls);

                // Nothing was saved in this round, so no row is left that a handler hasn't seen.
                if (log.Count == to)
                    break;

                from = to;
            }
        }
        catch
        {
            MarkRollbackOnly("a before-commit handler failed.");
            throw;
        }
        finally
        {
            db.LeaveStage(outer);
            State = SaveUnitState.Open;
        }
    }

    // ── After commit ────────────────────────────────────────────────────────────

    /// <summary>
    /// The unit is committed. From here on nothing is undone: a failure is kept and reported at the end, and the rest
    /// still runs. Work queued from now on joins the end of the log.
    /// </summary>
    public SaveUnit? EnterAfterCommit()
    {
        State = SaveUnitState.AfterCommit;
        savepoints = null;
        return db.EnterStage(this);
    }

    /// <summary>
    /// Runs the queued work in the order it was queued, then the after-commit handlers. Throws one
    /// <see cref="AfterCommitException"/> when any step after the commit failed.
    /// </summary>
    public async Task CompleteAfterCommitAsync(SaveUnit? outer)
    {
        try
        {
            await DrainAsync();

            if (db.SaveHandlers is { } handlers && HoldsRows(0))
                await DispatchAsync(handlers, SaveStage.AfterCommit, Combine(log!, 0, log!.Count, null, exceptOwn: false), 0, log.Count,
                    exceptOwn: false, db.AfterCommitToken);
        }
        catch (Exception failure)
        {
            // Not a handler's failure, which is kept where it happens: the stage itself failed.
            Fail(failure);
        }
        finally
        {
            db.LeaveStage(outer);
            State = SaveUnitState.Done;
            log = null;
        }

        if (failures is { Count: > 0 } failed)
        {
            failures = null;
            throw new AfterCommitException(failed);
        }
    }

    // Runs the work that is in the log and has not run yet. A piece that queues more work adds it to the end.
    private async Task DrainAsync()
    {
        if (log is null)
            return;

        var token = db.AfterCommitToken;

        for (; drained < log.Count; drained++)
        {
            if (log[drained].Work is not { Done: false } work)
                continue;

            try
            {
                if (work.RunTogether is { } together)
                {
                    var payloads = new List<object>();

                    for (var i = drained; i < log.Count; i++)
                    {
                        if (log[i].Work is { Done: false, RunTogether: not null } piece && Equals(piece.Key, work.Key))
                        {
                            piece.Done = true;
                            payloads.Add(piece.Payload!);
                        }
                    }

                    together(payloads);
                }
                else
                {
                    work.Done = true;
                    await work.Run!(token);
                }
            }
            catch (Exception failure)
            {
                Fail(failure);
            }
        }
    }

    // ── Rows and handlers ───────────────────────────────────────────────────────

    private bool HoldsRows(int from)
    {
        if (log is null)
            return false;

        for (var i = from; i < log.Count; i++)
            if (log[i].Rows is { Count: > 0 })
                return true;

        return false;
    }

    // The calls of a stage that some row among these has, in the order they run.
    private static List<SaveHandlerCall>? CallsOf(SaveHandlerRegistry handlers, SaveStage stage, List<SavedRow> rows)
    {
        List<SaveHandlerCall>? calls = null;
        Type? lastType = null;

        foreach (var row in rows)
        {
            if (row.EntityType == lastType || row.IsOwnedPart)
                continue;

            lastType = row.EntityType;

            foreach (var call in handlers.For(row.EntityType).For(stage))
                if (calls is null || !calls.Contains(call))
                    (calls ??= []).Add(call);
        }

        calls?.Sort(static (a, b) => a.Rank.CompareTo(b.Rank));
        return calls;
    }

    // Whether one of the calls still runs for one of the saves [from, to): a call that is switched off for every one of
    // them has no rows.
    private bool AnyOn(List<SaveHandlerCall> calls, int from, int to)
    {
        var switched = false;

        for (var i = from; i < to; i++)
        {
            if (log![i].Rows is not { Count: > 0 })
                continue;

            if (log[i].Switches is not { } switches)
                return true;

            switched = true;

            foreach (var call in calls)
                if (!SaveSwitches.IsOff(switches, call))
                    return true;
        }

        return !switched;
    }

    // Whether a save among [from, to) had handlers switched off, so each handler's rows are worked out for it alone.
    private bool Switched(int from, int to)
    {
        for (var i = from; i < to; i++)
            if (log![i].Switches is not null && log[i].Rows is { Count: > 0 })
                return true;

        return false;
    }

    // Calls each handler of a stage at the commit once, with its rows: the net rows over the saves [from, to) that the
    // handler was not switched off for. After the commit a failure is kept and the next handler still runs; before it,
    // the failure leaves through here.
    private async ValueTask DispatchAsync(SaveHandlerRegistry handlers, SaveStage stage, List<SavedRow> rows, int from, int to, bool exceptOwn,
        CancellationToken cancellationToken, List<SaveHandlerCall>? calls = null)
    {
        calls ??= CallsOf(handlers, stage, rows);

        if (calls is null)
            return;

        var switched = Switched(from, to);

        foreach (var call in calls)
        {
            // In a later round of before commit, a handler does not get back the records it saved itself.
            var own = exceptOwn && MadeBy(call, from, to);
            var mine = switched || own ? Combine(log!, from, to, call, own) : rows;

            if (stage != SaveStage.AfterCommit)
            {
                await InvokeAsync(handlers, call, mine, cancellationToken);
                continue;
            }

            try
            {
                await InvokeAsync(handlers, call, mine, cancellationToken);
            }
            catch (Exception failure)
            {
                Fail(failure);
            }

            await DrainAsync();
        }
    }

    private bool MadeBy(SaveHandlerCall call, int from, int to)
    {
        for (var i = from; i < to; i++)
            if (log![i].MadeBy is { } running && Array.Exists(running, handler => handler.Call == call))
                return true;

        return false;
    }

    private async ValueTask InvokeAsync(SaveHandlerRegistry handlers, SaveHandlerCall call, List<SavedRow> rows, CancellationToken cancellationToken)
    {
        // The rows of this call among the stage's rows. While every row so far is one, nothing is allocated.
        int[]? picked = null;
        var count = 0;
        Type? lastType = null;
        var lastTypeHasCall = false;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            if (row.EntityType != lastType)
            {
                lastType = row.EntityType;
                lastTypeHasCall = Array.IndexOf(handlers.For(lastType).For(call.Stage), call) >= 0;
            }

            if (lastTypeHasCall && !row.IsOwnedPart && !db.IsHandling(call, row))
            {
                if (picked is not null)
                    picked[count] = i;

                count++;
                continue;
            }

            if (picked is null)
            {
                picked = new int[rows.Count];

                for (var before = 0; before < count; before++)
                    picked[before] = before;
            }
        }

        if (count == 0)
            return;

        var handler = db.CreateHandler(call, out var owned);
        var running = new RunningHandler(call, rows, picked, count);

        db.Running(running);

        try
        {
            await call.InvokeAsync(handler, db, this, new SavedRowView(rows, picked, count, call.NeedsOriginal, call.HandlerType), cancellationToken);
        }
        finally
        {
            db.Ran(running);

            if (owned)
                await ShiftDbContext.DisposeHandlerAsync(handler);
        }
    }

    // One row per record over the saves [from, to) of a log. A record is matched by its entity type and key, the row's
    // record is the latest instance, and its Original is what the record held before the first of those saves. For one
    // handler, the saves it was switched off for are left out, and with exceptOwn so are the rows it saved itself while
    // it was handling them.
    private List<SavedRow> Combine(List<SaveLogItem> saves, int from, int to, SaveHandlerCall? forCall, bool exceptOwn)
    {
        var exceptFor = exceptOwn ? forCall : null;

        SaveLogItem? only = null;
        var count = 0;

        for (var i = from; i < to; i++)
        {
            if (saves[i].Rows is { Count: > 0 } && !(forCall is not null && SaveSwitches.IsOff(saves[i].Switches, forCall)))
            {
                only = saves[i];
                count++;
            }
        }

        if (count == 0)
            return NoRows;

        // One save that wrote each record once holds its net rows already.
        if (count == 1 && !only!.RowsRepeat && exceptFor is null)
            return only.Rows!;

        var model = db.Model;
        var net = new List<SavedRow>();
        var places = new Dictionary<RowKey, int>();

        for (var i = from; i < to; i++)
        {
            var save = saves[i];

            if (save.Rows is null || (forCall is not null && SaveSwitches.IsOff(save.Switches, forCall)))
                continue;

            foreach (var row in save.Rows)
            {
                if (exceptFor is not null && save.MadeBy is { } running && Array.Exists(running, handler => handler.Call == exceptFor && handler.Handles(model, row)))
                    continue;

                var key = RowKey.Of(model, row);

                if (!places.TryGetValue(key, out var at) || at < 0)
                {
                    places[key] = net.Count;
                    net.Add(row);
                    continue;
                }

                if (Merge(net[at], row) is { } merged)
                {
                    net[at] = merged;
                }
                else
                {
                    // Added and deleted again in the unit: the record was never committed.
                    net[at] = default;
                    places[key] = -1;
                }
            }
        }

        net.RemoveAll(static row => row.Entity is null);
        return net;
    }

    private static SavedRow? Merge(SavedRow first, SavedRow next) => (first.Change, next.Change) switch
    {
        (SavedRowChange.Added, SavedRowChange.Deleted) => null,
        (SavedRowChange.Added, _) => next with { Change = SavedRowChange.Added, Original = null, Flags = next.Flags & SavedRowFlags.OwnedPart },
        (SavedRowChange.Modified, SavedRowChange.Modified) => next with { Original = first.Original, Flags = Fold(first.Flags, next.Flags) },
        (SavedRowChange.Modified, SavedRowChange.Deleted) => next with { Original = first.Original, Flags = next.Flags & SavedRowFlags.OwnedPart },

        // Deleted, and added again with the same key: the record is still there, changed.
        (_, SavedRowChange.Added) => next with { Change = SavedRowChange.Modified, Original = first.Original },
        _ => next with { Original = first.Original },
    };

    // Whether the unit as a whole turned IsDeleted on or off: two saves that turned it on and off again did neither.
    private static SavedRowFlags Fold(SavedRowFlags first, SavedRowFlags next)
    {
        const SavedRowFlags deletion = SavedRowFlags.SoftDeleted | SavedRowFlags.Restored;

        var before = first & deletion;
        var after = next & deletion;
        var net = after == 0 ? before : before == 0 ? after : before == after ? after : 0;

        return (next & ~deletion) | net;
    }
}

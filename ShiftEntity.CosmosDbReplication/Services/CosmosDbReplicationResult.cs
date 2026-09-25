using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;

/// <summary>
/// What one catch-up run did: <see cref="Selected"/> rows were loaded, <see cref="Replicated"/> of them were recorded as
/// replicated, and each entry in <see cref="Failures"/> is a row that failed in one container. A failed row stays dirty,
/// so the next run retries it.
/// </summary>
public sealed class CosmosDbReplicationResult
{
    internal CosmosDbReplicationResult(string entity, int selected, int replicated,
        IReadOnlyList<CosmosDbReplicationFailure> failures)
    {
        Entity = entity;
        Selected = selected;
        Replicated = replicated;
        Failures = failures;
    }

    public string Entity { get; }

    public int Selected { get; }

    public int Replicated { get; }

    /// <summary>One entry per row and container that failed, ordered by row.</summary>
    public IReadOnlyList<CosmosDbReplicationFailure> Failures { get; }

    /// <summary>Rows with at least one failure. A row can fail in more than one container.</summary>
    public int Failed => Failures.Select(x => x.EntityId).Distinct().Count();

    public bool Succeeded => Failures.Count == 0;

    /// <summary>Throws <see cref="CosmosDbReplicationException"/> when a row failed, for a caller that must fail too.</summary>
    public void ThrowIfFailed()
    {
        if (!Succeeded)
            throw new CosmosDbReplicationException(this);
    }

    public override string ToString() => Succeeded
        ? $"{Entity}: {Replicated} of {Selected} rows replicated."
        : $"{Entity}: {Failed} of {Selected} rows failed and stay dirty for the next run. {DescribeFailures()}";

    internal string DescribeFailures()
    {
        const int listed = 20;
        var text = string.Join(" ", Failures.Take(listed).Select(x => $"ID {x.EntityId} in '{x.ContainerId}': {x.Error}"));
        return Failures.Count > listed ? $"{text} And {Failures.Count - listed} more." : text;
    }
}

/// <summary>A row that failed in one container, and why.</summary>
public sealed record CosmosDbReplicationFailure(long EntityId, string ContainerId, string Error);

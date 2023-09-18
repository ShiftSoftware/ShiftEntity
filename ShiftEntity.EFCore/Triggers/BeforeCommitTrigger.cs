using EntityFrameworkCore.Triggered;
using EntityFrameworkCore.Triggered.Transactions;
using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore.Triggers;

internal class BeforeCommitTrigger<Entity> : IBeforeCommitTrigger<Entity> where Entity : ShiftEntity<Entity>
{
    public Task BeforeCommit(ITriggerContext<Entity> context, CancellationToken cancellationToken)
    {
        if (context.Entity.BeforeCommitValidation != null)
            context.Entity.BeforeCommitValidation(context.Entity);

        return Task.CompletedTask;
    }
}

using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore.Triggers;

internal class GeneralTrigger<Entity> : IBeforeSaveTrigger<Entity>
    where Entity : ShiftEntity<Entity>
{
    public Task BeforeSave(ITriggerContext<Entity> context, CancellationToken cancellationToken)
    {
        if (context.ChangeType == ChangeType.Added)
        {
            var now = DateTime.UtcNow;

            context.Entity.LastSaveDate = now;
            context.Entity.CreateDate = now;

            context.Entity.IsDeleted = false;
        }

        if (context.ChangeType == ChangeType.Modified)
        {
            var now = DateTime.UtcNow;

            context.Entity.LastSaveDate = now;
        }

        return Task.CompletedTask;
    }
}

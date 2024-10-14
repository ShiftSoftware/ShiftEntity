using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore.Triggers;

internal class GeneralTrigger<Entity> : IBeforeSaveTrigger<Entity> where Entity : ShiftEntity<Entity>
{
    public Task BeforeSave(ITriggerContext<Entity> context, CancellationToken cancellationToken)
    {
        if (context.ChangeType == ChangeType.Added)
        {
            var now = DateTime.UtcNow;


            //context.Entity.Create() method allows setting the CreateDate and LastSaveDate properties
            //If it's set, we don't override it

            if (context.Entity.LastSaveDate == default)
                context.Entity.LastSaveDate = now;

            if (context.Entity.CreateDate == default)
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

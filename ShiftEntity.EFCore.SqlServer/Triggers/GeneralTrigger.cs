using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore.SqlServer.Triggers;

internal class GeneralTrigger : IBeforeSaveTrigger<ShiftEntityBase>
{
    public Task BeforeSave(ITriggerContext<ShiftEntityBase> context, CancellationToken cancellationToken)
    {        
        if (context.ChangeType == ChangeType.Added)
        {
            var now = DateTime.UtcNow;

            context.Entity.LastSaveDate = now;
            context.Entity.CreateDate = now;

            context.Entity.IsDeleted = false;
        }

        if(context.ChangeType==ChangeType.Modified)
        {
            var now = DateTime.UtcNow;

            context.Entity.LastSaveDate = now;
        }

        return Task.CompletedTask;
    }
}

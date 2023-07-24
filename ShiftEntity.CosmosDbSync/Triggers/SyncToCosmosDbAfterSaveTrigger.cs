using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.CosmosDbSync.Triggers;


internal class SyncToCosmosDbAfterSaveTrigger<Entity> : IAfterSaveTrigger<Entity>
    where Entity : ShiftEntity<Entity>
{
    public SyncToCosmosDbAfterSaveTrigger()
    {

    }
    public async Task AfterSave(ITriggerContext<Entity> context, CancellationToken cancellationToken)
    {
        //Do the actual sync
    }
}
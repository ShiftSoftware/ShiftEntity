using AutoMapper;
using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore.Triggers;

internal class ReloadAfterSaveTrigger<Entity> : IAfterSaveTrigger<Entity> where Entity : ShiftEntity<Entity>
{
    private readonly IShiftEntityFind<Entity>? shiftEntityFind;
    private readonly IMapper mapper;

    public ReloadAfterSaveTrigger(IMapper mapper, IShiftEntityFind<Entity>? shiftEntityFind=null)
    {
        this.shiftEntityFind = shiftEntityFind;
        this.mapper = mapper;
    }

    public async Task AfterSave(ITriggerContext<Entity> context, CancellationToken cancellationToken)
    {
        if (context.Entity.ReloadAfterSave)
        {
            if (context.ChangeType == ChangeType.Modified || context.ChangeType == ChangeType.Added)
            {
                if (shiftEntityFind is not null)
                {
                    var entity = context.Entity;

                    if(shiftEntityFind is not null)
                        entity = await shiftEntityFind.FindAsync(context.Entity.ID);

                    if (entity is not null)
                    {
                        mapper.Map(entity, context.Entity);
                    }
                }
            }
        }
    }
}

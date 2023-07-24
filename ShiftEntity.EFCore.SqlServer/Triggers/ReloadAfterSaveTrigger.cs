using AutoMapper;
using EntityFrameworkCore.Triggered;
using ShiftSoftware.EFCore.SqlServer;
using ShiftSoftware.ShiftEntity.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.EFCore.SqlServer.Triggers;

internal class ReloadAfterSaveTrigger<Entity> : IAfterSaveTrigger<Entity>
    where Entity : ShiftEntity<Entity>
{
    private readonly IShiftEntityFind<Entity>? shiftEntityFind;
    private readonly IMapper mapper;

    public ReloadAfterSaveTrigger(IShiftEntityFind<Entity>? shiftEntityFind, IMapper mapper)
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
                    var entity = await shiftEntityFind.FindAsync(context.Entity.ID);

                    if (entity is not null)
                    {
                        mapper.Map(entity, context.Entity);
                    }
                }
            }
        }
    }
}

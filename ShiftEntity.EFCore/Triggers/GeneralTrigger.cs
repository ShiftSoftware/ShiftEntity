using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore.Triggers;

internal class GeneralTrigger<Entity> : IBeforeSaveTrigger<Entity> where Entity : ShiftEntity<Entity>, new()
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
        
        //if (context.ChangeType == ChangeType.Added || context.ChangeType == ChangeType.Modified)
        //{
        //    if (typeof(Entity).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasUniqueHash<Entity>))))
        //    {
        //        var entryWithUniqueHash = (context.Entity as IEntityHasUniqueHash<Entity>)!;

        //        var uniqueHash = entryWithUniqueHash.CalculateUniqueHash();

        //        if (uniqueHash != null)
        //        {
        //            using var sha256 = System.Security.Cryptography.SHA512.Create();

        //            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(uniqueHash));

        //            context.Entity.Property("UniqueHash").CurrentValue = hashBytes;
        //        }
        //    }
        //}
        

        return Task.CompletedTask;
    }
}

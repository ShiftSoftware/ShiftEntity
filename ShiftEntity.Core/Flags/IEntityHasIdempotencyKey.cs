using System;

namespace ShiftSoftware.ShiftEntity.Core.Flags;

public interface IEntityHasIdempotencyKey<Entity> : IEntityHasIdempotencyKey
    where Entity : ShiftEntityBase, new()
{
    
}

public interface IEntityHasIdempotencyKey
{
    Guid? IdempotencyKey { get; set; }
}
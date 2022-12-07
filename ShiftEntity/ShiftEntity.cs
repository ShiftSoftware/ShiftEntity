﻿using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core.Dtos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public abstract class ShiftEntity<EntityType> : IShiftEntity
    where EntityType : class
{
    public Guid ID { get; private set; }
    public DateTime CreateDate { get; private set; }
    public DateTime LastSaveDate { get; private set; }
    public Guid? CreatedByUserID { get; private set; }
    public Guid? LastSavedByUserID { get; private set; }
    public bool IsDeleted { get; private set; }
    
    public EntityType CreateShiftEntity(Guid? userId = null)
    {
        var now = DateTime.UtcNow;

        LastSaveDate = now;
        CreateDate = now;

        CreatedByUserID = userId;
        LastSavedByUserID = userId;

        IsDeleted = false;

        return this as EntityType;
    }

    public EntityType UpdateShiftEntity(Guid? userId = null)
    {
        var now = DateTime.UtcNow;

        LastSaveDate = now;
        LastSavedByUserID = userId;

        return this as EntityType;
    }

    public EntityType DeleteShiftEntity(Guid? userId = null)
    {
        UpdateShiftEntity(userId);

        IsDeleted = true;

        return this as EntityType;
    }

    public async Task<List<RevisionDTO>> GetRevisionsAsync<T>(DbSet<T> dbSet, Guid id) where T : ShiftEntity<T>
    {
        var items = await dbSet
                .TemporalAll()
                .Where(x => x.ID == id)
                .Select(x => new RevisionDTO
                {
                    ValidFrom = EF.Property<DateTime>(x, "PeriodStart"),
                    ValidTo = EF.Property<DateTime>(x, "PeriodEnd"),
                    SavedByUserID = x.LastSavedByUserID,
                })
                .OrderByDescending(x => x.ValidTo)
                .ToListAsync();

        return items;
    }

    public async Task<T> FindAsync<T>(DbSet<T> dbSet, Guid id, DateTime? asOf) where T : ShiftEntity<T>
    {
        T? item;

        if (asOf == null)
            item = await dbSet.FindAsync(id);
        else
            item = await dbSet.TemporalAsOf(asOf.Value).FirstOrDefaultAsync(x => x.ID == id);

        return item;
    }
}

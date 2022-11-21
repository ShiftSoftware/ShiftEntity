using System;

namespace ShiftEntity.Core
{
    public interface IShiftEntity
    {

    }

    public abstract class ShiftEntity<EntityType, CrudDTOType> : IShiftEntity
        where EntityType : class
        where CrudDTOType : ICrudDTO
    {
        public Guid ID { get; private set; }
        public DateTime CreateDate { get; private set; }
        public DateTime LastSaveDate { get; private set; }
        public Guid? CreatedByUserID { get; private set; }
        public Guid? LastSavedByUserID { get; private set; }
        public bool IsDeleted { get; private set; }
        public abstract EntityType Create(CrudDTOType crudDto, Guid? userId = null);
        public abstract EntityType Update(CrudDTOType crudDto, Guid? userId = null);
        public abstract EntityType Delete(Guid? userId = null);

        protected EntityType CreateShiftEntity(Guid? userId = null)
        {
            var now = DateTime.UtcNow;

            LastSaveDate = now;
            CreateDate = now;

            CreatedByUserID = userId;
            LastSavedByUserID = userId;

            IsDeleted = false;

            return this as EntityType;
        }

        protected EntityType UpdateShiftEntity(Guid? userId = null)
        {
            var now = DateTime.UtcNow;

            LastSaveDate = now;
            LastSavedByUserID = userId;

            return this as EntityType;
        }

        protected EntityType DeleteShiftEntity(Guid? userId = null)
        {
            UpdateShiftEntity(userId);

            IsDeleted = true;

            return this as EntityType;
        }
    }

    public interface ICrudDTO
    {

    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class TemporalShiftEntity : Attribute
    {
    }
}

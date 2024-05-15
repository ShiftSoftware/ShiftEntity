namespace ShiftSoftware.ShiftEntity.Core.Flags;

public interface IEntityHasCompany<Entity>
    where Entity : ShiftEntityBase, new()
{
    long? CompanyID { get; set; }
}
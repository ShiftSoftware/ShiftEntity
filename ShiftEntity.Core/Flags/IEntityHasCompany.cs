namespace ShiftSoftware.ShiftEntity.Core.Flags;

public interface IEntityHasCompany<Entity>
    
{
    long? CompanyID { get; set; }
}
namespace ShiftSoftware.ShiftEntity.Model.Flags;

public interface IEntityHasCompany<Entity>
    
{
    long? CompanyID { get; set; }
}
namespace ShiftSoftware.ShiftEntity.Core.Flags;

public interface IEntityHasCompanyBranch<Entity>
    where Entity : ShiftEntityBase, new()
{
    long? CompanyBranchID { get; set; }
}

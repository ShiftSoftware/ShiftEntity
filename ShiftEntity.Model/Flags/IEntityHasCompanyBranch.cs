namespace ShiftSoftware.ShiftEntity.Model.Flags;

/// <summary>Non-generic seam carrying <c>CompanyBranchID</c> so the repository can stamp it on any entity, regardless of its closed generic type.</summary>
public interface IEntityHasCompanyBranch
{
    long? CompanyBranchID { get; set; }
}

public interface IEntityHasCompanyBranch<Entity> : IEntityHasCompanyBranch
{
}

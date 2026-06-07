namespace ShiftSoftware.ShiftEntity.Model.Flags;

/// <summary>Non-generic seam carrying <c>CompanyID</c> so the repository can stamp it on any entity, regardless of its closed generic type.</summary>
public interface IEntityHasCompany
{
    long? CompanyID { get; set; }
}

public interface IEntityHasCompany<Entity> : IEntityHasCompany
{
}

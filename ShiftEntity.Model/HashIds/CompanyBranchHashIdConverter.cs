namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class CompanyBranchHashIdConverter : JsonHashIdConverterAttribute<CompanyBranchHashIdConverter>
{
    public CompanyBranchHashIdConverter() : base(configurationName: IdentityConfigurationName)
    {
    }
}

namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class CompanyHashIdConverter : JsonHashIdConverterAttribute<CompanyHashIdConverter>
{
    public CompanyHashIdConverter() : base(configurationName: IdentityConfigurationName)
    {
    }
}

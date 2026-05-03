namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class AccessTreeHashIdConverter : JsonHashIdConverterAttribute<AccessTreeHashIdConverter>
{
    public AccessTreeHashIdConverter() : base(configurationName: IdentityConfigurationName)
    {
    }
}
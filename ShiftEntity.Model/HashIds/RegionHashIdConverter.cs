namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class RegionHashIdConverter : JsonHashIdConverterAttribute<RegionHashIdConverter>
{
    public RegionHashIdConverter() : base(configurationName: IdentityConfigurationName)
    {
    }
}

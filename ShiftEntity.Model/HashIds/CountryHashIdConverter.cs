namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class CountryHashIdConverter : JsonHashIdConverterAttribute<CountryHashIdConverter>
{
    public CountryHashIdConverter() : base(configurationName: IdentityConfigurationName)
    {
    }
}

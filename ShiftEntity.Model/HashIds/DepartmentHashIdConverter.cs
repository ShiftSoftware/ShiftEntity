namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class DepartmentHashIdConverter : JsonHashIdConverterAttribute<DepartmentHashIdConverter>
{
    public DepartmentHashIdConverter() : base(configurationName: IdentityConfigurationName)
    {
    }
}

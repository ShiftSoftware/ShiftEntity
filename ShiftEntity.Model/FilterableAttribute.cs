
namespace ShiftSoftware.ShiftEntity.Model;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public class FilterableAttribute : Attribute
{
    public bool Immediate { get; private set; }
    public bool Disabled { get; private set; }

    public FilterableAttribute(bool immediate = false, bool disable = false)
    {
        Immediate = immediate;
        Disabled = disable;
    }
}

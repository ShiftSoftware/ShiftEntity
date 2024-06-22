namespace ShiftSoftware.ShiftEntity.Functions.AppCheck;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class CheckAppCheckAttribute : Attribute
{
    public CheckAppCheckAttribute()
    {
    }
}
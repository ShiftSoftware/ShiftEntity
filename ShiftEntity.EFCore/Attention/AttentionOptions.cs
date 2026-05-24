namespace ShiftSoftware.ShiftEntity.EFCore.Attention;

public class AttentionOptions
{
    public TimeSpan ReRaiseWindow { get; set; } = TimeSpan.MaxValue;
}

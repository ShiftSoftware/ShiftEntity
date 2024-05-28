namespace ShiftSoftware.ShiftEntity.Functions.ReCaptcha;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class CheckReCaptchaAttribute : Attribute
{
    public CheckReCaptchaAttribute()
    {
    }

    public CheckReCaptchaAttribute(double minScore)
    {
        MinScore = minScore;
    }

    public double? MinScore { get; }
}
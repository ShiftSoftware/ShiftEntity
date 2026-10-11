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

    /// <summary>
    /// When true, the outcome of each verification (score, min score, action, hostname, error codes) is logged.
    /// Disabled by default.
    /// </summary>
    public bool LogResult { get; set; }
}
namespace ShiftSoftware.ShiftEntity.Functions.ReCaptcha;

public class ReCaptchaOptions
{
    public string HeaderKey { get; set; } = default!;
    public string SecretKey { get; set; } = default!;
    public double MinScore { get; set; }
}
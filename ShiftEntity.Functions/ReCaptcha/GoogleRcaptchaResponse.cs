namespace ShiftSoftware.ShiftEntity.Functions.ReCaptcha;

public class GoogleRcaptchaResponse
{
    public bool Success { get; set; }
    public double Score { get; set; }
    public string Action { get; set; } = default!;
    public DateTime Challenge_Ts { get; set; }
    public string HostName { get; set; } = default!;
}
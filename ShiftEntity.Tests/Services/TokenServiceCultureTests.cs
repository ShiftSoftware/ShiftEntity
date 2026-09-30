using ShiftSoftware.ShiftEntity.Core.Services;
using System.Globalization;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Services;

/// <summary>
/// A SAS token must not depend on the culture of the thread. One request signs it (the print-token endpoint, for
/// example) and a later request checks it (the print endpoint). A host that applies request localization runs each
/// request under the culture that the request asks for. The signed text carries the expiry as
/// "yyyy-MM-dd.HH-mm-ss-ffff". Before this was fixed, that text used the calendar of the current culture: 2026 was
/// 2569 under th-TH, 1405 under fa-IR and 1448 under ar-SA, and DateTime.MaxValue could not be written under ar-SA at
/// all. The check reads the text back as a Gregorian date. Then it writes the date again under its own culture and
/// signs that. Under en-US, the check read a year of 1405 or 1448 and refused the token as expired, or read 2569 and
/// accepted the token until 543 years after it expired. Under th-TH, fa-IR or ar-SA, the check wrote the year again in
/// its own calendar, so no token matched, not even one signed under the same culture.
/// </summary>
public class TokenServiceCultureTests
{
    private const string Descriptor = "/api/sample/print-token/k3Yp9";
    private const string Id = "k3Yp9";
    private const string Key = "test-sas-key";

    private static readonly DateTime Expiry = new DateTime(2070, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234567);

    // What the code before this change returned for Expiry and for DateTime.MaxValue under en-US. The invariant
    // culture gave the same values. Hosts on the earlier code issue these tokens, so the current code must produce the
    // same bytes. A token that never expires may be printed (in a QR code, for example), and a printed token cannot
    // be replaced.
    private const string RecordedExpires = "2070-01-02.03-04-05-1234";
    private const string RecordedToken = "a3e56d6696a7fb339f5e229ae2b808a4b5f0e3e80bb9097b7ef78d9d58b7809e";
    private const string RecordedNeverExpires = "9999-12-31.23-59-59-9999";
    private const string RecordedNeverExpiringToken = "6b5b3beff87605ae45ef2f43aca56b44e4097ab37b64f963391662d44c7cd350";

    /// <summary>
    /// en-US and the invariant culture, where the earlier code already gave the recorded values. Other Gregorian
    /// cultures gave them too (tr-TR has its own lower case for "I", and the token is lower-case hex). Then one culture
    /// for each of the three other default calendars: Thai Buddhist (th-TH), Persian (fa-IR) and Umm al-Qura (ar-SA).
    /// </summary>
    public static TheoryData<string> AllCultures => new()
    {
        "en-US", "invariant", "ru", "ar-IQ", "ku-IQ", "tr-TR", "th-TH", "fa-IR", "ar-SA",
    };

    /// <summary>
    /// The culture that signs and the culture that checks. Each of the three other calendars is paired with en-US in
    /// both directions, and with itself (the same browser asks for a token and then opens the URL).
    /// </summary>
    public static TheoryData<string, string> SigningAndCheckingCultures => new()
    {
        { "th-TH", "en-US" }, { "fa-IR", "en-US" }, { "ar-SA", "en-US" },
        { "en-US", "th-TH" }, { "en-US", "fa-IR" }, { "en-US", "ar-SA" },
        { "th-TH", "th-TH" }, { "fa-IR", "fa-IR" }, { "ar-SA", "ar-SA" },
    };

    [Fact]
    public void TestCultures_StillUseAnotherCalendar()
    {
        // If the culture data of the platform changes, the tests below could pass without testing anything. This test
        // fails first in that case.
        var date = new DateTime(2026, 9, 30);

        Assert.NotEqual("2026", InCulture("th-TH", () => date.ToString("yyyy")));
        Assert.NotEqual("2026", InCulture("fa-IR", () => date.ToString("yyyy")));
        Assert.NotEqual("2026", InCulture("ar-SA", () => date.ToString("yyyy")));
        Assert.Throws<ArgumentOutOfRangeException>(() => InCulture("ar-SA", () => DateTime.MaxValue.ToString("yyyy")));
    }

    [Theory]
    [MemberData(nameof(AllCultures))]
    public void Token_UnderAnyCulture_IsTheOneTheEarlierCodeIssuedUnderEnUs(string culture)
    {
        var (token, expires) = InCulture(culture, () => TokenService.GenerateSASToken(Descriptor, Id, Expiry, Key));

        Assert.Equal(RecordedExpires, expires);
        Assert.Equal(RecordedToken, token);
    }

    [Theory]
    [MemberData(nameof(AllCultures))]
    public void NeverExpiringToken_UnderAnyCulture_IsTheOneTheEarlierCodeIssuedUnderEnUs(string culture)
    {
        // Under ar-SA the earlier code threw: DateTime.MaxValue is after the last date of the Umm al-Qura calendar.
        var (token, expires) = InCulture(culture, () => TokenService.GenerateSASToken(Descriptor, Id, DateTime.MaxValue, Key));

        Assert.Equal(RecordedNeverExpires, expires);
        Assert.Equal(RecordedNeverExpiringToken, token);
    }

    [Theory]
    [MemberData(nameof(AllCultures))]
    public void TokensFromTheEarlierCode_UnderAnyCulture_Validate(string culture)
    {
        Assert.True(InCulture(culture, () => TokenService.ValidateSASToken(Descriptor, Id, RecordedExpires, RecordedToken, Key)));
        Assert.True(InCulture(culture, () => TokenService.ValidateSASToken(Descriptor, Id, RecordedNeverExpires, RecordedNeverExpiringToken, Key)));
    }

    [Theory]
    [MemberData(nameof(SigningAndCheckingCultures))]
    public void Token_SignedAndCheckedUnderAnyCultures_Validates(string signingCulture, string checkingCulture)
    {
        var expiry = DateTime.UtcNow.AddHours(1);

        var signed = InCulture(signingCulture, () => TokenService.GenerateSASToken(Descriptor, Id, expiry, Key));

        Assert.True(InCulture(checkingCulture, () => TokenService.ValidateSASToken(Descriptor, Id, signed.expires, signed.token, Key)));

        // The token also expires at the time the caller asked for. Under th-TH, the earlier code wrote the year 2569. A
        // check under en-US read that as a Gregorian year, so the token was valid there for 543 years too long.
        Assert.Equal(InCulture("invariant", () => TokenService.GenerateSASToken(Descriptor, Id, expiry, Key)), signed);
    }

    [Theory]
    [MemberData(nameof(SigningAndCheckingCultures))]
    public void ExpiredToken_SignedAndCheckedUnderAnyCultures_IsRejected(string signingCulture, string checkingCulture)
    {
        var signed = InCulture(signingCulture, () => TokenService.GenerateSASToken(Descriptor, Id, DateTime.UtcNow.AddHours(-1), Key));

        Assert.False(InCulture(checkingCulture, () => TokenService.ValidateSASToken(Descriptor, Id, signed.expires, signed.token, Key)));
    }

    [Theory]
    [MemberData(nameof(SigningAndCheckingCultures))]
    public void NeverExpiringToken_SignedAndCheckedUnderAnyCultures_Validates(string signingCulture, string checkingCulture)
    {
        var signed = InCulture(signingCulture, () => TokenService.GenerateSASToken(Descriptor, Id, DateTime.MaxValue, Key));

        Assert.True(InCulture(checkingCulture, () => TokenService.ValidateSASToken(Descriptor, Id, signed.expires, signed.token, Key)));
        Assert.Equal((RecordedNeverExpiringToken, RecordedNeverExpires), signed);
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the current culture and UI culture set to <paramref name="culture"/>
    /// ("invariant" is the invariant culture), and puts the previous cultures back afterwards.
    /// </summary>
    private static T InCulture<T>(string culture, Func<T> action)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUICulture = CultureInfo.CurrentUICulture;
        var target = culture == "invariant" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(culture);

        CultureInfo.CurrentCulture = target;
        CultureInfo.CurrentUICulture = target;

        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUICulture;
        }
    }
}

using ShiftSoftware.ShiftEntity.Core.Notifications;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System.Globalization;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Notifications;

/// <summary>The one layout every notification email uses, and what protected fields do when no user is signed in.</summary>
public class NotificationEmailLayoutTests
{
    private const string Attack = "<script>alert('x')</script>";
    private const string EncodedAttack = "&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;";

    private static NotificationEmailBody Compose(NotificationEmailContent content, NotificationEmailOptions? options = null)
        => NotificationEmailLayout.Compose(content, options ?? new());

    private static NotificationEmailContent Content(
        string subject = "A subject",
        string headline = "A headline",
        IReadOnlyList<NotificationEmailDetail>? details = null,
        string? buttonText = null,
        string? buttonUrl = null) => new()
        {
            Subject = subject,
            Headline = headline,
            Details = details ?? [],
            ButtonText = buttonText,
            ButtonUrl = buttonUrl,
        };

    [Fact]
    public void EveryValue_IsEncoded_IncludingTextACustomerTyped()
    {
        var body = Compose(
            Content(
                subject: Attack,
                headline: Attack,
                details: [new(Attack, Attack)],
                buttonText: Attack,
                buttonUrl: "https://example.com/?q=\"><script>alert('x')</script>"),
            new() { Footer = Attack, LogoUrl = "https://example.com/logo.png?\"><script>alert('x')</script>" });

        Assert.DoesNotContain("<script>", body.HtmlBody);
        Assert.Contains($"<title>{EncodedAttack}</title>", body.HtmlBody);
        Assert.Contains(EncodedAttack, body.HtmlBody);
    }

    [Fact]
    public void AnEmptyDetailRow_IsLeftOut()
    {
        var body = Compose(Content(details: [new("Kept", "A value"), new("Null row", null), new("Blank row", "   ")]));

        Assert.Contains("Kept", body.HtmlBody);
        Assert.Contains("Kept: A value", body.TextBody);
        Assert.DoesNotContain("Null row", body.HtmlBody);
        Assert.DoesNotContain("Blank row", body.HtmlBody);
        Assert.DoesNotContain("Null row", body.TextBody);
        Assert.DoesNotContain("Blank row", body.TextBody);
    }

    [Fact]
    public void NoDetails_LeavesOutTheTable()
    {
        var body = Compose(Content());

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body.HtmlBody, "<h1"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(body.HtmlBody, "<table").Count);
    }

    [Fact]
    public void ThereIsAPlainTextVersion_WithEveryField_AndNoMarkup()
    {
        var body = Compose(
            Content(headline: "A headline", details: [new("VIN", "ABC123")], buttonText: "Open it", buttonUrl: "https://example.com/1"),
            new() { Footer = "A footer" });

        Assert.Contains("A headline", body.TextBody);
        Assert.Contains("VIN: ABC123", body.TextBody);
        Assert.Contains("Open it: https://example.com/1", body.TextBody);
        Assert.Contains("A footer", body.TextBody);
        Assert.DoesNotContain("<", body.TextBody);
    }

    [Fact]
    public void PlainText_IsNotEncoded()
    {
        Assert.Contains("Tom & Jerry <a>", Compose(Content(headline: "Tom & Jerry <a>")).TextBody);
    }

    [Fact]
    public void AMissingLogo_Works()
    {
        var body = Compose(Content());

        Assert.DoesNotContain("<img", body.HtmlBody);
    }

    [Theory]
    [InlineData("https://example.com/logo.png")]
    [InlineData("cid:logo")]
    public void ALogo_Appears(string logo)
    {
        Assert.Contains($"<img src=\"{logo}\"", Compose(Content(), new() { LogoUrl = logo }).HtmlBody);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("/relative.png")]
    public void ALogoWithAnUnsafeOrPartialLink_IsLeftOut(string logo)
    {
        Assert.DoesNotContain("<img", Compose(Content(), new() { LogoUrl = logo }).HtmlBody);
    }

    [Fact]
    public void AButton_HasAFallbackLink()
    {
        var body = Compose(Content(buttonText: "Open it", buttonUrl: "https://example.com/a?b=1&c=2"));

        Assert.Contains("Open it</a>", body.HtmlBody);
        Assert.Contains("If the button does not work", body.HtmlBody);
        Assert.Contains(">https://example.com/a?b=1&amp;c=2</a>", body.HtmlBody);
    }

    [Fact]
    public void TheFallbackSentence_CanBeInTheEmailsLanguage()
    {
        var content = new NotificationEmailContent
        {
            Subject = "s",
            Headline = "h",
            ButtonText = "Open it",
            ButtonUrl = "https://example.com",
            ButtonFallbackText = "Translated <sentence>",
        };

        var html = Compose(content).HtmlBody;

        Assert.Contains("Translated &lt;sentence&gt;", html);
        Assert.DoesNotContain("If the button does not work", html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("/relative/link")]
    [InlineData("not a link")]
    [InlineData(null)]
    public void AButtonWithAnUnsafeOrMissingLink_IsLeftOut(string? url)
    {
        var body = Compose(Content(buttonText: "Open it", buttonUrl: url));

        Assert.DoesNotContain("Open it", body.HtmlBody);
        Assert.DoesNotContain("Open it", body.TextBody);
        Assert.DoesNotContain("If the button does not work", body.HtmlBody);
    }

    [Fact]
    public void AButtonWithNoText_IsLeftOut()
    {
        Assert.DoesNotContain("example.com", Compose(Content(buttonUrl: "https://example.com")).HtmlBody);
    }

    [Theory]
    [InlineData("First\r\nBcc: someone@example.com")]
    [InlineData("First\nBcc: someone@example.com")]
    public void ALineBreakInASubject_CanNotAddAMailHeader(string subject)
    {
        var body = Compose(Content(subject: subject));

        Assert.Equal("First Bcc: someone@example.com", body.Subject);
    }

    [Fact]
    public void LineBreaksInAValue_AreKept()
    {
        var body = Compose(Content(details: [new("Message", "One\r\nTwo")]));

        Assert.Contains("One<br>Two", body.HtmlBody);
    }

    [Theory]
    [InlineData("ar-IQ", "rtl", "text-align:right", "padding:6px 0 6px 12px")]
    [InlineData("ku", "rtl", "text-align:right", "padding:6px 0 6px 12px")]
    [InlineData("en-US", "ltr", "text-align:left", "padding:6px 12px 6px 0")]
    public void TheCaptureCulture_IsTheOneRendered(string culture, string direction, string align, string padding)
    {
        var previous = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);

            var html = Compose(Content(details: [new("Label", "Value")])).HtmlBody;

            Assert.Contains($"""lang="{culture}" dir="{direction}" """.TrimEnd(), html);
            Assert.Contains(align, html);
            Assert.Contains(padding, html);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    // ── Protected fields, with no signed-in user ────────────────────────────────

    private sealed class ProtectedDTO
    {
        public string? VIN { get; set; }

        [Pii(PiiKind.Phone)]
        public PiiFieldDTO? Phone { get; set; }
    }

    private static readonly PiiDtoProtector protector = new(new PiiFieldProtection(new DefaultPiiMasker(Microsoft.Extensions.Options.Options.Create(new PiiOptions()))));

    // What the mapping rules do: the raw value goes in Value, until the protector runs.
    private static ProtectedDTO Mapped() => new() { VIN = "ABC123", Phone = new PiiFieldDTO { Value = "07501234567" } };

    [Fact]
    public void AProtectedField_IsMasked_WithNoSignedInUser()
    {
        var dto = Mapped();

        protector.Protect(dto);

        Assert.Null(dto.Phone!.Value);
        Assert.NotNull(dto.Phone.Display);
        Assert.DoesNotContain("07501234567", dto.Phone.Display);
        Assert.Equal("ABC123", dto.VIN);
    }

    [Fact]
    public void AnEmailBuiltFromAProtectedDTO_CarriesOnlyTheMask()
    {
        var dto = Mapped();
        protector.Protect(dto);

        var body = Compose(Content(details: [new("Phone", dto.Phone!.Display), new("VIN", dto.VIN)]));

        Assert.DoesNotContain("07501234567", body.HtmlBody);
        Assert.DoesNotContain("07501234567", body.TextBody);
    }

    [Fact]
    public void AFieldThatWasNeverProtected_HasNoDisplay_SoItsRowIsLeftOut()
    {
        // A map alone, with no protector: Display is empty, so a host reading Display sends nothing rather than the value.
        var dto = Mapped();

        var body = Compose(Content(details: [new("Phone", dto.Phone!.Display)]));

        Assert.DoesNotContain("Phone", body.TextBody);
        Assert.DoesNotContain("07501234567", body.TextBody);
    }
}

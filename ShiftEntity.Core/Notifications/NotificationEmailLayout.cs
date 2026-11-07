using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>A composed email, without its recipients.</summary>
public sealed record NotificationEmailBody(string Subject, string HtmlBody, string TextBody);

/// <summary>
/// The layout of every notification email: logo, headline, detail rows, button with a fallback link, and footer, as
/// self-contained HTML and plain text. Every value is encoded, because a value can be text that a customer typed.
/// </summary>
public static class NotificationEmailLayout
{
    /// <summary>Composes the email in the current culture.</summary>
    public static NotificationEmailBody Compose(NotificationEmailContent content, NotificationEmailOptions options)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        // A line break in a subject could add a mail header in a sender that writes headers itself.
        var subject = OneLine(content.Subject);
        var details = content.Details.Where(detail => !string.IsNullOrWhiteSpace(detail.Value)).ToList();
        var buttonUrl = SafeLink(content.ButtonUrl);
        var hasButton = buttonUrl is not null && !string.IsNullOrWhiteSpace(content.ButtonText);
        var logo = SafeImage(options.LogoUrl);
        var footer = string.IsNullOrWhiteSpace(options.Footer) ? null : options.Footer;

        var fallback = string.IsNullOrWhiteSpace(content.ButtonFallbackText) ? DefaultFallbackText : content.ButtonFallbackText;

        return new(subject, Html(subject, content.Headline, details, hasButton ? content.ButtonText : null, buttonUrl, fallback, logo, footer),
            Text(content.Headline, details, hasButton ? content.ButtonText : null, buttonUrl, footer));
    }

    private const string DefaultFallbackText = "If the button does not work, copy this link into your browser:";

    private static string Html(string subject, string headline, List<NotificationEmailDetail> details,
        string? buttonText, string? buttonUrl, string fallback, string? logo, string? footer)
    {
        var culture = CultureInfo.CurrentUICulture;
        var rightToLeft = culture.TextInfo.IsRightToLeft;
        var direction = rightToLeft ? "rtl" : "ltr";
        var align = rightToLeft ? "right" : "left";

        // The gap between a label and its value. The order is top, right, bottom, left.
        var labelPadding = rightToLeft ? "6px 0 6px 12px" : "6px 12px 6px 0";

        var html = new StringBuilder();

        html.Append($"""
            <!doctype html>
            <html lang="{H(culture.Name)}" dir="{direction}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="referrer" content="no-referrer"><title>{H(subject)}</title></head>
            <body style="margin:0;background:#f2f5f9;color:#243247;font-family:Arial,sans-serif">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:32px 16px">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:600px;background:#ffffff;border:1px solid #dce3ec;border-radius:12px">
                  <tr><td style="padding:32px;text-align:{align}">

            """);

        if (logo is not null)
            html.Append($"""<p style="margin:0 0 24px"><img src="{H(logo)}" alt="" height="40" style="display:block;height:40px;width:auto;border:0"></p>""");

        html.Append($"""<h1 style="margin:0 0 24px;font-size:24px;line-height:1.3">{Lines(headline)}</h1>""");

        if (details.Count > 0)
        {
            html.Append("""<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:0 0 24px;font-size:14px;line-height:1.5">""");

            foreach (var detail in details)
                html.Append($"""<tr><td valign="top" style="padding:{labelPadding};color:#52627a;white-space:nowrap">{H(detail.Label)}</td><td valign="top" style="padding:6px 0;word-break:break-word">{Lines(detail.Value!)}</td></tr>""");

            html.Append("</table>");
        }

        if (buttonText is not null)
        {
            html.Append($"""<p style="margin:28px 0"><a href="{H(buttonUrl!)}" rel="noreferrer" style="display:inline-block;padding:14px 22px;background:#234fc7;color:#ffffff;text-decoration:none;border-radius:6px;font-weight:bold">{H(buttonText)}</a></p>""");
            html.Append($"""<p style="font-size:13px;line-height:1.6">{H(fallback)}</p>""");
            html.Append($"""<p style="font-size:12px;word-break:break-all"><a href="{H(buttonUrl!)}" rel="noreferrer" style="color:#234fc7">{H(buttonUrl!)}</a></p>""");
        }

        if (footer is not null)
        {
            html.Append("""<hr style="border:0;border-top:1px solid #dce3ec;margin:28px 0">""");
            html.Append($"""<p style="font-size:13px;line-height:1.6;color:#52627a">{Lines(footer)}</p>""");
        }

        html.Append("""

                  </td></tr>
                </table>
              </td></tr></table>
            </body></html>
            """);

        return html.ToString();
    }

    private static string Text(string headline, List<NotificationEmailDetail> details, string? buttonText, string? buttonUrl, string? footer)
    {
        var text = new StringBuilder(headline).Append('\n');

        if (details.Count > 0)
        {
            text.Append('\n');

            foreach (var detail in details)
                text.Append(detail.Label).Append(": ").Append(detail.Value).Append('\n');
        }

        if (buttonText is not null)
            text.Append('\n').Append(buttonText).Append(": ").Append(buttonUrl).Append('\n');

        if (footer is not null)
            text.Append('\n').Append(footer).Append('\n');

        return text.ToString();
    }

    private static string H(string value) => WebUtility.HtmlEncode(value);

    // Encodes the value and keeps its line breaks.
    private static string Lines(string value) => H(value.Replace("\r\n", "\n")).Replace("\n", "<br>");

    private static string OneLine(string value) => string.Join(" ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // Encoding doesn't make a link safe: "javascript:" stays a working link after encoding.
    private static string? SafeLink(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http" ? url : null;
    }

    private static string? SafeImage(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http" or "cid" ? url : null;
    }
}

using System.Globalization;
using System.Net;
using SjolystInnovation.AppKit.Email;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

/// <summary>
/// Pins the auth emails to the HTML they had before they were rendered through <see cref="EmailLayout"/>.
/// <see cref="Before"/> is a copy of the renderer as it was, so every kind in both languages must match it
/// byte for byte. The one deliberate change since then keeps the badge on one line, with a gap to the
/// company details, and moves it under them on narrow screens. The copy has that change too.
/// </summary>
public class AuthEmailParityTests
{
    private static readonly EmailBrand Full = new("Pyttogpanne")
    {
        LegalName = "Pyttogpanne AS",
        OrgNumber = "945678901",
        VatRegistered = true,
        Address = "Torget 2, 5014 Bergen",
        Email = "post@pyttogpanne.example",
    };

    private static readonly EmailBrand Bare = new("A&B <Co>") { OrgNumber = "12 34", Email = " " };

    public static TheoryData<string, EmailLanguage, string, string, bool, string?> Cases()
    {
        var data = new TheoryData<string, EmailLanguage, string, string, bool, string?>();
        foreach (var kind in Enum.GetNames<AuthEmailKind>())
        {
            foreach (var language in Enum.GetValues<EmailLanguage>())
            {
                data.Add(kind, language, "#182232", "#0284c7", true, "Kari");
                data.Add(kind, language, "#0f3d3a", "#00887a", true, null);
                data.Add(kind, language, "#ffd400", "#ffd400", false, "<b>Kari</b>");
                data.Add(kind, language, "#182232", "#ffd400", false, " ");
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_kind_in_each_language_renders_the_same_HTML_as_before(
        string kindName, EmailLanguage language, string header, string accent, bool fullBrand, string? firstName)
    {
        var kind = Enum.Parse<AuthEmailKind>(kindName);
        var options = new AppKitAuthEmailOptions { Language = language, HeaderColor = header, AccentColor = accent };
        var brand = fullBrand ? Full : Bare;
        const string Page = "https://pyttogpanne.example/nb/reset-password?from=email";
        var lifetime = TimeSpan.FromMinutes(90);

        var now = AuthEmailRenderer.Render(kind, options, brand, firstName, "kari+test@example.no", "AB12CD", lifetime, Page);
        var before = Before(kind, options, brand, firstName, "kari+test@example.no", "AB12CD", lifetime, Page);

        Assert.Equal(before.Subject, now.Subject);
        Assert.Equal(before.To, now.To);
        Assert.Equal(before.HtmlBody, now.HtmlBody);
    }

    private const string NarrowStyle = "<style>@media (max-width:480px){.appkit-brand,.appkit-badge{display:block!important;width:auto!important}.appkit-badge{text-align:left!important;padding:10px 0 0!important}}</style>";

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string F(string format, string value) => string.Format(CultureInfo.InvariantCulture, format, value);

    private static bool ReadsOnWhite(string hex)
    {
        static double Channel(string hex, int at)
        {
            var c = int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Channel(hex, 1) + 0.7152 * Channel(hex, 3) + 0.0722 * Channel(hex, 5);
        return 1.05 / (luminance + 0.05) >= 3;
    }

    private static string? OrgNumberLine(EmailBrand brand, EmailLanguage language)
    {
        var number = brand.OrgNumber?.Trim();
        if (string.IsNullOrEmpty(number))
            return null;
        var digits = number.Replace(" ", "", StringComparison.Ordinal);
        if (digits.Length == 9 && digits.All(char.IsAsciiDigit))
            number = $"{digits[..3]} {digits[3..6]} {digits[6..]}";
        var label = language == EmailLanguage.Norwegian ? "Org.nr." : "Org. no.";
        return brand.VatRegistered ? $"{label} {number} MVA" : $"{label} {number}";
    }

    private static string Link(string page, string email, string code) =>
        $"{page}{(page.Contains('?', StringComparison.Ordinal) ? '&' : '?')}email={Uri.EscapeDataString(email)}&code={Uri.EscapeDataString(code)}";

    private static string Duration(TimeSpan lifetime, AuthEmailWords words)
    {
        var minutes = Math.Max(1, (int)Math.Round(lifetime.TotalMinutes, MidpointRounding.AwayFromZero));
        var (count, unit) = minutes >= 1410 ? ((int)Math.Round(minutes / 1440.0, MidpointRounding.AwayFromZero), words.Days)
            : minutes >= 60 ? ((int)Math.Round(minutes / 60.0, MidpointRounding.AwayFromZero), words.Hours)
            : (minutes, words.Minutes);
        return $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? unit.One : unit.Many)}";
    }

    private static EmailMessage Before(AuthEmailKind kind, AppKitAuthEmailOptions options, EmailBrand brand,
        string? firstName, string to, string code, TimeSpan lifetime, string page)
    {
        const string Ink = "#1a1a1a";
        var words = AuthEmailRenderer.Words(options.Language);
        var text = words.Kinds[kind];
        var link = Link(page, to, code);
        var hello = string.IsNullOrWhiteSpace(firstName) ? words.Hello : F(words.HelloName, H(firstName));
        var brandLines = string.Join("<br>", new[] { brand.LegalName, OrgNumberLine(brand, options.Language), brand.Address, brand.Email }
            .Where(l => !string.IsNullOrWhiteSpace(l)).Select(H));
        var header = options.HeaderColor;
        var accent = options.AccentColor;
        var headerText = ReadsOnWhite(header) ? "#fff" : Ink;
        var accentText = ReadsOnWhite(accent) ? "#fff" : Ink;
        var codeColor = ReadsOnWhite(header) ? header : Ink;
        var labelColor = ReadsOnWhite(accent) ? accent : Ink;
        var badge = ReadsOnWhite(header) ? "background:rgba(255,255,255,.25);border:1px solid rgba(255,255,255,.5)" : "background:rgba(0,0,0,.08);border:1px solid rgba(0,0,0,.4)";
        const string Paragraph = "line-height:1.6;color:#333;font-size:13px";

        var html = $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">{{NarrowStyle}}</head>
            <body style="margin:0;background:#fff;font-family:Arial,Helvetica,sans-serif;color:#1a1a1a">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="border-collapse:collapse;max-width:600px;margin:0 auto">
              <tr><td style="background:{{header}};color:{{headerText}};padding:28px 32px 20px">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr>
                  <td class="appkit-brand" style="vertical-align:top">
                    <div style="font-size:22px;font-weight:700;letter-spacing:.04em;margin-bottom:4px">{{H(brand.Name)}}</div>
                    <div style="font-size:11px;opacity:.85;line-height:1.5">{{brandLines}}</div>
                  </td>
                  <td class="appkit-badge" style="vertical-align:top;text-align:right;padding-left:12px">
                    <span style="display:inline-block;{{badge}};padding:2px 10px;border-radius:20px;font-size:10px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;white-space:nowrap">{{H(text.Badge)}}</span>
                  </td>
                </tr></table>
              </td></tr>
              <tr><td style="padding:24px 32px">
                <h1 style="font-size:18px;margin:0 0 12px">{{H(text.Heading)}}</h1>
                <p style="margin:0 0 10px;{{Paragraph}}">{{hello}}</p>
                <p style="margin:0;{{Paragraph}}">{{H(F(text.Intro, brand.Name))}}</p>
                <div style="text-align:center;font-size:9px;font-weight:700;text-transform:uppercase;letter-spacing:.1em;color:{{labelColor}};margin:22px 0 6px">{{H(words.CodeLabel)}}</div>
                <div style="text-align:center;font-family:'Courier New',Courier,monospace;font-size:32px;font-weight:700;letter-spacing:.3em;color:{{codeColor}};background:#f1f5f9;border:1px solid #cbd5e1;border-radius:8px;padding:18px 12px;margin:0 0 20px">{{H(code)}}</div>
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:4px 0 20px">
                  <a href="{{H(link)}}" style="display:inline-block;padding:12px 22px;border-radius:8px;background:{{accent}};color:{{accentText}};font-weight:700;font-size:14px;text-decoration:none">{{H(text.Button)}}</a>
                </td></tr></table>
                <p style="margin:0 0 8px;{{Paragraph}}">{{H(F(words.Expiry, Duration(lifetime, words)))}}</p>
                <p style="margin:0;{{Paragraph}}">{{H(text.Ignore)}}</p>
                <p style="margin:16px 0 0;color:#6b7280;font-size:11px;line-height:1.5;word-break:break-all">{{H(words.Fallback)}} {{H(link)}}</p>
              </td></tr>
              <tr><td style="padding:14px 32px 24px;border-top:1px solid #e5e7eb;font-size:10px;color:#888;line-height:1.5">{{H(F(words.Footer, brand.LegalName is { Length: > 0 } legal ? legal : brand.Name))}}</td></tr>
            </table>
            </body></html>
            """;
        return new EmailMessage(to, F(text.Subject, brand.Name), html);
    }
}

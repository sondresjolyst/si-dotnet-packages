using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SjolystInnovation.AppKit.Email;

/// <summary>A button in an email that links to a page.</summary>
/// <param name="Text">The button text. The layout encodes it.</param>
/// <param name="Url">The absolute http or https address the button opens.</param>
public sealed record EmailButton(string Text, string Url);

/// <summary>What <see cref="EmailLayout.Render"/> puts in the branded layout.</summary>
/// <param name="Company">The company details for the header.</param>
/// <param name="Language">The language of the organization number line.</param>
/// <param name="Badge">The short label in the top right corner of the header, such as <c>Invitation</c>. The layout encodes it.</param>
/// <param name="Heading">The heading above the body. The layout encodes it.</param>
/// <param name="BodyHtml">The body as HTML, with every value already encoded by the caller.</param>
public sealed record EmailLayoutRequest(EmailCompany Company, EmailLayoutLanguage Language, string Badge, string Heading, string BodyHtml)
{
    /// <summary>
    /// The background color of the header band, as <c>#rrggbb</c>. Text on the band is white, or near
    /// black when the color is too light for white text. Defaults to <c>#182232</c>.
    /// </summary>
    public string HeaderColor { get; init; } = "#182232";

    /// <summary>
    /// The color of the button, as <c>#rrggbb</c>. The button text follows the same rule as
    /// <see cref="HeaderColor"/>. Defaults to <c>#0284c7</c>.
    /// </summary>
    public string AccentColor { get; init; } = "#0284c7";

    /// <summary>The button shown after the body. Leave it out for an email without a button.</summary>
    public EmailButton? Button { get; init; }

    /// <summary>HTML shown after the button, with every value already encoded by the caller. Leave it out when there is nothing after the button.</summary>
    public string? AfterButtonHtml { get; init; }

    /// <summary>The small line at the bottom, such as why the recipient got the email. The layout encodes it. Leave it out for no footer.</summary>
    public string? Footer { get; init; }
}

/// <summary>
/// Renders transactional emails in one branded layout: a colored header with the brand name, the company
/// details and a badge, then a heading, the body, an optional button and an optional footer line. The
/// HTML uses tables and inline styles, so it reads the same in common email clients.
/// </summary>
public static partial class EmailLayout
{
    /// <summary>The near black used for text where a brand color is too light to read on.</summary>
    public const string Ink = "#1a1a1a";

    /// <summary>The inline style of body text in the layout, for paragraphs in <see cref="EmailLayoutRequest.BodyHtml"/>.</summary>
    public const string TextStyle = "line-height:1.6;color:#333;font-size:13px";

    /// <summary>
    /// Puts the badge under the company details on narrow screens, so a long badge never pushes the page
    /// wider than the screen. Clients that drop the style keep the badge to the right.
    /// </summary>
    private const string NarrowScreenStyle =
        "<style>@media (max-width:480px){.appkit-brand,.appkit-badge{display:block!important;width:auto!important}.appkit-badge{text-align:left!important;padding:10px 0 0!important}}</style>";

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    /// <summary>Checks whether a value is a color in the <c>#rrggbb</c> form the layout takes.</summary>
    public static bool IsColor(string? value) => value is not null && HexColor().IsMatch(value);

    /// <summary>
    /// Checks whether white text is readable on the color, and the color on white, at a WCAG contrast
    /// ratio of at least 3 to 1. That is the WCAG level for large text.
    /// </summary>
    /// <param name="hex">A color as <c>#rrggbb</c>.</param>
    /// <exception cref="ArgumentException">The color is not in the <c>#rrggbb</c> form.</exception>
    public static bool ReadsOnWhite(string hex)
    {
        if (!IsColor(hex))
            throw new ArgumentException("The color must be in the #rrggbb form.", nameof(hex));
        static double Channel(string hex, int at)
        {
            var c = int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Channel(hex, 1) + 0.7152 * Channel(hex, 3) + 0.0722 * Channel(hex, 5);
        return 1.05 / (luminance + 0.05) >= 3;
    }

    /// <summary>
    /// Formats the organization number line in the language, such as <c>Org.nr. 923 202 374 MVA
    /// Foretaksregisteret</c>, or returns null for a blank number. Nine digits, with or without spaces, are
    /// grouped in threes, and anything else is shown trimmed as given. <c>MVA</c> follows when the company is
    /// VAT registered, then the register name when one is set.
    /// </summary>
    public static string? OrgNumberLine(EmailCompany company, EmailLayoutLanguage language)
    {
        ArgumentNullException.ThrowIfNull(company);
        var number = company.OrgNumber?.Trim();
        if (string.IsNullOrEmpty(number))
            return null;
        var digits = number.Replace(" ", "", StringComparison.Ordinal);
        if (digits.Length == 9 && digits.All(char.IsAsciiDigit))
            number = $"{digits[..3]} {digits[3..6]} {digits[6..]}";
        var line = $"{(language == EmailLayoutLanguage.Norwegian ? "Org.nr." : "Org. no.")} {number}";
        if (company.VatRegistered)
            line += " MVA";
        if (!string.IsNullOrWhiteSpace(company.RegisterName))
            line += $" {company.RegisterName.Trim()}";
        return line;
    }

    /// <summary>
    /// Lists the company lines as plain text: the legal name, the organization number line, the address and
    /// the email. Blank ones are left out. The layout shows them in the header, and a plain text email can
    /// use them as its footer.
    /// </summary>
    public static IReadOnlyList<string> CompanyLines(EmailCompany company, EmailLayoutLanguage language)
    {
        ArgumentNullException.ThrowIfNull(company);
        return new[] { company.LegalName, OrgNumberLine(company, language), company.Address, company.Email }
            .Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!).ToArray();
    }

    /// <summary>Renders the whole HTML document of the email.</summary>
    /// <exception cref="ArgumentException">A color is not <c>#rrggbb</c>, or the button address is not an absolute http or https address.</exception>
    public static string Render(EmailLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Company);
        if (!IsColor(request.HeaderColor))
            throw new ArgumentException("HeaderColor must be in the #rrggbb form.", nameof(request));
        if (!IsColor(request.AccentColor))
            throw new ArgumentException("AccentColor must be in the #rrggbb form.", nameof(request));
        if (request.Button is { } b && !IsPageUrl(b.Url))
            throw new ArgumentException("The button address must be an absolute http or https address.", nameof(request));

        var header = request.HeaderColor;
        var accent = request.AccentColor;
        var headerText = ReadsOnWhite(header) ? "#fff" : Ink;
        var accentText = ReadsOnWhite(accent) ? "#fff" : Ink;
        var badge = ReadsOnWhite(header)
            ? "background:rgba(255,255,255,.25);border:1px solid rgba(255,255,255,.5)"
            : "background:rgba(0,0,0,.08);border:1px solid rgba(0,0,0,.4)";
        var companyLines = string.Join("<br>", CompanyLines(request.Company, request.Language).Select(H));

        var html = new StringBuilder();
        void Line(string text) => html.Append(text).Append('\n');
        void Block(string? block)
        {
            if (string.IsNullOrEmpty(block))
                return;
            html.Append(block);
            if (!block.EndsWith('\n'))
                html.Append('\n');
        }

        Line("<!DOCTYPE html>");
        Line("<html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" + NarrowScreenStyle + "</head>");
        Line($"<body style=\"margin:0;background:#fff;font-family:Arial,Helvetica,sans-serif;color:{Ink}\">");
        Line("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse;max-width:600px;margin:0 auto\">");
        Line($"  <tr><td style=\"background:{header};color:{headerText};padding:28px 32px 20px\">");
        Line("    <table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
        Line("      <td class=\"appkit-brand\" style=\"vertical-align:top\">");
        Line($"        <div style=\"font-size:22px;font-weight:700;letter-spacing:.04em;margin-bottom:4px\">{H(request.Company.Name)}</div>");
        Line($"        <div style=\"font-size:11px;opacity:.85;line-height:1.5\">{companyLines}</div>");
        Line("      </td>");
        Line("      <td class=\"appkit-badge\" style=\"vertical-align:top;text-align:right;padding-left:12px\">");
        Line($"        <span style=\"display:inline-block;{badge};padding:2px 10px;border-radius:20px;font-size:10px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;white-space:nowrap\">{H(request.Badge)}</span>");
        Line("      </td>");
        Line("    </tr></table>");
        Line("  </td></tr>");
        Line("  <tr><td style=\"padding:24px 32px\">");
        Line($"    <h1 style=\"font-size:18px;margin:0 0 12px\">{H(request.Heading)}</h1>");
        Block(request.BodyHtml);
        if (request.Button is { } button)
        {
            Line("    <table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr><td align=\"center\" style=\"padding:4px 0 20px\">");
            Line($"      <a href=\"{H(button.Url)}\" style=\"display:inline-block;padding:12px 22px;border-radius:8px;background:{accent};color:{accentText};font-weight:700;font-size:14px;text-decoration:none\">{H(button.Text)}</a>");
            Line("    </td></tr></table>");
        }
        Block(request.AfterButtonHtml);
        Line("  </td></tr>");
        if (!string.IsNullOrWhiteSpace(request.Footer))
            Line($"  <tr><td style=\"padding:14px 32px 24px;border-top:1px solid #e5e7eb;font-size:10px;color:#888;line-height:1.5\">{H(request.Footer)}</td></tr>");
        Line("</table>");
        html.Append("</body></html>");
        return html.ToString();
    }

    private static bool IsPageUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}

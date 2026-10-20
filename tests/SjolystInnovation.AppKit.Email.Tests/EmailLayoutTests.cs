using Xunit;

namespace SjolystInnovation.AppKit.Email.Tests;

public class EmailLayoutTests
{
    private static readonly EmailCompany Brand = new("Altinnendata")
    {
        LegalName = "ALTINNENDATA AS",
        OrgNumber = "938433240",
        RegisterName = "Foretaksregisteret",
        Address = "Mårvegen 21a, 4347 Lye",
        Email = "post@example.no",
    };

    private static EmailLayoutRequest Request(EmailLayoutLanguage language = EmailLayoutLanguage.Norwegian) =>
        new(Brand, language, "Ordrebekreftelse", "Takk for bestillingen", "<p>Ordrenummer: <strong>A-1</strong></p>");

    [Fact]
    public void The_header_shows_the_brand_the_company_lines_and_the_badge()
    {
        var html = EmailLayout.Render(Request());
        Assert.StartsWith("<!DOCTYPE html>\n<html>", html);
        Assert.EndsWith("</table>\n</body></html>", html);
        Assert.Contains(">Altinnendata</div>", html);
        Assert.Contains(">ALTINNENDATA AS<br>Org.nr. 938 433 240 Foretaksregisteret<br>M&#229;rvegen 21a, 4347 Lye<br>post@example.no</div>", html);
        Assert.Contains(";white-space:nowrap\">Ordrebekreftelse</span>", html);
        Assert.Contains("<style>@media (max-width:480px){.appkit-brand,.appkit-badge{display:block!important;", html);
        Assert.Contains("<td class=\"appkit-badge\" style=\"vertical-align:top;text-align:right;padding-left:12px\">", html);
        Assert.Contains("<h1 style=\"font-size:18px;margin:0 0 12px\">Takk for bestillingen</h1>\n<p>Ordrenummer: <strong>A-1</strong></p>\n  </td></tr>", html);
    }

    [Fact]
    public void English_writes_the_org_number_label_in_English()
    {
        var html = EmailLayout.Render(Request(EmailLayoutLanguage.English));
        Assert.Contains("<br>Org. no. 938 433 240 Foretaksregisteret<br>", html);
    }

    [Fact]
    public void Without_a_button_after_button_HTML_or_footer_those_parts_are_left_out()
    {
        var html = EmailLayout.Render(Request());
        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("border-top:1px solid #e5e7eb", html);
    }

    [Fact]
    public void The_button_the_HTML_after_it_and_the_footer_follow_the_body_in_order()
    {
        var html = EmailLayout.Render(Request() with
        {
            Button = new EmailButton("Betal med Vipps", "https://example.no/pay?a=1&b=2"),
            AfterButtonHtml = "<p>Lenken er gyldig til i morgen.</p>",
            Footer = "Sendt fra <Altinnendata>",
        });
        var body = html.IndexOf("Ordrenummer", StringComparison.Ordinal);
        var button = html.IndexOf(">Betal med Vipps</a>", StringComparison.Ordinal);
        var after = html.IndexOf("Lenken er gyldig", StringComparison.Ordinal);
        var footer = html.IndexOf("Sendt fra &lt;Altinnendata&gt;</td></tr>", StringComparison.Ordinal);
        Assert.True(body < button && button < after && after < footer, html);
        Assert.Contains("href=\"https://example.no/pay?a=1&amp;b=2\"", html);
    }

    [Fact]
    public void The_colours_come_from_the_request()
    {
        var html = EmailLayout.Render(Request() with { HeaderColor = "#0f3d3a", AccentColor = "#00887a", Button = new EmailButton("Ok", "https://example.no") });
        Assert.Contains("background:#0f3d3a;color:#fff;padding:28px", html);
        Assert.Contains("background:#00887a;color:#fff;font-weight:700", html);
        Assert.Contains("background:rgba(255,255,255,.25)", html);
    }

    [Fact]
    public void Light_colours_get_near_black_text()
    {
        var html = EmailLayout.Render(Request() with { HeaderColor = "#ffd400", AccentColor = "#ffd400", Button = new EmailButton("Ok", "https://example.no") });
        Assert.Contains("background:#ffd400;color:#1a1a1a;padding:28px", html);
        Assert.Contains("background:#ffd400;color:#1a1a1a;font-weight:700", html);
        Assert.Contains("background:rgba(0,0,0,.08)", html);
    }

    [Fact]
    public void The_defaults_are_the_garge_colours()
    {
        var request = Request();
        Assert.Equal("#182232", request.HeaderColor);
        Assert.Equal("#0284c7", request.AccentColor);
    }

    [Fact]
    public void The_brand_badge_heading_footer_and_button_text_are_encoded()
    {
        var html = EmailLayout.Render(new EmailLayoutRequest(new EmailCompany("A&B <Co>") { Address = "<script>" }, EmailLayoutLanguage.English, "<i>", "<b>Hi</b>", "")
        {
            Button = new EmailButton("<x>", "https://example.no"),
            Footer = "<u>",
        });
        Assert.Contains(">A&amp;B &lt;Co&gt;</div>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains(">&lt;i&gt;</span>", html);
        Assert.Contains(">&lt;b&gt;Hi&lt;/b&gt;</h1>", html);
        Assert.Contains(">&lt;x&gt;</a>", html);
        Assert.Contains(">&lt;u&gt;</td>", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Theory]
    [InlineData("red", "#00887a")]
    [InlineData("#00887a", "#00887a;background:url(x)")]
    public void A_colour_that_is_not_rrggbb_is_refused(string header, string accent) =>
        Assert.Throws<ArgumentException>(() => EmailLayout.Render(Request() with { HeaderColor = header, AccentColor = accent }));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/admin/orders/1")]
    [InlineData("")]
    public void A_button_that_is_not_an_absolute_http_address_is_refused(string url) =>
        Assert.Throws<ArgumentException>(() => EmailLayout.Render(Request() with { Button = new EmailButton("Ok", url) }));

    [Theory]
    [InlineData(EmailLayoutLanguage.Norwegian, false, null, "Org.nr. 923 202 374")]
    [InlineData(EmailLayoutLanguage.Norwegian, true, null, "Org.nr. 923 202 374 MVA")]
    [InlineData(EmailLayoutLanguage.English, false, null, "Org. no. 923 202 374")]
    [InlineData(EmailLayoutLanguage.English, true, null, "Org. no. 923 202 374 MVA")]
    [InlineData(EmailLayoutLanguage.Norwegian, false, "Foretaksregisteret", "Org.nr. 923 202 374 Foretaksregisteret")]
    [InlineData(EmailLayoutLanguage.Norwegian, true, " Foretaksregisteret ", "Org.nr. 923 202 374 MVA Foretaksregisteret")]
    [InlineData(EmailLayoutLanguage.English, true, "Foretaksregisteret", "Org. no. 923 202 374 MVA Foretaksregisteret")]
    [InlineData(EmailLayoutLanguage.English, false, " ", "Org. no. 923 202 374")]
    public void The_org_number_line_follows_the_language_the_VAT_flag_and_the_register(EmailLayoutLanguage language, bool vat, string? register, string line) =>
        Assert.Equal(line, EmailLayout.OrgNumberLine(new EmailCompany("X") { OrgNumber = "923202374", VatRegistered = vat, RegisterName = register }, language));

    [Theory]
    [InlineData("923202374", "923 202 374")]
    [InlineData(" 923 202 374 ", "923 202 374")]
    [InlineData("9232 02374", "923 202 374")]
    [InlineData("12345678", "12345678")]
    [InlineData(" 92320237X ", "92320237X")]
    [InlineData("9232023741", "9232023741")]
    public void Nine_digits_are_grouped_in_threes_and_anything_else_is_shown_trimmed(string number, string shown) =>
        Assert.Equal($"Org. no. {shown}", EmailLayout.OrgNumberLine(new EmailCompany("X") { OrgNumber = number }, EmailLayoutLanguage.English));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_org_number_leaves_the_line_out_with_its_VAT_and_register(string? number)
    {
        var brand = Brand with { OrgNumber = number, VatRegistered = true };
        Assert.Null(EmailLayout.OrgNumberLine(brand, EmailLayoutLanguage.Norwegian));
        Assert.Equal(["ALTINNENDATA AS", "Mårvegen 21a, 4347 Lye", "post@example.no"], EmailLayout.CompanyLines(brand, EmailLayoutLanguage.Norwegian));
    }

    [Fact]
    public void Company_lines_leave_out_blank_details()
    {
        var lines = EmailLayout.CompanyLines(new EmailCompany("Garge") { OrgNumber = "912 345 678", Email = " " }, EmailLayoutLanguage.English);
        Assert.Equal(["Org. no. 912 345 678"], lines);
    }

    [Theory]
    [InlineData("#182232", true)]
    [InlineData("#0f3d3a", true)]
    [InlineData("#00887a", true)]
    [InlineData("#0284c7", true)]
    [InlineData("#000000", true)]
    [InlineData("#949494", true)]
    [InlineData("#959595", false)]
    [InlineData("#ffd400", false)]
    [InlineData("#ffffff", false)]
    [InlineData("#FFD400", false)]
    [InlineData("#0000ff", true)]
    [InlineData("#00c000", false)]
    public void White_text_is_used_down_to_a_contrast_of_3_to_1(string colour, bool white) =>
        Assert.Equal(white, EmailLayout.ReadsOnWhite(colour));

    [Fact]
    public void ReadsOnWhite_refuses_a_value_that_is_not_a_colour() =>
        Assert.Throws<ArgumentException>(() => EmailLayout.ReadsOnWhite("#fff"));
}

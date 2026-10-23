using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Pdf.Tests;

public sealed class WithdrawalFormTests
{
    private static WithdrawalFormRequest Request(WithdrawalFormLanguage language = WithdrawalFormLanguage.Norwegian, DateOnly? receivedOn = null) =>
        new(
            new WithdrawalFormSeller("Altinnendata")
            {
                LegalName = "Altinnendata Sjølyst",
                OrganizationNumber = "999 888 777",
                AddressLines = ["Mårvegen 21a", "4347 Lye"],
                Email = "post@example.no",
                Phone = "+47 99 99 99 99",
            },
            new WithdrawalFormOrder("AID-000042", new DateOnly(2026, 10, 9), ["Gaming PC <RTX>", "Mus & matte"]) { ReceivedOn = receivedOn })
        { Language = language };

    // The visible text, with the style block and tags dropped, entities decoded and whitespace collapsed.
    private static string Text(string html)
    {
        var noStyle = Regex.Replace(html, "<style>.*?</style>", "", RegexOptions.Singleline);
        var decoded = System.Net.WebUtility.HtmlDecode(Regex.Replace(noStyle, "<[^>]+>", " "));
        return Regex.Replace(decoded, @"\s+", " ").Replace(" ,", ",", StringComparison.Ordinal);
    }

    [Fact]
    public void Norwegian_HasTheAnnexWording_FilledIn()
    {
        var html = WithdrawalForm.Html(Request());
        var text = Text(html);

        Assert.StartsWith("<!doctype html><html lang=\"nb\">", html);
        Assert.Contains("Standardskjema for bruk av angrerett", text);
        Assert.Contains("(Fyll ut og returner dette skjemaet dersom du ønsker å gå fra avtalen.)", text);
        Assert.Contains("Utfylt skjema sendes til: Altinnendata Sjølyst (999 888 777), Mårvegen 21a, 4347 Lye, post@example.no, +47 99 99 99 99", text);
        Assert.Contains("Jeg underretter herved om at jeg ønsker å gå fra min avtale om kjøp av følgende varer: Gaming PC <RTX>, Mus & matte", text);
        Assert.Contains("Ordrenummer: AID-000042", text);
        Assert.Contains("Avtalen ble inngått den: 09.10.2026", text);
        foreach (var blank in new[] { "Varen ble mottatt den:", "Forbrukerens navn:", "Forbrukerens adresse:", "Dato:", "Forbrukerens underskrift (kun dersom skjemaet sendes på papir):" })
            Assert.Contains(blank, text);
    }

    [Fact]
    public void English_HasTheModelFormWording_FilledIn()
    {
        var html = WithdrawalForm.Html(Request(WithdrawalFormLanguage.English));
        var text = Text(html);

        Assert.StartsWith("<!doctype html><html lang=\"en\">", html);
        Assert.Contains("Model withdrawal form", text);
        Assert.Contains("(Complete and return this form only if you wish to withdraw from the contract.)", text);
        Assert.Contains("To: Altinnendata Sjølyst (999 888 777), Mårvegen 21a, 4347 Lye, post@example.no, +47 99 99 99 99", text);
        Assert.Contains("I hereby give notice that I withdraw from my contract of sale of the following goods: Gaming PC <RTX>, Mus & matte", text);
        Assert.Contains("Order number: AID-000042", text);
        Assert.Contains("Ordered on: 09.10.2026", text);
        foreach (var blank in new[] { "Received on:", "Name of consumer:", "Address of consumer:", "Date:", "Signature of consumer (only if this form is notified on paper):" })
            Assert.Contains(blank, text);
    }

    [Theory]
    [InlineData(WithdrawalFormLanguage.Norwegian)]
    [InlineData(WithdrawalFormLanguage.English)]
    public void TheBuyersFields_AreLeftBlank(WithdrawalFormLanguage language)
    {
        var html = WithdrawalForm.Html(Request(language));

        // Received on, name, address, date and signature.
        Assert.Equal(5, Regex.Count(html, "<span class=\"blank\"></span>"));
    }

    [Theory]
    [InlineData(WithdrawalFormLanguage.Norwegian, "Varen ble mottatt den:</span> 15.10.2026")]
    [InlineData(WithdrawalFormLanguage.English, "Received on:</span> 15.10.2026")]
    public void AReceivedDate_IsFilledIn_AndTheBlankGoes(WithdrawalFormLanguage language, string expected)
    {
        var html = WithdrawalForm.Html(Request(language, new DateOnly(2026, 10, 15)));

        Assert.Contains(expected, html);
        Assert.Equal(4, Regex.Count(html, "<span class=\"blank\"></span>"));
    }

    [Fact]
    public void EveryValue_IsHtmlEncoded()
    {
        const string evil = "<script>alert(1)</script> & \"co\"";
        var request = new WithdrawalFormRequest(
            new WithdrawalFormSeller(evil)
            {
                LegalName = evil + "L",
                OrganizationNumber = evil + "O",
                AddressLines = [evil + "A1", evil + "A2"],
                Email = evil + "E",
                Phone = evil + "P",
            },
            new WithdrawalFormOrder(evil + "R", new DateOnly(2026, 1, 2), [evil + "I1", evil + "I2"]));

        var html = WithdrawalForm.Html(request);

        Assert.DoesNotContain("<script", html);
        foreach (var suffix in new[] { "L", "O", "A1", "A2", "E", "P", "R", "I1", "I2" })
            Assert.Contains($"&lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;co&quot;{suffix}", html);
    }

    [Fact]
    public void TheName_IsUsedWhenThereIsNoLegalName_AndBlankPartsAreLeftOut()
    {
        var request = new WithdrawalFormRequest(
            new WithdrawalFormSeller("Altinnendata") { LegalName = " ", AddressLines = ["Mårvegen 21a", " "], Email = "post@example.no", Phone = "" },
            new WithdrawalFormOrder("1", new DateOnly(2026, 1, 2), ["Vare"]));

        var html = WithdrawalForm.Html(request);

        Assert.Contains("<div class=\"company\">Altinnendata</div>", html);
        Assert.Contains("Utfylt skjema sendes til: Altinnendata, Mårvegen 21a, post@example.no", Text(html));
    }

    [Fact]
    public void TheDocument_IsSelfContained_WithAStrictPolicy()
    {
        var html = WithdrawalForm.Html(Request());

        Assert.Contains("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'\">", html);
        Assert.Contains("@page { size: A4; margin: 20mm 18mm; }", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void FileName_FollowsTheLanguage()
    {
        Assert.Equal("angrerettskjema.pdf", WithdrawalForm.FileName());
        Assert.Equal("angrerettskjema.pdf", WithdrawalForm.FileName(WithdrawalFormLanguage.Norwegian));
        Assert.Equal("withdrawal-form.pdf", WithdrawalForm.FileName(WithdrawalFormLanguage.English));
    }

    [Fact]
    public void Html_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => WithdrawalForm.Html(null!));
    }

    [Fact]
    public async Task TheHtml_GoesToTheRenderer_AsGiven()
    {
        var fake = new FakeBrowser();
        await using var renderer = new ChromiumPdfRenderer(new PdfRendererOptions(), new FakeLogger(), _ => Task.FromResult(fake.Browser));
        var html = WithdrawalForm.Html(Request());

        var pdf = await renderer.RenderAsync(html);

        Assert.Equal(FakeBrowser.Pdf, pdf);
        Assert.Equal([html], fake.Html);
    }
}

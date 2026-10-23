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
    public void Norwegian_HasTheOfficialWording_FilledIn()
    {
        var html = WithdrawalForm.Html(Request());
        var text = Text(html);

        Assert.StartsWith("<!doctype html><html lang=\"nb\">", html);
        Assert.Contains("Angreskjema", text);
        Assert.Contains("ved kjøp av varer og tjenester som ikke er finansielle tjenester", text);
        Assert.Contains("Fyll ut og returner dette skjemaet dersom du ønsker å gå fra avtalen", text);
        Assert.Contains("Utfylt skjema sendes til: Altinnendata Sjølyst (org.nr. 999 888 777), Mårvegen 21a, 4347 Lye, post@example.no", text);
        Assert.DoesNotContain("99 99", text);
        Assert.Contains("Jeg/vi underretter herved om at jeg/vi ønsker å gå fra min/vår avtale om kjøp av følgende: (sett kryss)", text);
        Assert.Contains("X varer (spesifiser på linjene nedenfor)", text);
        Assert.Contains("tjenester (spesifiser på linjene nedenfor)", text);
        Assert.Contains("Sett kryss og dato:", text);
        Assert.Contains("Avtalen ble inngått den", text);
        Assert.Contains("(ved kjøp av tjenester)", text);
        Assert.Contains("Varen ble mottatt den", text);
        Assert.Contains("(ved kjøp av varer)", text);
        Assert.Contains("Ordrenummer: AID-000042 (bestilt 09.10.2026)", text);
        foreach (var label in new[] { "Forbrukerens/forbrukernes navn:", "Forbrukerens/forbrukernes adresse:", "Dato:", "Forbrukerens/forbrukernes underskrift (dersom papirskjema benyttes):" })
            Assert.Contains(label, text);
        Assert.Equal(1, Regex.Count(html, "class=\"box ticked\""));
        Assert.Equal(3, Regex.Count(html, "class=\"box\""));
    }

    [Fact]
    public void English_HasTheDirectiveWording_FilledIn()
    {
        var html = WithdrawalForm.Html(Request(WithdrawalFormLanguage.English));
        var text = Text(html);

        Assert.StartsWith("<!doctype html><html lang=\"en\">", html);
        Assert.Contains("Model withdrawal form", text);
        Assert.Contains("(complete and return this form only if you wish to withdraw from the contract)", text);
        Assert.Contains("To: Altinnendata Sjølyst (org. no. 999 888 777), Mårvegen 21a, 4347 Lye, post@example.no", text);
        Assert.Contains("I/We hereby give notice that I/We withdraw from my/our contract of sale of the following goods:", text);
        Assert.Contains("Order number: AID-000042", text);
        Assert.Contains("Ordered on: 09.10.2026", text);
        Assert.Contains("Received on:", text);
        Assert.Contains("Name of consumer(s):", text);
        Assert.Contains("Address of consumer(s):", text);
        Assert.Contains("Signature of consumer(s) (only if this form is notified on paper):", text);
        Assert.Contains("Date:", text);
        // The directive puts the signature before the date.
        Assert.True(text.IndexOf("Signature of consumer(s)", StringComparison.Ordinal) < text.LastIndexOf("Date:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(WithdrawalFormLanguage.Norwegian, 6)]
    [InlineData(WithdrawalFormLanguage.English, 5)]
    public void TheBuyersFields_AreLeftBlank(WithdrawalFormLanguage language, int blanks)
    {
        var html = WithdrawalForm.Html(Request(language));

        // Norwegian: agreement date, received date, name, address, date and signature. English: received on, name, address, signature and date.
        Assert.Equal(blanks, Regex.Count(html, "<span class=\"blank\"></span>"));
    }

    [Fact]
    public void ItemsAreOnePerLine_AndNoItemsGiveBlankLines()
    {
        var html = WithdrawalForm.Html(Request());
        Assert.Contains("<div class=\"item\">Gaming PC &lt;RTX&gt;</div><div class=\"item\">Mus &amp; matte</div>", html);

        var none = WithdrawalForm.Html(new WithdrawalFormRequest(new WithdrawalFormSeller("A"), new WithdrawalFormOrder("1", new DateOnly(2026, 1, 2), [])));
        Assert.Equal(3, Regex.Count(none, "<div class=\"item\">&nbsp;</div>"));
    }

    [Fact]
    public void AReceivedDate_IsFilledIn_AndTicked()
    {
        var no = WithdrawalForm.Html(Request(WithdrawalFormLanguage.Norwegian, new DateOnly(2026, 10, 15)));
        Assert.Contains("class=\"box ticked\">X</span> Varen ble mottatt den 15.10.2026", no);
        Assert.Equal(5, Regex.Count(no, "<span class=\"blank\"></span>"));

        var en = WithdrawalForm.Html(Request(WithdrawalFormLanguage.English, new DateOnly(2026, 10, 15)));
        Assert.Contains("Received on:</span> 15.10.2026", en);
        Assert.Equal(4, Regex.Count(en, "<span class=\"blank\"></span>"));
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
            },
            new WithdrawalFormOrder(evil + "R", new DateOnly(2026, 1, 2), [evil + "I1", evil + "I2"]));

        foreach (var language in new[] { WithdrawalFormLanguage.Norwegian, WithdrawalFormLanguage.English })
        {
            var html = WithdrawalForm.Html(request with { Language = language });

            Assert.DoesNotContain("<script", html);
            foreach (var suffix in new[] { "L", "O", "A1", "A2", "E", "R", "I1", "I2" })
                Assert.Contains($"&lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;co&quot;{suffix}", html);
        }
    }

    [Fact]
    public void TheName_IsUsedWhenThereIsNoLegalName_AndBlankPartsAreLeftOut()
    {
        var request = new WithdrawalFormRequest(
            new WithdrawalFormSeller("Altinnendata") { LegalName = " ", AddressLines = ["Mårvegen 21a", " "] },
            new WithdrawalFormOrder("1", new DateOnly(2026, 1, 2), ["Vare"]));

        var html = WithdrawalForm.Html(request);

        Assert.Contains("<div class=\"company\">Altinnendata</div>", html);
        Assert.Contains("Utfylt skjema sendes til: Altinnendata, Mårvegen 21a", Text(html));
        Assert.DoesNotContain("org.nr.", html);
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

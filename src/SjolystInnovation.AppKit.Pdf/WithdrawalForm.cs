using System.Globalization;
using System.Net;
using System.Text;

namespace SjolystInnovation.AppKit.Pdf;

/// <summary>The language of the withdrawal form.</summary>
public enum WithdrawalFormLanguage
{
    /// <summary>Norwegian bokmål, the official form, blankett Q-0319B.</summary>
    Norwegian,

    /// <summary>English, the model withdrawal form in Directive 2011/83/EU, Annex I(B).</summary>
    English,
}

/// <summary>The seller the buyer returns the form to.</summary>
/// <param name="Name">The name the shop trades under. Shown when there is no legal name.</param>
public sealed record WithdrawalFormSeller(string Name)
{
    /// <summary>The company's legal name. Shown instead of <see cref="Name"/> when set.</summary>
    public string? LegalName { get; init; }

    /// <summary>The organization number. Shown after the name when set.</summary>
    public string? OrganizationNumber { get; init; }

    /// <summary>The address, one line per entry, such as a street and then a postal code and place.</summary>
    public IReadOnlyList<string> AddressLines { get; init; } = [];

    /// <summary>The email address the buyer returns the form to. Optional, and left out when blank.</summary>
    public string? Email { get; init; }
}

/// <summary>The order the form is about.</summary>
/// <param name="Reference">The order number or reference.</param>
/// <param name="OrderedOn">The date the agreement was made.</param>
/// <param name="Items">The goods ordered, one plain string each.</param>
public sealed record WithdrawalFormOrder(string Reference, DateOnly OrderedOn, IReadOnlyList<string> Items)
{
    /// <summary>The date the buyer received the goods. When null, the line is left blank for the buyer.</summary>
    public DateOnly? ReceivedOn { get; init; }
}

/// <summary>Everything the withdrawal form is filled in with.</summary>
/// <param name="Seller">The seller.</param>
/// <param name="Order">The order.</param>
public sealed record WithdrawalFormRequest(WithdrawalFormSeller Seller, WithdrawalFormOrder Order)
{
    /// <summary>The language of the form. Defaults to Norwegian.</summary>
    public WithdrawalFormLanguage Language { get; init; } = WithdrawalFormLanguage.Norwegian;
}

/// <summary>
/// The standard withdrawal form, as an HTML document for <see cref="IPdfRenderer"/>. In Norwegian it follows
/// forskrift 20. juni 2014 nr. 777 § 1 (blankett Q-0319B), and in English Directive 2011/83/EU, Annex I(B).
/// The form is for goods. The seller and the order are filled in. The buyer writes the name, the address, the
/// date and the signature by hand. Every value is HTML encoded, the CSS is inline, and a Content-Security-Policy
/// blocks anything that would load from the network, so the form renders with scripts off and no network.
/// </summary>
public static class WithdrawalForm
{
    private const string Csp = "default-src 'none'; img-src data:; style-src 'unsafe-inline'";

    private const string Blank = "<span class=\"blank\"></span>";

    private const string Style = """
        <style>
          @page { size: A4; margin: 20mm 18mm; }
          body { font-family: "Liberation Sans", Arial, Helvetica, sans-serif; font-size: 10.5pt; line-height: 1.45; color: #1a1a1a; }
          header { border-bottom: 2px solid #0f3d3a; padding-bottom: 8pt; margin-bottom: 14pt; }
          header .company { font-size: 9pt; color: #0f3d3a; font-weight: bold; text-transform: uppercase; letter-spacing: .04em; }
          h1 { font-size: 17pt; margin: 4pt 0 6pt; }
          p { overflow-wrap: anywhere; }
          .lead { font-style: italic; }
          .field { margin: 12pt 0; }
          .field .label { font-weight: bold; }
          .blank { display: inline-block; min-width: 70mm; border-bottom: 1px solid #333; height: 1.1em; vertical-align: bottom; }
          .seller { margin: 4pt 0 0 0; }
          .subtitle { margin: 0; font-size: 10pt; }
          .note { margin-top: 18pt; font-size: 9.5pt; }
          .check { margin: 6pt 0; }
          .box { display: inline-block; width: 4mm; height: 4mm; border: 1px solid #333; vertical-align: text-bottom; text-align: center; font-size: 9pt; line-height: 4mm; font-weight: bold; }
          .item { border-bottom: 1px solid #333; min-height: 1.4em; margin: 4pt 0; }
        </style>
        """;

    /// <summary>The full HTML document of the form, ready for <see cref="IPdfRenderer.RenderAsync"/>.</summary>
    public static string Html(WithdrawalFormRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Seller);
        ArgumentNullException.ThrowIfNull(request.Order);

        var no = request.Language == WithdrawalFormLanguage.Norwegian;
        var seller = request.Seller;
        var order = request.Order;
        var title = no ? "Angreskjema" : "Model withdrawal form";
        var received = order.ReceivedOn is { } r ? Date(r) : Blank;

        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"<header><div class=\"company\">{Enc(CompanyName(seller))}</div><h1>{title}</h1>");
        if (no)
        {
            body.Append("<p class=\"subtitle\">ved kjøp av varer og tjenester som ikke er finansielle tjenester</p></header>");
            body.Append("<p class=\"lead\">Fyll ut og returner dette skjemaet dersom du ønsker å gå fra avtalen</p>");
            Field(body, "Utfylt skjema sendes til", $"<p class=\"seller\">{SellerLine(seller, "org.nr.")}</p>");
            body.Append("<p>Jeg/vi underretter herved om at jeg/vi ønsker å gå fra min/vår avtale om kjøp av følgende: (sett kryss)</p>");
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"check\">{Box(true)} varer (spesifiser på linjene nedenfor)</div>");
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"check\">{Box(false)} tjenester (spesifiser på linjene nedenfor)</div>");
            body.Append(ItemLines(order.Items));
            Field(body, "Ordrenummer", $"{Enc(order.Reference)} (bestilt {Date(order.OrderedOn)})");
            body.Append("<p><strong>Sett kryss og dato:</strong></p>");
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"check\">{Box(false)} Avtalen ble inngått den {Blank} (ved kjøp av tjenester)</div>");
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"check\">{Box(order.ReceivedOn is not null)} Varen ble mottatt den {received} (ved kjøp av varer)</div>");
            Field(body, "Forbrukerens/forbrukernes navn", Blank);
            Field(body, "Forbrukerens/forbrukernes adresse", Blank);
            Field(body, "Dato", Blank);
            Field(body, "Forbrukerens/forbrukernes underskrift (dersom papirskjema benyttes)", Blank);
        }
        else
        {
            body.Append("</header><p class=\"lead\">(complete and return this form only if you wish to withdraw from the contract)</p>");
            Field(body, "To", $"<p class=\"seller\">{SellerLine(seller, "org. no.")}</p>");
            body.Append("<p>I/We (*) hereby give notice that I/We (*) withdraw from my/our (*) contract of sale of the following goods (*)/for the provision of the following service (*),</p>");
            body.Append(ItemLines(order.Items));
            Field(body, "Order number", Enc(order.Reference));
            body.Append(CultureInfo.InvariantCulture, $"<div class=\"field\"><span class=\"label\">Ordered on (*)</span> {Date(order.OrderedOn)} / <span class=\"label\">received on (*)</span> {received}</div>");
            Field(body, "Name of consumer(s)", Blank);
            Field(body, "Address of consumer(s)", Blank);
            Field(body, "Signature of consumer(s) (only if this form is notified on paper)", Blank);
            Field(body, "Date", Blank);
            body.Append("<p class=\"note\">(*) Delete as appropriate.</p>");
        }

        return $"<!doctype html><html lang=\"{(no ? "nb" : "en")}\"><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"{Csp}\"><title>{title}</title>{Style}</head><body>{body}</body></html>";
    }

    /// <summary>The suggested file name, <c>angrerettskjema.pdf</c> in Norwegian and <c>withdrawal-form.pdf</c> in English.</summary>
    public static string FileName(WithdrawalFormLanguage language = WithdrawalFormLanguage.Norwegian) =>
        language == WithdrawalFormLanguage.Norwegian ? "angrerettskjema.pdf" : "withdrawal-form.pdf";

    private static string SellerLine(WithdrawalFormSeller seller, string organizationLabel)
    {
        var name = Enc(CompanyName(seller));
        if (!string.IsNullOrWhiteSpace(seller.OrganizationNumber))
            name += $" ({organizationLabel} {Enc(seller.OrganizationNumber.Trim())})";
        var parts = new List<string> { name };
        parts.AddRange((seller.AddressLines ?? []).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => Enc(l.Trim())));
        if (!string.IsNullOrWhiteSpace(seller.Email)) parts.Add(Enc(seller.Email.Trim()));
        return string.Join(", ", parts);
    }

    // One line per item, or three blank lines to write on when there are none.
    private static string ItemLines(IReadOnlyList<string>? items)
    {
        var shown = (items ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
        if (shown.Count == 0) return string.Concat(Enumerable.Repeat("<div class=\"item\">&nbsp;</div>", 3));
        return string.Concat(shown.Select(i => $"<div class=\"item\">{Enc(i.Trim())}</div>"));
    }

    private static string Box(bool ticked) =>
        ticked ? "<span class=\"box ticked\">X</span>" : "<span class=\"box\"></span>";

    private static void Field(StringBuilder body, string label, string value) =>
        body.Append(CultureInfo.InvariantCulture, $"<div class=\"field\"><span class=\"label\">{Enc(label)}:</span> {value}</div>");

    private static string CompanyName(WithdrawalFormSeller seller) =>
        string.IsNullOrWhiteSpace(seller.LegalName) ? seller.Name : seller.LegalName;

    private static string Date(DateOnly date) => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}

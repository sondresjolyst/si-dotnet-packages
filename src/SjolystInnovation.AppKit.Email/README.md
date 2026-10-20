# SjolystInnovation.AppKit.Email

This package sends transactional email through the Brevo API and reads Brevo's delivery statistics.
Use it for the emails your app sends one at a time, such as contact form messages, receipts, and sign-in
codes. A send returns a result instead of throwing, so an email that Brevo refuses never breaks the
request that sends it. A branded layout with the company details is included, and your app writes the
body.

```sh
dotnet add package SjolystInnovation.AppKit.Email
```

## Get started

`AddAppKitBrevo` registers `IEmailSender` and `IEmailStats` with settings from the configuration
section you pass.

```csharp
using System.Net;
using SjolystInnovation.AppKit.Email;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAppKitBrevo(builder.Configuration.GetSection("BrevoSettings"));

var app = builder.Build();

app.MapPost("/api/contact", async (ContactRequest request, IEmailSender email, CancellationToken cancellationToken) =>
{
    var message = new EmailMessage("post@example.com", "New enquiry", $"<p>{WebUtility.HtmlEncode(request.Message)}</p>")
    {
        ReplyTo = request.Email,
    };
    var result = await email.SendAsync(message, cancellationToken);
    return result.Sent ? Results.NoContent() : Results.Problem("The message could not be sent.");
});

app.Run();

public sealed record ContactRequest(string Email, string Message);
```

```json
"BrevoSettings": {
  "ApiKey": "xkeysib-...",
  "SenderEmail": "post@example.com",
  "SenderName": "Example"
}
```

| Setting | Default | Meaning |
| --- | --- | --- |
| `ApiKey` | None | Sent as the `api-key` header. Required. |
| `SenderEmail` | None | The From address. Brevo must know it as a sender. Required. |
| `SenderName` | None | The From name. Required. |
| `BaseUrl` | `https://api.brevo.com/v3/` | The API root. Point it at a fake Brevo in tests. |
| `Tag` | None | Sent in `tags` on each email, and the statistics count only emails with it. Useful when several apps share one Brevo account. |
| `Sandbox` | `false` | When true, each email carries the `X-Sib-Sandbox: drop` header. Brevo accepts it but neither delivers nor logs it. |

If the key, the sender address, or the sender name is missing, or the API root is not an absolute
URL, the app fails to start. The error names the setting by its full path, such as
`BrevoSettings:ApiKey is required.`

## Send an email

```csharp
var result = await email.SendAsync(new EmailMessage("kari@example.no", "Your invoice", html)
{
    ReplyTo = "post@example.com",
    Attachments = [new EmailAttachment("invoice-0007.pdf", pdf)],
});
```

`IEmailSender.SendAsync` posts to `smtp/email`. Brevo takes each attachment's type from its file
name. If the recipient is empty, `SendAsync` throws `ArgumentException`. If the reply-to is blank, it
is left out.

| Outcome | Result |
| --- | --- |
| Brevo accepts it | `Sent` is true and `MessageId` is Brevo's ID. |
| Brevo refuses it | `Sent` is false and `Error` holds Brevo's code and message. The client logs it as an error. |
| The connection fails or times out | `Sent` is false and `Error` holds the exception message. The client logs it as an error. |
| The caller cancels | `SendAsync` throws `OperationCanceledException`. |

The log names the status and Brevo's reason, never the recipient.

## Branded layout

`EmailLayout.Render` wraps your email in a branded layout: a colored header with the brand name, the
company details and a badge, then a heading, your body, an optional button, optional HTML after the
button, and an optional footer line. It uses tables and inline styles, so common email clients show it
the same way. On screens narrower than 480 pixels the badge moves under the company details, in
clients that read the style in the head. The AppKit.Auth emails use the same layout.

```csharp
var company = new EmailCompany("Example")
{
    LegalName = "Example AS",
    OrgNumber = "923202374",
    VatRegistered = true,
    RegisterName = "Foretaksregisteret",
    Address = "Torget 2, 5014 Bergen",
    Email = "post@example.com",
};

var html = EmailLayout.Render(new EmailLayoutRequest(company, EmailLayoutLanguage.Norwegian, "Betalingslenke", "Ny betalingslenke",
    $"<p style=\"margin:0 0 10px;{EmailLayout.TextStyle}\">Ordrenummer: <strong>{WebUtility.HtmlEncode(reference)}</strong></p>")
{
    HeaderColor = "#0f3d3a",
    AccentColor = "#00887a",
    Button = new EmailButton("Betal med Vipps", paymentUrl),
});
await email.SendAsync(new EmailMessage(buyer, "Ny betalingslenke", html), cancellationToken);
```

| Request | Default | Meaning |
| --- | --- | --- |
| `Company` | None | The company details for the header. Blank details are left out. |
| `Language` | None | `EmailLayoutLanguage.English` or `Norwegian`, for the organization number label. |
| `Badge`, `Heading` | None | Plain text. The layout encodes it. |
| `BodyHtml` | None | Your HTML, with every value already encoded. `EmailLayout.TextStyle` gives paragraphs the layout's text style. |
| `HeaderColor` | `#182232` | The header band, as `#rrggbb`. |
| `AccentColor` | `#0284c7` | The button, as `#rrggbb`. |
| `Button` | None | The button text and an absolute http or https address. |
| `AfterButtonHtml` | None | Your HTML after the button, already encoded. |
| `Footer` | None | A plain text line at the bottom. The layout encodes it. |

Text on the header and the button is white. If a color is too light for white text, with a WCAG
contrast below 3 to 1, that text turns near black. `EmailLayout.ReadsOnWhite` makes the same check
for your own colored text. A color that is not `#rrggbb`, or a button address that is not an absolute
http or https address, throws `ArgumentException`.

The header lists the legal name, the organization number line, the address and the email. Pass the
organization number as stored. `EmailLayout.OrgNumberLine` writes the line in the email's language:

| `OrgNumber` | `VatRegistered` | `RegisterName` | Norwegian | English |
| --- | --- | --- | --- | --- |
| `923202374` | `false` | None | `Org.nr. 923 202 374` | `Org. no. 923 202 374` |
| `923202374` | `true` | None | `Org.nr. 923 202 374 MVA` | `Org. no. 923 202 374 MVA` |
| `923202374` | `true` | `Foretaksregisteret` | `Org.nr. 923 202 374 MVA Foretaksregisteret` | `Org. no. 923 202 374 MVA Foretaksregisteret` |
| Blank | Either | Either | No line | No line |

Nine digits, with or without spaces, are grouped in threes. Anything else is shown trimmed as given.
Both languages write `MVA`, because Norwegian bookkeeping rules (bokføringsforskriften § 5-1-2) put
those letters after the organization number of a business in the VAT register, also in English.
`EmailLayout.CompanyLines` gives the same company lines as plain text, for a plain text email or a
footer of your own.

## Read statistics

```csharp
var stats = await emailStats.GetAsync(days: 30);
```

`IEmailStats.GetAsync` reads Brevo's aggregated report for the last `days` days, today included. The
`EmailStats` it returns counts requests, delivered, hard and soft bounces, spam reports, blocked, and
invalid. If `Tag` is set, it counts only emails with that tag. Brevo reports on at most 90 days, which
`IEmailStats.MaxDays` gives. If `days` is below 1 or above `MaxDays`, or Brevo cannot be read,
`GetAsync` throws. Another provider can implement `IEmailStats` and set `MaxDays` to its own limit. It
defaults to no limit.

`MapAppKitEmailStats` maps the stats for an admin page, behind the `Admin` policy by default:

```csharp
app.MapAppKitEmailStats();                                          // GET /api/admin/email-stats
app.MapAppKitEmailStats("/admin/mail", o => o.Policy = "Support");  // another route and policy
```

| Request | Answer |
| --- | --- |
| `GET` or `GET ?days=7` | `200 { days, requests, delivered, hardBounces, softBounces, spamReports, blocked, invalid }`. `days` defaults to 30. |
| `days` below 1 | `400`, a validation problem on `days`: `Must be at least 1.` |
| `days` above `MaxDays`, 90 for Brevo | `400`, a validation problem on `days`: `Must be at most 90.` Brevo is not asked. |
| Brevo cannot be read | `502`, a problem with the `detail` `Failed to fetch email stats from Brevo.` The endpoint logs the error. |

The endpoint needs ASP.NET Core, so the package references the `Microsoft.AspNetCore.App` framework.

## Links

- [Source code](https://github.com/sondresjolyst/si-dotnet-packages/tree/main/src/SjolystInnovation.AppKit.Email)
- [Changelog](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/src/SjolystInnovation.AppKit.Email/CHANGELOG.md)
- [All AppKit packages](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/README.md)

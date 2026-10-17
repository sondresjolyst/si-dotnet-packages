# SjolystInnovation.AppKit.Email

This package sends transactional email through the Brevo API and reads Brevo's delivery statistics.
Use it for the emails your app sends one at a time, such as contact form messages, receipts, and sign-in
codes. A send returns a result instead of throwing, so an email that Brevo refuses never breaks the
request that sends it. Your app keeps its own templates.

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

## Read statistics

```csharp
var stats = await emailStats.GetAsync(days: 30);
```

`IEmailStats.GetAsync` reads Brevo's aggregated report for the last `days` days, today included. The
`EmailStats` it returns counts requests, delivered, hard and soft bounces, spam reports, blocked, and
invalid. If `days` is below 1, or Brevo cannot be read, `GetAsync` throws.

`MapAppKitEmailStats` maps the stats for an admin page, behind the `Admin` policy by default:

```csharp
app.MapAppKitEmailStats();                                          // GET /api/admin/email-stats
app.MapAppKitEmailStats("/admin/mail", o => o.Policy = "Support");  // another route and policy
```

| Request | Answer |
| --- | --- |
| `GET` or `GET ?days=7` | `200 { days, requests, delivered, hardBounces, softBounces, spamReports, blocked, invalid }`. `days` defaults to 30. |
| `days` below 1 | `400`, a validation problem on `days`. |
| Brevo cannot be read | `502`, a problem with the `detail` `Failed to fetch email stats from Brevo.` The endpoint logs the error. |

The endpoint needs ASP.NET Core, so the package references the `Microsoft.AspNetCore.App` framework.

## Links

- [Source code](https://github.com/sondresjolyst/si-dotnet-packages/tree/main/src/SjolystInnovation.AppKit.Email)
- [Changelog](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/src/SjolystInnovation.AppKit.Email/CHANGELOG.md)
- [All AppKit packages](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/README.md)

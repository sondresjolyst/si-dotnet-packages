# SjolystInnovation.AppKit.Email

This package sends transactional email through Brevo's API. A send returns a result instead of
throwing, so an email that Brevo refuses never breaks the request that sends it. Your app keeps its
own templates.

```sh
dotnet add package SjolystInnovation.AppKit.Email
```

## Setup

```csharp
builder.Services.AddAppKitBrevo(builder.Configuration.GetSection("BrevoSettings"));
```

| Setting | Default | |
| --- | --- | --- |
| `ApiKey` | none | Sent as the `api-key` header. |
| `SenderEmail` | none | The From address. Brevo must know it as a sender. |
| `SenderName` | none | The From name. |
| `BaseUrl` | `https://api.brevo.com/v3/` | Point it at a fake Brevo in tests. |

If the key, the sender address, or the sender name is missing, or the API root is not an absolute
URL, the app fails to start. The error names the setting by its full path, such as
`BrevoSettings:ApiKey is required.`

## Sending

```csharp
var result = await email.SendAsync(new EmailMessage("kari@example.no", "Your code", html)
{
    ReplyTo = enquiry.Email,
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

## Statistics

```csharp
var stats = await emailStats.GetAsync(days: 30);
```

`IEmailStats.GetAsync` reads Brevo's aggregated report for the last `days` days, today included. The
report counts requests, delivered, hard and soft bounces, spam reports, blocked, and invalid. If
Brevo cannot be read, `GetAsync` throws.

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

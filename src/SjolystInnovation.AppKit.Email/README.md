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
URL, the app fails to start.

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

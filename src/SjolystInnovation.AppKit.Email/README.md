# SjolystInnovation.AppKit.Email

Transactional email through Brevo's API. A send returns a result instead of throwing, so an email
that Brevo refuses never breaks the request that sends it. The app keeps its own templates.

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

The app fails to start without the key, the sender address, the sender name or an absolute API root.

## Sending

```csharp
var result = await email.SendAsync(new EmailMessage("kari@example.no", "Your code", html)
{
    ReplyTo = enquiry.Email,
    Attachments = [new EmailAttachment("invoice-0007.pdf", pdf)],
});
```

`IEmailSender.SendAsync` posts to `smtp/email`. Brevo takes each attachment's type from its file
name. An empty recipient throws `ArgumentException`, and a blank reply-to is left out.

| Outcome | Result |
| --- | --- |
| Brevo accepts it | `Sent` is true and `MessageId` is Brevo's id. |
| Brevo refuses it | `Sent` is false and `Error` holds Brevo's code and message. Logged as an error. |
| The connection fails or times out | `Sent` is false and `Error` holds the exception message. Logged as an error. |
| The caller cancels | `OperationCanceledException`. |

The log names the status and Brevo's reason, never the recipient.

## Statistics

```csharp
var stats = await emailStats.GetAsync(days: 30);
```

`IEmailStats.GetAsync` reads Brevo's aggregated report for the last days, today included: requests,
delivered, hard and soft bounces, spam reports, blocked and invalid. It throws when Brevo cannot be
read.

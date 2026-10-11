# Si.Vipps

A client for the Vipps MobilePay Recurring, ePayment, Userinfo and Webhooks APIs, with webhook
signature checks. It targets .NET 10.

## Setup

```csharp
builder.Services.AddVipps(o =>
{
    o.Live = new VippsCredentials
    {
        BaseUrl = "https://api.vipps.no",
        ClientId = config["Vipps:ClientId"]!,
        ClientSecret = config["Vipps:ClientSecret"]!,
        SubscriptionKey = config["Vipps:SubscriptionKey"]!,
        MerchantSerialNumber = config["Vipps:MerchantSerialNumber"]!,
    };
    o.Test = new VippsCredentials { BaseUrl = "https://apitest.vipps.no", /* the test credentials */ };
    o.SystemName = "my-app";
    o.SystemVersion = "1.4.2";
});
```

`AddVipps` registers `IVippsClient` with its own `HttpClient`. Calls go to the live environment
unless the app registers its own `IVippsEnvironment`, such as one that reads an admin setting. A
call on an existing payment or agreement takes the environment it was created in as `isTest`, so
switching the setting never sends it to the wrong one.

## Calls

| Call | Vipps API |
| --- | --- |
| `CreateAgreementAsync`, `GetAgreementAsync`, `StopAgreementAsync`, `UpdateAgreementMaxAmountAsync` | Recurring v3 agreements, with variable pricing |
| `CreateChargeAsync` | Recurring v3 charges |
| `CreatePaymentAsync`, `GetPaymentAsync`, `CapturePaymentAsync`, `CancelPaymentAsync`, `RefundPaymentAsync` | ePayment v1, reserved and captured later, with a receipt |
| `GetUserInfoAsync` | Userinfo, for the profile a user shared |
| `RegisterWebhookAsync`, `ListWebhooksAsync`, `DeleteWebhookAsync` | Webhooks v1 |

Amounts are in øre. Every call sends the access token, the subscription key, the merchant serial
number and the `Vipps-System-*` headers, and a call that changes something takes an idempotency
key. The client keeps an access token per environment until a minute before it expires.

The client holds the limits Vipps sets, listed in `VippsLimits`:

- An agreement amount must be between 1 øre and NOK 20 000. A larger one throws before anything is
  sent.
- A product name is cut to 45 characters and a description to 100, never inside a character.
- A charge falls due two days from today at the earliest, and its order id is cut to 50 characters.
- A receipt line sends its VAT rate as `taxRate` in basis points, with the VAT split out of the line
  total.

A failed call throws `HttpRequestException`. It logs Vipps's error code, error reference and trace
id and the body's length, never the body, which may echo a phone number or a name. For debugging,
`LogFailedResponseBodies` logs the body of a failure that carries no error ids.

## Webhooks

```csharp
var result = VippsWebhookSignature.Verify(new VippsWebhookRequest
{
    Method = request.Method,
    PathAndQuery = request.Path + request.QueryString,
    Host = request.Headers.Host.ToString(),
    Authorization = request.Headers.Authorization,
    Date = request.Headers["x-ms-date"],
    ContentSha256 = request.Headers["x-ms-content-sha256"],
    Body = rawBody,
}, secret, DateTimeOffset.UtcNow);
```

It checks the content hash, the HMAC-SHA256 signature over the method, path, date, host and hash,
and that the request was signed at most eight days ago, since Vipps retries a failed webhook for up
to seven. A replayed event is for the receiver to stop, by keeping the event ids it processed for
longer than that. `VippsEvents` lists the event names, with `Payments` and `Recurring` for
registering a webhook.

`VippsAddressFormatter.Format` puts a Userinfo address on one line.

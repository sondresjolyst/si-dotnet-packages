# SjolystInnovation.AppKit.Vipps

This package is a client for the Vipps MobilePay Recurring, ePayment, Userinfo, and Webhooks APIs,
with webhook signature checks. It targets .NET 10.

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
unless your app registers its own `IVippsEnvironment`, such as one that reads an admin setting. A
call on an existing payment or agreement takes the environment it was created in as `isTest`, so
switching the setting never sends it to the wrong one.

## Calls

| Call | Vipps API |
| --- | --- |
| `CreateAgreementAsync`, `GetAgreementAsync`, `StopAgreementAsync`, `UpdateAgreementMaxAmountAsync` | Recurring v3 agreements, with variable pricing |
| `CreateChargeAsync`, `RefundChargeAsync` | Recurring v3 charges, and refunds of captured ones |
| `CreatePaymentAsync`, `GetPaymentAsync`, `CapturePaymentAsync`, `CancelPaymentAsync`, `RefundPaymentAsync` | ePayment v1, reserved and captured later, with a receipt |
| `GetUserInfoAsync` | Userinfo, for the profile a user shared |
| `RegisterWebhookAsync`, `ListWebhooksAsync`, `DeleteWebhookAsync` | Webhooks v1 |

Amounts are in øre. Every call sends the access token, the subscription key, the merchant serial
number, and the `Vipps-System-*` headers. A call that changes an agreement, a charge, or a payment
takes an idempotency key. `RegisterWebhookAsync` makes its own. The client keeps an access token per
environment until a minute before it expires.

The client applies the limits that Vipps sets, which `VippsLimits` lists:

- An agreement amount must be between 1 øre and NOK 20 000. An amount outside that range throws
  before anything is sent.
- A product name is cut to 45 characters and a description to 100, never inside a character.
- A charge falls due two days from today at the earliest, and its order ID is cut to 50 characters.
- A charge refund answers without a body, and Vipps sends no webhook for it. Record the refund when
  the call returns. Its description is cut to 100 characters.
- A receipt line sends its VAT rate as `taxRate` in basis points, with the VAT split out of the line
  total.

A failed call throws `HttpRequestException`. It logs the Vipps error code, error reference, trace ID,
and body length. It never logs the body, because the body may echo a phone number or a name. If you
need to debug, set `LogFailedResponseBodies` to log the body of a failure that carries no error IDs.

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

`Verify` checks the content hash and the HMAC-SHA256 signature over the method, path, date, host, and
hash. It also checks that the request was signed at most eight days ago, because Vipps retries a
failed webhook for up to seven. Stopping a replayed event is up to the receiver. Keep the IDs of the
events you processed for longer than eight days. `VippsEvents` lists the event names, with `Payments`
and `Recurring` for registering a webhook.

`VippsAddressFormatter.Format` puts a Userinfo address on one line.

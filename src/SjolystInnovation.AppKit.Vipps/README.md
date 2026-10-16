# SjolystInnovation.AppKit.Vipps

This package is a client for the Vipps MobilePay Recurring, ePayment, Userinfo, and Webhooks APIs,
with signed webhook endpoints that skip redelivered events, webhook registration at startup, and
Norwegian phone number checks. It targets .NET 10.

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

If your configuration has the flat layout, such as the `Vipps__ClientId` and `Vipps__TestClientId`
environment variables, pass the section instead:

```csharp
builder.Services.AddVipps(builder.Configuration.GetSection("Vipps"), o => o.SystemName = "my-app");
```

The live keys are `BaseUrl`, `ClientId`, `ClientSecret`, `SubscriptionKey`, and
`MerchantSerialNumber`. The test keys are the same with a `Test` prefix. A missing base URL keeps its
default.

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

The webhook endpoints take ePayment and Recurring events for your app's own merchant. Each webhook
is a slot, with a registration and a secret in the live and in the test environment.

```csharp
builder.Services.AddVipps(builder.Configuration.GetSection("Vipps"));
builder.Services.AddAppKitVippsWebhooks<AppDbContext>(o =>
{
    o.PublicBaseUrl = "https://api.example.com";
    o.AddSlot("shop", VippsWebhookKind.Payment, "/api/shop/webhook");
    o.AddSlot("subscription", VippsWebhookKind.Recurring, "/api/subscriptions/webhook");
    o.AdvisoryLockKey = 7300114;
});
builder.Services.AddScoped<IVippsPaymentWebhookHandler, ShopWebhookHandler>();
builder.Services.AddScoped<IVippsRecurringWebhookHandler, SubscriptionWebhookHandler>();

app.MapAppKitVippsWebhook<AppDbContext>("shop");
app.MapAppKitVippsWebhook<AppDbContext>("subscription");
```

Your DbContext implements `IVippsWebhookDbContext` and calls `modelBuilder.AddAppKitVippsWebhooks()`
in `OnModelCreating`. That maps the processed events to the `ProcessedWebhookEvents` table, with an
`Id` of up to 200 characters, a `Source` of up to 50, an index on `ProcessedAt`, and an index on
`Source` and `Id`. Pass another table name if you need one.

### Handle an event

For each request, the endpoint does the following:

1. It answers 401 when the signature headers are missing or the signed date is stale, before it
   reads the body. A body over `MaxBodyBytes`, 64 KB by default, gets 413.
2. It reads the body bytes and checks the signature against the slot's live secret, then its test
   secret. The signed path includes any path base. The secret that matches sets `IsTest`. A bad
   signature gets 401, and a signed body that is not an event gets 400. The endpoint keeps a slot's
   secrets in memory for `SecretCacheDuration`, 30 seconds by default, and the registration service
   clears them when it stores new registrations.
3. It builds the event key. An ePayment event uses its `pspReference`, or else the reference and the
   event name. A Recurring event uses its `eventId`, or else the agreement, the event type, the
   charge, and the time it happened. A test event's key starts with `test:`.
4. It skips an event whose key it already processed, with 200.
5. It checks that `msn` is the merchant serial number of the environment that signed the event. An
   event whose `msn` differs is rejected. An ePayment event without `msn` is rejected too, because
   Vipps documents it as required there. A Recurring event without `msn` is accepted, because the
   Vipps Webhooks events page lists it among the properties a Recurring payload "may include". Set
   `AllowMissingRecurringMsn` to false to reject those as well.
6. It calls your handler.

Your handler changes your entities on the same DbContext without saving, and returns an outcome:

| Outcome | What the endpoint does |
| --- | --- |
| `VippsWebhookOutcome.Applied()` | Saves your changes and the processed marker in one `SaveChangesAsync`, then answers 200. |
| `VippsWebhookOutcome.Rejected` | Drops your changes, saves the marker, and answers 200. |
| `VippsWebhookOutcome.Retry` | Saves nothing and answers 500, so Vipps sends the event again. |

A rejected event still gets 200, because Vipps retries any 4xx or 5xx for seven days and holds back
the payment's later events until one succeeds. Log the reason where someone notices it. A handler
that throws, or a save that fails, also gets 500 with nothing saved.

If a concurrent delivery of the same event saves first, the endpoint drops this delivery's changes
and answers 200. Pass side effects such as an email to `Applied`. They run after the save, never for
a delivery that lost that race, and a failure there is only logged.

```csharp
public sealed class ShopWebhookHandler(AppDbContext db, IOrderEmails emails) : IVippsPaymentWebhookHandler
{
    public async Task<VippsWebhookOutcome> HandleAsync(VippsWebhookDelivery<VippsPaymentEvent> delivery, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Reference == delivery.Event.Reference, ct);
        if (order is null || order.IsTest != delivery.IsTest) return VippsWebhookOutcome.Rejected;
        order.Status = delivery.Event.Name;
        return VippsWebhookOutcome.Applied(token => emails.SendAsync(order.Id, token));
    }
}
```

### Registration

After the app starts, a hosted service makes sure each slot is registered in each environment whose
credentials are set. It registers `PublicBaseUrl` followed by the slot's path, for the events in
`VippsEvents.Payments` or `VippsEvents.Recurring`. It replaces a stored registration that Vipps no
longer lists, or whose secret it can no longer read. Once the new registration is stored, it deletes
the other registrations for the same URL, because their secrets are unknown. With
`AdvisoryLockKey` set on PostgreSQL, instances that start together register one at a time. Set
`RegisterOnStart` to false to turn the service off.

The registrations go to an `IVippsWebhookRegistrationStore`. The default store keeps them in the
`VippsWebhookRegistrations` table that `modelBuilder.AddAppKitVippsWebhookRegistrations()` maps.
To keep them in your own columns, register your own store as a scoped service. Its `SetAsync`
changes your tracked entities, and the service saves your DbContext afterwards. `SavedAsync` runs
after the commit, for example to clear a cache of the secrets.

`IVippsWebhookSecretProtector` protects each secret with ASP.NET Core Data Protection, so persist
your Data Protection keys. A stored value without the `enc:v1:` prefix is read as plain text. An app
that moves from its own protector sets `SecretPurpose` and `SecretPrefix` to its old values, so its
stored secrets stay readable.

### Cleanup and signatures

A hosted service deletes processed events older than `Retention`, 30 days by default, at startup
and every six hours. The app fails to start if the retention does not exceed
`VippsWebhookSignature.MaxAge`, because a replay signed earlier could otherwise pass.

To check a signature yourself, call `VippsWebhookSignature.Verify`:

```csharp
var result = VippsWebhookSignature.Verify(new VippsWebhookRequest
{
    Method = request.Method,
    PathAndQuery = request.Path + request.QueryString,
    Host = request.Headers.Host.ToString(),
    Authorization = request.Headers.Authorization,
    Date = request.Headers["x-ms-date"],
    ContentSha256 = request.Headers["x-ms-content-sha256"],
    RawBody = bodyBytes,
}, secret, DateTimeOffset.UtcNow);
```

`Verify` checks the content hash of the raw body bytes and the HMAC-SHA256 signature over the method,
path, date, host, and hash. It also checks that the request was signed at most eight days ago,
because Vipps retries a failed webhook for up to seven.

## Helpers

- `VippsPhoneNumber.TryNormalize` turns a Norwegian mobile number into the digits Vipps expects, such
  as `4791234567`.
- `VippsAddressFormatter.Format` puts a Userinfo address on one line.

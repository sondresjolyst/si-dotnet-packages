# SjolystInnovation.AppKit.Vipps

This package is a client for the Vipps MobilePay Recurring, ePayment, Userinfo, and Webhooks APIs. It
also maps signed webhook endpoints that skip redelivered events, registers the webhooks at startup,
and checks Norwegian phone numbers. Use it when your app takes one-off payments or subscriptions
through Vipps MobilePay and needs to act on payment events safely.

```sh
dotnet add package SjolystInnovation.AppKit.Vipps
```

## Get started

This example creates an ePayment for an order and handles its webhook events.

```csharp
using Microsoft.EntityFrameworkCore;
using SjolystInnovation.AppKit.Vipps;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddAppKitVipps(builder.Configuration.GetSection("Vipps"), o =>
{
    o.SystemName = "my-app";
    o.SystemVersion = "1.4.2";
});
builder.Services.AddAppKitVippsWebhooks<AppDbContext>(o =>
{
    o.PublicBaseUrl = "https://api.example.com";
    o.AddSlot("shop", VippsWebhookKind.Payment, "/api/shop/webhook");
});
builder.Services.AddScoped<IVippsPaymentWebhookHandler, ShopWebhookHandler>();

var app = builder.Build();

app.MapPost("/api/shop/orders/{id:int}/pay", async (int id, AppDbContext db, IVippsClient vipps, CancellationToken cancellationToken) =>
{
    var order = await db.Orders.FindAsync([id], cancellationToken);
    if (order is null)
        return Results.NotFound();
    if (!VippsPhoneNumber.TryNormalize(order.Phone, out var phone))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["phone"] = ["Not a Norwegian mobile number."] });

    var payment = await vipps.CreatePaymentAsync(new VippsCreatePaymentRequest
    {
        Reference = order.Reference,
        AmountInOre = order.TotalInOre,
        PhoneNumber = phone,
        ReturnUrl = $"https://example.com/orders/{order.Id}",
        Description = $"Order {order.Reference}",
        ReceiptLines = [new VippsReceiptLine { Id = "1", Name = "Coffee beans", UnitPriceInOre = order.TotalInOre, Quantity = 1, TaxRateBasisPoints = 1500 }],
    }, idempotencyKey: order.Reference, cancellationToken);

    order.IsTest = payment.IsTest;
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { payment.RedirectUrl });
});

app.MapAppKitVippsWebhook<AppDbContext>("shop");

app.Run();

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IVippsWebhookDbContext
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<VippsProcessedWebhookEvent> ProcessedWebhookEvents => Set<VippsProcessedWebhookEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddAppKitVippsProcessedWebhookEvents();
        modelBuilder.AddAppKitVippsWebhookRegistrations();
    }
}

public class Order
{
    public int Id { get; set; }
    public required string Reference { get; set; }
    public required string Phone { get; set; }
    public int TotalInOre { get; set; }
    public bool IsTest { get; set; }
    public string Status { get; set; } = "";
}

public sealed class ShopWebhookHandler(AppDbContext db) : IVippsPaymentWebhookHandler
{
    public async Task<VippsWebhookOutcome> HandleAsync(VippsWebhookDelivery<VippsPaymentEvent> delivery, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Reference == delivery.Event.Reference, cancellationToken);
        if (order is null || order.IsTest != delivery.IsTest)
            return VippsWebhookOutcome.Rejected;
        order.Status = delivery.Event.Name;
        return VippsWebhookOutcome.Applied();
    }
}
```

| Call | What it does |
| --- | --- |
| `AddAppKitVipps` | Registers `IVippsClient` with options from a callback or a configuration section. |
| `AddAppKitVippsWebhooks<TContext>` | Registers the webhook slots, the registration service, and the cleanup of processed events. |
| `MapAppKitVippsWebhook<TContext>` | Maps the `POST` endpoint of one slot. |
| `AddAppKitVippsProcessedWebhookEvents` | Maps the `ProcessedWebhookEvents` table in `OnModelCreating`. |
| `AddAppKitVippsWebhookRegistrations` | Maps the `VippsWebhookRegistrations` table for the default registration store. |

## Client setup

`AddAppKitVipps` registers `IVippsClient` with its own `HttpClient`. You can set the credentials in
code:

```csharp
builder.Services.AddAppKitVipps(o =>
{
    o.Live = new VippsCredentials
    {
        BaseUrl = "https://api.vipps.no",
        ClientId = builder.Configuration["Vipps:ClientId"]!,
        ClientSecret = builder.Configuration["Vipps:ClientSecret"]!,
        SubscriptionKey = builder.Configuration["Vipps:SubscriptionKey"]!,
        MerchantSerialNumber = builder.Configuration["Vipps:MerchantSerialNumber"]!,
    };
    o.SystemName = "my-app";
});
```

If your configuration has the flat layout, such as the `Vipps__ClientId` and `Vipps__TestClientId`
environment variables, pass the section instead, as in the get started example. The live keys are
`BaseUrl`, `ClientId`, `ClientSecret`, `SubscriptionKey`, and `MerchantSerialNumber`. The test keys are
the same with a `Test` prefix. A missing base URL keeps its default.

| Option | Default | Meaning |
| --- | --- | --- |
| `Live` | Base URL `https://api.vipps.no` | The live credentials. |
| `Test` | Base URL `https://apitest.vipps.no` | The test credentials. |
| `SystemName` | Empty | The `Vipps-System-Name` header, such as the app's name. At most 30 characters are sent. |
| `SystemVersion` | `1.0.0` | The `Vipps-System-Version` header. Build metadata after a `+` is left out, and at most 30 characters are sent. |
| `LogFailedResponseBodies` | `false` | Whether to log the body of a failed response that carries no Vipps error IDs. For debugging only. |

Calls go to the live environment unless your app registers its own `IVippsEnvironment`, such as one
that reads an admin setting. `FixedVippsEnvironment` is the default, which always answers live. A call
on an existing payment or agreement takes the environment it was created in as `isTest`, so switching
the setting never sends it to the wrong one.

## Calls

| Call | Vipps API |
| --- | --- |
| `CreateAgreementAsync`, `GetAgreementAsync`, `StopAgreementAsync`, `UpdateAgreementMaxAmountAsync` | Recurring v3 agreements, with variable pricing |
| `CreateChargeAsync`, `RefundChargeAsync` | Recurring v3 charges, and refunds of captured ones |
| `CreatePaymentAsync`, `GetPaymentAsync`, `CapturePaymentAsync`, `CancelPaymentAsync`, `RefundPaymentAsync` | ePayment v1, reserved and captured later, with a receipt |
| `GetUserInfoAsync` | Userinfo, for the profile a user shared |
| `RegisterWebhookAsync`, `ListWebhooksAsync`, `DeleteWebhookAsync` | Webhooks v1 |

`GetPaymentAsync` and the authorized event carry `CaptureGuaranteedUntil`, the last moment a capture is guaranteed.

Amounts are in øre. Every call sends the access token, the subscription key, the merchant serial
number, and the `Vipps-System-*` headers. A call that changes an agreement, a charge, or a payment
takes an idempotency key. `RegisterWebhookAsync` makes its own. The client keeps an access token per
environment until a minute before it expires.

The client applies the limits that Vipps sets, which `VippsLimits` lists:

- An agreement amount must be between 1 øre and NOK 20 000. An amount outside that range throws
  before anything is sent.
- A product name is cut to 45 characters and a description to 100, never inside a character.
- A charge's `Due` is a `DateOnly`, sent as `yyyy-MM-dd`. It falls due two days from today in UTC at
  the earliest, and its order ID is cut to 50 characters.
- A charge refund answers without a body, and Vipps sends no webhook for it. Record the refund when
  the call returns. Its description is cut to 100 characters.
- A receipt line sends its VAT rate as `taxRate` in basis points, with the VAT split out of the line
  total.

`VippsLimits.Truncate` and `VippsLimits.SystemHeader` apply the same cuts, for values your app shows
or stores.

A failed call throws `HttpRequestException`. It logs the Vipps error code, error reference, trace ID,
and body length. It never logs the body, because the body may echo a phone number or a name. If you
need to debug, set `LogFailedResponseBodies` to log the body of a failure that carries no error IDs.

## Webhooks

The webhook endpoints take ePayment and Recurring events for your app's own merchant. Each webhook
is a slot, with a registration and a secret in the live and in the test environment.

```csharp
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

Your `DbContext` implements `IVippsWebhookDbContext` and calls
`modelBuilder.AddAppKitVippsProcessedWebhookEvents()` in `OnModelCreating`. That maps the processed
events to the `ProcessedWebhookEvents` table, with an `Id` of up to 200 characters, a `Source` of up
to 50, an index on `ProcessedAt`, and an index on `Source` and `Id`. Pass another table name if you
need one.

| Option | Default | Meaning |
| --- | --- | --- |
| `Slots` | None | The webhooks of the app. Add each with `AddSlot(name, kind, path)`. A slot name has 1 to 50 characters, and each slot needs its own name and path. |
| `PublicBaseUrl` | Empty | The public root URL of the API. Each slot registers this URL followed by its path. Without it, nothing is registered. |
| `RegisterOnStart` | `true` | Whether a hosted service registers the webhooks after the app starts. |
| `AdvisoryLockKey` | `null` | The PostgreSQL advisory lock that instances starting together take, so they register one at a time. |
| `Retention` | 30 days | How long a processed event is kept to stop a redelivery. It must exceed `VippsWebhookSignature.MaxAge`. |
| `AllowMissingRecurringMsn` | `true` | Whether a Recurring event without a merchant serial number is accepted. |
| `MaxBodyBytes` | 64 KB | The largest body the endpoint reads. |
| `SecretCacheDuration` | 30 seconds | How long the endpoint keeps a slot's secrets in memory. |
| `SecretPurpose` | `SjolystInnovation.AppKit.Vipps.webhook-secret.v1` | The Data Protection purpose that protects stored secrets. |
| `SecretPrefix` | `enc:v1:` | The prefix that marks a protected secret. |

A slot's events default to `VippsEvents.Payments` or `VippsEvents.Recurring`, after its kind. To
register other events, add a `VippsWebhookSlot` with its own `Events` to `Slots`.

### Handle an event

For each request, the endpoint does the following:

1. It answers `401` when the signature headers are missing or the signed date is stale, before it
   reads the body. A body over `MaxBodyBytes` gets `413`.
2. It reads the body bytes and checks the signature against the slot's live secret, then its test
   secret. The signed path includes any path base. The secret that matches sets `IsTest`. A bad
   signature gets `401`, and a signed body that is not an event gets `400`. The endpoint keeps a
   slot's secrets in memory for `SecretCacheDuration`, and the registration service clears them when
   it stores new registrations.
3. It builds the event key. An ePayment event uses its `pspReference`, or else the reference and the
   event name. A Recurring event uses its `eventId`, or else the agreement, the event type, the
   charge, and the UTC ticks of the time it happened. A time without an offset counts as UTC. A test
   event's key starts with `test:`.
4. It skips an event whose key it already processed, with `200`.
5. It checks that `msn` is the merchant serial number of the environment that signed the event. An
   event whose `msn` differs is rejected. An ePayment event without `msn` is rejected too, because
   Vipps documents it as required there. A Recurring event without `msn` is accepted, because the
   Vipps Webhooks events page lists it among the properties a Recurring payload "may include". Set
   `AllowMissingRecurringMsn` to `false` to reject those as well.
6. It calls your handler.

Your handler changes your entities on the same `DbContext` without saving, and returns an outcome:

| Outcome | What the endpoint does |
| --- | --- |
| `VippsWebhookOutcome.Applied()` | Saves your changes and the processed marker in one `SaveChangesAsync`, then answers `200`. |
| `VippsWebhookOutcome.Rejected` | Drops your changes, saves the marker, and answers `200`. |
| `VippsWebhookOutcome.Retry` | Saves nothing and answers `500`, so Vipps sends the event again. |

A rejected event still gets `200`, because Vipps retries any 4xx or 5xx for seven days and holds back
the payment's later events until one succeeds. Log the reason where someone notices it. A handler
that throws, or a save that fails, also gets `500` with nothing saved.

If a concurrent delivery of the same event saves first, the endpoint drops this delivery's changes
and answers `200`. Pass side effects such as an email to `Applied`. They run after the save, never for
a delivery that lost that race, and a failure there is only logged.

```csharp
return VippsWebhookOutcome.Applied(token => emails.SendAsync(order.Id, token));
```

### Registration

After the app starts, a hosted service makes sure each slot is registered in each environment whose
credentials are set. It registers `PublicBaseUrl` followed by the slot's path, for the slot's events.
It replaces a stored registration that Vipps no longer lists, or whose secret it can no longer read.
Once the new registration is stored, it deletes the other registrations for the same URL, because
their secrets are unknown. With `AdvisoryLockKey` set on PostgreSQL, instances that start together
register one at a time. Set `RegisterOnStart` to `false` to turn the service off.

The registrations go to an `IVippsWebhookRegistrationStore`. The default store keeps them in the
`VippsWebhookRegistrations` table that `modelBuilder.AddAppKitVippsWebhookRegistrations()` maps. To
keep them in your own columns, register your own store as a scoped service. Its `SetAsync` changes
your tracked entities, and the service saves your `DbContext` afterward. `SavedAsync` runs after the
commit, for example to clear a cache of the secrets.

`IVippsWebhookSecretProtector` protects each secret with ASP.NET Core Data Protection, so persist your
Data Protection keys. A stored value without the `enc:v1:` prefix is read as plain text. An app that
moves from its own protector sets `SecretPurpose` and `SecretPrefix` to its old values, so its stored
secrets stay readable.

### Cleanup and signatures

A hosted service deletes processed events older than `Retention` at startup and every six hours. The
app fails to start if the retention does not exceed `VippsWebhookSignature.MaxAge`, because a replay
signed earlier could otherwise pass.

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

`Verify` returns a `VippsWebhookVerification` with a `VippsWebhookSignatureResult` and the age of
the signature. It checks the content hash of the raw body bytes and the HMAC-SHA256 signature over
the method, path, date, host, and hash. It also checks that the request was signed at most eight days
ago, because Vipps retries a failed webhook for up to seven.

## Helpers

- `VippsPhoneNumber.TryNormalize` turns a Norwegian mobile number into the digits Vipps expects, such
  as `4791234567`.
- `VippsAddressFormatter.Format` puts a Userinfo address on one line.

## Links

- [Source code](https://github.com/sondresjolyst/si-dotnet-packages/tree/main/src/SjolystInnovation.AppKit.Vipps)
- [Changelog](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/src/SjolystInnovation.AppKit.Vipps/CHANGELOG.md)
- [All AppKit packages](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/README.md)

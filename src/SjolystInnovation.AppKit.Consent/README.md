# SjolystInnovation.AppKit.Consent

This package keeps a server-side log of the cookie consent choices that your site's consent banner
sends. Each row records what was sent and the time the server received it. Use it as part of how you
document consent.

The log does not prove who made a choice. The endpoint is open, so any client can send to it, and the
package cannot tell a real click from a crafted request. Treat a row as "this choice was sent for this
browser at this time", not as proof of a person's consent.

- The browser makes a random consent id once and keeps it with its stored choice. It sends the id with
  every choice.
- Every request adds a row. Rows are never changed, so the rows for one consent id are the choices sent
  for that browser.
- The log is pseudonymous. A row holds **no IP address and no user agent**. It is tied to an account
  only when the request was signed in, through the user id.
- The server sets the time of each row. A time sent by the client is never used.
- A daily job deletes rows older than three years, or the period you choose.

Every refused field answers a validation problem with a stable code, so your frontend can react to it.

```sh
dotnet add package SjolystInnovation.AppKit.Consent
```

## Get started

This example signs requests in with AppKit.Auth's JWT bearer scheme, so the history endpoint answers
`401` without a token and a signed-in visitor's choice gets their user id. It limits the open endpoint
with a named policy of 20 requests per client IP each minute.

```csharp
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SjolystInnovation.AppKit.Auth;
using SjolystInnovation.AppKit.Consent;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddAppKitIdentity<User, ApplicationDbContext>();
builder.Services.AddAppKitAuth<ApplicationDbContext>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddAppKitJwtBearer<User>(isActive: u => !u.IsDeleted);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole("Admin"));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("consent", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddAppKitConsent<ApplicationDbContext>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapAppKitConsent<ApplicationDbContext>("/api", o => o.RateLimitPolicy = "consent");

app.Run();

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<User>(options), IAuthDbContext
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddAppKitRefreshTokens<User>();
        modelBuilder.AddAppKitConsent();
    }
}
```

`User` is your Identity user, as the AppKit.Auth README shows. Then add a migration to your app for the
`ConsentRecords` table. Behind a proxy, call AppKit.Hosting's `UseAppKitForwardedHeaders` first, so the
limiter sees the client's address.

## API

| Member | What it does |
| --- | --- |
| `AddAppKitConsent<TContext>` on `IServiceCollection` | Registers the options and the daily job that deletes old rows. |
| `AddAppKitConsent` on `ModelBuilder` | Maps the `ConsentRecords` table in `OnModelCreating`. |
| `MapAppKitConsent<TContext>` | Maps the consent endpoints. |
| `ConsentRecord` | One logged choice. Its properties are init-only. |
| `ConsentSources` | The allowed sources, `banner` and `settings`. |
| `ConsentErrorCodes` | The stable codes for refused fields. |
| `ConsentExtensions.MaxBodyBytes`, `ConsentExtensions.MaxHistory` | The body limit of 1024 bytes and the history limit of 100 rows. |
| `AppKitConsentOptions` | `KeepFor`, with `DefaultKeepFor`, `DefaultKeepForDays`, `MinimumKeepFor`, and `MinimumKeepForDays`. |
| `AppKitConsentEndpointOptions` | `RateLimitPolicy`, `RateLimitedExternally`, and the history's `Policy`. |

## Endpoints

`MapAppKitConsent` maps these routes under its prefix, which defaults to `/api`.

| Route | Who | What it does |
| --- | --- | --- |
| `POST consent` | Everyone | Checks the body, adds one row, and answers `204 No Content`. |
| `GET consent/{consentId}` | `Policy`, `Admin` by default | Lists the newest choices of one consent id, newest first. |

The body of `POST consent` is at most 1024 bytes. A larger body answers `413`. The server applies the
limit, so it holds on Kestrel and other servers that support a request size limit.

```json
{
  "consentId": "3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c",
  "marketing": true,
  "noticeVersion": "2026-10-24",
  "schemaVersion": 1,
  "source": "banner"
}
```

| Field | Rule |
| --- | --- |
| `consentId` | Required. A GUID in the form `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`, not all zeros. `crypto.randomUUID()` makes one. |
| `marketing` | Required. `true` or `false`. |
| `noticeVersion` | Required. At most 64 of the characters `A-Z a-z 0-9 . _ -`. |
| `schemaVersion` | Required. A whole number of at least 1. |
| `source` | Required. `banner` or `settings`, in lower case. |

Other fields, such as a time, are ignored. When the request is signed in, the row gets the user id from
the name identifier claim, or from `sub` when there is none. A user id longer than 450 characters is
left out and logged as a warning, and the choice is still recorded. A request that is not signed in
gets no user id. The endpoint reads nothing else from the request, so no IP address or user agent
reaches the table.

`GET consent/{consentId}` returns at most 100 rows. Add `?take=` for fewer. A `take` below 1 counts as
1 and one above 100 counts as 100.

## Errors

A refused body answers `400` with a validation problem. Next to the English messages in `errors`, it
has a `codes` extension keyed by field, and a `limits` extension with the limit for each `tooLong`:

```json
{
  "errors": { "NoticeVersion": ["At most 64 characters."] },
  "codes": { "NoticeVersion": ["tooLong"] },
  "limits": { "NoticeVersion": 64 }
}
```

| Code | Meaning |
| --- | --- |
| `required` | The field is missing or blank. |
| `tooLong` | The notice version is longer than 64 characters. |
| `invalidConsentId` | The consent id is not a GUID in the form above, or it is all zeros. |
| `invalidNoticeVersion` | The notice version has a character outside `A-Z a-z 0-9 . _ -`. |
| `invalidSchemaVersion` | The schema version is less than 1. |
| `invalidSource` | The source is not `banner` or `settings`. |

A body that is not JSON, or a field of the wrong JSON type, answers `400` before these checks.

## Rate limiting

`POST consent` is open to everyone. Without a limit, anyone can add rows as fast as they can send
requests, and the table grows without limit until the daily job removes rows older than `KeepFor`.
When `RateLimitPolicy` is null, `MapAppKitConsent` logs a warning at startup, unless
`RateLimitedExternally` is true. Pick one of these.

A named policy, registered with the ASP.NET Core rate limiter as in [Get started](#get-started), and
passed by name:

```csharp
app.MapAppKitConsent<ApplicationDbContext>("/api", o => o.RateLimitPolicy = "consent");
```

Or, with AppKit.Hosting's `AddAppKitRateLimiting`, a rule for the route in the `RateLimiting`
section. Leave `RateLimitPolicy` null then, and set `RateLimitedExternally` to skip the startup
warning. The package cannot see the rule, so the flag is a promise your app keeps. If the rule is
removed, nothing warns you.

```json
"RateLimiting": {
  "Rules": [
    { "Endpoint": "POST:/api/consent", "Period": "1m", "Limit": 20 }
  ]
}
```

```csharp
app.MapAppKitConsent<ApplicationDbContext>("/api", o => o.RateLimitedExternally = true);
```

The policy applies only to `POST consent`. The package registers no limiter of its own.

## Keeping rows

`AddAppKitConsent` registers a hosted service that deletes rows whose `DecidedAt` is older than
`KeepFor`, when the app starts and once a day after that. It deletes 1000 rows per statement until
none are left. On a provider without bulk delete, such as the in-memory provider, it loads and removes
each batch instead.

`KeepFor` defaults to `AppKitConsentOptions.DefaultKeepFor`, which is 1096 days: three years with one
leap day. It must be at least `MinimumKeepFor`, 365 days, or the app fails to start.

```csharp
builder.Services.AddAppKitConsent<ApplicationDbContext>(o => o.KeepFor = TimeSpan.FromDays(730));
```

## The table

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `long` | The key. |
| `ConsentId` | `Guid` | Indexed with `DecidedAt`. |
| `Marketing` | `bool` | |
| `NoticeVersion` | text, at most 64 | |
| `SchemaVersion` | `int` | |
| `DecidedAt` | `DateTime`, UTC | Set by the server. Indexed. |
| `UserId` | text, at most 450, nullable | Only for a signed-in request. |
| `Source` | text, at most 32 | `banner` or `settings`. |

There is no column for an IP address or a user agent.

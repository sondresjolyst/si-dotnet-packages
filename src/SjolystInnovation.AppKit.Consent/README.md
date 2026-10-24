# SjolystInnovation.AppKit.Consent

This package keeps a server-side log of the cookie consent choices that visitors make in your site's
consent banner. Use it when you want a record of what each browser agreed to, and when, without
storing who the visitor is.

- The browser makes a random consent id once and keeps it with its stored choice. It sends the id with
  every choice.
- Every choice adds a row. Rows are never changed, so the rows for one consent id are its history.
- The log is pseudonymous. A row holds **no IP address and no user agent**. It is tied to a person only
  when the visitor was signed in, through the user id.
- The server sets the time of each choice. A time sent by the browser is never used.
- A daily job deletes rows older than three years, or the period you choose.

Every refused field answers a validation problem with a stable code, so your frontend can react to it.

```sh
dotnet add package SjolystInnovation.AppKit.Consent
```

## Get started

```csharp
using Microsoft.EntityFrameworkCore;
using SjolystInnovation.AppKit.Consent;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddAuthentication();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole("Admin"));
builder.Services.AddAppKitConsent<ApplicationDbContext>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapAppKitConsent<ApplicationDbContext>("/api");

app.Run();

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddAppKitConsent();
    }
}
```

Then add a migration to your app for the `ConsentRecords` table.

## API

| Member | What it does |
| --- | --- |
| `AddAppKitConsent<TContext>` on `IServiceCollection` | Registers the options and the daily job that deletes old rows. |
| `AddAppKitConsent` on `ModelBuilder` | Maps the `ConsentRecords` table in `OnModelCreating`. |
| `MapAppKitConsent<TContext>` | Maps the consent endpoints. |
| `ConsentRecord` | One logged choice. |
| `ConsentSources` | The allowed sources, `banner` and `settings`. |
| `ConsentErrorCodes` | The stable codes for refused fields. |
| `AppKitConsentOptions` | `KeepFor`, and the default `DefaultKeepFor` with `DefaultKeepForDays`. |
| `AppKitConsentEndpointOptions` | `RateLimitPolicy` and the history's `Policy`. |

## Endpoints

`MapAppKitConsent` maps these routes under its prefix, which defaults to `/api`.

| Route | Who | What it does |
| --- | --- | --- |
| `POST consent` | Everyone | Checks the body, adds one row, and answers `204 No Content`. |
| `GET consent/{consentId}` | `Policy`, `Admin` by default | Lists the choices of one consent id, newest first. |

The body of `POST consent`:

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
the name identifier claim, or from `sub` when there is none. A request that is not signed in gets no
user id. The endpoint reads nothing else from the request, so no IP address or user agent reaches the
table.

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

`POST consent` is open to everyone, so limit it. Pick one of these.

With AppKit.Hosting, add a rule for the route to the `RateLimiting` section:

```json
{ "Endpoint": "POST:/api/consent", "Period": "1m", "Limit": 20 }
```

Or register a named policy with the ASP.NET Core rate limiter and pass its name:

```csharp
app.MapAppKitConsent<ApplicationDbContext>("/api", o => o.RateLimitPolicy = "consent");
```

The policy applies only to `POST consent`. The package registers no limiter of its own.

## Keeping rows

`AddAppKitConsent` registers a hosted service that deletes rows whose `DecidedAt` is older than
`KeepFor`, when the app starts and once a day after that. `KeepFor` defaults to
`AppKitConsentOptions.DefaultKeepFor`, which is 1096 days: three years with one leap day. It must be
positive, or the app fails to start.

```csharp
builder.Services.AddAppKitConsent<ApplicationDbContext>(o => o.KeepFor = TimeSpan.FromDays(730));
```

## The table

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `long` | The key. |
| `ConsentId` | `Guid` | Indexed. |
| `Marketing` | `bool` | |
| `NoticeVersion` | text, at most 64 | |
| `SchemaVersion` | `int` | |
| `DecidedAt` | `DateTime`, UTC | Set by the server. Indexed. |
| `UserId` | text, at most 450, nullable | Only for a signed-in visitor. |
| `Source` | text, at most 32 | `banner` or `settings`. |

There is no column for an IP address or a user agent.

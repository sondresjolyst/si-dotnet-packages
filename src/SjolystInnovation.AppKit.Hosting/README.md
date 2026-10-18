# SjolystInnovation.AppKit.Hosting

This package holds the startup pieces for an ASP.NET Core API that runs behind an ingress, such as in
Kubernetes. It takes the client IP only from trusted proxies, limits requests per client IP, adds
security headers and HTTPS, answers unreadable request bodies with 400, maps health probes, logs
requests without secrets, and applies migrations on start. Use it when you want these defaults in
every API without writing them again. You can adopt each piece on its own.

```sh
dotnet add package SjolystInnovation.AppKit.Hosting
```

## Get started

The order of the middleware matters. The sections below say why each call sits where it does.

```csharp
using Microsoft.EntityFrameworkCore;
using SjolystInnovation.AppKit.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddProblemDetails();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("https://example.com")));
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddAppKitHealthChecks<ApplicationDbContext>();
builder.Services.AddAppKitRateLimiting(builder.Configuration.GetSection("RateLimiting"));

var app = builder.Build();

await app.Services.MigrateAppKitDatabaseAsync<ApplicationDbContext>(app.Configuration);

app.UseAppKitForwardedHeaders();
app.UseExceptionHandler();
app.UseAppKitSecurityHeaders();
app.UseAppKitHttps();

app.UseRouting();
app.UseAppKitRequestLogging();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseAppKitRateLimiting();
app.UseAppKitBadRequests();

app.MapAppKitHealthChecks();
app.MapGet("/api/products", () => Results.Ok());

app.Run();

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options);
```

```json
{
  "TrustedProxies": ["10.42.0.0/16"], // the k3s default pod network, use your own
  "RateLimiting": {
    "Rules": [
      { "Endpoint": "*:/api/auth/login", "Period": "1m", "Limit": 10 }
    ]
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore.Hosting.Diagnostics": "Warning"
    }
  }
}
```

| Call | What it does |
| --- | --- |
| `UseAppKitForwardedHeaders` | Reads the client IP and scheme from trusted proxies only. |
| `AddAppKitRateLimiting`, `UseAppKitRateLimiting` | Limits requests per client IP with rules from configuration. |
| `UseAppKitSecurityHeaders` | Adds `X-Content-Type-Options` and `X-Frame-Options` to every response. |
| `UseAppKitHttps` | Sends HSTS and redirects HTTP to HTTPS outside Development. |
| `UseAppKitBadRequests` | Answers a JSON body that is not valid UTF-8, and any other unreadable request, with a 4xx instead of a 500. |
| `UseAppKitRequestLogging` | Logs each request without its secrets. |
| `AddAppKitHealthChecks`, `MapAppKitHealthChecks` | Maps the liveness and readiness probes. |
| `MigrateAppKitDatabaseAsync` | Applies pending Entity Framework Core migrations on start. |

## Trusted proxies

`UseAppKitForwardedHeaders` reads `X-Forwarded-For` and `X-Forwarded-Proto` only from callers in
`TrustedProxies`, and only the hop the proxy added. Any other caller keeps its own address, whatever
headers it sends. List the network the ingress runs in, which in Kubernetes is the pod network. The example uses the default pod network of k3s. Use your own cluster's range. To
read the list from another configuration section, pass its name, as in
`UseAppKitForwardedHeaders("Ingress:Proxies")`.

```json
"TrustedProxies": [
  "10.42.0.0/16" // the k3s default pod network, use your own
]
```

An IPv4 caller on a dual-stack socket arrives as `::ffff:10.42.3.17`, and is matched against IPv4
entries. If an entry is not a CIDR, the app fails to start. If the list is empty, no forwarded header
is read. Outside Development the app then logs a warning, because behind an ingress every client
would share its address.

Call `UseAppKitForwardedHeaders` first, so later middleware sees the client's address and scheme.

## Rate limiting

`AddAppKitRateLimiting` registers the ASP.NET Core rate limiter with rules from configuration.

```json
"RateLimiting": {
  "Rules": [
    { "Endpoint": "*:/api/auth/login", "Period": "1m", "Limit": 10 },
    { "Endpoint": "POST:/api/contact", "Period": "1m", "Limit": 5 },
    { "Endpoint": "*:/api/products*", "Period": "1m", "Limit": 60 }
  ]
}
```

| Field | Meaning |
| --- | --- |
| `Endpoint` | `VERB:/path`. The verb is an HTTP method or `*`. In the path, `*` matches any run of characters. The whole path must match, ignoring case and trailing slashes. |
| `Period` | A whole number with `s`, `m`, `h`, or `d`. |
| `Limit` | Requests per client IP in each period. |

Each rule keeps a fixed window per client IP, taken from the connection after
`UseAppKitForwardedHeaders`. The limiter reads no header for it. A request that matches several rules
counts against each. A rejected request gets `429`, a `Retry-After` header in seconds, and a problem
details body with a title but no detail, so the client shows its own message. If a rule cannot be
read, the app fails to start.

Call `UseAppKitRateLimiting` after `UseCors`, so browser preflight requests are not counted. Counts
live in memory, per instance.

## Security headers

`UseAppKitSecurityHeaders` adds `X-Content-Type-Options: nosniff` and `X-Frame-Options: DENY` to every
response.

## HTTPS

Outside Development, `UseAppKitHttps` sends HSTS and redirects plain HTTP to HTTPS. Paths under
`/health` are never redirected, because probes reach the pod over plain HTTP. Call it after
`UseAppKitForwardedHeaders`, so a request that the ingress received over HTTPS is not redirected.

## Bad requests

`UseAppKitBadRequests` makes every endpoint answer a request it cannot read with a client error and a
problem details body, never with a 500.

- A body with a JSON content type that is not valid UTF-8 gets `400` before the endpoint runs. JSON is
  UTF-8, so this covers `application/json` and types like `application/merge-patch+json` with no
  charset or a UTF-8 one. Without the check, an endpoint that binds a `JsonElement` throws when it
  reads a string, and the exception handler answers `500`.
- A minimal API endpoint that cannot bind a parameter gets `400` with a problem body in every
  environment. That covers a body that is not valid JSON, a missing required body, and a query or route
  value that does not parse. Outside Development, ASP.NET Core answers these with an empty `400` by
  default, so the call sets `RouteHandlerOptions.ThrowOnBadRequest` to `true`, as Development already
  has it.
- A `BadHttpRequestException` gets its own status code instead of reaching the exception handler. The
  endpoints above throw one, and Kestrel throws one with `413` for a body over the size limit.

The problem body has a title but no detail, so the client shows its own message. The reason is logged
at Information. A `JsonException` that an endpoint throws itself is left to the exception handler,
because it can come from a reply the API read from another service. A response the endpoint writes
itself is left alone, also an empty `400`. A body with a content type the endpoint does not accept gets
`415` from routing before the endpoint runs, and that response has no body.

Call it after `UseAuthorization` and `UseAppKitRateLimiting`, so a caller without access still gets
`401` or `403`, and a limited caller gets `429` before the body is read. Call it before you map any
endpoint, because an endpoint reads `ThrowOnBadRequest` when it is mapped. Only requests that matched
an endpoint are checked. The check reads the body once and rewinds it, so the endpoint reads it from
the start.

## Request logging

`UseAppKitRequestLogging` logs each request so you can follow traffic without leaking secrets. It
writes `Incoming Request {@LogData}` at Information with the method and path, and `Header {@LogData}`
at Debug for each request header. It never reads the request body.

- Headers in `SensitiveHeaders` are left out.
- A header whose name contains an entry of `SensitiveHeaderParts` is logged with `[Redacted]` as its
  value. That covers custom headers such as `X-Vipps-Signature` or `X-CSRF-Token`.
- The query string is left out, because it can carry a token, such as the `access_token` that SignalR
  sends. If you turn on `LogQueryString`, the value of each parameter whose name contains an entry of
  `SensitiveQueryParameters` is replaced with `[Redacted]`.
- A path segment, query value, or header value that looks like an email address is replaced with
  `[Redacted]`.
- Line breaks are removed from every logged value, so a request cannot forge a log line.
- Paths under `SkippedPaths` are not logged at all.

```csharp
app.UseAppKitRequestLogging(o =>
{
    o.SkippedPaths.Add("/metrics");
    o.SensitiveHeaders.Add("X-Tenant-Secret");
    o.SensitiveHeaderParts.Add("tenant");
});
```

| Option | Default | Meaning |
| --- | --- | --- |
| `SkippedPaths` | `/health` | Path prefixes that are not logged, matched by whole segments and ignoring case. Each entry starts with `/`, or the call throws `ArgumentException`. |
| `SensitiveHeaders` | `Authorization`, `Cookie`, `Set-Cookie`, `Proxy-Authorization`, `X-Api-Key`, `Idempotency-Key` | Headers that are never logged, ignoring case. |
| `SensitiveHeaderParts` | `token`, `auth`, `secret`, `session`, `signature`, `csrf`, `xsrf`, `cookie`, `key`, `password` | Parts of header names whose values are redacted, ignoring case. |
| `LogQueryString` | `false` | Whether the query string is logged, redacted, with the path. |
| `SensitiveQueryParameters` | `token`, `password`, `secret`, `code`, `key`, `signature`, `sig`, `email` | Parts of parameter names whose values are redacted, ignoring case. `token` also covers `access_token` and `refresh_token`. |

Each set starts with its defaults, so `Add` extends the list and `Remove` or `Clear` shrinks it.

ASP.NET Core logs its own `Request starting` record at Information under
`Microsoft.AspNetCore.Hosting.Diagnostics`, and that record has the full URL with the query string. If
a query can carry a token, raise that category to `Warning`, as in the configuration in
the get started section.

`LogSanitizer.Sanitize` removes line breaks from a value, `LogSanitizer.LooksLikeEmail` checks the
shape of an email address, and `LogSanitizer.Redacted` is the `[Redacted]` placeholder. Use them when
your own log messages include values from a request.

## Health checks

`AddAppKitHealthChecks<TContext>` adds a check that the `DbContext` reaches its database.
`MapAppKitHealthChecks` maps two anonymous probes:

| Path | Who | What it does |
| --- | --- | --- |
| `/health` | Everyone | Liveness and startup. Runs no checks, so a database outage does not restart the pod. |
| `/health/ready` | Everyone | Readiness. Fails while a check tagged `ready` fails, which takes the pod out of the Service. |

To add more readiness checks, add them to the builder that `AddAppKitHealthChecks` returns, with the
tag `"ready"`.

## Migrations on start

`MigrateAppKitDatabaseAsync<TContext>` applies the pending Entity Framework Core migrations when the
app starts. It creates its own scope, so you can call it on `app.Services` right after
`builder.Build()`.

```csharp
await app.Services.MigrateAppKitDatabaseAsync<ApplicationDbContext>(app.Configuration, o =>
{
    // An index build on a large table takes longer than the default command timeout.
    o.CommandTimeout = TimeSpan.FromMinutes(15);
});
```

| Option | Default | Meaning |
| --- | --- | --- |
| `CommandTimeout` | `null` | The command timeout while migrations run. `null` keeps the provider's timeout. The previous timeout comes back afterward, also when a migration fails. |
| `Retries` | 5 | Retries when the database cannot be reached. |
| `RetryDelay` | 2 seconds | The wait before the first retry. Each later wait is twice as long. |

The helper logs `Applying {Count} pending migration(s): {Migrations}` before it migrates. If nothing
is pending, it only writes a debug message.

To turn it off, for example when a separate job runs the migrations, set `Database:AutoMigrate` to
`false`. The helper then logs that it skipped and returns. The setting defaults to `true`.

```json
"Database": {
  "AutoMigrate": false
}
```

The helper retries only while it checks for pending migrations, which is when it first reaches the
database. It retries a provider error marked transient, a socket error, or a timeout. A failing
migration is never retried, so the app fails to start and the error is in the log.

## Links

- [Source code](https://github.com/sondresjolyst/si-dotnet-packages/tree/main/src/SjolystInnovation.AppKit.Hosting)
- [Changelog](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/src/SjolystInnovation.AppKit.Hosting/CHANGELOG.md)
- [All AppKit packages](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/README.md)

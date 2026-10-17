# SjolystInnovation.AppKit.Hosting

This package holds startup pieces for an ASP.NET Core API behind an ingress: the client IP taken only
from trusted proxies, per-IP rate limiting on that IP, security headers, HTTPS, health probes, request
logging without secrets, and migrations on start. You can adopt each piece on its own.

```sh
dotnet add package SjolystInnovation.AppKit.Hosting
```

## Setup

```csharp
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
app.UseCors("AllowAllOrigins");
app.UseAuthentication();
app.UseAuthorization();
app.UseAppKitRateLimiting();
app.MapControllers();
app.MapAppKitHealthChecks();
```

## Trusted proxies

`UseAppKitForwardedHeaders` reads `X-Forwarded-For` and `X-Forwarded-Proto` only from callers in
`TrustedProxies`, and only the hop the proxy added. Any other caller keeps its own address, whatever
headers it sends. List the network the ingress runs in, which in Kubernetes is the pod network.

```json
"TrustedProxies": [
  "192.169.0.0/16"
]
```

An IPv4 caller on a dual-stack socket arrives as `::ffff:192.169.36.80`, and is matched against IPv4
entries. If an entry is not a CIDR, the app fails to start. If the list is empty, no forwarded header
is read, and outside Development the app logs a warning, because behind an ingress every client would
share its address.

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

| Field | |
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
`UseAppKitForwardedHeaders`, so a request the ingress received over HTTPS is not redirected.

## Request logging

`UseAppKitRequestLogging` logs each request so you can follow traffic without leaking secrets. It writes
`Incoming Request {@LogData}` at Information with the method and path, and `Header {@LogData}` at Debug
for each request header. It never reads the request body.

- Headers in `SensitiveHeaders` are left out.
- The query string is left out, because it can carry a token, such as the `access_token` that SignalR
  sends. If you turn on `LogQueryString`, the value of each parameter whose name contains an entry of
  `SensitiveQueryParameters` is replaced with `[Redacted]`.
- A path segment or query value that looks like an email address is replaced with `[Redacted]`.
- Line breaks are removed from every logged value, so a request cannot forge a log line.
- Paths under `SkippedPaths` are not logged at all.

```csharp
app.UseAppKitRequestLogging(o =>
{
    o.SkippedPaths.Add("/metrics");
    o.SensitiveHeaders.Add("X-Tenant-Secret");
});
```

| Option | Default | |
| --- | --- | --- |
| `SkippedPaths` | `/health` | Path prefixes that are not logged, matched by whole segments and ignoring case. Each entry starts with `/`, or the app fails to start. |
| `SensitiveHeaders` | `Authorization`, `Cookie`, `Set-Cookie`, `Proxy-Authorization`, `X-Api-Key`, `Idempotency-Key` | Headers that are never logged, ignoring case. |
| `LogQueryString` | `false` | Whether the query string is logged, redacted, with the path. |
| `SensitiveQueryParameters` | `token`, `password`, `secret`, `code`, `key`, `signature`, `sig`, `email` | Parts of parameter names whose values are redacted, ignoring case. `token` also covers `access_token` and `refresh_token`. |

ASP.NET Core logs its own `Request starting` record at Information under
`Microsoft.AspNetCore.Hosting.Diagnostics`, and that record has the full URL with the query string. If a
query can carry a token, raise that category to `Warning`.

`LogSanitizer.Sanitize` removes line breaks from a value, and `LogSanitizer.LooksLikeEmail` checks the
shape of an email address. Use them when your own log messages include values from a request.

## Health checks

`AddAppKitHealthChecks<TContext>` adds a check that the `DbContext` reaches its database.
`MapAppKitHealthChecks` maps two anonymous probes:

| Path | |
| --- | --- |
| `/health` | Liveness and startup. Runs no checks, so a database outage does not restart the pod. |
| `/health/ready` | Readiness. Fails while a check tagged `ready` fails, which takes the pod out of the Service. |

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

| Option | |
| --- | --- |
| `CommandTimeout` | The command timeout while migrations run. The previous timeout comes back afterward, also when a migration fails. Defaults to `null`, which keeps the provider's timeout. |
| `Retries` | Retries when the database cannot be reached. Defaults to 5. |
| `RetryDelay` | The wait before the first retry. Each later wait is twice as long. Defaults to 2 seconds. |

The helper logs `Applying {Count} pending migration(s): {Migrations}` before it migrates. If nothing is
pending, it only writes a debug message.

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

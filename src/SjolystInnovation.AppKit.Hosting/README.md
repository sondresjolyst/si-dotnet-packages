# SjolystInnovation.AppKit.Hosting

Startup pieces for an ASP.NET Core API behind an ingress: the client IP taken only from trusted
proxies, per-IP rate limiting on that IP, security headers, HTTPS and health probes. Each piece is
adopted on its own.

```sh
dotnet add package SjolystInnovation.AppKit.Hosting
```

## Setup

```csharp
builder.Services.AddAppKitHealthChecks<ApplicationDbContext>();
builder.Services.AddAppKitRateLimiting(builder.Configuration.GetSection("RateLimiting"));

var app = builder.Build();

app.UseAppKitForwardedHeaders();
app.UseExceptionHandler();
app.UseAppKitSecurityHeaders();
app.UseAppKitHttps();

app.UseRouting();
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

An IPv4 caller on a dual-stack socket, which arrives as `::ffff:192.169.36.80`, is matched against
IPv4 entries. The app fails to start on an entry that is not a CIDR. With an empty list no
forwarded header is read, and outside Development a warning is logged, because behind an ingress
every client would share its address.

Call it first, so later middleware sees the client's address and scheme.

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
| `Endpoint` | `VERB:/path`. The verb is an HTTP method or `*`. In the path, `*` matches any run of characters. The whole path must match, ignoring case. |
| `Period` | A whole number with `s`, `m`, `h` or `d`. |
| `Limit` | Requests per client IP in each period. |

Each rule keeps a fixed window per client IP, taken from the connection after
`UseAppKitForwardedHeaders`. No header is read for it. A request that matches several rules counts
against each. A rejected request gets `429`, a `Retry-After` header in seconds, and a problem details
body with a title but no detail, so the client shows its own message. The app fails to start on a rule it cannot read.

Call `UseAppKitRateLimiting` after `UseCors`, so browser preflight requests are not counted. Counts
live in memory, per instance.

## Security headers

`UseAppKitSecurityHeaders` adds `X-Content-Type-Options: nosniff` and `X-Frame-Options: DENY` to every
response.

## HTTPS

Outside Development, `UseAppKitHttps` sends HSTS and redirects plain HTTP to HTTPS. Paths under
`/health` are never redirected, since probes reach the pod over plain HTTP. Call it after
`UseAppKitForwardedHeaders`, so a request the ingress received over HTTPS is not redirected.

## Health checks

`AddAppKitHealthChecks<TContext>` adds a check that the DbContext reaches its database.
`MapAppKitHealthChecks` maps two anonymous probes:

| Path | |
| --- | --- |
| `/health` | Liveness and startup. Runs no checks, so a database outage does not restart the pod. |
| `/health/ready` | Readiness. Fails while a check tagged `ready` fails, which takes the pod out of the Service. |

Add more readiness checks to the builder `AddAppKitHealthChecks` returns, with the tag `"ready"`.

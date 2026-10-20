# si-dotnet-packages

This repository holds the shared .NET packages for the Sjølyst Innovation apps. They are the .NET side
of the `@sjolystinnovation/app-kit` npm package, with one package per area. Each package is named
`SjolystInnovation.AppKit.<Area>`, has a namespace of the same name, targets .NET 10, and is published
on NuGet under the Apache-2.0 license.

## Packages

| Package | What it does |
| --- | --- |
| [SjolystInnovation.AppKit.AdminStats](src/SjolystInnovation.AppKit.AdminStats/README.md) | Daily admin statistics in your app's own snapshot table, with a history endpoint that adds today's live counts. |
| [SjolystInnovation.AppKit.Auth](src/SjolystInnovation.AppKit.Auth/README.md) | JWT access tokens, rotating refresh tokens, Identity defaults, reset and verification codes, user administration, and account deletion, with stable error codes. |
| [SjolystInnovation.AppKit.Email](src/SjolystInnovation.AppKit.Email/README.md) | Transactional email and delivery statistics through Brevo, with a send result instead of exceptions. |
| [SjolystInnovation.AppKit.Hosting](src/SjolystInnovation.AppKit.Hosting/README.md) | Trusted proxies, rate limiting, security headers, HTTPS, 400 for unreadable request bodies, health checks, request logging without secrets, and migrations on start. |
| [SjolystInnovation.AppKit.Pdf](src/SjolystInnovation.AppKit.Pdf/README.md) | HTML to PDF through headless Chromium, with one reused browser, scripts and network requests off by default, and a render timeout. |
| [SjolystInnovation.AppKit.SiteSettings](src/SjolystInnovation.AppKit.SiteSettings/README.md) | Admin-managed company details, branding, legal pages, and page sections, with stable error codes. |
| [SjolystInnovation.AppKit.Storage](src/SjolystInnovation.AppKit.Storage/README.md) | Uploaded files on local disk, checked by their first bytes and served with a safe `Content-Disposition`, and the disk use of the stores. |
| [SjolystInnovation.AppKit.Storage.Images](src/SjolystInnovation.AppKit.Storage.Images/README.md) | Content images with resized WebP renditions, EXIF orientation, cache-friendly serving, and orphan cleanup. |
| [SjolystInnovation.AppKit.Vipps](src/SjolystInnovation.AppKit.Vipps/README.md) | A Vipps MobilePay client with signed webhook endpoints, webhook registration, and phone number checks. |

## Get started

1. Add the packages your app needs:

   ```sh
   dotnet add package SjolystInnovation.AppKit.Hosting
   dotnet add package SjolystInnovation.AppKit.Auth
   ```

2. Register the services and map the endpoints in `Program.cs`. Each package follows the same pattern:
   `AddAppKit*` registers services, `MapAppKit*` maps endpoints, `UseAppKit*` adds middleware, and an
   `AddAppKit*` extension on `ModelBuilder` maps the package's tables in your own `DbContext`.

   ```csharp
   using SjolystInnovation.AppKit.Hosting;

   var builder = WebApplication.CreateBuilder(args);
   builder.Services.AddAppKitRateLimiting(builder.Configuration.GetSection("RateLimiting"));

   var app = builder.Build();
   app.UseAppKitForwardedHeaders();
   app.UseAppKitSecurityHeaders();
   app.UseAppKitRequestLogging();
   app.UseAppKitRateLimiting();
   app.UseAppKitBadRequests();
   app.MapGet("/api/hello", () => "Hello");
   app.Run();
   ```

3. Add a migration for the tables of the packages that store data. Your app keeps its own migrations.

Each package README has a complete `Program.cs` example, the options with their defaults, and the
endpoints it maps.

To work on the packages, see the [contributing guide](CONTRIBUTING.md).

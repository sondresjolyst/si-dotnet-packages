# si-dotnet-packages

This repository holds the shared .NET packages for the Sjølyst Innovation apps. They are published on
NuGet under the Apache-2.0 license. They are the .NET side of `@sjolystinnovation/app-kit`, with one
package per area. Each package is named `SjolystInnovation.AppKit.<Area>` and has a namespace of the
same name.

| Package | What it does |
| --- | --- |
| [SjolystInnovation.AppKit.Auth](src/SjolystInnovation.AppKit.Auth/README.md) | JWT access tokens and bearer validation, rotating refresh tokens with reuse detection and cleanup, Identity defaults, and password reset and email verification codes for ASP.NET Core Identity apps. |
| [SjolystInnovation.AppKit.Email](src/SjolystInnovation.AppKit.Email/README.md) | Transactional email through Brevo, with reply-to, attachments, a send result instead of exceptions, and delivery statistics. |
| [SjolystInnovation.AppKit.Hosting](src/SjolystInnovation.AppKit.Hosting/README.md) | Forwarded headers from trusted proxies only, per-IP rate limiting, security headers, HTTPS, health checks, and migrations on start for ASP.NET Core APIs behind an ingress. |
| [SjolystInnovation.AppKit.SiteSettings](src/SjolystInnovation.AppKit.SiteSettings/README.md) | Admin-managed company details, site branding, and legal pages: settings, public company info, logo and icon uploads, and editable terms, privacy, and cookie pages, with Norwegian organization number and postal code checks. |
| [SjolystInnovation.AppKit.Vipps](src/SjolystInnovation.AppKit.Vipps/README.md) | A client for the Vipps MobilePay Recurring, ePayment, Userinfo, and Webhooks APIs, with webhook signature checks. |

```sh
dotnet add package SjolystInnovation.AppKit.Auth
dotnet add package SjolystInnovation.AppKit.Email
dotnet add package SjolystInnovation.AppKit.Hosting
dotnet add package SjolystInnovation.AppKit.SiteSettings
dotnet add package SjolystInnovation.AppKit.Vipps
```

To work on the packages, see the [contributing guide](CONTRIBUTING.md).

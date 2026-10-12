# si-dotnet-packages

Shared .NET packages for the Sjølyst Innovation apps, published on NuGet under Apache-2.0. They are
the .NET side of `@sjolystinnovation/app-kit`, one package per area, each named
`SjolystInnovation.AppKit.<Area>` with a namespace of the same name.

| Package | What it does |
| --- | --- |
| [SjolystInnovation.AppKit.Auth](src/SjolystInnovation.AppKit.Auth/README.md) | JWT access tokens, rotating refresh tokens with reuse detection, and password reset and email verification codes for ASP.NET Core Identity apps. |
| [SjolystInnovation.AppKit.Vipps](src/SjolystInnovation.AppKit.Vipps/README.md) | A client for the Vipps MobilePay Recurring, ePayment, Userinfo and Webhooks APIs, with webhook signature checks. |

```sh
dotnet add package SjolystInnovation.AppKit.Auth
dotnet add package SjolystInnovation.AppKit.Vipps
```

See [CONTRIBUTING.md](CONTRIBUTING.md) to work on them.

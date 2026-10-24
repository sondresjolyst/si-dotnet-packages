# Contributing

Bug reports, questions, and pull requests are welcome. If your change is bigger than a small fix, open
an issue first to agree on the approach.

## Run the checks

```sh
dotnet build
dotnet run --project tests/SjolystInnovation.AppKit.AdminStats.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Auth.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Consent.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Email.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Hosting.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Pdf.Tests
dotnet run --project tests/SjolystInnovation.AppKit.SiteSettings.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Storage.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Storage.Images.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Vipps.Tests
```

The tests use Microsoft.Testing.Platform, so each test project runs as a program. CI runs the same
build and tests on every pull request.

## Good to know

- The packages target .NET 10. Warnings are errors, at the `latest-recommended` analysis level.
- Apart from other packages in this repository, a package depends only on Microsoft packages, such as
  `Microsoft.Extensions.*`, Entity Framework Core, `System.IdentityModel.Tokens.Jwt`, and SkiaSharp.
  For example, Auth depends on Email. The one exception is Pdf, which drives Chromium through
  PuppeteerSharp. Dependencies stay at their current version. Dependabot raises
  them, and a release follows.
- A package holds no app's types. An app maps its own models to the package's requests.
- A new area is a new package named `SjolystInnovation.AppKit.<Area>`, in `src/` with its tests in
  `tests/`, and a namespace of the same name.
- Versions come from release-please, based on Conventional Commits. Do not edit `<Version>` by hand.

## Commits

Use Conventional Commits with one scope, such as `feat(vipps): add refunds`. The title says what
changed. Use the body for what the title and the diff do not show.

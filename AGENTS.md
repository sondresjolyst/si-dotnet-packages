# AGENTS.md

.NET packages published to NuGet. Read [CONTRIBUTING.md](CONTRIBUTING.md) first.

## Verify before reporting work as done

```sh
dotnet build
dotnet run --project tests/SjolystInnovation.AppKit.AdminStats.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Auth.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Email.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Hosting.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Pdf.Tests
dotnet run --project tests/SjolystInnovation.AppKit.SiteSettings.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Storage.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Storage.Images.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Vipps.Tests
```

## Never

- Never put an app's models or settings into a package. Take plain request records.
- Never pin a Microsoft package below its current version. Dependabot keeps them
  current.
- Never edit `<Version>` by hand. release-please owns it.
- Never commit, push or open a pull request unless asked.

## Writing

English prose in comments, commit messages and documentation. No em dashes and no semicolons in
prose. Comments describe current behaviour. Commit and pull request bodies stay short.

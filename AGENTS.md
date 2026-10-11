# AGENTS.md

.NET packages published to NuGet. Read [CONTRIBUTING.md](CONTRIBUTING.md) first.

## Verify before reporting work as done

```sh
dotnet build
dotnet run --project tests/SjolystInnovation.AppKit.Vipps.Tests
```

## Never

- Never put an app's models or settings into a package. Take plain request records.
- Never raise a `Microsoft.Extensions.*` floor without a reason in the code.
- Never edit `<Version>` by hand. release-please owns it.
- Never commit, push or open a pull request unless asked.

## Writing

English prose in comments, commit messages and documentation. No em dashes and no semicolons in
prose. Comments describe current behaviour. Commit and pull request bodies stay short.

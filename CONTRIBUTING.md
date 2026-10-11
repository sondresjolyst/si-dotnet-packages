# Contributing

Bug reports, questions and pull requests are welcome. For anything bigger than a small fix, open an
issue first so we can agree on the shape.

## Running the checks

```sh
dotnet build
dotnet run --project tests/Si.Vipps.Tests
```

The tests run on Microsoft.Testing.Platform, so they run as a program. CI runs the same build and
tests on every pull request.

## Good to know

- The packages target .NET 10. Warnings are errors, at the `latest-recommended` analysis level.
- A package depends only on `Microsoft.Extensions.*` abstractions, at the lowest version it needs,
  so an app picks its own versions.
- A package holds no app's types. An app maps its own models to the package's requests.
- Versions come from release-please, out of Conventional Commits. Do not edit `<Version>` by hand.

## Commits

Conventional Commits with one scope, such as `feat(vipps): add refunds`. The title says what
changed. A body is for what the title and the diff do not show.

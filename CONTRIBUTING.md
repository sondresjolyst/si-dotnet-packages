# Contributing

Bug reports, questions and pull requests are welcome. For anything bigger than a small fix, open an
issue first so we can agree on the shape.

## Running the checks

```sh
dotnet build
dotnet run --project tests/SjolystInnovation.AppKit.Auth.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Hosting.Tests
dotnet run --project tests/SjolystInnovation.AppKit.Vipps.Tests
```

The tests run on Microsoft.Testing.Platform, so they run as a program. CI runs the same build and
tests on every pull request.

## Good to know

- The packages target .NET 10. Warnings are errors, at the `latest-recommended` analysis level.
- A package depends only on Microsoft packages, such as `Microsoft.Extensions.*`, Entity Framework
  Core and `System.IdentityModel.Tokens.Jwt`, kept at their current version. Dependabot raises them,
  and a release follows.
- A package holds no app's types. An app maps its own models to the package's requests.
- A new area is a new package named `SjolystInnovation.AppKit.<Area>`, in `src/` with its tests in
  `tests/`, and a namespace of the same name.
- Versions come from release-please, out of Conventional Commits. Do not edit `<Version>` by hand.

## Commits

Conventional Commits with one scope, such as `feat(vipps): add refunds`. The title says what
changed. A body is for what the title and the diff do not show.

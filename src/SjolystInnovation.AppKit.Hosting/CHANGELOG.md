# Changelog

## [1.1.0](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Hosting-v1.0.3...SjolystInnovation.AppKit.Hosting-v1.1.0) (2026-10-22)


### Features

* **hosting:** add IpTruncator for storing client addresses ([#111](https://github.com/sondresjolyst/si-dotnet-packages/issues/111)) ([3ab6b63](https://github.com/sondresjolyst/si-dotnet-packages/commit/3ab6b6395423c995c4ed70cbad15e1755d374c8b))

## [1.0.3](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Hosting-v1.0.2...SjolystInnovation.AppKit.Hosting-v1.0.3) (2026-10-18)


### Bug Fixes

* **hosting:** give the empty 415 and 405 answers from routing a problem details body ([#87](https://github.com/sondresjolyst/si-dotnet-packages/issues/87)) ([311347f](https://github.com/sondresjolyst/si-dotnet-packages/commit/311347f775909720887ea368743718e563b33ace)), closes [#85](https://github.com/sondresjolyst/si-dotnet-packages/issues/85)

## [1.0.2](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Hosting-v1.0.1...SjolystInnovation.AppKit.Hosting-v1.0.2) (2026-10-18)


### Bug Fixes

* **hosting:** give a minimal API binding failure a problem details body in every environment ([#84](https://github.com/sondresjolyst/si-dotnet-packages/issues/84)) ([979d8ce](https://github.com/sondresjolyst/si-dotnet-packages/commit/979d8ceb390144eaae95490ad80a9e2917777156)), closes [#83](https://github.com/sondresjolyst/si-dotnet-packages/issues/83)

## [1.0.1](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Hosting-v1.0.0...SjolystInnovation.AppKit.Hosting-v1.0.1) (2026-10-18)


### Bug Fixes

* **hosting:** answer unreadable JSON request bodies with 400 instead of 500 ([#79](https://github.com/sondresjolyst/si-dotnet-packages/issues/79)) ([2505d79](https://github.com/sondresjolyst/si-dotnet-packages/commit/2505d798805b9efbb1cc6b0e8c87e5b403663f1c)), closes [#76](https://github.com/sondresjolyst/si-dotnet-packages/issues/76)

## [1.0.0](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Hosting-v0.3.0...SjolystInnovation.AppKit.Hosting-v1.0.0) (2026-10-17)


### Documentation

* rewrite the Hosting, Vipps and AdminStats READMEs for nuget.org ([#70](https://github.com/sondresjolyst/si-dotnet-packages/issues/70)) ([55dd038](https://github.com/sondresjolyst/si-dotnet-packages/commit/55dd03875742f0ceee65be6ad70f13a84f5ae608))

## [0.3.0](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Hosting-v0.2.0...SjolystInnovation.AppKit.Hosting-v0.3.0) (2026-10-17)


### Features

* **hosting:** add UseAppKitRequestLogging with log sanitizing ([#54](https://github.com/sondresjolyst/si-dotnet-packages/issues/54)) ([1d7f9bb](https://github.com/sondresjolyst/si-dotnet-packages/commit/1d7f9bb60e11189f4d316d4da6927636cc11349e))

## [0.2.0](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Hosting-v0.1.0...SjolystInnovation.AppKit.Hosting-v0.2.0) (2026-10-16)


### ⚠ BREAKING CHANGES

* **site-settings:** align SiteSettings, Hosting and Email for 1.0 ([#44](https://github.com/sondresjolyst/si-dotnet-packages/issues/44))

### Features

* **hosting:** add MigrateAppKitDatabaseAsync ([#37](https://github.com/sondresjolyst/si-dotnet-packages/issues/37)) ([10e3a6a](https://github.com/sondresjolyst/si-dotnet-packages/commit/10e3a6ab93b7696530f888d722c8968e8555b648))


### Code Refactoring

* **site-settings:** align SiteSettings, Hosting and Email for 1.0 ([#44](https://github.com/sondresjolyst/si-dotnet-packages/issues/44)) ([b201d6c](https://github.com/sondresjolyst/si-dotnet-packages/commit/b201d6c054c38ad24f80f9a5ec0f965b5a030735))

## 0.1.0 (2026-10-13)


### Features

* **hosting:** add SjolystInnovation.AppKit.Hosting for trusted proxies, rate limiting, security headers and health checks ([#15](https://github.com/sondresjolyst/si-dotnet-packages/issues/15)) ([2244146](https://github.com/sondresjolyst/si-dotnet-packages/commit/22441467ba1d0d43cb96756da9a02246bbf1fbad))

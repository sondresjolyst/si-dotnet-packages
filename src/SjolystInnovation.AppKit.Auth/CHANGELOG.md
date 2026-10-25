# Changelog

## [1.1.1](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Auth-v1.1.0...SjolystInnovation.AppKit.Auth-v1.1.1) (2026-10-25)


### Dependencies

* **nuget:** bump `Microsoft.Extensions.Configuration` from 10.0.12 to 10.0.13 ([#124](https://github.com/sondresjolyst/si-dotnet-packages/issues/124)) ([bf242b7](https://github.com/sondresjolyst/si-dotnet-packages/commit/bf242b78b3ace861d6d6a4cd139c657135206c08))
* **nuget:** Bump Microsoft.AspNetCore.Authentication.JwtBearer and 8 others ([#123](https://github.com/sondresjolyst/si-dotnet-packages/issues/123)) ([6551264](https://github.com/sondresjolyst/si-dotnet-packages/commit/655126460b80d03f3c79634d41a5ddda7784bc3d))
* **nuget:** Bump Microsoft.AspNetCore.Authentication.JwtBearer and 8 others ([#131](https://github.com/sondresjolyst/si-dotnet-packages/issues/131)) ([12d946b](https://github.com/sondresjolyst/si-dotnet-packages/commit/12d946b49cfb8c80001722dce4f8508f50d0a472))

## [1.1.0](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Auth-v1.0.1...SjolystInnovation.AppKit.Auth-v1.1.0) (2026-10-20)


### Features

* **email:** add a branded email layout with the company details and render the auth emails through it ([#109](https://github.com/sondresjolyst/si-dotnet-packages/issues/109)) ([c210c2b](https://github.com/sondresjolyst/si-dotnet-packages/commit/c210c2bde09790aea661d99269aacf3875bda70d)), closes [#104](https://github.com/sondresjolyst/si-dotnet-packages/issues/104)

## [1.0.1](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Auth-v1.0.0...SjolystInnovation.AppKit.Auth-v1.0.1) (2026-10-17)


### Bug Fixes

* **auth:** write MVA after the organization number in English emails too ([#73](https://github.com/sondresjolyst/si-dotnet-packages/issues/73)) ([a2ea028](https://github.com/sondresjolyst/si-dotnet-packages/commit/a2ea0283cb71db354b81d7e6f225f76079966502))

## [1.0.0](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Auth-v0.9.0...SjolystInnovation.AppKit.Auth-v1.0.0) (2026-10-17)


### ⚠ BREAKING CHANGES

* **auth:** add a stable code to every error of the auth, account and user admin endpoints ([#63](https://github.com/sondresjolyst/si-dotnet-packages/issues/63))
* **auth:** format the org number line of the auth emails in the email's language ([#62](https://github.com/sondresjolyst/si-dotnet-packages/issues/62))
* **auth:** make the refresh token service and the reset code helpers internal ([#61](https://github.com/sondresjolyst/si-dotnet-packages/issues/61))

### Features

* **auth:** add a stable code to every error of the auth, account and user admin endpoints ([#63](https://github.com/sondresjolyst/si-dotnet-packages/issues/63)) ([d7b2e8d](https://github.com/sondresjolyst/si-dotnet-packages/commit/d7b2e8d43a9ed1a526665b945accf1cd7eb49698))
* **auth:** add GET roles to MapAppKitUserAdmin ([#64](https://github.com/sondresjolyst/si-dotnet-packages/issues/64)) ([1ec7e51](https://github.com/sondresjolyst/si-dotnet-packages/commit/1ec7e51c46819e5ed5ae335dc8ab55c528e92bdc))
* **auth:** format the org number line of the auth emails in the email's language ([#62](https://github.com/sondresjolyst/si-dotnet-packages/issues/62)) ([6ce6cfb](https://github.com/sondresjolyst/si-dotnet-packages/commit/6ce6cfb9a7935236c2db2c747c1b90a365dbcdc4))


### Code Refactoring

* **auth:** make the refresh token service and the reset code helpers internal ([#61](https://github.com/sondresjolyst/si-dotnet-packages/issues/61)) ([a28d0c7](https://github.com/sondresjolyst/si-dotnet-packages/commit/a28d0c775d023a58a4386a602885a8b22c10497c))

## [0.9.0](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Auth-v0.8.0...SjolystInnovation.AppKit.Auth-v0.9.0) (2026-10-16)


### ⚠ BREAKING CHANGES

* **auth:** one admin role setting and aligned names for 1.0 ([#45](https://github.com/sondresjolyst/si-dotnet-packages/issues/45))

### Features

* **auth:** add claims helpers and SeedAppKitRolesAsync ([#36](https://github.com/sondresjolyst/si-dotnet-packages/issues/36)) ([f943397](https://github.com/sondresjolyst/si-dotnet-packages/commit/f9433970d82f3576444bcd6967cc046a8e0048d1))


### Code Refactoring

* **auth:** one admin role setting and aligned names for 1.0 ([#45](https://github.com/sondresjolyst/si-dotnet-packages/issues/45)) ([47115d8](https://github.com/sondresjolyst/si-dotnet-packages/commit/47115d850d9237fd68d0616eabdd2221be2d6933))

## [0.8.0](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Auth-v0.7.0...SjolystInnovation.AppKit.Auth-v0.8.0) (2026-10-14)


### Features

* **auth:** run IUserRolesChangedHandler after role changes and apply them at once ([#29](https://github.com/sondresjolyst/si-dotnet-packages/issues/29)) ([3594e6f](https://github.com/sondresjolyst/si-dotnet-packages/commit/3594e6f3a4753703125151bab3b8512158dbb211))

## [0.7.0](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Auth-v0.6.0...SjolystInnovation.AppKit.Auth-v0.7.0) (2026-10-14)


### Features

* **auth:** add shared reset, invite and verification emails with AddAppKitAuthEmails ([#27](https://github.com/sondresjolyst/si-dotnet-packages/issues/27)) ([ec740b2](https://github.com/sondresjolyst/si-dotnet-packages/commit/ec740b2592cd2924470baf252bb862ee87f14fc4))

## [0.6.0](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Auth-v0.5.0...SjolystInnovation.AppKit.Auth-v0.6.0) (2026-10-14)


### Features

* **auth:** add IAccountDeleter and MapAppKitAccount for profile, password, export and account deletion ([#25](https://github.com/sondresjolyst/si-dotnet-packages/issues/25)) ([9e5118e](https://github.com/sondresjolyst/si-dotnet-packages/commit/9e5118e24c58f435879154e11e946d573f2bf4c6))

## [0.5.0](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Auth-v0.4.0...SjolystInnovation.AppKit.Auth-v0.5.0) (2026-10-14)


### Features

* **auth:** add MapAppKitUserAdmin for users, roles and invites, and first-admin seeding ([#23](https://github.com/sondresjolyst/si-dotnet-packages/issues/23)) ([8d14943](https://github.com/sondresjolyst/si-dotnet-packages/commit/8d149436a828521cfcbc14f18aae32872f36bed6))

## [0.4.0](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Auth-v0.3.0...SjolystInnovation.AppKit.Auth-v0.4.0) (2026-10-14)


### Features

* **auth:** add MapAppKitAuthEndpoints for login, refresh, logout and password reset ([#21](https://github.com/sondresjolyst/si-dotnet-packages/issues/21)) ([d6a0149](https://github.com/sondresjolyst/si-dotnet-packages/commit/d6a0149b9692d4e8f149215ab2e76be91354c6f5))

## [0.3.0](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Auth-v0.2.0...SjolystInnovation.AppKit.Auth-v0.3.0) (2026-10-13)


### Features

* **auth:** add Identity defaults, JWT bearer validation with a user check, and refresh token cleanup ([#17](https://github.com/sondresjolyst/si-dotnet-packages/issues/17)) ([240b0a6](https://github.com/sondresjolyst/si-dotnet-packages/commit/240b0a6ba936daf130df11230581fdbfec76ec14))

## [0.2.0](https://github.com/sondresjolyst/si-dotnet-packages/compare/SjolystInnovation.AppKit.Auth-v0.1.0...SjolystInnovation.AppKit.Auth-v0.2.0) (2026-10-12)


### Features

* **auth:** add RevokeAsync and RevokeAllForUserAsync to end sessions ([#11](https://github.com/sondresjolyst/si-dotnet-packages/issues/11)) ([41e8dac](https://github.com/sondresjolyst/si-dotnet-packages/commit/41e8dac43aafc22e246e0021675c5f92f18d612c))

## 0.1.0 (2026-10-12)


### Features

* **auth:** add SjolystInnovation.AppKit.Auth for tokens and email codes ([#9](https://github.com/sondresjolyst/si-dotnet-packages/issues/9)) ([0d8f380](https://github.com/sondresjolyst/si-dotnet-packages/commit/0d8f3806f687f9a4a7224f8e3381a641d97f9553))

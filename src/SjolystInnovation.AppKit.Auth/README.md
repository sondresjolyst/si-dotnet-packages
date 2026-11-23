# SjolystInnovation.AppKit.Auth

This package provides sign-in for an ASP.NET Core Identity app that serves a separate frontend: JWT
access tokens and their validation, rotating refresh tokens with reuse detection, Identity defaults,
password reset and email verification codes, branded emails for those codes, an opt-in public
sign-up, user administration, and account deletion. Use it when your API signs users in with tokens instead of cookies. Your app maps
the package's endpoints, and can send the package's emails or keep its own.

```sh
dotnet add package SjolystInnovation.AppKit.Auth
```

The package targets .NET 11 (`net11.0`), so your app must target .NET 11 or later.

The package depends on `SjolystInnovation.AppKit.Email`, which sends the code emails through Brevo.

## Get started

This example maps the sign-in, account, and user admin endpoints and seeds the first admin.

```csharp
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SjolystInnovation.AppKit.Auth;
using SjolystInnovation.AppKit.Email;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddAppKitIdentity<User, ApplicationDbContext>();
builder.Services.AddAppKitAuth<ApplicationDbContext>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddAppKitJwtBearer<User>(isActive: u => !u.IsDeleted);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole("Admin"));

builder.Services.AddAppKitBrevo(builder.Configuration.GetSection("BrevoSettings"));
builder.Services.AddSingleton<IEmailBrand, FixedEmailBrand>();
builder.Services.AddAppKitAuthEmails<User>(o => o.SetPasswordUrl = "https://example.com/reset-password");
builder.Services.AddAppKitAccounts<User, ApplicationDbContext>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapAppKitAuth<User, ApplicationDbContext>("/api/auth", o => o.IsActive = u => !u.IsDeleted);
app.MapAppKitAccount<User>("/api/users");
app.MapAppKitUserAdmin<User, ApplicationDbContext>("/api/users", o =>
{
    o.Roles = ["Admin", "Member"];
    o.NewUser = u => new User { FirstName = u.FirstName, LastName = u.LastName };
});

await app.Services.SeedAppKitRolesAsync(["Admin", "Member"]);
await app.Services.SeedAppKitFirstAdminAsync<User>(app.Configuration,
    u => new User { FirstName = u.FirstName, LastName = u.LastName });

app.Run();

public class User : IdentityUser, IAppKitAccount, IPasswordResetUser
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? PasswordResetCodeHash { get; set; }
    public DateTime? PasswordResetCodeExpiration { get; set; }
    public int PasswordResetAttempts { get; set; }
}

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<User>(options), IAuthDbContext
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.AddAppKitRefreshTokens<User>();
    }
}

public sealed class FixedEmailBrand : IEmailBrand
{
    public Task<EmailBrand> GetBrandAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new EmailBrand("Example") { Email = "post@example.com" });
}
```

```json
"Jwt": {
  "Key": "a-signing-key-of-at-least-32-bytes",
  "Issuer": "https://api.example.com"
},
"Seed": {
  "AdminEmail": "admin@example.com",
  "AdminPassword": "Set-me-in-a-secret-1"
}
```

| Call | What it does |
| --- | --- |
| `AddAppKitIdentity<TUser, TContext>` | Registers ASP.NET Core Identity with the package's rules. |
| `AddAppKitAuth<TContext>` | Registers `IJwtTokenService`, the refresh token service behind the sign-in endpoints, and the refresh token cleanup. |
| `AddAppKitJwtBearer<TUser>` | Makes JWT bearer the default scheme and checks each token's user. |
| `AddAppKitRefreshTokens<TUser>` | Maps the `RefreshTokens` table in `OnModelCreating`. |
| `AddAppKitAuthEmails<TUser>`, `AddAppKitVerificationEmails<TUser>` | Registers the branded code emails. |
| `AddAppKitAccounts<TUser, TContext>` | Registers account deletion and the admin role. |
| `MapAppKitAuth<TUser, TContext>` | Maps sign-in, refresh, sign-out, and password reset. |
| `MapAppKitAccount<TUser>` | Maps the signed-in user's own account. |
| `MapAppKitUserAdmin<TUser, TContext>` | Maps user and role administration. |
| `SeedAppKitRolesAsync`, `SeedAppKitFirstAdminAsync<TUser>` | Creates the roles and the first admin at startup. |
| `AddAppKitSelfRegistration<TUser, TContext>` | Opts in to public sign-up, as the sign-up section describes. |
| `AddAppKitAccountConsents<TUser>` | Maps the consent tables and the unique email index in `OnModelCreating`. |
| `MapAppKitRegistration<TUser, TContext>` | Maps `register`, `verify-email`, `resend-verification`, and `accept-terms`. |

## Settings

The settings bind to `AppKitAuthOptions` from the configuration section you pass to `AddAppKitAuth`,
usually `Jwt`. If a setting cannot work, the app fails to start and the error names the section.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Key` | None | The HS256 signing key, at least 32 bytes as UTF-8. |
| `Issuer` | None | Written as both issuer and audience. |
| `AccessTokenLifetime` | `01:00:00` | How long an access token lasts. |
| `RefreshTokenLifetimeMonths` | 6 | How many calendar months a refresh token lasts. |
| `MaxLiveRefreshTokens` | 5 | How many refresh tokens one user may hold at once, across devices. |
| `ResetCodeCooldown` | `00:01:00` | How long after a reset code was issued before `request-password-reset` issues a new one. |

If the key is too short, a lifetime or the cap is not positive, or the cooldown is negative, the app fails to start.

`AddAppKitAuth` also runs a hosted service that deletes expired refresh tokens when the app starts
and once a day after. A revoked token is kept until it expires, so presenting it again is still
caught as a replay.

`AddAppKitRefreshTokens` maps the `RefreshTokens` table: `Id`, `Token` (the SHA-256 hash), `UserId`,
`Expires`, `Created`, and `Revoked`, with a foreign key to the user that cascades on delete and an
index on `UserId`.

## Identity

`AddAppKitIdentity<TUser, TContext>` registers ASP.NET Core Identity with `IdentityRole`, EF stores,
and the default token providers.

| Rule | Value |
| --- | --- |
| Lockout | 30 minutes after 5 failed sign-ins, new users included. |
| Password | At least 8 characters with a digit, a lowercase letter, and an uppercase letter. No symbol needed. |
| Email | Unique per user. |

To change any of them, pass a callback. It runs after the defaults.

## Bearer tokens

`AddAppKitJwtBearer<TUser>` makes JWT bearer the default scheme, also when Identity was registered
first, and validates the tokens `IJwtTokenService` writes: the signature, the issuer and audience,
and the lifetime with 30 seconds of clock skew. Call it after `AddAppKitAuth`.

The bearer check refuses a token when its user no longer exists or `isActive` returns false for the
user. The check also replaces the token's roles with the user's current roles. The check caches its
answer per user for `UserCheckCacheDuration`. A deleted account stops working, and a role taken away
stops counting, within that time instead of when the access token expires.

SignalR cannot send a header when it opens a WebSocket, so list the paths that may take the token as
`?access_token=`:

```csharp
builder.Services.AddAppKitJwtBearer<User>(u => !u.IsDeleted, o => o.QueryTokenPaths.Add("/hubs"));
```

Make sure that access logs for those paths do not write the query string.

| Option | Default | Meaning |
| --- | --- | --- |
| `QueryTokenPaths` | Empty | The paths where the token may come as `?access_token=`. Paths match by whole segments. |
| `UserCheckCacheDuration` | 1 minute | How long the result of the user check is cached per user. |

## Claims

`AppKitClaims` adds extension methods on `ClaimsPrincipal` for the checks your endpoints repeat. The
package's own endpoints use them too.

| Method | Returns |
| --- | --- |
| `GetUserId()` | The `ClaimTypes.NameIdentifier` claim, or the `sub` claim when the first is missing. Null when the user has neither. |
| `IsCaller(userId)` | Whether the user's ID equals `userId`, compared ordinally. False when the user has no ID. |
| `IsInAnyRole(roles)` | Whether the user holds at least one of `roles`. Role names are case sensitive. |

```csharp
app.MapGet("/orders/{userId}", (string userId, ClaimsPrincipal user) =>
    user.IsCaller(userId) || user.IsInAnyRole("Admin", "Support") ? Results.Ok() : Results.Forbid());
```

## Sign-in endpoints

`MapAppKitAuth<TUser, TContext>` maps the sign-in endpoints, so your app does not have to write them.
The reset email goes out through `IAuthEmails<TUser>`. For an implementation, see the emails section.

```csharp
builder.Services.AddAppKitAuthEmails<User>(o => o.SetPasswordUrl = "https://example.com/reset-password");

app.MapAppKitAuth<User, ApplicationDbContext>("/api/auth", o => o.IsActive = u => !u.IsDeleted);
```

All endpoints are anonymous `POST` requests under the prefix, which defaults to `/api/auth`:

| Route | Body | What it does |
| --- | --- | --- |
| `login` | `email`, `password` | Answers `200 { token, refreshToken }`. A wrong password counts toward the lockout. An app that opted in to sign-up adds `emailVerified` and `pendingConsents`. |
| `refresh-token` | `token`, `refreshToken` | Answers `200 { token, refreshToken }`. A used token ends every session. It adds the same two for an opted-in app. |
| `logout` | `refreshToken` | Answers `204`, whether or not the token was live. |
| `request-password-reset` | `email` | Answers `200`, whether or not the address has an account. Emails a code to one that does, unless the last code is newer than `ResetCodeCooldown` or has used up its attempts. |
| `reset-password` | `email`, `code`, `newPassword` | Answers `200`, and every session ends. Answers `429` after 5 wrong codes. In an opted-in app a right code also confirms the email. |

Refusals answer `{ message, code }` with `400`, `401`, or `429`. A missing or malformed field answers
a validation problem keyed by field, as does a password Identity rejects. Each error carries a stable
code, so the app can show its own text, as the error codes section describes. If `IsActive` refuses a
user, the endpoints treat the user like an unknown one.

The endpoints log user IDs, never email addresses.

| Option | Default | Meaning |
| --- | --- | --- |
| `IsActive` | Every user is active | A check for whether a user may sign in, refresh tokens, and reset their password. |

## Error codes

Every error of the auth, account, and user admin endpoints carries a stable code from
`AuthErrorCodes`, next to its English message. The app shows its own text for a code it knows, in its
own language, and the English message for a code it does not.

An error that belongs to no field answers a JSON body with the message and the code:

```json
{ "message": "Invalid email or password.", "code": "invalidCredentials" }
```

A field error answers a validation problem. The `codes` extension is keyed like `errors`, with one
code for each message at the same place. The `limits` extension holds the number for `tooLong`,
`tooShort`, and `passwordTooShort`:

```json
{
  "errors": { "NewPassword": ["Passwords must be at least 8 characters.", "Passwords must have at least one digit ('0'-'9')."] },
  "codes": { "NewPassword": ["passwordTooShort", "passwordRequiresDigit"] },
  "limits": { "NewPassword": 8 }
}
```

| Code | Endpoint | Status |
| --- | --- | --- |
| `invalidCredentials` | `login`. A wrong password, or an unknown or inactive user. | `401` |
| `lockedOut` | `login`, after 5 failed sign-ins. | `401` |
| `invalidToken` | `refresh-token`, when the access token cannot be read. | `400` |
| `userNotFound` | `refresh-token`, when the user is gone or inactive. | `401` |
| `invalidRefreshToken` | `refresh-token`, when the refresh token is unknown, used, or expired. | `401` |
| `invalidResetCode` | `reset-password`. A wrong or expired code, or an unknown or inactive user. | `400` |
| `tooManyResetAttempts` | `reset-password`, after 5 wrong codes. | `429` |
| `profileUpdateFailed` | `PUT {id}/profile`, when Identity cannot save. | `400` |
| `lastAdmin` | `DELETE {id}/account` and `DELETE {id}/roles/{role}`, for the last active admin. | `409` |
| `accountChangeConflict` | `DELETE {id}/account`, when another change collided. A retry may succeed. | `409` |
| `unknownRole` | `POST {id}/roles` and `DELETE {id}/roles/{role}`. On `invite` it is a field error on `Role`. | `400` |
| `ownAdminRole` | `DELETE {id}/roles/{role}`, for the caller's own admin role. | `400` |
| `roleAssignFailed` | `POST {id}/roles` and `invite`, when Identity cannot give the role. | `400` |
| `roleRemoveFailed` | `DELETE {id}/roles/{role}`, when Identity cannot take the role. | `400` |
| `emailTaken` | `invite`, for an email with an account. Also a field error on `Email` if Identity refuses a duplicate. | `409` |
| `termsChanged` | `register` and `accept-terms`, when the version of the terms or a further document is not the current one. For a further document, or a document named in `accept-terms`, the body also holds `document`. | `409` |
| `invalidCode` | `verify-email`, for a wrong or expired code. | `400` |
| `alreadyVerified` | `verify-email` and `resend-verification`, for a verified email. | `409` |
| `tooManyAttempts` | `verify-email` and `resend-verification`, after 5 wrong codes until the code expires. | `429` |
| `tooManyRequests` | `resend-verification`, inside the cooldown or over the owner cap. `register`, over the network cap. | `429` |
| `tryAgain` | `verify-email` and `reset-password`, when the result could not be saved. A retry may succeed. | `409` |
| `signUpRefused` | `register`, when the app's `ISignUpGuard` says no. | `400` |
| `emailNotVerified` | An endpoint with `RequireVerifiedEmail()`, for an unconfirmed email. | `403` |
| `termsAcceptanceRequired` | An endpoint with `RequireCurrentTerms()`, for a user with a pending document. | `403` |

| Field code | Fields |
| --- | --- |
| `required` | Any required field that is blank. |
| `invalidEmail` | `Email` on `login`, `request-password-reset`, `reset-password`, `invite`, and `register`, also over 200 characters. |
| `tooLong` | `FirstName` and `LastName`, over 50 characters. `UserName` on `register`, over `MaxUserNameLength`. On `register` also `Password`, `TermsVersion`, `Locale`, and `Documents.{document}`. On `accept-terms` also `Document`. The limit is in `limits`. |
| `tooShort` | `UserName` on `register`, under `MinUserNameLength`. The limit is in `limits`. |
| `passwordTooShort` | `NewPassword`. Identity's required length is in `limits`. |
| `passwordRequiresDigit`, `passwordRequiresLower`, `passwordRequiresUpper` | `NewPassword`. |
| `passwordRequiresNonAlphanumeric`, `passwordRequiresUniqueChars` | `NewPassword`, when the app's Identity options require them. |
| `weakPassword` | `NewPassword`, for any other password rule, such as one the app adds. |
| `wrongPassword` | `CurrentPassword` on `PUT {id}/password`. |
| `unknownRole` | `Role` on `invite`. |
| `emailTaken` | `Email` on `invite`, when Identity refuses a duplicate. |
| `ageNotConfirmed` | `AgeConfirmed` on `register`. |
| `termsNotAccepted` | `TermsAccepted` on `register`, and `Documents.{document}` for a further document the request left out. |
| `invalidUserName` | `UserName` on `register`, for an `@` or a character Identity does not allow. |
| `userNameTaken` | `UserName` on `register`. |
| `unknownDocument` | `Document` on `accept-terms`, for a name that is neither `TermsDocument` nor in `AdditionalDocuments`. Posting it again gives the same answer. |

`login` never says whether an email has an account, so it has no code for an unconfirmed email. A
`429` from the rate limiter in `SjolystInnovation.AppKit.Hosting` carries `tooManyRequests`.
Successful answers, such as `{ message }` after a reset, have no code. Show your own text for them.

## Emails

`AddAppKitAuthEmails<TUser>` registers an `IAuthEmails<TUser>` that sends the password reset and
invite codes. `AddAppKitVerificationEmails<TUser>` adds an `IVerificationEmails<TUser>` for the email
verification code. All three share one layout: a colored header with the company details, the code
in a large box, a button to the app's page, and the same link as text under it. With `VerifyInApp`,
the verification email has no button and no link. It tells the person to enter the code in the app.
`SetPasswordInApp` does the same for the reset and invite emails.

The emails go through `IEmailSender` and render with `EmailLayout`, both from
`SjolystInnovation.AppKit.Email`, so the app's own emails can share the look. The company details come
from an `IEmailBrand` that your app registers, so they can follow the app's own settings.

```csharp
builder.Services.AddAppKitBrevo(builder.Configuration.GetSection("BrevoSettings"));
builder.Services.AddScoped<IEmailBrand, EmailBrandFromSettings>();
builder.Services.AddAppKitAuthEmails<User>(o =>
{
    o.Language = EmailLanguage.Norwegian;
    o.HeaderColor = "#0f3d3a";
    o.AccentColor = "#00887a";
    o.SetPasswordUrl = "https://example.com/reset-password";
    o.VerifyEmailUrl = "https://example.com/profile";
});
builder.Services.AddAppKitVerificationEmails<User>();
```

```csharp
public sealed class EmailBrandFromSettings(ApplicationDbContext db) : IEmailBrand
{
    public async Task<EmailBrand> GetBrandAsync(CancellationToken cancellationToken)
    {
        var s = await db.AppSettings.SingleAsync(cancellationToken);
        return new EmailBrand(s.CompanyName)
        {
            LegalName = s.LegalName,
            OrgNumber = s.OrgNumber,
            VatRegistered = s.VatRegistered,
            Address = s.Address,
            Email = s.PublicEmail,
        };
    }
}
```

The header lists the legal name, the organization number line, the address, and the email, and leaves
out blank ones. The organization number line follows the rules in the
[Email package](../SjolystInnovation.AppKit.Email/README.md#branded-layout), including `MVA` for a VAT
registered business.

| Option | Default | Meaning |
| --- | --- | --- |
| `Language` | `English` | `English` or `Norwegian`. |
| `HeaderColor` | `#182232` | The header and the code, as `#rrggbb`. |
| `AccentColor` | `#0284c7` | The button and the code label, as `#rrggbb`. |
| `SetPasswordUrl` | None | Required, unless `SetPasswordInApp` is set. The page that takes a reset or invite code, as an absolute http or https address. |
| `SetPasswordInApp` | `false` | Whether the person types the reset or invite code into the app instead of opening a page. `SetPasswordUrl` must then be empty. |
| `VerifyEmailUrl` | None | Required with verification emails and the default sign-up emails, unless `VerifyInApp` is set. The page that takes a verification code, as an absolute http or https address. |
| `VerifyInApp` | `false` | Whether the person types the verification code into the app instead of opening a page. `VerifyEmailUrl` must then be empty. |

Text on the header and the button is white. If a color is too light for white text, with a WCAG
contrast below 3 to 1, that text turns near black, and so do the code and its label. That way a
yellow brand stays readable.

The links add `email` and `code` to the page's query string, so the page can fill them in. The
email reads the expiry from the code itself, so an invite says 7 days and a reset says 30 minutes.
The app checks the options at startup. `SetPasswordUrl` and `VerifyEmailUrl` must be absolute http or
https addresses, so a missing value, a relative path or another scheme such as `javascript:` or `myapp:`
fails the start. An https app link, such as an iOS universal link or an Android app link, passes and
opens the app where it is set up. With `SetPasswordInApp` or `VerifyInApp`, a page that is set for the
same emails fails the start, because the emails would leave it out. If the user has no issued code,
sending throws `InvalidOperationException`.

## User administration

`MapAppKitUserAdmin<TUser, TContext>` maps the admin endpoints for users and roles. Your user class
implements `IAppKitAccount` with `FirstName`, `LastName`, `CreatedAt`, `IsDeleted`, and `DeletedAt`.
The invite email goes out through `IAuthEmails<TUser>.SendInviteAsync`. The admin role comes from
`AppKitAccountOptions.AdminRole`, so call `AddAppKitAccounts` first, as the account deletion section
describes. `NewUser` gets an `AppKitNewUser` with `Email`, `FirstName`, and `LastName`.

```csharp
app.MapAppKitUserAdmin<User, ApplicationDbContext>("/api/users", o =>
{
    o.Roles = RoleNames.AllRoles;
    o.Policy = Policies.Admin;
    o.NewUser = u => new User { FirstName = u.FirstName, LastName = u.LastName };
});
```

The routes sit under the prefix, which defaults to `/api/users`:

| Route | Who | What it does |
| --- | --- | --- |
| `GET` | `Policy` | Answers the users by email with their roles. `?includeDeleted=true` adds deleted users. |
| `GET roles` | `Policy` | Answers `Roles` as a JSON array of names, in the order you set them, for example `["Default", "Admin"]`. |
| `POST {id}/roles` with `role` | `Policy` | Gives the role and answers `200`. A role outside `Roles` gives `400`, a missing or deleted user `404`. |
| `DELETE {id}/roles/{role}` | `Policy` | Takes the role away and answers `204`. Taking your own admin role gives `400`. Taking it from the last active admin gives `409`. |
| `POST invite` with `email`, `firstName`, `lastName`, `role` | `Policy` | Creates the account without a password, with a confirmed email, and emails a code that lasts `InviteLifetime`. The invitee sets a password with it on `reset-password`. An email that already has an account gives `409`. |

| Option | Default | Meaning |
| --- | --- | --- |
| `Roles` | Empty | The roles an admin may give. The endpoints refuse any other role. |
| `Policy` | `Admin` | The authorization policy every route requires. |
| `NewUser` | None | Required. Creates a user from an `AppKitNewUser`, for an invite. |
| `InviteLifetime` | 7 days | How long an invite code lasts. |

If `NewUser` is not set, or if the app did not call `AddAppKitAccounts`, mapping throws.

After an admin gives or takes away a role, the bearer check forgets the user's cached roles, so the
change applies at once on that instance. Then every `IUserRolesChangedHandler<TUser>` the app
registers runs, such as one that updates what the role grants. A handler that throws fails the
request, but the role change is already saved.

```csharp
builder.Services.AddScoped<IUserRolesChangedHandler<User>, SecurityRoleSync>();
```

`SeedAppKitFirstAdminAsync<TUser>` creates the first admin from `Seed:AdminEmail` and
`Seed:AdminPassword` while nobody holds the admin role, or gives an existing user with that email the
role. It does nothing until both are set. It reads the admin role from `AppKitAccountOptions.AdminRole`
and throws if the app did not call `AddAppKitAccounts`.

`SeedAppKitRolesAsync` creates each role that does not exist yet and logs the roles it creates. Roles
that exist stay as they are, so you can call it on every startup. Call it before
`SeedAppKitFirstAdminAsync`, which needs the admin role to exist.

## Account deletion

`AddAppKitAccounts<TUser, TContext>` registers `IAccountDeleter<TUser>` and `AppKitAccountOptions`.
`AppKitAccountOptions.AdminRole` is the one place that names the admin role. The account endpoints,
the user admin endpoints, the first admin seed, and the last admin check all read it. It defaults to
`Admin`.

```csharp
builder.Services.AddAppKitAccounts<User, ApplicationDbContext>(o => o.AdminRole = RoleNames.Admin);
builder.Services.AddScoped<IAccountDataEraser<User>, DeviceDataEraser>();
```

Deleting keeps the user row, so records that point at it, such as invoices kept for bookkeeping, keep
a valid user. In one transaction, `DeleteAsync` does the following:

1. It runs every `IAccountDataEraser<TUser>` the app registers.
2. It scrubs the user. Names, email, and username become placeholders. The phone, password, codes, and
   confirmed email are cleared. The account is locked for good, `IsDeleted` and `DeletedAt` are set,
   and the security stamp is rotated.
3. It deletes every refresh token of the user.

If a step fails, everything rolls back. Once committed, every `IAccountDeletedHandler<TUser>` runs,
such as one that closes the user's live connections, and the bearer check forgets the user so a token
in use stops at once on that instance. Other instances keep their cached answer until it expires,
within `UserCheckCacheDuration`, and no instance can issue a new token because the refresh tokens are
gone.

You cannot delete the last active admin. `DeleteAsync` throws `LastAdminException`. The deletion runs
in a serializable transaction with that check inside, so two admins deleting each other at once cannot
both succeed. The database aborts one, and `DeleteAsync` throws `AccountChangeConflictException`. A
retry may succeed. Load the user again before you retry, because a failed deletion makes the context
forget the entities it tracks.

## Account endpoints

`MapAppKitAccount<TUser>` maps the signed-in user's own account under the prefix, which defaults to
`/api/users`. Every route requires a signed-in user. The user or an admin may act, except that only
the user changes their own password.

| Route | Who | What it does |
| --- | --- | --- |
| `GET {id}/profile` | The user or an admin | Answers `{ id, userName, firstName, lastName, email, createdAt }`. |
| `PUT {id}/profile` with `firstName`, `lastName` | The user or an admin | Saves and answers the updated profile. Names are required, at most 50 characters. |
| `PUT {id}/password` with `currentPassword`, `newPassword` | The user | Changes the password, ends every session of the user, this device's included, and answers `200`. Every device signs in again. A wrong current password is a validation problem on `CurrentPassword`, and a weak new one on `NewPassword`. Neither ends a session. |
| `GET {id}/export` | The user or an admin | Answers `{ account, roles, exportedAt }`, plus `data` from an `IAccountDataExporter<TUser>` when the app registers one. |
| `DELETE {id}/account` | The user or an admin | Deletes the account through `IAccountDeleter<TUser>` and answers `204`. The last admin, or a change that collided with it, gives `409`. |

A missing or deleted account gives `404`, and someone else's account gives `403`.

## Refresh tokens

The sign-in endpoints issue, rotate, and revoke the refresh tokens. The service behind them is
internal, so the rules below hold for every app.

- Signing in deletes the oldest live tokens over `MaxLiveRefreshTokens`. Deleting rather than revoking
  keeps an evicted device from looking like a replay.
- A refresh swaps the token for a new one in one transaction. A token that was already used revokes
  every live token of the user, so a stolen token stops working for everyone holding one.
- Signing out deletes the token while it is live, so a stale tab that sends it later gets a plain
  refusal instead of setting off replay detection. A token that was already used stays, so presenting
  it still revokes every session of the user.
- A password reset or a password change deletes every live token of the user, so each device signs in again.

`IJwtTokenService` stays public. `Build(user, roles)` writes an access token for a user and roles,
and `ReadUserIdFromExpired(token)` reads the user ID from an access token that may have expired.

## Password reset codes

Your user class implements `IPasswordResetUser` with `PasswordResetCodeHash`,
`PasswordResetCodeExpiration`, and `PasswordResetAttempts`. The `request-password-reset` and
`reset-password` endpoints and the invite use them. A reset code lasts 30 minutes, and a correct code
also clears the lockout. `request-password-reset` issues a new code only when the last one is at least
`ResetCodeCooldown` old. After 5 wrong codes it issues none until that code expires, so a code allows
5 guesses in all. A code that has used up its attempts expires after 15 minutes at the latest, so a new one can be asked for then. The answer is the same in every case. An invite code, which can last longer than 30
minutes, does not delay a new code.

## Email verification codes

To add email verification, give the user three columns by implementing `IEmailVerificationUser`:
`EmailVerificationCodeHash`, `EmailVerificationCodeExpiration`, and `EmailVerificationAttempts`. Add a
migration for them. Then send a code at registration and from a resend endpoint, with your own email
text or `IVerificationEmails<TUser>`, and check it from a verify endpoint. These helpers stay public
because the package maps no such endpoints unless the app opts in to sign-up.

```csharp
var code = EmailVerificationCodes.Issue(user);    // valid for 1 hour
await users.UpdateAsync(user);
// email the code

var check = EmailVerificationCodes.Check(user, dto.Code);
if (check == CodeCheck.Valid) EmailVerificationCodes.Complete(user);
await users.UpdateAsync(user);
```

`Check` gives `AlreadyVerified` for a confirmed email. Verification and reset codes both stop with
`TooManyAttempts` after 5 wrong codes, until a new code is issued. The helpers change the user object
only, so save it after each call.

## Sign-up

An app can opt in to public sign-up. It adds `register`, `verify-email`, `resend-verification`, and
`accept-terms`. A person signs up with an email and a password, accepts the terms and any further documents, confirms an age, and
verifies the email with a code. The app removes accounts that never verify. An app that does not call
`AddAppKitSelfRegistration` is unchanged. It gets no route, no consent table read, no new claim, and
the same `login` answer as before.

### Set up

1. Implement the interfaces. `IEmailVerificationUser` holds the code columns and
   `IRemovalReminderUser` holds the time of the last removal reminder. Without the second, no
   reminder is sent. The `DbContext` implements `IAccountConsentDbContext`.

   ```csharp
   public class User : IdentityUser, IAppKitAccount, IPasswordResetUser, IEmailVerificationUser, IRemovalReminderUser
   {
       // The members from Get started, and:
       public string? EmailVerificationCodeHash { get; set; }
       public DateTime? EmailVerificationCodeExpiration { get; set; }
       public int EmailVerificationAttempts { get; set; }
       public DateTime? RemovalReminderSentAt { get; set; }
   }

   public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
       : IdentityDbContext<User>(options), IAuthDbContext, IAccountConsentDbContext
   {
       // RefreshTokens from Get started, and:
       public DbSet<AccountConsent> AccountConsents => Set<AccountConsent>();
       public DbSet<ConsentRequirement> ConsentRequirements => Set<ConsentRequirement>();
       public DbSet<SignUpMailLog> SignUpMailLogs => Set<SignUpMailLog>();
   }
   ```

2. Opt in. The app needs `AddAppKitAuth` with a signing key, an `IEmailBrand`, and an `IEmailSender`.
   The default emails also need `VerifyEmailUrl`, or `VerifyInApp` as
   [Codes entered in the app](#codes-entered-in-the-app) shows. The sign-up does not need `AddAppKitAuthEmails`,
   which only the reset and invite emails need. Without a `SetPasswordUrl`, or with `SetPasswordInApp`,
   the notice to an existing account names the reset step in words. To send your own emails, register
   `IRegistrationEmails<User>` before this call. The app then keeps any `VerifyEmailUrl` and `VerifyInApp`. If an option cannot
   work, the app fails to start.

   ```csharp
   builder.Services.AddAppKitSelfRegistration<User, ApplicationDbContext>(o =>
   {
       o.NewUser = r => new User { FirstName = r.FirstName, LastName = r.LastName };
       o.Role = "Member";
       o.RequireNames = true;
   });
   ```

3. Map the consent tables and the unique email index in `OnModelCreating`.

   ```csharp
   protected override void OnModelCreating(ModelBuilder builder)
   {
       base.OnModelCreating(builder);
       builder.AddAppKitRefreshTokens<User>();
       builder.AddAppKitAccountConsents<User>();
   }
   ```

4. Map the routes. The prefix defaults to `/api/auth`. Mapping throws if step 2 is missing.

   ```csharp
   app.MapAppKitRegistration<User, ApplicationDbContext>("/api/auth");
   ```

5. Add a migration. It creates `AccountConsents`, `ConsentRequirements`, and `SignUpMailLogs`, makes the
   index on `NormalizedEmail` unique, and adds the user columns from step 1. Resolve any two accounts
   with one email first, or the migration fails. At startup the package checks that the model has the
   unique index, but it reads the model and cannot see the database. It does not notice a migration you
   did not apply.

   ```sh
   dotnet ef migrations add SignUp
   ```

| Option | Default | Meaning |
| --- | --- | --- |
| `NewUser` | None | Required. Creates a user from an `AppKitRegistration` with `Email`, `UserName`, `FirstName`, `LastName`, and `Locale`. |
| `Role` | None | The role a new user gets. |
| `MinimumAge` | 16 | The age a person confirms to be at least. Must be positive. |
| `TermsDocument` | `terms` | The document name of the terms in the consent rows. 1 to 32 ASCII letters, digits, hyphens, or underscores, so it is never blank and has no spaces. |
| `AdditionalDocuments` | None | Further documents a person accepts at sign-up, such as `privacy`. Each name is 1 to 32 ASCII letters, digits, hyphens, or underscores, so it is never blank and has no spaces. Each name is unique, and neither `TermsDocument` nor `age`. |
| `RequireNames` | `false` | Whether `firstName` and `lastName` are required, at most 50 characters each. |
| `AllowUserName` | `false` | Whether the person may choose a user name. Without one the account gets a generated one. |
| `RequireUserName` | `false` | Whether every sign-up must send a user name. A missing or blank one is `required` on `UserName`. It also turns on what `AllowUserName` does, so that need not be set. |
| `MinUserNameLength` | 1 | The fewest characters of a user name, after trimming. Shorter is `tooShort`. At least 1 and at most `MaxUserNameLength`. |
| `MaxUserNameLength` | 50, or the narrower of the `UserName` and `NormalizedUserName` columns when that is under 50 | The most characters of a user name, after trimming. Longer is `tooLong`. When the sign-up takes a user name, the app fails to start if a value it sets is longer than either column, 256 with the Identity default. The check reads `HasMaxLength`, not a `HasColumnType` override, so keep `HasMaxLength` in step with the column type. |
| `RecordTruncatedIp` | `false` | Whether each consent row keeps the caller's address cut to its first three parts for IPv4 and 48 bits for IPv6. |
| `ResendCooldown` | 1 minute | The wait before a new verification code, and between notices to one existing mailbox. |
| `MaxEmailsPerAddressPerDay` | 5 | The most emails per mailbox and UTC day that anyone who types the address can cause. |
| `MaxOwnerEmailsPerAddressPerDay` | 5 | The most emails per account and UTC day that the owner causes. |
| `MaxSignUpsPerNetworkPerDay` | 20 | The most `register` requests per network and UTC day. |
| `UnverifiedLifetime` | 30 days | How long an account may stay unverified. |
| `RunCleanupInBackground` | `true` | Whether a hosted service runs the cleanup at startup and once a day. |
| `RemindBefore` | 14 days, 3 days | How long before removal each reminder goes out. Each must be shorter than `UnverifiedLifetime`. An empty list sends none. |

The `IRegistrationHandler<User>` services run inside the sign-up transaction, so an exception rolls the
sign-up back. The `IEmailVerifiedHandler<User>` services run after an email is verified, also by a
password reset. The default emails are written in Norwegian for the locales `nb`, `no` and `nn`, and in
English for `en`, whatever the region part. Any other locale, or none, gets the `Language` of
`AppKitAuthEmailOptions`. The verification email at `register` uses the `locale` of the request. The
notice to an existing account, the removal reminder and `resend-verification` use the locale of the
latest consent row of the user.

### Routes

| Route | Who | What it does |
| --- | --- | --- |
| `POST register` with `email`, `password`, `ageConfirmed`, `termsAccepted`, `termsVersion`, and optional `userName`, `firstName`, `lastName`, `locale`, `documents`. `userName` is required with `RequireUserName`. | Anyone | Answers `200 { message }` for a new and an existing address alike. A new address gets an unverified account and a code by email. An existing active account gets a notice instead, at most once per `ResendCooldown`. A field error is a validation problem, a version that is not current gives `409`, a refusal from `ISignUpGuard` gives `400`, and a network over its cap gives `429`. |
| `POST verify-email` with `code` | Signed-in user | Confirms the email, ends every session, and answers `200 { message }`. A wrong or expired code gives `400`, a verified account `409`, and 5 wrong codes `429`. |
| `POST resend-verification` | Signed-in user | Sends a new code and answers `200 { message }`. Inside the cooldown, or over the owner cap, it gives `429`. While 5 wrong codes lock the current one, it gives `429` until that code has expired. A verified account gives `409`. |
| `POST accept-terms` with `termsVersion`, optional `locale`, optional `document` | Signed-in user | Records the acceptance of the terms, or of the document named in `document`, and answers `204`, whether the email is verified or not. A version that is not current gives `409`. |

Verify and resend need the signed-in account, because a code alone never verifies one. The code
reaches the owner of the address, and the password belongs to whoever signed up. If the code alone
verified, the owner of an address could verify an account that someone else holds the password to.
After verify the person signs in again, because the sessions ended.

Every check that needs no account runs in a fixed order before the lookup by email, so no refusal
depends on whether the address has an account. The email is at most 200 characters and the password at
most 128. The error codes section lists the codes.

### Codes entered in the app

An app with no web page, such as a mobile app, sets `VerifyInApp` and `SetPasswordInApp`, and leaves
`VerifyEmailUrl` and `SetPasswordUrl` empty. The verification email and the removal reminders then show
the code with no button and no link, and tell the person to enter the code in the app. The reset and
invite emails do the same, and the invite subject names the app. The notice to an existing account
tells the person to select that they forgot the password on the sign-in screen in the app. The app
sends a verification code to `verify-email` for the signed-in account, and a reset or invite code to
`reset-password` with the email and the new password. The example calls `AddAppKitAuthEmails`, because
that call covers the reset and invite emails. The sign-up alone needs only the options.

```csharp
builder.Services.AddAppKitAuthEmails<User>(o =>
{
    o.VerifyInApp = true;
    o.SetPasswordInApp = true;
});
builder.Services.AddAppKitSelfRegistration<User, ApplicationDbContext>(o =>
{
    o.NewUser = r => new User { FirstName = r.FirstName, LastName = r.LastName };
});
```

```ts
await fetch(`${api}/verify-email`, {
  method: "POST",
  headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
  body: JSON.stringify({ code }),
});

await fetch(`${api}/reset-password`, {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ email, code, newPassword }),
});
```

Each option covers its own emails, so an app can verify in the app and still reset on a web page.

To open the app from the email instead, set `VerifyEmailUrl` to an https app link. The package does not
take a custom scheme such as `myapp://verify`. Gmail and some other mail apps remove such links, and
the text link under the button would not open in a browser.

### User names

A user name comes only from the person, and only when `AllowUserName` or `RequireUserName` is on. It
is trimmed, is `MinUserNameLength` to `MaxUserNameLength` characters, 1 to 50 by default, never
contains `@`, and passes the allowed characters and the duplicate check of Identity. With
`RequireUserName` a sign-up without one is refused with `required`. In every other case the account
gets a generated one, never the email. Whatever user name `NewUser` sets is replaced. Sign-in is by
email, so a generated name is never shown or typed.

An app that shows the user name, for example in a menu, requires one:

```csharp
builder.Services.AddAppKitSelfRegistration<User, ApplicationDbContext>(o =>
{
    o.NewUser = r => new User();
    o.RequireUserName = true;
    o.MinUserNameLength = 3;
    o.MaxUserNameLength = 64;
});
```

Without a `MaxUserNameLength` from the app, the maximum is 50, or the narrower of the `UserName` and
`NormalizedUserName` columns in the EF model when that is under 50. A value the app sets is kept, and the
app fails to start when it is longer than either column, or when `MinUserNameLength` is longer than the
maximum that applies. The check reads the length that `HasMaxLength` sets. It does not read a
`HasColumnType` override such as `varchar(32)`, so an app that narrows a column that way also sets
`HasMaxLength` to the same length.

These rules cover the sign-up only. `invite` takes no user name from the request and uses the one that
`AppKitUserAdminOptions.NewUser` sets, or else the email. The first admin seed uses the one that the
`newUser` argument of `SeedAppKitFirstAdminAsync` sets, or else the email. `PUT {id}/profile` does not
change the user name.

### Terms and age

A sign-up stores two rows in `AccountConsents` with the source `register`. One is the terms, with
`TermsDocument` and the version. The other is the age, with the document `age` and `MinimumAge` as its
version. Each document in `AdditionalDocuments` adds one more row. A row holds the user, document, version, locale, time, source, and the shortened address when
`RecordTruncatedIp` is on. Rows are only added. `accept-terms` adds rows with the source `reaccept`.

An `IConsentVersionSource` tells the package which version of a document is current. The sign-up and
`accept-terms` accept exactly that version and answer `409 termsChanged` for any other. They ask the
source with the `locale` of the request and store it on the row, so a person who fetched the terms in a
language and sends that locale and version back is accepted. The list of documents to accept again in
the login and refresh answers asks with the locale of the user's latest acceptance of the document, or
with none when the user has none. Marking a requirement asks with no locale. With no source, or no version from it, the package stores `unversioned` and
ignores the version the client sent. The SiteSettings package gives the version of a legal page, so
the key of the page can be the document name:

```csharp
public sealed class LegalVersionSource(ApplicationDbContext db) : IConsentVersionSource
{
    private static readonly string[] Locales = ["no", "en"];

    public Task<string?> GetCurrentVersionAsync(string document, string? locale, CancellationToken cancellationToken) =>
        db.GetCurrentLegalPageVersionAsync(document, locale, Locales, cancellationToken);
}

builder.Services.AddScoped<IConsentVersionSource, LegalVersionSource>();
```

A request with no locale gets the version of the first language in `Locales`. The
[SiteSettings package](../SjolystInnovation.AppKit.SiteSettings/README.md#current-version) describes
how a version is made.

### More than one document

To have people accept a privacy policy next to the terms, list it in `AdditionalDocuments`:

```csharp
builder.Services.AddAppKitSelfRegistration<User, ApplicationDbContext>(o =>
{
    o.NewUser = r => new User();
    o.AdditionalDocuments = ["privacy"];
});
```

The terms stay in `termsAccepted` and `termsVersion`. The client sends each further document in
`documents`, with the version the person saw:

```json
{
  "email": "ada@example.com",
  "password": "Password1",
  "ageConfirmed": true,
  "termsAccepted": true,
  "termsVersion": "2026-11-01",
  "documents": { "privacy": "2026-10-15" },
  "locale": "en"
}
```

Each document is checked against its own current version from `IConsentVersionSource`, in the same way
as the terms, and gets its own row. A listed document that the request leaves out is a field error,
`termsNotAccepted` on `Documents.privacy`. A version that is not current answers `409 termsChanged`
with the document in the body:

```json
{ "message": "The document privacy has changed. Read the current version and accept it again.", "code": "termsChanged", "document": "privacy" }
```

The sign-up ignores names in `documents` that the app does not list, and the terms in `documents`.
An app without `AdditionalDocuments` and a client without `documents` work as before.

To accept a document again, send its name in `document` to `accept-terms`, with its version in
`termsVersion`:

```json
{ "document": "privacy", "termsVersion": "2026-12-01", "locale": "en" }
```

Without `document`, `accept-terms` accepts the terms and answers as before. A conflict about a named
document holds `document`. A name that is neither `TermsDocument` nor in `AdditionalDocuments` is a
field error, `unknownDocument` on `Document`, and records nothing.

### Asking people to accept again

When a document changes, publish the new text first, in every language the app serves. A person who
accepts in a language whose text was not updated still meets the requirement, since acceptances are
compared by time. Then call `RequireAcceptanceAsync` on an injected
`IConsentRequirements`, without a transaction around it. Calling it twice with the same document and
time changes nothing. The document must be `TermsDocument` or in `AdditionalDocuments`, because
`accept-terms` takes no other name. Any other name throws an `ArgumentException` and marks nothing.

```csharp
await requirements.RequireAcceptanceAsync("terms", DateTime.UtcNow.AddDays(14), cancellationToken);
```

A requirement binds from `effectiveFrom`. A user is pending for the document from then, unless the
user's latest acceptance of it, of any version, was made at or after the time of the call. An
acceptance before `effectiveFrom` still counts, so a person can accept ahead of the day. Accepting
always works, and a pending user always gets a new row. The version in the requirement is a record and
is never compared. Each document has its own requirements, so a new privacy policy makes people pending
for `privacy` only, and accepting it clears that one.

```csharp
await requirements.RequireAcceptanceAsync("privacy", DateTime.UtcNow.AddDays(14), cancellationToken);
```

For an opted-in app, `login` and `refresh-token` answer `{ token, refreshToken, emailVerified,
pendingConsents }`, where `pendingConsents` lists `document` and `version` for each pending document
on its own, in name order. `AppKitClaims.GetPendingConsents` gives the same list on the server.

### Filters

Two filters work on an endpoint or a group. Both let an anonymous request through to the normal
authorization answer, so combine them with `RequireAuthorization`. They throw on the first request if
the app did not opt in.

```csharp
app.MapGroup("/api/orders").RequireAuthorization().RequireVerifiedEmail().RequireCurrentTerms();
```

| Filter | Answer for a signed-in user it stops |
| --- | --- |
| `RequireVerifiedEmail()` | `403 emailNotVerified`, for an unconfirmed email. |
| `RequireCurrentTerms()` | `403 termsAcceptanceRequired`, for a user with a pending document. |

A user who is unverified or pending can still call every endpoint without a filter. That includes
`verify-email`, `resend-verification`, `accept-terms`, and the account endpoints for their own profile,
export, and deletion. Verifying and accepting take effect at once on the instance that served them.

### Email limits

Counts live in the database per UTC day, so they hold across instances and restarts.

- Anyone who types an address causes the notice and the verification email at `register`. Together
  they share `MaxEmailsPerAddressPerDay` for the mailbox.
- The owner causes `resend-verification` and the removal reminders. Together they share
  `MaxOwnerEmailsPerAddressPerDay` for the account, so people who are not signed in cannot use it up.
- No mailbox gets more than the two caps together, whatever the accounts.
- Over a cap, `register` sends nothing and answers the same. `resend-verification` answers
  `429 tooManyRequests` and the code of the last email stays valid. The new code is saved in the same transaction as the counts, so a refused or lost resend stores nothing.

Two addresses share a mailbox when they match after the mailbox key folds them. The key is a best
effort. It ignores case, drops a `+tag`, drops a trailing dot on the domain, writes an
internationalised domain in ASCII, counts `googlemail.com` as `gmail.com`, and removes dots in the
local part for Gmail. It applies no other provider rule.

Each `register` request that passes the field, guard, and terms checks counts once for its network,
whether or not the email has an account. The network is the caller's address cut to its first three
parts for IPv4 and 48 bits for IPv6. Over `MaxSignUpsPerNetworkPerDay` the answer is `429` and nothing
else happens. A caller with no known address is not counted. Behind a proxy, set up forwarded headers
as the Hosting package describes, or every caller shares the proxy's address. A mobile carrier or an
office can share one network, so an app may raise the cap.

To check a request before a sign-up goes on, register an `ISignUpGuard`. It runs after the field
checks and before any lookup by email. When it returns false, `register` answers `400 signUpRefused`.

```csharp
public sealed class CaptchaGuard(ICaptchaVerifier captcha) : ISignUpGuard
{
    public Task<bool> MaySignUpAsync(HttpContext http, string email) =>
        captcha.VerifyAsync(http.Request.Headers["X-Captcha-Token"].ToString(), http.RequestAborted);
}

builder.Services.AddScoped<ISignUpGuard, CaptchaGuard>();
```

`ICaptchaVerifier` is your own.

### Unverified accounts

`IUnverifiedAccountCleanup.RunOnceAsync` removes an account when it is active, its email is
unconfirmed, it holds a consent row with the source `register`, and it is older than
`UnverifiedLifetime`. An invited account never matches. Each account is removed in its own
transaction after every `IAccountDataEraser<User>` has run. The user row is deleted for real, with its
refresh tokens and consents, so the email is free again. An account with a live verification code
stays until the code expires, and at most one day past its removal time.

Before that, it sends the reminders of `RemindBefore` through `IRegistrationEmails<User>`, once each. A
run after a long stop sends the latest due one only. A reminder carries a fresh code and counts toward
the owner cap. Over the cap it waits for a later run, and the code of the last email stays valid. The fresh code is saved in the same transaction as the counts. The run also deletes the email counts from before
yesterday. One failing account is logged and does not stop the run.

A hosted service runs it at startup and once a day. To use your own scheduler, set
`RunCleanupInBackground` to `false` and call it. The result holds `Removed`, `Reminded`, `Failed`, and `Skipped`. A skipped account is one that another host removed or changed at the same moment, or one where an `IAccountDataEraser<User>` threw a concurrency exception. The account stays and the next run tries it again.

```csharp
var cleanup = app.Services.GetRequiredService<IUnverifiedAccountCleanup>();
var result = await cleanup.RunOnceAsync(cancellationToken);
```

### Password reset

Someone may sign up with an address that is not theirs. The owner of the address gets the notice
email, and the password reset is the way in. A right reset code sets the owner's password, and in an
opted-in app it also confirms the email and runs the `IEmailVerifiedHandler<User>` services. Every
session of the account ends, including the sessions of whoever signed up.

### Tokens

In an opted-in app an access token carries the claim `sst`, a short hash of the user's security stamp.
The bearer check refuses a token whose stamp differs from the user's. A password change, a password
reset, a verification, and an account deletion rotate the stamp, so they end the access tokens at once
on the instance that served them. A token without the claim, issued before the opt-in, stays valid
until it expires.

### Export and delete

`GET {id}/export` also answers `consents`, with `document`, `version`, `acceptedAt`, `source`, and
`locale` for each row. It never holds the shortened address. Deleting an account clears the address
and the locale of its consent rows and keeps the rows.

### Limits

- Instances must share a clock. The times of acceptances and requirements are compared.
- After a requirement takes effect at once, another instance may let a pending user through for up to
  70 seconds. That is 10 seconds of requirement cache plus `UserCheckCacheDuration`. Marking a
  requirement clears the cache of the instance that did it.
- After a verification or a password reset, another instance may accept an older token for up to
  `UserCheckCacheDuration`.
- Someone who types an address can use up that mailbox's sign-up emails for a day. The password reset
  still works.
- The mailbox key is a best effort. Other spellings of one inbox count apart.
- The counts are hashed with a key from the signing key. Changing the signing key resets them.
- A new and an existing address answer the same, but the response time still differs a little.

### Rate limits

Add rules for the four routes to the `RateLimiting` section of the Hosting package. The caps above
bound the emails, and these bound the requests.

```json
"RateLimiting": {
  "Rules": [
    { "Endpoint": "POST:/api/auth/register", "Period": "1m", "Limit": 5 },
    { "Endpoint": "POST:/api/auth/verify-email", "Period": "1m", "Limit": 10 },
    { "Endpoint": "POST:/api/auth/resend-verification", "Period": "1m", "Limit": 3 },
    { "Endpoint": "POST:/api/auth/accept-terms", "Period": "1m", "Limit": 20 }
  ]
}
```

## Links

- [Source code](https://github.com/sondresjolyst/si-dotnet-packages/tree/main/src/SjolystInnovation.AppKit.Auth)
- [Changelog](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/src/SjolystInnovation.AppKit.Auth/CHANGELOG.md)
- [All AppKit packages](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/README.md)

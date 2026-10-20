# SjolystInnovation.AppKit.Auth

This package provides sign-in for an ASP.NET Core Identity app that serves a separate frontend: JWT
access tokens and their validation, rotating refresh tokens with reuse detection, Identity defaults,
password reset and email verification codes, branded emails for those codes, user administration, and
account deletion. Use it when your API signs users in with tokens instead of cookies. Your app maps
the package's endpoints, and can send the package's emails or keep its own.

```sh
dotnet add package SjolystInnovation.AppKit.Auth
```

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

If the key is too short, or a lifetime or the cap is not positive, the app fails to start.

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
| `login` | `email`, `password` | Answers `200 { token, refreshToken }`. A wrong password counts toward the lockout. |
| `refresh-token` | `token`, `refreshToken` | Answers `200 { token, refreshToken }`. A used token ends every session. |
| `logout` | `refreshToken` | Answers `204`, whether or not the token was live. |
| `request-password-reset` | `email` | Answers `200`, whether or not the address has an account. Emails a code to one that does. |
| `reset-password` | `email`, `code`, `newPassword` | Answers `200`, and every session ends. Answers `429` after 5 wrong codes. |

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
code for each message at the same place. The `limits` extension holds the number for `tooLong` and
`passwordTooShort`:

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

| Field code | Fields |
| --- | --- |
| `required` | Any required field that is blank. |
| `invalidEmail` | `Email` on `login`, `request-password-reset`, `reset-password`, and `invite`. |
| `tooLong` | `FirstName` and `LastName`, over 50 characters. The limit is in `limits`. |
| `passwordTooShort` | `NewPassword`. Identity's required length is in `limits`. |
| `passwordRequiresDigit`, `passwordRequiresLower`, `passwordRequiresUpper` | `NewPassword`. |
| `passwordRequiresNonAlphanumeric`, `passwordRequiresUniqueChars` | `NewPassword`, when the app's Identity options require them. |
| `weakPassword` | `NewPassword`, for any other password rule, such as one the app adds. |
| `wrongPassword` | `CurrentPassword` on `PUT {id}/password`. |
| `unknownRole` | `Role` on `invite`. |
| `emailTaken` | `Email` on `invite`, when Identity refuses a duplicate. |

`login` never says whether an email has an account, so it has no code for an unconfirmed email. A
`429` from the rate limiter in `SjolystInnovation.AppKit.Hosting` has no code. The status says it all.
Successful answers, such as `{ message }` after a reset, have no code. Show your own text for them.

## Emails

`AddAppKitAuthEmails<TUser>` registers an `IAuthEmails<TUser>` that sends the password reset and
invite codes. `AddAppKitVerificationEmails<TUser>` adds an `IVerificationEmails<TUser>` for the email
verification code. All three share one layout: a colored header with the company details, the code
in a large box, a button to the app's page, and the same link as text under it.

The emails go through `IEmailSender` and render with `EmailLayout`, both from
`SjolystInnovation.AppKit.Email`, so the app's own emails can share the look. The company details come
from an `IEmailBrand` that your app registers, so they can follow the app's own settings. `EmailBrand`
and `EmailLanguage` live in the `SjolystInnovation.AppKit.Email` namespace.

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
registered business and an optional register name.

| Option | Default | Meaning |
| --- | --- | --- |
| `Language` | `English` | `English` or `Norwegian`. |
| `HeaderColor` | `#182232` | The header and the code, as `#rrggbb`. |
| `AccentColor` | `#0284c7` | The button and the code label, as `#rrggbb`. |
| `SetPasswordUrl` | None | Required. The page that takes a reset or invite code. |
| `VerifyEmailUrl` | None | Required with verification emails. The page that takes a verification code. |

Text on the header and the button is white. If a color is too light for white text, with a WCAG
contrast below 3 to 1, that text turns near black, and so do the code and its label. That way a
yellow brand stays readable.

The links add `email` and `code` to the page's query string, so the page can fill them in. The
email reads the expiry from the code itself, so an invite says 7 days and a reset says 30 minutes.
The app checks the options at startup. If the user has no issued code, sending throws
`InvalidOperationException`.

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
retry may succeed.

## Account endpoints

`MapAppKitAccount<TUser>` maps the signed-in user's own account under the prefix, which defaults to
`/api/users`. Every route requires a signed-in user. The user or an admin may act, except that only
the user changes their own password.

| Route | Who | What it does |
| --- | --- | --- |
| `GET {id}/profile` | The user or an admin | Answers `{ id, userName, firstName, lastName, email, createdAt }`. |
| `PUT {id}/profile` with `firstName`, `lastName` | The user or an admin | Saves and answers the updated profile. Names are required, at most 50 characters. |
| `PUT {id}/password` with `currentPassword`, `newPassword` | The user | Changes the password and answers `200`. A wrong current password is a validation problem on `CurrentPassword`, and a weak new one on `NewPassword`. |
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
- A password reset deletes every live token of the user, so each device signs in again.

`IJwtTokenService` stays public. `Build(user, roles)` writes an access token for a user and roles,
and `ReadUserIdFromExpired(token)` reads the user ID from an access token that may have expired.

## Password reset codes

Your user class implements `IPasswordResetUser` with `PasswordResetCodeHash`,
`PasswordResetCodeExpiration`, and `PasswordResetAttempts`. The `request-password-reset` and
`reset-password` endpoints and the invite use them. A reset code lasts 30 minutes, and a correct code
also clears the lockout.

## Email verification codes

To add email verification, give the user three columns by implementing `IEmailVerificationUser`:
`EmailVerificationCodeHash`, `EmailVerificationCodeExpiration`, and `EmailVerificationAttempts`. Add a
migration for them. Then send a code at registration and from a resend endpoint, with your own email
text or `IVerificationEmails<TUser>`, and check it from a verify endpoint. These helpers stay public
because the package maps no registration or verify endpoint.

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

## Links

- [Source code](https://github.com/sondresjolyst/si-dotnet-packages/tree/main/src/SjolystInnovation.AppKit.Auth)
- [Changelog](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/src/SjolystInnovation.AppKit.Auth/CHANGELOG.md)
- [All AppKit packages](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/README.md)

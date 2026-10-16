# SjolystInnovation.AppKit.Auth

This package provides Identity defaults, access tokens and their validation, rotating refresh tokens,
and email codes for an ASP.NET Core Identity app. Your app can map the package's endpoints and send its
emails, or keep its own endpoints, messages, and emails and call these services from them.

```sh
dotnet add package SjolystInnovation.AppKit.Auth
```

## Setup

The settings bind to `AppKitAuthOptions` from the configuration section you pass to `AddAppKitAuth`,
usually `Jwt`. If a setting cannot work, the app fails to start and the error names the section.

| Setting | Default | |
| --- | --- | --- |
| `Key` | none | The HS256 signing key, at least 32 bytes. |
| `Issuer` | none | Written as both issuer and audience. |
| `AccessTokenLifetime` | `01:00:00` | |
| `RefreshTokenLifetimeMonths` | `6` | |
| `MaxLiveRefreshTokens` | `5` | Per user, across devices. |

If the key is too short, or a lifetime or the cap is not positive, the app fails to start.

```csharp
builder.Services.AddAppKitIdentity<User, ApplicationDbContext>();
builder.Services.AddAppKitAuth<ApplicationDbContext>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddAppKitJwtBearer<User>(isActive: u => !u.IsDeleted);
```

`AddAppKitAuth` also runs a hosted service that deletes expired refresh tokens when the app starts
and once a day after. A revoked token is kept until it expires, so presenting it again is still
caught as a replay.

Your `DbContext` holds the refresh tokens:

```csharp
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<User>(options), IAuthDbContext
{
    public DbSet<RefreshToken> RefreshTokens { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.AddAppKitRefreshTokens<User>();
    }
}
```

`AddAppKitRefreshTokens` maps the `RefreshTokens` table: `Id`, `Token` (the SHA-256 hash), `UserId`,
`Expires`, `Created` and `Revoked`, with a foreign key to the user that cascades on delete and an
index on `UserId`.

## Identity

`AddAppKitIdentity<TUser, TContext>` registers ASP.NET Core Identity with `IdentityRole`, EF stores, and
the default token providers.

| Rule | |
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
answer per user for a minute, so a deleted account stops working, and a role taken away stops counting, within a
minute instead of when the access token expires.

SignalR cannot send a header when it opens a WebSocket, so list the paths that may take the token as
`?access_token=`:

```csharp
builder.Services.AddAppKitJwtBearer<User>(u => !u.IsDeleted, o => o.QueryTokenPaths.Add("/hubs"));
```

Make sure that access logs for those paths do not write the query string.

## Claims

`AppKitClaims` adds extension methods on `ClaimsPrincipal` for the checks your endpoints repeat. The
package's own endpoints use them too.

| Method | Returns |
| --- | --- |
| `GetUserId()` | The `ClaimTypes.NameIdentifier` claim, or the `sub` claim when the first is missing. Null when the user has neither. |
| `IsCaller(userId)` | Whether the user's id equals `userId`, compared ordinally. False when the user has no id. |
| `IsInAnyRole(roles)` | Whether the user holds at least one of `roles`. Role names are case sensitive. |

```csharp
app.MapGet("/orders/{userId}", (string userId, ClaimsPrincipal user) =>
    user.IsCaller(userId) || user.IsInAnyRole("Admin", "Support") ? Results.Ok() : Results.Forbid());
```

## Endpoints

`MapAppKitAuth<TUser, TContext>` maps the sign-in endpoints, so your app does not have to
write them. The reset email goes out through `IAuthEmails<TUser>`. For an implementation, see
[Emails](#emails).

```csharp
builder.Services.AddAppKitAuthEmails<User>(o => o.SetPasswordUrl = "https://example.com/reset-password");

app.MapAppKitAuth<User, ApplicationDbContext>("/api/auth", o => o.IsActive = u => !u.IsDeleted);
```

All endpoints are anonymous `POST` requests under the prefix:

| Route | Body | Answer |
| --- | --- | --- |
| `login` | `email`, `password` | `200 { token, refreshToken }`. A wrong password counts toward the lockout. |
| `refresh-token` | `token`, `refreshToken` | `200 { token, refreshToken }`. A used token ends every session. |
| `logout` | `refreshToken` | `204`, whether or not the token was live. |
| `request-password-reset` | `email` | `200`, whether or not the address has an account. Emails a code to one that does. |
| `reset-password` | `email`, `code`, `newPassword` | `200`, and every session ends. `429` after 5 wrong codes. |

Refusals answer `{ message }` with `400` or `401`. A missing or malformed field answers a validation
problem keyed by field, as does a password Identity rejects. If `IsActive` refuses a user, the
endpoints treat the user like an unknown one. You can translate every message in `o.Messages`.

The endpoints log user IDs, never email addresses.

## Emails

`AddAppKitAuthEmails<TUser>` registers an `IAuthEmails<TUser>` that sends the password reset and
invite codes. `AddAppKitVerificationEmails<TUser>` adds an `IVerificationEmails<TUser>` for the email
verification code. All three share one layout: a colored header with the company details, the code
in a large box, a button to the app's page and the same link as text under it.

The emails go through `IEmailSender` from `SjolystInnovation.AppKit.Email`. The company details come
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
        return new EmailBrand(s.CompanyName) { LegalName = s.LegalName, OrgNumber = $"Org.nr. {s.OrgNumber}", Address = s.Address, Email = s.PublicEmail };
    }
}
```

| Option | Default | |
| --- | --- | --- |
| `Language` | `English` | `English` or `Norwegian`. |
| `HeaderColor` | `#182232` | The header and the code, as `#rrggbb`. |
| `AccentColor` | `#0284c7` | The button and the code label, as `#rrggbb`. |
| `SetPasswordUrl` | | Required. The page that takes a reset or invite code. |
| `VerifyEmailUrl` | | Required with verification emails. The page that takes a verification code. |

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
`AppKitAccountOptions.AdminRole`, so call `AddAppKitAccounts` first. For details, see
[Account deletion](#account-deletion). `NewUser` gets an `AppKitNewUser` with `Email`, `FirstName`, and
`LastName`.

```csharp
app.MapAppKitUserAdmin<User, ApplicationDbContext>("/api/users", o =>
{
    o.Roles = RoleNames.AllRoles;
    o.Policy = Policies.Admin;
    o.NewUser = u => new User { FirstName = u.FirstName, LastName = u.LastName };
});
```

| Route | Answer |
| --- | --- |
| `GET` | Users by email with their roles. `?includeDeleted=true` adds deleted users. |
| `POST {id}/roles` with `role` | `200`. A role outside `Roles` gives `400`, a missing or deleted user `404`. |
| `DELETE {id}/roles/{role}` | `204`. Taking your own admin role gives `400`. Taking it from the last active admin gives `409`. |
| `POST invite` with `email`, `firstName`, `lastName`, `role` | `200`. Creates the account without a password, with a confirmed email, and emails a code that lasts `InviteLifetime`, seven days by default. The invitee sets a password with it on `reset-password`. An email that already has an account gives `409`. |

Every route requires `Policy`. If `NewUser` is not set, or if the app did not call `AddAppKitAccounts`, mapping throws.

After an admin gives or takes away a role, the bearer check forgets the user's cached roles, so the change
applies at once on that instance, and every `IUserRolesChangedHandler<TUser>` the app registers runs,
such as one that updates what the role grants. A handler that throws fails the request, but the role
change is already saved.

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

```csharp
await app.Services.SeedAppKitRolesAsync(RoleNames.AllRoles);
await app.Services.SeedAppKitFirstAdminAsync<User>(app.Configuration, u => new User { FirstName = u.FirstName, LastName = u.LastName });
```

## Account deletion

`AddAppKitAccounts<TUser, TContext>` registers `IAccountDeleter<TUser>` and `AppKitAccountOptions`.
`AppKitAccountOptions.AdminRole` is the one place that names the admin role. The account endpoints, the
user admin endpoints, the first admin seed, and the last admin check all read it. It defaults to `Admin`. Deleting keeps the user row, so
records that point at it, such as invoices kept for bookkeeping, keep a valid user. In one transaction
it runs every `IAccountDataEraser<TUser>` the app registers, then scrubs the user: names, email, and
username become placeholders, the phone, password, codes, and confirmed email are cleared, the account
is locked for good, `IsDeleted` and `DeletedAt` are set, and the security stamp is rotated. Every
refresh token is deleted. If a step fails, everything rolls back. Once committed, every
`IAccountDeletedHandler<TUser>` runs, such as one that closes the user's live connections, and the
bearer check forgets the user so a token in use stops at once on that instance. Other instances keep
their cached answer until it expires, within `UserCheckCacheDuration`, and no instance can issue a new
token because the refresh tokens are gone.

```csharp
builder.Services.AddAppKitAccounts<User, ApplicationDbContext>(o => o.AdminRole = RoleNames.Admin);
builder.Services.AddScoped<IAccountDataEraser<User>, DeviceDataEraser>();
```

You cannot delete the last active admin. `DeleteAsync` throws `LastAdminException`. The deletion runs
in a serializable transaction with that check inside, so two admins deleting each other at once cannot
both succeed. The database aborts one, and `DeleteAsync` throws `AccountChangeConflictException`. A
retry may succeed.

## Account endpoints

`MapAppKitAccount<TUser>` maps the signed-in user's own account under the prefix. The user or an admin
may act, except that only the user changes their own password.

| Route | Answer |
| --- | --- |
| `GET {id}/profile` | `{ id, userName, firstName, lastName, email, createdAt }` |
| `PUT {id}/profile` with `firstName`, `lastName` | The updated profile. Names are required, at most 50 characters. |
| `PUT {id}/password` with `currentPassword`, `newPassword` | `200`. A wrong current password or a weak new one is a validation problem on `NewPassword`. |
| `GET {id}/export` | `{ account, roles, exportedAt }`, plus `data` from an `IAccountDataExporter<TUser>` when the app registers one. |
| `DELETE {id}/account` | `204` through `IAccountDeleter`. The last admin, or a change that collided with it, gives `409`. |

A missing or deleted account gives `404`, and someone else's account gives `403`.

## Signing in

```csharp
var roles = await users.GetRolesAsync(user);
var token = jwt.Build(user, roles);
var refreshToken = await refreshTokens.IssueAsync(user.Id);
await db.SaveChangesAsync();
```

`IssueAsync` deletes the oldest live tokens over the cap. Deleting rather than revoking keeps an
evicted device from looking like a replay.

## Refreshing

```csharp
var userId = jwt.ReadUserIdFromExpired(request.Token);
if (userId == null) return BadRequest();
var user = await users.FindByIdAsync(userId);
if (user == null) return Unauthorized();

var result = await refreshTokens.RotateAsync(user.Id, request.RefreshToken);
if (result.Status != RotationStatus.Rotated) return Unauthorized();
return Ok(new { token = jwt.Build(user, await users.GetRolesAsync(user)), refreshToken = result.RefreshToken });
```

`RotateAsync` saves and commits on its own. A token that was already used gives `Replayed` and
revokes every live token of the user, so a stolen token stops working for everyone holding one.

## Signing out

```csharp
await refreshTokens.RevokeAsync(request.RefreshToken);
return NoContent();
```

`RevokeAsync` deletes the token while it is live, so a stale tab that sends it later gets a plain
refusal instead of setting off replay detection. A token that was already used stays, so presenting
it still revokes every session of the user.

## Password reset

Your user class implements `IPasswordResetUser` with `PasswordResetCodeHash`,
`PasswordResetCodeExpiration`, and `PasswordResetAttempts`.

```csharp
var code = PasswordResetCodes.Issue(user);        // valid for 30 minutes
await users.UpdateAsync(user);
// email the code

switch (PasswordResetCodes.Check(user, dto.Code))
{
    case CodeCheck.TooManyAttempts: return TooManyRequests();
    case CodeCheck.Invalid: await users.UpdateAsync(user); return BadRequest();
}
// reset the password with UserManager, then
PasswordResetCodes.Complete(user);                // also clears the lockout
await users.UpdateAsync(user);
await refreshTokens.RevokeAllForUserAsync(user.Id); // every device signs in again
```

## Email verification

To add email verification, give the user three columns by implementing `IEmailVerificationUser`:
`EmailVerificationCodeHash`, `EmailVerificationCodeExpiration`, and `EmailVerificationAttempts`. Add a
migration for them. Then send a code at registration and from a resend endpoint, with your own email
text or `IVerificationEmails<TUser>`, and check it from a verify endpoint.

```csharp
var code = EmailVerificationCodes.Issue(user);    // valid for 1 hour
await users.UpdateAsync(user);
// email the code

var check = EmailVerificationCodes.Check(user, dto.Code);
if (check == CodeCheck.Valid) EmailVerificationCodes.Complete(user);
await users.UpdateAsync(user);
```

`Check` gives `AlreadyVerified` for a confirmed email. Both code types stop with `TooManyAttempts`
after 5 wrong codes, until a new code is issued. The helpers change the user object only, so save
it after each call.

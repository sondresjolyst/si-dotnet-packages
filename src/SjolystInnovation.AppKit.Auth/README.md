# SjolystInnovation.AppKit.Auth

Identity defaults, access tokens and their validation, rotating refresh tokens and email codes for an
ASP.NET Core Identity app. The app keeps its own endpoints, messages and emails, and calls these
services from them.

```sh
dotnet add package SjolystInnovation.AppKit.Auth
```

## Setup

The settings come from the `Jwt` configuration section.

| Setting | Default | |
| --- | --- | --- |
| `Key` | none | HS256 signing key, at least 32 bytes. |
| `Issuer` | none | Written as both issuer and audience. |
| `AccessTokenLifetime` | `01:00:00` | |
| `RefreshTokenLifetimeMonths` | `6` | |
| `MaxLiveRefreshTokens` | `5` | Per user, across devices. |

The app fails to start when the key is too short or a lifetime or the cap is not positive.

```csharp
builder.Services.AddAppKitIdentity<User, ApplicationDbContext>();
builder.Services.AddAppKitAuth<ApplicationDbContext>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddAppKitJwtBearer<User>(isActive: u => !u.IsDeleted);
```

`AddAppKitAuth` also runs a hosted service that deletes expired refresh tokens when the app starts
and once a day after. A revoked token is kept until it expires, so presenting it again is still
caught as a replay.

The DbContext holds the refresh tokens:

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

`AddAppKitIdentity<TUser, TContext>` registers ASP.NET Core Identity with `IdentityRole`, EF stores and
the default token providers.

| Rule | |
| --- | --- |
| Lockout | 30 minutes after 5 failed sign-ins, new users included. |
| Password | At least 8 characters with a digit, a lower case and an upper case letter. No symbol needed. |
| Email | Unique per user. |

Pass a callback to change any of them. It runs after the defaults.

## Bearer tokens

`AddAppKitJwtBearer<TUser>` makes JWT bearer the default scheme, also when Identity was registered
first, and validates the tokens `IJwtTokenService` writes: the signature, the issuer and audience,
and the lifetime with 30 seconds of clock skew. Call it after `AddAppKitAuth`.

A token is refused when its user no longer exists or `isActive` returns false for the user. The
check also replaces the token's roles with the user's current roles. The answer is cached per user
for a minute, so a deleted account stops working, and a role taken away stops counting, within a
minute instead of when the access token expires.

SignalR cannot send a header when it opens a WebSocket, so list the paths that may take the token as
`?access_token=`:

```csharp
builder.Services.AddAppKitJwtBearer<User>(u => !u.IsDeleted, o => o.QueryTokenPaths.Add("/hubs"));
```

Keep access logs for those paths from writing the query string.

## Endpoints

`MapAppKitAuthEndpoints<TUser, TContext>` maps the sign-in endpoints, so the app does not write them.
The app sends the reset email with its own template through `IAuthEmails<TUser>`.

```csharp
builder.Services.AddScoped<IAuthEmails<User>, AuthEmails>();

app.MapAppKitAuthEndpoints<User, ApplicationDbContext>("/api/auth", o => o.IsActive = u => !u.IsDeleted);
```

All are anonymous POSTs under the prefix:

| Route | Body | Answer |
| --- | --- | --- |
| `login` | `email`, `password` | `200 { token, refreshToken }`. A wrong password counts towards the lockout. |
| `refresh-token` | `token`, `refreshToken` | `200 { token, refreshToken }`. A used token ends every session. |
| `logout` | `refreshToken` | `204`, whether or not the token was live. |
| `request-password-reset` | `email` | `200`, whether or not the address has an account. Emails a code to one that does. |
| `reset-password` | `email`, `code`, `newPassword` | `200`, and every session ends. `429` after 5 wrong codes. |

Refusals answer `{ message }` with `400` or `401`. A missing or malformed field answers a validation
problem keyed by field, as does a password Identity rejects. A user `IsActive` refuses is treated
like an unknown one. Every message is in `o.Messages` and can be translated.

The endpoints log user ids, never addresses.

## User administration

`MapAppKitUserAdmin<TUser, TContext>` maps the admin endpoints for users and roles. The user implements
`IAppKitAccount` (`FirstName`, `LastName`, `CreatedAt`, `IsDeleted`), and the invite email goes out
through `IAuthEmails<TUser>.SendInviteAsync`, which throws unless the app implements it.

```csharp
app.MapAppKitUserAdmin<User, ApplicationDbContext>("/api/users", o =>
{
    o.Roles = RoleNames.AllRoles;
    o.AdminRole = RoleNames.Admin;
    o.Policy = Policies.Admin;
    o.NewUser = (email, first, last) => new User { FirstName = first, LastName = last };
});
```

| Route | Answer |
| --- | --- |
| `GET` | Users by email with their roles. `?includeDeleted=true` adds deleted users. |
| `POST {id}/roles` with `role` | `200`. A role outside `Roles` gives `400`, a missing or deleted user `404`. |
| `DELETE {id}/roles/{role}` | `204`. Nobody takes their own admin role (`400`), and the last active admin keeps it (`409`). |
| `POST invite` with `email`, `firstName`, `lastName`, `role` | `200`. Creates the account without a password, with a confirmed email, and emails a code that lasts `InviteLifetime` (seven days). The invitee sets a password with it on `reset-password`. An email with an account gives `409`. |

Every route needs `Policy`. Mapping fails without `NewUser`.

`SeedAppKitFirstAdminAsync<TUser>` creates the first admin from `Seed:AdminEmail` and
`Seed:AdminPassword` while nobody holds the admin role, or gives an existing user with that email the
role. It does nothing until both are set.

```csharp
await app.Services.SeedAppKitFirstAdminAsync<User>(app.Configuration, (email, first, last) => new User { FirstName = first, LastName = last });
```

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

The user implements `IPasswordResetUser` with `PasswordResetCodeHash`, `PasswordResetCodeExpiration`
and `PasswordResetAttempts`.

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
`EmailVerificationCodeHash`, `EmailVerificationCodeExpiration` and `EmailVerificationAttempts`. Add a
migration for them. Then send a code at registration and from a resend endpoint, with the app's own
email text, and check it from a verify endpoint.

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

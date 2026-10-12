# SjolystInnovation.AppKit.Auth

Access tokens, rotating refresh tokens and email codes for an ASP.NET Core Identity app. The app
keeps its own endpoints, messages and emails, and calls these services from them.

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
builder.Services.AddAppKitAuth<ApplicationDbContext>(builder.Configuration.GetSection("Jwt"));
```

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

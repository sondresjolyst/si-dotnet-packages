using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>Creates and checks short codes that a user types in from an email.</summary>
internal static class VerificationCodes
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>How many wrong codes a user may try before they have to ask for a new one.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Generates a 6 character code from A-Z and 0-9.</summary>
    public static string Generate()
    {
        var code = new char[6];
        for (var i = 0; i < code.Length; i++)
            code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(code);
    }

    /// <summary>Computes the base64 SHA-256 hash of the text, as stored for codes and refresh tokens.</summary>
    public static string Hash(string text) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static bool Matches(string? storedHash, DateTime? expires, string code, TimeProvider? time)
    {
        if (storedHash == null || expires == null || expires < (time ?? TimeProvider.System).GetUtcNow().UtcDateTime)
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(storedHash), Encoding.UTF8.GetBytes(Hash(code)));
    }
}

/// <summary>The result of checking a code.</summary>
public enum CodeCheck
{
    /// <summary>The code is correct and not expired.</summary>
    Valid,

    /// <summary>The code is wrong or expired. This counts as an attempt.</summary>
    Invalid,

    /// <summary>Five wrong codes have been tried. A new code has to be issued.</summary>
    TooManyAttempts,

    /// <summary>The email is already confirmed.</summary>
    AlreadyVerified,
}

/// <summary>The user fields a password reset code needs.</summary>
public interface IPasswordResetUser
{
    /// <summary>The hash of the current reset code, or null if there is none.</summary>
    string? PasswordResetCodeHash { get; set; }
    /// <summary>When the reset code expires, in UTC.</summary>
    DateTime? PasswordResetCodeExpiration { get; set; }
    /// <summary>How many wrong reset codes the user has tried.</summary>
    int PasswordResetAttempts { get; set; }
}

/// <summary>
/// Resets passwords with a code sent by email. The helpers change only the user object. The caller
/// saves it.
/// </summary>
internal static class PasswordResetCodes
{
    /// <summary>How long a reset code lasts by default. 30 minutes.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Stores the hash of a new code and returns the code. The code is valid for 30 minutes, or for
    /// <paramref name="lifetime"/> when given, for example a longer one for an invitation.
    /// </summary>
    public static string Issue<TUser>(TUser user, TimeProvider? time = null, TimeSpan? lifetime = null) where TUser : IdentityUser, IPasswordResetUser
    {
        ArgumentNullException.ThrowIfNull(user);
        var code = VerificationCodes.Generate();
        user.PasswordResetCodeHash = VerificationCodes.Hash(code);
        user.PasswordResetCodeExpiration = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime.Add(lifetime ?? Lifetime);
        user.PasswordResetAttempts = 0;
        return code;
    }

    /// <summary>Checks a code. A wrong or expired code counts as an attempt.</summary>
    public static CodeCheck Check<TUser>(TUser user, string code, TimeProvider? time = null) where TUser : IdentityUser, IPasswordResetUser
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.PasswordResetAttempts >= VerificationCodes.MaxAttempts)
            return CodeCheck.TooManyAttempts;
        if (!VerificationCodes.Matches(user.PasswordResetCodeHash, user.PasswordResetCodeExpiration, code, time))
        {
            user.PasswordResetAttempts++;
            return CodeCheck.Invalid;
        }
        return CodeCheck.Valid;
    }

    /// <summary>
    /// Clears the code and the lockout after the new password is set. Failed sign-ins send most people
    /// here, and whoever proved the code from their mailbox may sign in at once.
    /// </summary>
    public static void Complete<TUser>(TUser user) where TUser : IdentityUser, IPasswordResetUser
    {
        ArgumentNullException.ThrowIfNull(user);
        user.PasswordResetCodeHash = null;
        user.PasswordResetCodeExpiration = null;
        user.PasswordResetAttempts = 0;
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
    }
}

/// <summary>The user fields an email verification code needs.</summary>
public interface IEmailVerificationUser
{
    /// <summary>The hash of the current verification code, or null if there is none.</summary>
    string? EmailVerificationCodeHash { get; set; }
    /// <summary>When the verification code expires, in UTC.</summary>
    DateTime? EmailVerificationCodeExpiration { get; set; }
    /// <summary>How many wrong verification codes the user has tried.</summary>
    int EmailVerificationAttempts { get; set; }
}

/// <summary>
/// Verifies email addresses with a code sent by email. The helpers change only the user object. The
/// caller saves it.
/// </summary>
public static class EmailVerificationCodes
{
    /// <summary>How long a verification code lasts. 1 hour.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>Stores the hash of a new code, valid for 1 hour, and returns the code.</summary>
    public static string Issue<TUser>(TUser user, TimeProvider? time = null) where TUser : IdentityUser, IEmailVerificationUser
    {
        ArgumentNullException.ThrowIfNull(user);
        var code = VerificationCodes.Generate();
        user.EmailVerificationCodeHash = VerificationCodes.Hash(code);
        user.EmailVerificationCodeExpiration = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime.Add(Lifetime);
        user.EmailVerificationAttempts = 0;
        return code;
    }

    /// <summary>Checks a code. A wrong or expired code counts as an attempt.</summary>
    public static CodeCheck Check<TUser>(TUser user, string code, TimeProvider? time = null) where TUser : IdentityUser, IEmailVerificationUser
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.EmailConfirmed)
            return CodeCheck.AlreadyVerified;
        if (user.EmailVerificationAttempts >= VerificationCodes.MaxAttempts)
            return CodeCheck.TooManyAttempts;
        if (!VerificationCodes.Matches(user.EmailVerificationCodeHash, user.EmailVerificationCodeExpiration, code, time))
        {
            user.EmailVerificationAttempts++;
            return CodeCheck.Invalid;
        }
        return CodeCheck.Valid;
    }

    /// <summary>Confirms the email and clears the code.</summary>
    public static void Complete<TUser>(TUser user) where TUser : IdentityUser, IEmailVerificationUser
    {
        ArgumentNullException.ThrowIfNull(user);
        user.EmailConfirmed = true;
        user.EmailVerificationCodeHash = null;
        user.EmailVerificationCodeExpiration = null;
        user.EmailVerificationAttempts = 0;
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>
/// The stable codes for the errors of the auth, account, and user admin endpoints, so a client can show
/// its own text in its own language. Every error keeps its English message next to the code.
/// </summary>
/// <remarks>
/// <para>
/// A field error is a validation problem. Its <c>codes</c> extension is keyed by field like <c>errors</c>,
/// with one code for each message at the same place, for example
/// <c>{"errors": {"Email": ["Required."]}, "codes": {"Email": ["required"]}, "limits": {}}</c>. The
/// <c>limits</c> extension holds the number that goes with <see cref="TooLong"/> or
/// <see cref="PasswordTooShort"/> for that field.
/// </para>
/// <para>
/// Any other error is a JSON body with the English message and the code, for example
/// <c>{"message": "Invalid email or password.", "code": "invalidCredentials"}</c>.
/// </para>
/// <para>
/// A client shows its own text for a code it knows and the English message for one it does not.
/// </para>
/// </remarks>
public static class AuthErrorCodes
{
    /// <summary>The field is blank and must have a value.</summary>
    public const string Required = "required";

    /// <summary>The value is not an email address. On an invite, it is also longer than 200 characters.</summary>
    public const string InvalidEmail = "invalidEmail";

    /// <summary>The text is longer than its limit. The <c>limits</c> extension holds the limit for the field.</summary>
    public const string TooLong = "tooLong";

    /// <summary>
    /// The new password is shorter than Identity's required length. The <c>limits</c> extension holds the
    /// required length for the field.
    /// </summary>
    public const string PasswordTooShort = "passwordTooShort";

    /// <summary>The new password has no digit.</summary>
    public const string PasswordRequiresDigit = "passwordRequiresDigit";

    /// <summary>The new password has no lowercase letter.</summary>
    public const string PasswordRequiresLower = "passwordRequiresLower";

    /// <summary>The new password has no uppercase letter.</summary>
    public const string PasswordRequiresUpper = "passwordRequiresUpper";

    /// <summary>The new password has no symbol, when the app's Identity options require one.</summary>
    public const string PasswordRequiresNonAlphanumeric = "passwordRequiresNonAlphanumeric";

    /// <summary>The new password has too few different characters, when the app's Identity options require more.</summary>
    public const string PasswordRequiresUniqueChars = "passwordRequiresUniqueChars";

    /// <summary>The new password breaks a password rule that has no code of its own, such as one the app added.</summary>
    public const string WeakPassword = "weakPassword";

    /// <summary>The current password is wrong when the user changes their password. The error is keyed by <c>CurrentPassword</c>.</summary>
    public const string WrongPassword = "wrongPassword";

    /// <summary>The email or password is wrong, or the user is unknown or not active. The answer is the same for all of them.</summary>
    public const string InvalidCredentials = "invalidCredentials";

    /// <summary>The sign-in is locked after too many failed attempts.</summary>
    public const string LockedOut = "lockedOut";

    /// <summary>The access token sent to refresh cannot be read, for example because this app did not sign it.</summary>
    public const string InvalidToken = "invalidToken";

    /// <summary>The user of the access token sent to refresh is gone or not active.</summary>
    public const string UserNotFound = "userNotFound";

    /// <summary>The refresh token is unknown, used, or expired.</summary>
    public const string InvalidRefreshToken = "invalidRefreshToken";

    /// <summary>The reset code is wrong or expired, or the user is unknown or not active.</summary>
    public const string InvalidResetCode = "invalidResetCode";

    /// <summary>The reset code has had too many wrong attempts. The user has to request a new one.</summary>
    public const string TooManyResetAttempts = "tooManyResetAttempts";

    /// <summary>Identity could not save the profile.</summary>
    public const string ProfileUpdateFailed = "profileUpdateFailed";

    /// <summary>The change would leave no active admin, by deleting the last admin or taking the admin role from them.</summary>
    public const string LastAdmin = "lastAdmin";

    /// <summary>Another change to the accounts collided with the deletion, which rolled back. A retry may succeed.</summary>
    public const string AccountChangeConflict = "accountChangeConflict";

    /// <summary>The role is not one of the roles the user admin endpoints allow.</summary>
    public const string UnknownRole = "unknownRole";

    /// <summary>An admin tried to take their own admin role.</summary>
    public const string OwnAdminRole = "ownAdminRole";

    /// <summary>Identity could not give the user the role.</summary>
    public const string RoleAssignFailed = "roleAssignFailed";

    /// <summary>Identity could not take the role from the user.</summary>
    public const string RoleRemoveFailed = "roleRemoveFailed";

    /// <summary>The email already has an account.</summary>
    public const string EmailTaken = "emailTaken";
}

/// <summary>The body of an error that belongs to no field: the English message and its code.</summary>
internal sealed record AuthError(string Message, string Code);

internal static class AuthResults
{
    public static JsonHttpResult<AuthError> Error(string code, string message, int status) =>
        TypedResults.Json(new AuthError(message, code), statusCode: status);
}

/// <summary>The field errors of one request, with a code for each message and the limit that goes with a code.</summary>
internal sealed class AuthFieldErrors
{
    public const string RequiredMessage = "Required.";
    public const string InvalidEmailMessage = "Not a valid email address.";

    private readonly Dictionary<string, List<string>> _errors = [];
    private readonly Dictionary<string, List<string>> _codes = [];
    private readonly Dictionary<string, int> _limits = [];

    public bool Any => _errors.Count > 0;

    public AuthFieldErrors Add(string field, string code, string message, int? limit = null)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            _errors[field] = messages = [];
            _codes[field] = [];
        }
        messages.Add(message);
        _codes[field].Add(code);
        if (limit is { } number)
            _limits[field] = number;
        return this;
    }

    public AuthFieldErrors Required(string field) => Add(field, AuthErrorCodes.Required, RequiredMessage);

    /// <summary>Adds a <c>required</c> error when the value is blank, or a <c>tooLong</c> error when it is longer than <paramref name="max"/>.</summary>
    public void Name(string field, string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            Required(field);
        else if (value.Length > max)
            Add(field, AuthErrorCodes.TooLong, $"At most {max} characters.", max);
    }

    /// <summary>
    /// Adds Identity's password errors with their codes. A wrong current password goes on
    /// <paramref name="currentField"/> when one is given, and every other error on <paramref name="field"/>.
    /// </summary>
    public AuthFieldErrors Password(string field, IEnumerable<IdentityError> errors, int requiredLength, string? currentField = null)
    {
        foreach (var error in errors)
        {
            if (currentField is not null && error.Code == nameof(IdentityErrorDescriber.PasswordMismatch))
            {
                Add(currentField, AuthErrorCodes.WrongPassword, error.Description);
                continue;
            }
            var code = PasswordCode(error.Code);
            Add(field, code, error.Description, code == AuthErrorCodes.PasswordTooShort ? requiredLength : null);
        }
        return this;
    }

    internal static string PasswordCode(string identityCode) => identityCode switch
    {
        nameof(IdentityErrorDescriber.PasswordTooShort) => AuthErrorCodes.PasswordTooShort,
        nameof(IdentityErrorDescriber.PasswordRequiresDigit) => AuthErrorCodes.PasswordRequiresDigit,
        nameof(IdentityErrorDescriber.PasswordRequiresLower) => AuthErrorCodes.PasswordRequiresLower,
        nameof(IdentityErrorDescriber.PasswordRequiresUpper) => AuthErrorCodes.PasswordRequiresUpper,
        nameof(IdentityErrorDescriber.PasswordRequiresNonAlphanumeric) => AuthErrorCodes.PasswordRequiresNonAlphanumeric,
        nameof(IdentityErrorDescriber.PasswordRequiresUniqueChars) => AuthErrorCodes.PasswordRequiresUniqueChars,
        nameof(IdentityErrorDescriber.PasswordMismatch) => AuthErrorCodes.WrongPassword,
        _ => AuthErrorCodes.WeakPassword,
    };

    /// <summary>A validation problem with the English messages in <c>errors</c> and the <c>codes</c> and <c>limits</c> extensions.</summary>
    public ValidationProblem ToResult() =>
        TypedResults.ValidationProblem(
            _errors.ToDictionary(e => e.Key, e => e.Value.ToArray()),
            extensions: new Dictionary<string, object?>
            {
                ["codes"] = _codes.ToDictionary(c => c.Key, c => c.Value.ToArray()),
                ["limits"] = _limits,
            });
}

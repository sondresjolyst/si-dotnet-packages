using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public class AuthErrorsTests
{
    private static readonly IdentityErrorDescriber Describer = new();

    private static IDictionary<string, object?> Extensions(ValidationProblem problem) => problem.ProblemDetails.Extensions;

    private static string[] Codes(ValidationProblem problem, string field) =>
        ((Dictionary<string, string[]>)Extensions(problem)["codes"]!)[field];

    private static Dictionary<string, int> Limits(ValidationProblem problem) => (Dictionary<string, int>)Extensions(problem)["limits"]!;

    [Theory]
    [InlineData("PasswordTooShort", "passwordTooShort")]
    [InlineData("PasswordRequiresDigit", "passwordRequiresDigit")]
    [InlineData("PasswordRequiresLower", "passwordRequiresLower")]
    [InlineData("PasswordRequiresUpper", "passwordRequiresUpper")]
    [InlineData("PasswordRequiresNonAlphanumeric", "passwordRequiresNonAlphanumeric")]
    [InlineData("PasswordRequiresUniqueChars", "passwordRequiresUniqueChars")]
    [InlineData("PasswordMismatch", "wrongPassword")]
    [InlineData("PwnedPassword", "weakPassword")]
    [InlineData("", "weakPassword")]
    public void Each_Identity_password_error_has_a_code(string identityCode, string code) =>
        Assert.Equal(code, AuthFieldErrors.PasswordCode(identityCode));

    [Fact]
    public void Password_errors_keep_Identity_s_text_and_put_the_length_only_on_too_short()
    {
        var problem = new AuthFieldErrors()
            .Password("NewPassword", [Describer.PasswordRequiresDigit(), Describer.PasswordTooShort(12)], 12)
            .ToResult();

        Assert.Equal(["passwordRequiresDigit", "passwordTooShort"], Codes(problem, "NewPassword"));
        Assert.Equal([Describer.PasswordRequiresDigit().Description, Describer.PasswordTooShort(12).Description], problem.ProblemDetails.Errors["NewPassword"]);
        Assert.Equal(new Dictionary<string, int> { ["NewPassword"] = 12 }, Limits(problem));
    }

    [Fact]
    public void Password_errors_without_too_short_have_no_limit()
    {
        var problem = new AuthFieldErrors().Password("NewPassword", [Describer.PasswordRequiresUpper()], 8).ToResult();

        Assert.Empty(Limits(problem));
    }

    [Fact]
    public void A_wrong_current_password_goes_on_the_current_field_when_there_is_one()
    {
        var problem = new AuthFieldErrors()
            .Password("NewPassword", [Describer.PasswordMismatch(), Describer.PasswordRequiresLower()], 8, currentField: "CurrentPassword")
            .ToResult();

        Assert.Equal(["wrongPassword"], Codes(problem, "CurrentPassword"));
        Assert.Equal(["passwordRequiresLower"], Codes(problem, "NewPassword"));
    }

    [Fact]
    public void Without_a_current_field_a_password_mismatch_stays_on_the_field()
    {
        var problem = new AuthFieldErrors().Password("NewPassword", [Describer.PasswordMismatch()], 8).ToResult();

        Assert.Equal(["NewPassword"], problem.ProblemDetails.Errors.Keys);
        Assert.Equal(["wrongPassword"], Codes(problem, "NewPassword"));
    }

    [Fact]
    public void A_taken_email_or_user_name_is_email_taken_and_anything_else_invalid_email()
    {
        var problem = UserAdminHandlers<IdentityTestUser, IdentityTestDbContext>.InvalidNewAccount(
            [Describer.DuplicateEmail("a@b.invalid"), Describer.DuplicateUserName("a@b.invalid"), Describer.InvalidUserName("a b"), Describer.InvalidEmail("x")]);

        Assert.Equal(["emailTaken", "emailTaken", "invalidEmail", "invalidEmail"], Codes(problem, "Email"));
        Assert.Equal(4, problem.ProblemDetails.Errors["Email"].Length);
    }

    [Theory]
    [InlineData(null, "required", null)]
    [InlineData(" ", "required", null)]
    [InlineData("12345", null, null)]
    [InlineData("123456", "tooLong", 5)]
    public void A_name_is_required_and_at_most_its_limit(string? value, string? code, int? limit)
    {
        var errors = new AuthFieldErrors();
        errors.Name("FirstName", value, 5);

        Assert.Equal(code is not null, errors.Any);
        if (code is null)
            return;
        var problem = errors.ToResult();
        Assert.Equal([code], Codes(problem, "FirstName"));
        Assert.Equal(limit is null ? [] : new Dictionary<string, int> { ["FirstName"] = limit.Value }, Limits(problem));
    }

    // The codes are a contract with the clients that translate them, so their values never change.
    [Fact]
    public void The_codes_keep_their_values()
    {
        string[] expected =
        [
            "required", "invalidEmail", "tooLong", "passwordTooShort", "passwordRequiresDigit", "passwordRequiresLower",
            "passwordRequiresUpper", "passwordRequiresNonAlphanumeric", "passwordRequiresUniqueChars", "weakPassword",
            "wrongPassword", "invalidCredentials", "lockedOut", "invalidToken", "userNotFound", "invalidRefreshToken",
            "invalidResetCode", "tooManyResetAttempts", "profileUpdateFailed", "lastAdmin", "accountChangeConflict",
            "unknownRole", "ownAdminRole", "roleAssignFailed", "roleRemoveFailed", "emailTaken",
        ];
        var actual = typeof(AuthErrorCodes).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToArray();
        Assert.Equal(expected, actual);
        Assert.All(typeof(AuthErrorCodes).GetFields(), f => Assert.Equal(char.ToLowerInvariant(f.Name[0]) + f.Name[1..], f.GetRawConstantValue()));
    }
}

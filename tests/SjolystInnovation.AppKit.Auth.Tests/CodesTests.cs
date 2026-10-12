using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public class CodesTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);

    [Fact]
    public void Generate_MakesSixCharactersFromLettersAndDigits()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => VerificationCodes.Generate()).ToList();

        Assert.All(codes, c => Assert.Matches("^[A-Z0-9]{6}$", c));
        Assert.True(codes.Distinct().Count() > 190);
        var seen = string.Concat(codes);
        Assert.Contains(seen, char.IsDigit);
        Assert.Contains(seen, char.IsLetter);
    }

    [Fact]
    public void Hash_IsBase64Sha256() =>
        Assert.Equal("ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=", VerificationCodes.Hash("abc"));

    [Fact]
    public void PasswordReset_IssueStoresTheHashForThirtyMinutesAndResetsAttempts()
    {
        var user = new TestUser { PasswordResetAttempts = 4 };

        var code = PasswordResetCodes.Issue(user, _time);

        Assert.Equal(VerificationCodes.Hash(code), user.PasswordResetCodeHash);
        Assert.Equal(Start.UtcDateTime.AddMinutes(30), user.PasswordResetCodeExpiration);
        Assert.Equal(0, user.PasswordResetAttempts);
    }

    [Fact]
    public void PasswordReset_IssueTakesALongerLifetimeForAnInvitation()
    {
        var user = new TestUser();

        PasswordResetCodes.Issue(user, _time, TimeSpan.FromDays(7));

        Assert.Equal(Start.UtcDateTime.AddDays(7), user.PasswordResetCodeExpiration);
    }

    [Fact]
    public void PasswordReset_AcceptsTheCodeUntilItExpires()
    {
        var user = new TestUser();
        var code = PasswordResetCodes.Issue(user, _time);
        _time.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(CodeCheck.Valid, PasswordResetCodes.Check(user, code, _time));
        Assert.Equal(0, user.PasswordResetAttempts);
    }

    [Fact]
    public void PasswordReset_CountsAWrongCode()
    {
        var user = new TestUser();
        PasswordResetCodes.Issue(user, _time);

        Assert.Equal(CodeCheck.Invalid, PasswordResetCodes.Check(user, "WRONG1", _time));
        Assert.Equal(1, user.PasswordResetAttempts);
    }

    [Fact]
    public void PasswordReset_CountsAnExpiredCode()
    {
        var user = new TestUser();
        var code = PasswordResetCodes.Issue(user, _time);
        _time.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromTicks(1));

        Assert.Equal(CodeCheck.Invalid, PasswordResetCodes.Check(user, code, _time));
        Assert.Equal(1, user.PasswordResetAttempts);
    }

    [Fact]
    public void PasswordReset_RefusesWhenNoCodeWasIssued()
    {
        var user = new TestUser();

        Assert.Equal(CodeCheck.Invalid, PasswordResetCodes.Check(user, "", _time));
        Assert.Equal(CodeCheck.Invalid, PasswordResetCodes.Check(new TestUser { PasswordResetCodeHash = VerificationCodes.Hash("ABC123") }, "ABC123", _time));
    }

    [Fact]
    public void PasswordReset_StopsAfterFiveWrongCodesEvenForTheRightOne()
    {
        var user = new TestUser();
        var code = PasswordResetCodes.Issue(user, _time);
        for (var i = 0; i < 4; i++) PasswordResetCodes.Check(user, "WRONG1", _time);
        Assert.Equal(CodeCheck.Invalid, PasswordResetCodes.Check(user, "WRONG1", _time));

        Assert.Equal(CodeCheck.TooManyAttempts, PasswordResetCodes.Check(user, code, _time));
        Assert.Equal(5, user.PasswordResetAttempts);
    }

    [Fact]
    public void PasswordReset_CompleteClearsTheCodeAndTheLockout()
    {
        var user = new TestUser { LockoutEnd = Start.AddHours(1), AccessFailedCount = 3 };
        PasswordResetCodes.Issue(user, _time);
        user.PasswordResetAttempts = 2;

        PasswordResetCodes.Complete(user);

        Assert.Null(user.PasswordResetCodeHash);
        Assert.Null(user.PasswordResetCodeExpiration);
        Assert.Equal(0, user.PasswordResetAttempts);
        Assert.Null(user.LockoutEnd);
        Assert.Equal(0, user.AccessFailedCount);
    }

    [Fact]
    public void EmailVerification_IssueStoresTheHashForOneHourAndResetsAttempts()
    {
        var user = new TestUser { EmailVerificationAttempts = 5 };

        var code = EmailVerificationCodes.Issue(user, _time);

        Assert.Equal(VerificationCodes.Hash(code), user.EmailVerificationCodeHash);
        Assert.Equal(Start.UtcDateTime.AddHours(1), user.EmailVerificationCodeExpiration);
        Assert.Equal(0, user.EmailVerificationAttempts);
    }

    [Fact]
    public void EmailVerification_AcceptsTheCodeUntilItExpires()
    {
        var user = new TestUser();
        var code = EmailVerificationCodes.Issue(user, _time);
        _time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(CodeCheck.Valid, EmailVerificationCodes.Check(user, code, _time));
        Assert.Equal(0, user.EmailVerificationAttempts);
    }

    [Fact]
    public void EmailVerification_CountsAWrongOrExpiredCode()
    {
        var user = new TestUser();
        var code = EmailVerificationCodes.Issue(user, _time);

        Assert.Equal(CodeCheck.Invalid, EmailVerificationCodes.Check(user, "WRONG1", _time));
        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromTicks(1));
        Assert.Equal(CodeCheck.Invalid, EmailVerificationCodes.Check(user, code, _time));
        Assert.Equal(2, user.EmailVerificationAttempts);
    }

    [Fact]
    public void EmailVerification_RefusesWhenNoCodeWasIssued() =>
        Assert.Equal(CodeCheck.Invalid, EmailVerificationCodes.Check(new TestUser(), "", _time));

    [Fact]
    public void EmailVerification_StopsAfterFiveWrongCodesEvenForTheRightOne()
    {
        var user = new TestUser();
        var code = EmailVerificationCodes.Issue(user, _time);
        for (var i = 0; i < 4; i++) EmailVerificationCodes.Check(user, "WRONG1", _time);
        Assert.Equal(CodeCheck.Invalid, EmailVerificationCodes.Check(user, "WRONG1", _time));

        Assert.Equal(CodeCheck.TooManyAttempts, EmailVerificationCodes.Check(user, code, _time));
        Assert.Equal(5, user.EmailVerificationAttempts);
    }

    [Fact]
    public void EmailVerification_ReportsAnAlreadyConfirmedEmailFirst()
    {
        var user = new TestUser { EmailConfirmed = true, EmailVerificationAttempts = 5 };
        var code = EmailVerificationCodes.Issue(user, _time);

        Assert.Equal(CodeCheck.AlreadyVerified, EmailVerificationCodes.Check(user, code, _time));
        Assert.Equal(CodeCheck.AlreadyVerified, EmailVerificationCodes.Check(user, "WRONG1", _time));
        Assert.Equal(0, user.EmailVerificationAttempts);
    }

    [Fact]
    public void EmailVerification_CompleteConfirmsTheEmailAndClearsTheCode()
    {
        var user = new TestUser();
        EmailVerificationCodes.Issue(user, _time);
        user.EmailVerificationAttempts = 2;

        EmailVerificationCodes.Complete(user);

        Assert.True(user.EmailConfirmed);
        Assert.Null(user.EmailVerificationCodeHash);
        Assert.Null(user.EmailVerificationCodeExpiration);
        Assert.Equal(0, user.EmailVerificationAttempts);
    }

    [Fact]
    public void Helpers_UseTheSystemClockByDefault()
    {
        var user = new TestUser();
        var before = DateTime.UtcNow;

        var code = PasswordResetCodes.Issue(user);
        EmailVerificationCodes.Issue(user);

        Assert.InRange(user.PasswordResetCodeExpiration!.Value, before.AddMinutes(30), DateTime.UtcNow.AddMinutes(30));
        Assert.InRange(user.EmailVerificationCodeExpiration!.Value, before.AddHours(1), DateTime.UtcNow.AddHours(1));
        Assert.Equal(CodeCheck.Valid, PasswordResetCodes.Check(user, code));
    }
}

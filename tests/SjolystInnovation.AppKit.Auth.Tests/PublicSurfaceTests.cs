using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public class PublicSurfaceTests
{
    [Theory]
    [InlineData(typeof(IRefreshTokenService))]
    [InlineData(typeof(RotationStatus))]
    [InlineData(typeof(RotationResult))]
    [InlineData(typeof(VerificationCodes))]
    [InlineData(typeof(PasswordResetCodes))]
    public void ImplementationHelpers_AreInternal(Type type) => Assert.False(type.IsPublic);

    [Theory]
    [InlineData(typeof(IJwtTokenService))]
    [InlineData(typeof(IPasswordResetUser))]
    [InlineData(typeof(IEmailVerificationUser))]
    [InlineData(typeof(EmailVerificationCodes))]
    [InlineData(typeof(CodeCheck))]
    public void ExtensionPoints_StayPublic(Type type) => Assert.True(type.IsPublic);
}

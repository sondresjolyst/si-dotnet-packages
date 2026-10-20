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

    [Theory]
    [InlineData(typeof(EmailBrand))]
    [InlineData(typeof(EmailLanguage))]
    [InlineData(typeof(IEmailBrand))]
    public void The_email_brand_types_stay_in_the_Auth_namespace(Type type) =>
        Assert.Equal("SjolystInnovation.AppKit.Auth", type.Namespace);
}

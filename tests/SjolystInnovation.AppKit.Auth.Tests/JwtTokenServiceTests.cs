using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public class JwtTokenServiceTests
{
    private const string Key = "test-signing-key-0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);
    private readonly IdentityUser _user = new() { Id = "user-1", UserName = "ada", Email = "ada@example.com" };

    private JwtTokenService Service(string key = Key) =>
        new(Options.Create(new AuthOptions { Key = key, Issuer = "test-issuer" }), _time);

    [Fact]
    public void Build_WritesTheUserRolesAndTimes()
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(Service().Build(_user, ["Admin", "Editor"]));

        Assert.Equal("HS256", jwt.Header.Alg);
        Assert.Equal("user-1", jwt.Claims.Single(c => c.Type == "sub").Value);
        Assert.Equal("ada", jwt.Claims.Single(c => c.Type == "unique_name").Value);
        Assert.Equal("ada@example.com", jwt.Claims.Single(c => c.Type == "email").Value);
        Assert.Equal(["Admin", "Editor"], jwt.Claims.Where(c => c.Type == "role").Select(c => c.Value));
        Assert.Equal("test-issuer", jwt.Issuer);
        Assert.Equal(["test-issuer"], jwt.Audiences);
        Assert.Equal(Start.UtcDateTime, jwt.IssuedAt);
        Assert.Equal(Start.UtcDateTime.AddSeconds(-5), jwt.ValidFrom);
        Assert.Equal(Start.UtcDateTime.AddHours(1), jwt.ValidTo);
    }

    [Fact]
    public void Build_WritesEmptyNamesWhenTheUserHasNone()
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(Service().Build(new IdentityUser { Id = "u" }, []));

        Assert.Equal("", jwt.Claims.Single(c => c.Type == "unique_name").Value);
        Assert.Equal("", jwt.Claims.Single(c => c.Type == "email").Value);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "role");
    }

    [Fact]
    public void Build_PassesTheValidationTheApisUse()
    {
        _time.SetUtcNow(DateTimeOffset.UtcNow);
        var token = Service().Build(_user, ["Admin"]);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = "test-issuer",
            ValidAudience = "test-issuer",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)),
        };

        var principal = new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);

        Assert.True(principal.IsInRole("Admin"));
        Assert.Equal("user-1", principal.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public void ReadUserIdFromExpired_ReadsATokenLongPastItsExpiry()
    {
        var token = Service().Build(_user, []);

        Assert.Equal("user-1", Service().ReadUserIdFromExpired(token));
    }

    [Fact]
    public void ReadUserIdFromExpired_RefusesATokenSignedWithAnotherKey()
    {
        var token = Service("another-signing-key-0123456789abcdef0123456789").Build(_user, []);

        Assert.Null(Service().ReadUserIdFromExpired(token));
    }

    [Fact]
    public void ReadUserIdFromExpired_RefusesAnUnsignedToken()
    {
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: [new Claim("sub", "user-1")]));

        Assert.Null(Service().ReadUserIdFromExpired(token));
    }

    [Fact]
    public void ReadUserIdFromExpired_RefusesAnotherAlgorithmWithTheSameKey()
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.WriteToken(handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", "user-1")]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key + Key)), SecurityAlgorithms.HmacSha512Signature),
        }));

        Assert.Null(new JwtTokenService(Options.Create(new AuthOptions { Key = Key + Key }), _time).ReadUserIdFromExpired(token));
    }

    [Fact]
    public void ReadUserIdFromExpired_IsNullForATokenWithoutAUser()
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.WriteToken(handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("email", "ada@example.com")]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256Signature),
        }));

        Assert.Null(Service().ReadUserIdFromExpired(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    public void ReadUserIdFromExpired_IsNullForGarbage(string token) =>
        Assert.Null(Service().ReadUserIdFromExpired(token));
}

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>Writes access tokens and reads the user from an expired one.</summary>
public interface IJwtTokenService
{
    /// <summary>
    /// Builds an HS256 token with the claims <c>sub</c>, <c>unique_name</c>, <c>email</c>, and one
    /// <c>role</c> claim per role. The token is valid from 5 seconds ago, to allow for clock skew. An app that
    /// called <see cref="SelfRegistrationServiceCollectionExtensions.AddAppKitSelfRegistration{TUser, TContext}"/>
    /// also gets the claim <c>sst</c>, a short hash of the user's security stamp, which the bearer check compares
    /// with the current stamp. Other apps get no such claim.
    /// </summary>
    string Build(IdentityUser user, IEnumerable<string> roles);

    /// <summary>
    /// Reads the user id from a token this app signed, whether or not it has expired. Returns null when
    /// the signature is wrong, the algorithm is not HS256, or the token has no user id.
    /// </summary>
    string? ReadUserIdFromExpired(string token);
}

/// <summary>The claim that ties an access token to the user's security stamp, for apps that opted in to sign-up.</summary>
internal static class StampClaim
{
    public const string Type = "sst";

    /// <summary>Returns the first 12 bytes of the SHA-256 of the stamp as 16 base64url characters, so the stamp itself never leaves the server.</summary>
    public static string Hash(string stamp) =>
        Base64UrlEncoder.Encode(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(stamp)).AsSpan(0, 12).ToArray());
}

internal sealed class JwtTokenService(IOptions<AppKitAuthOptions> options, TimeProvider time, SelfRegistrationRegistered? selfRegistration = null) : IJwtTokenService
{
    private readonly AppKitAuthOptions _options = options.Value;

    public string Build(IdentityUser user, IEnumerable<string> roles)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.UniqueName, user.UserName ?? string.Empty),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (selfRegistration is not null && user.SecurityStamp is { } stamp)
            claims.Add(new Claim(StampClaim.Type, StampClaim.Hash(stamp)));

        var now = time.GetUtcNow().UtcDateTime;
        var handler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.Add(_options.AccessTokenLifetime),
            SigningCredentials = new SigningCredentials(SigningKey(), SecurityAlgorithms.HmacSha256Signature),
            Issuer = _options.Issuer,
            Audience = _options.Issuer,
        };
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    public string? ReadUserIdFromExpired(string token)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateAudience = false,
            ValidateIssuer = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = SigningKey(),
            ValidateLifetime = false,
        };
        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, parameters, out var validated);
            if (validated is not JwtSecurityToken jwt
                || !jwt.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.OrdinalIgnoreCase))
                return null;
            return principal.GetUserId();
        }
        // The token comes from the client, so any failure to read it means the app did not sign it.
        catch (Exception)
        {
            return null;
        }
    }

    private SymmetricSecurityKey SigningKey() => new(Encoding.UTF8.GetBytes(_options.Key));
}

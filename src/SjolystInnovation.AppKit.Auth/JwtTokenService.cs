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
    /// An HS256 token with the claims <c>sub</c>, <c>unique_name</c>, <c>email</c> and one <c>role</c>
    /// claim per role. It is valid from 5 seconds ago, to allow for clock skew.
    /// </summary>
    string Build(IdentityUser user, IEnumerable<string> roles);

    /// <summary>
    /// The user id of a token this app signed, whether or not it has expired. Null when the signature
    /// is wrong, the algorithm is not HS256 or the token has no user id.
    /// </summary>
    string? ReadUserIdFromExpired(string token);
}

internal sealed class JwtTokenService(IOptions<AuthOptions> options, TimeProvider time) : IJwtTokenService
{
    private readonly AuthOptions _options = options.Value;

    public string Build(IdentityUser user, IEnumerable<string> roles)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.UniqueName, user.UserName ?? string.Empty),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

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
            return principal.Claims.FirstOrDefault(c =>
                c.Type == JwtRegisteredClaimNames.Sub || c.Type == ClaimTypes.NameIdentifier)?.Value;
        }
        // The token comes from the client, so any failure to read it means it is not ours.
        catch (Exception)
        {
            return null;
        }
    }

    private SymmetricSecurityKey SigningKey() => new(Encoding.UTF8.GetBytes(_options.Key));
}

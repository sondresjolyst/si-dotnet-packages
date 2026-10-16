using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>Reads the signed-in user's id and roles from a <see cref="ClaimsPrincipal"/>.</summary>
public static class AppKitClaims
{
    /// <summary>
    /// Gets the user id from the <see cref="ClaimTypes.NameIdentifier"/> claim, or from the <c>sub</c> claim
    /// when the principal has no <see cref="ClaimTypes.NameIdentifier"/> claim.
    /// </summary>
    /// <param name="principal">The signed-in user.</param>
    /// <returns>The user id, or null when the principal has neither claim.</returns>
    public static string? GetUserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
    }

    /// <summary>
    /// Checks whether the principal is the user with the given id. The comparison is ordinal. A principal
    /// without a user id is never the caller.
    /// </summary>
    /// <param name="principal">The signed-in user.</param>
    /// <param name="userId">The id of the user to compare with.</param>
    /// <returns>True when the principal's user id equals <paramref name="userId"/>.</returns>
    public static bool IsCaller(this ClaimsPrincipal principal, string userId) =>
        principal.GetUserId() is { } id && string.Equals(id, userId, StringComparison.Ordinal);

    /// <summary>
    /// Checks whether the principal holds at least one of the roles. Each check goes through
    /// <see cref="ClaimsPrincipal.IsInRole(string)"/>, so role names are case sensitive.
    /// </summary>
    /// <param name="principal">The signed-in user.</param>
    /// <param name="roles">The role names to look for.</param>
    /// <returns>True when the principal is in any of <paramref name="roles"/>. False when none are given.</returns>
    public static bool IsInAnyRole(this ClaimsPrincipal principal, params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(roles);
        return roles.Any(principal.IsInRole);
    }
}

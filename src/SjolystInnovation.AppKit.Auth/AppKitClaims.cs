using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;

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

    // Written by the bearer check for apps that opted in to sign-up, and replaced on every request, like the roles.
    internal const string EmailVerifiedClaim = "appkit:email_verified";
    internal const string PendingConsentClaim = "appkit:pending_consent";

    // Says that the bearer check worked out the pending documents for this request, so an empty list means nothing is pending.
    internal const string PendingCheckedClaim = "appkit:pending_checked";

    internal static bool HasPendingFact(this ClaimsPrincipal principal) => principal.HasClaim(PendingCheckedClaim, "true");

    private static readonly JsonSerializerOptions PendingJson = new(JsonSerializerDefaults.Web);

    internal static string PendingToClaim(PendingConsent pending) => JsonSerializer.Serialize(pending, PendingJson);

    /// <summary>
    /// Checks whether the signed-in user has confirmed the email address. The bearer check sets this fact on each
    /// request from the same cached user check as the roles, and only for an app that called
    /// <see cref="SelfRegistrationServiceCollectionExtensions.AddAppKitSelfRegistration{TUser, TContext}"/>. For any
    /// other principal the answer is false.
    /// </summary>
    /// <param name="principal">The signed-in user.</param>
    /// <returns>True when the email is confirmed.</returns>
    public static bool IsEmailVerified(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.HasClaim(EmailVerifiedClaim, "true");
    }

    /// <summary>
    /// Gets the documents the signed-in user must accept again, each with the version that is current. The bearer
    /// check sets them like <see cref="IsEmailVerified"/>. The list is empty when nothing is pending and for any
    /// principal the check did not see.
    /// </summary>
    /// <param name="principal">The signed-in user.</param>
    /// <returns>The pending documents, in document order.</returns>
    public static IReadOnlyList<PendingConsent> GetPendingConsents(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        List<PendingConsent> pending = [];
        foreach (var claim in principal.FindAll(PendingConsentClaim))
        {
            if (JsonSerializer.Deserialize<PendingConsent>(claim.Value, PendingJson) is { } item)
                pending.Add(item);
        }
        return pending;
    }
}

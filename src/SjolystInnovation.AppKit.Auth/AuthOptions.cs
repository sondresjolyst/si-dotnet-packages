namespace SjolystInnovation.AppKit.Auth;

/// <summary>Token settings, bound from the app's <c>Jwt</c> configuration section.</summary>
public sealed class AuthOptions
{
    /// <summary>The HS256 signing key. At least 32 bytes as UTF-8.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Written as both the issuer and the audience of an access token.</summary>
    public string? Issuer { get; set; }

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How many calendar months a refresh token lasts.</summary>
    public int RefreshTokenLifetimeMonths { get; set; } = 6;

    /// <summary>How many refresh tokens one user may hold at once, across devices and browsers.</summary>
    public int MaxLiveRefreshTokens { get; set; } = 5;
}

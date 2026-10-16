namespace SjolystInnovation.AppKit.Auth;

/// <summary>The token options, bound from the app's <c>Jwt</c> configuration section.</summary>
public sealed class AuthOptions
{
    /// <summary>The HS256 signing key. Must be at least 32 bytes as UTF-8.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The value written as both the issuer and the audience of an access token.</summary>
    public string? Issuer { get; set; }

    /// <summary>How long an access token lasts. Defaults to 1 hour.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How many calendar months a refresh token lasts. Defaults to 6.</summary>
    public int RefreshTokenLifetimeMonths { get; set; } = 6;

    /// <summary>How many refresh tokens one user may hold at once, across devices and browsers. Defaults to 5.</summary>
    public int MaxLiveRefreshTokens { get; set; } = 5;
}

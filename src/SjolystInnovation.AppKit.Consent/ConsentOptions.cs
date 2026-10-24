namespace SjolystInnovation.AppKit.Consent;

/// <summary>The options for <see cref="ConsentExtensions.AddAppKitConsent{TContext}(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{AppKitConsentOptions}?)"/>.</summary>
public sealed class AppKitConsentOptions
{
    /// <summary>The number of days in <see cref="DefaultKeepFor"/>: three years, with one leap day.</summary>
    public const int DefaultKeepForDays = 3 * 365 + 1;

    /// <summary>How long a row is kept by default, <see cref="DefaultKeepForDays"/> days.</summary>
    public static readonly TimeSpan DefaultKeepFor = TimeSpan.FromDays(DefaultKeepForDays);

    /// <summary>
    /// How long a row is kept after <see cref="ConsentRecord.DecidedAt"/>. A daily job deletes older rows.
    /// Defaults to <see cref="DefaultKeepFor"/>. It must be positive.
    /// </summary>
    public TimeSpan KeepFor { get; set; } = DefaultKeepFor;
}

/// <summary>The options for <see cref="ConsentExtensions.MapAppKitConsent{TContext}"/>.</summary>
public sealed class AppKitConsentEndpointOptions
{
    /// <summary>
    /// The name of a rate limiting policy the app registered with <c>AddRateLimiter</c>, applied to
    /// <c>POST consent</c>. Null applies none, for an app that limits the route another way, such as
    /// with a rule for AppKit.Hosting's <c>AddAppKitRateLimiting</c>. Defaults to null.
    /// </summary>
    public string? RateLimitPolicy { get; set; }

    /// <summary>The authorization policy that reading the history of a consent id requires. Defaults to <c>Admin</c>.</summary>
    public string Policy { get; set; } = "Admin";
}

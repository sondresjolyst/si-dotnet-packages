namespace SjolystInnovation.AppKit.Consent;

/// <summary>The options for <see cref="ConsentExtensions.AddAppKitConsent{TContext}(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{AppKitConsentOptions}?)"/>.</summary>
public sealed class AppKitConsentOptions
{
    /// <summary>The number of days in <see cref="DefaultKeepFor"/>: three years, with one leap day.</summary>
    public const int DefaultKeepForDays = 3 * 365 + 1;

    /// <summary>The number of days in <see cref="MinimumKeepFor"/>.</summary>
    public const int MinimumKeepForDays = 365;

    /// <summary>How long a row is kept by default, <see cref="DefaultKeepForDays"/> days.</summary>
    public static readonly TimeSpan DefaultKeepFor = TimeSpan.FromDays(DefaultKeepForDays);

    /// <summary>The shortest <see cref="KeepFor"/> the app starts with, <see cref="MinimumKeepForDays"/> days.</summary>
    public static readonly TimeSpan MinimumKeepFor = TimeSpan.FromDays(MinimumKeepForDays);

    /// <summary>
    /// How long a row is kept after <see cref="ConsentRecord.DecidedAt"/>. A daily job deletes older rows.
    /// Defaults to <see cref="DefaultKeepFor"/>. It must be at least <see cref="MinimumKeepFor"/>.
    /// </summary>
    public TimeSpan KeepFor { get; set; } = DefaultKeepFor;
}

/// <summary>The options for <see cref="ConsentExtensions.MapAppKitConsent{TContext}"/>.</summary>
public sealed class AppKitConsentEndpointOptions
{
    /// <summary>
    /// The name of a rate limiting policy the app registered with <c>AddRateLimiter</c>, applied to
    /// <c>POST consent</c>. Null applies none and logs a warning at startup. Leave it null only when the
    /// app limits the route another way, such as with a rule for AppKit.Hosting's
    /// <c>AddAppKitRateLimiting</c>. Defaults to null.
    /// </summary>
    public string? RateLimitPolicy { get; set; }

    /// <summary>
    /// Set it to true when the app limits <c>POST consent</c> outside the package, such as with a rule for
    /// AppKit.Hosting's <c>AddAppKitRateLimiting</c>. It only skips the startup warning for a null
    /// <see cref="RateLimitPolicy"/>. The package does not check that a limit exists, so it is a promise the
    /// app keeps. Defaults to false.
    /// </summary>
    public bool RateLimitedExternally { get; set; }

    /// <summary>The authorization policy that reading the history of a consent id requires. Defaults to <c>Admin</c>.</summary>
    public string Policy { get; set; } = "Admin";
}

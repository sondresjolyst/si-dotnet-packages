using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SjolystInnovation.AppKit.Auth;

// Marks that AddAppKitSelfRegistration ran, so later code can tell that the app opted in.
internal sealed class SelfRegistrationRegistered;

/// <summary>Registers the opt-in public sign-up.</summary>
public static class SelfRegistrationServiceCollectionExtensions
{
    /// <summary>
    /// Opts the app in to the public sign-up and binds <see cref="AppKitRegistrationOptions{TUser}"/>. The app
    /// fails to start when <c>NewUser</c> is missing, when the unverified lifetime or the minimum age is not
    /// positive, when the resend cooldown is negative, when a reminder offset is not positive and shorter than
    /// the lifetime, or when the terms document name is blank. An app that never calls this sees no change.
    /// </summary>
    public static IServiceCollection AddAppKitSelfRegistration<TUser, TContext>(
        this IServiceCollection services, Action<AppKitRegistrationOptions<TUser>> configure)
        where TUser : IdentityUser
        where TContext : DbContext, IAccountConsentDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<AppKitRegistrationOptions<TUser>>()
            .Configure(configure)
            .Validate(o => o.NewUser is not null, "AppKitRegistrationOptions.NewUser is required. Set it in AddAppKitSelfRegistration.")
            .Validate(o => o.UnverifiedLifetime > TimeSpan.Zero, "The unverified account lifetime must be positive.")
            .Validate(o => o.ResendCooldown >= TimeSpan.Zero, "The resend cooldown must not be negative.")
            .Validate(o => o.MinimumAge > 0, "The minimum age must be positive.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.TermsDocument), "The terms document name must not be blank.")
            .Validate(o => o.RemindBefore is not null && o.RemindBefore.All(r => r > TimeSpan.Zero && r < o.UnverifiedLifetime),
                "Every reminder offset must be positive and shorter than the unverified account lifetime.")
            .ValidateOnStart();
        services.TryAddSingleton<SelfRegistrationRegistered>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<AccountConsentService<TContext>>();
        return services;
    }
}

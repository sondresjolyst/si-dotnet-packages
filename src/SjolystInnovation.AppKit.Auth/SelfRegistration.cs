using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SjolystInnovation.AppKit.Email;

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
    /// the lifetime, or when the terms document name is blank. It registers an <see cref="IRegistrationEmails{TUser}"/>
    /// that sends the verification email, the notice to an existing account and the removal reminder through
    /// <see cref="IEmailSender"/> in the layout of the other auth emails, unless the app registered its own first. The verification
    /// email and the reminder greet with no name. The default needs an <see cref="IEmailBrand"/> and an <see cref="IEmailSender"/>,
    /// and <see cref="AppKitAuthEmailOptions.VerifyEmailUrl"/> for the verification email. It registers the default <see cref="IConsentRequirements"/>. The bearer check then reads whether the email is confirmed and which documents the user must accept again, and the login and refresh answers gain <c>emailVerified</c> and <c>pendingConsents</c>. It also makes access tokens carry a hash of the security stamp, so verifying an email ends earlier access tokens at once. A token without that claim, issued before the app opted in, stays valid until it expires. It also registers <see cref="IUnverifiedAccountCleanup"/> and a hosted service that runs it when the app starts and once a day, unless <see cref="AppKitRegistrationOptions{TUser}.RunCleanupInBackground"/> is false. The app also fails to start when the EF model of <typeparamref name="TContext"/> has no unique index on the normalised email of <typeparamref name="TUser"/>. <see cref="AccountConsentModelBuilderExtensions.AddAppKitAccountConsents{TUser}"/> adds it. The check reads the model only and cannot see the database, so the app must also apply the migration that creates the index. The account export then lists the consent rows of the user under <c>consents</c>, without the IP address, and deleting an account clears the stored IP address and locale of its consent rows and keeps the rows. An app that never calls this sees no change.
    /// </summary>
    public static IServiceCollection AddAppKitSelfRegistration<TUser, TContext>(
        this IServiceCollection services, Action<AppKitRegistrationOptions<TUser>> configure)
        where TUser : IdentityUser, IAppKitAccount, IEmailVerificationUser
        where TContext : DbContext, IAccountConsentDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<AppKitRegistrationOptions<TUser>>()
            .Configure(configure)
            .Validate(o => o.NewUser is not null, "AppKitRegistrationOptions.NewUser is required. Set it in AddAppKitSelfRegistration.")
            .Validate(o => o.UnverifiedLifetime > TimeSpan.Zero, "The unverified account lifetime must be positive.")
            .Validate(o => o.ResendCooldown >= TimeSpan.Zero, "The resend cooldown must not be negative.")
            .Validate(o => o.MaxEmailsPerAddressPerDay > 0 && o.MaxOwnerEmailsPerAddressPerDay > 0, "The most emails per address per day must be positive.")
            .Validate(o => o.MaxSignUpsPerNetworkPerDay > 0, "The most sign-ups per network per day must be positive.")
            .Validate(o => o.MinimumAge > 0, "The minimum age must be positive.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.TermsDocument), "The terms document name must not be blank.")
            .Validate(o => o.AdditionalDocuments is not null
                    && o.AdditionalDocuments.All(d => !string.IsNullOrWhiteSpace(d) && d.Length <= 32 && d.Trim() == d && d != o.TermsDocument && d != "age")
                    && o.AdditionalDocuments.Distinct(StringComparer.Ordinal).Count() == o.AdditionalDocuments.Count,
                "Every further document needs a name of at most 32 characters with no outer spaces that is not repeated and is neither the terms nor age.")
            .Validate(o => o.RemindBefore is not null && o.RemindBefore.All(r => r > TimeSpan.Zero && r < o.UnverifiedLifetime),
                "Every reminder offset must be positive and shorter than the unverified account lifetime.")
            .ValidateOnStart();
        // The sign-up count hashes are keyed from the signing key. Without AddAppKitAuth the key stays empty and fails here.
        services.AddOptions<AppKitAuthOptions>()
            .Validate(o => AppKitAuthOptions.IsLongEnough(o.Key),
                $"The sign-up keys its count hashes from the signing key. Call AddAppKitAuth and set a signing key of at least {AppKitAuthOptions.MinimumKeyBytes} bytes.")
            .ValidateOnStart();
        services.AddMemoryCache();
        services.TryAddSingleton<SelfRegistrationRegistered>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<AccountConsentService<TContext>>();
        services.TryAddScoped<SignUpMailLimiter<TContext>>();
        services.TryAddScoped<IAccountConsentExporter>(sp => sp.GetRequiredService<AccountConsentService<TContext>>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAccountDataEraser<TUser>, AccountConsentEraser<TUser, TContext>>());
        services.TryAddScoped<IPendingConsentReader>(sp => sp.GetRequiredService<AccountConsentService<TContext>>());
        services.TryAddScoped<IConsentRequirements, ConsentRequirements<TContext>>();
        services.TryAddScoped<IRegistrationEmails<TUser>, RegistrationEmails<TUser>>();
        services.TryAddSingleton<IUnverifiedAccountCleanup, UnverifiedAccountCleanup<TUser, TContext>>();
        services.AddHostedService<SelfRegistrationModelCheck<TUser, TContext>>();
        services.AddHostedService<UnverifiedAccountCleanupService<TUser>>();
        return services;
    }
}

// Fails the start of the app when the model has no unique index on the normalised email. The sign-up relies on it to
// keep two sign-ups for one email from both creating an account. The check reads the EF model only and cannot see the
// database, so the app must also apply the migration that creates the index.
internal sealed class SelfRegistrationModelCheck<TUser, TContext>(IServiceScopeFactory scopes) : IHostedService
    where TUser : class
    where TContext : DbContext
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<TContext>().Model;
        var unique = model.FindEntityType(typeof(TUser))?.GetIndexes()
            .Any(i => i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == "NormalizedEmail") ?? false;
        if (!unique)
            throw new InvalidOperationException(
                $"The sign-up needs a unique index on NormalizedEmail of {typeof(TUser).Name} in the model of {typeof(TContext).Name}. Call AddAppKitAccountConsents<{typeof(TUser).Name}>() on the ModelBuilder in OnModelCreating, and apply the migration that creates the index. This check reads the model only and cannot see the database.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

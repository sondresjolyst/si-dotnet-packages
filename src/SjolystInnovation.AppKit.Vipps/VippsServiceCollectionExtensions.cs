using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>Registers the Vipps client.</summary>
public static class VippsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IVippsClient"/> with its own <see cref="HttpClient"/>. Calls go to the live
    /// environment unless you register your own <see cref="IVippsEnvironment"/>, before or after this call.
    /// </summary>
    public static IHttpClientBuilder AddAppKitVipps(this IServiceCollection services, Action<VippsOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<IVippsEnvironment>(new FixedVippsEnvironment(false));
        services.TryAddSingleton(TimeProvider.System);
        return services.AddHttpClient<IVippsClient, VippsClient>();
    }

    /// <summary>
    /// Registers <see cref="IVippsClient"/> with the credentials in a flat configuration section, such as
    /// the <c>Vipps__ClientId</c> and <c>Vipps__TestClientId</c> environment variables. The live keys are
    /// <c>BaseUrl</c>, <c>ClientId</c>, <c>ClientSecret</c>, <c>SubscriptionKey</c>, and
    /// <c>MerchantSerialNumber</c>. The test keys are the same with a <c>Test</c> prefix. A missing base
    /// URL keeps its default. <paramref name="configure"/> runs after the section is read.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="section">The configuration section, for example <c>Vipps</c>.</param>
    /// <param name="configure">An optional callback for the other options, such as <see cref="VippsOptions.SystemName"/>.</param>
    /// <returns>The builder of the client's <see cref="HttpClient"/>.</returns>
    public static IHttpClientBuilder AddAppKitVipps(this IServiceCollection services, IConfigurationSection section, Action<VippsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        return services.AddAppKitVipps(o =>
        {
            ReadFlat(section, o);
            configure?.Invoke(o);
        });
    }

    internal static void ReadFlat(IConfigurationSection section, VippsOptions options)
    {
        Read(section, "", options.Live);
        Read(section, "Test", options.Test);
    }

    private static void Read(IConfigurationSection section, string prefix, VippsCredentials credentials)
    {
        var baseUrl = section[prefix + "BaseUrl"];
        if (!string.IsNullOrEmpty(baseUrl)) credentials.BaseUrl = baseUrl;
        credentials.ClientId = section[prefix + "ClientId"] ?? string.Empty;
        credentials.ClientSecret = section[prefix + "ClientSecret"] ?? string.Empty;
        credentials.SubscriptionKey = section[prefix + "SubscriptionKey"] ?? string.Empty;
        credentials.MerchantSerialNumber = section[prefix + "MerchantSerialNumber"] ?? string.Empty;
    }
}

/// <summary>Registers the webhook endpoints' services, the registration, and the cleanup.</summary>
public static class VippsWebhookServiceCollectionExtensions
{
    /// <summary>
    /// Registers what <see cref="VippsWebhookEndpointExtensions.MapAppKitVippsWebhook{TContext}"/> needs: the
    /// options, <see cref="IVippsWebhookSecretProtector"/>, and a default <see cref="IVippsWebhookRegistrationStore"/>
    /// over the table of <see cref="VippsWebhookRegistrationEntry"/>. A store you register before or after
    /// this call replaces it. It also adds a hosted service that deletes old processed events and, unless
    /// <see cref="VippsWebhookOptions.RegisterOnStart"/> is off, one that registers the webhooks after the
    /// app starts. Call <see cref="VippsServiceCollectionExtensions.AddAppKitVipps(IServiceCollection, Action{VippsOptions})"/>
    /// as well. The app fails to start when the retention does not exceed
    /// <see cref="VippsWebhookSignature.MaxAge"/> or a slot is invalid.
    /// </summary>
    /// <typeparam name="TContext">The app's DbContext.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The callback that adds the slots and sets the options.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddAppKitVippsWebhooks<TContext>(this IServiceCollection services, Action<VippsWebhookOptions> configure)
        where TContext : DbContext, IVippsWebhookDbContext
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<VippsWebhookOptions>()
            .Configure(configure)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<VippsWebhookOptions>, VippsWebhookOptionsValidator>());
        services.AddDataProtection();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IVippsWebhookSecretProtector, VippsWebhookSecretProtector>();
        services.TryAddSingleton<VippsWebhookSecretCache>();
        services.TryAddScoped<IVippsWebhookRegistrationStore, EfVippsWebhookRegistrationStore<TContext>>();
        services.AddHostedService<VippsWebhookCleanupService<TContext>>();
        services.AddHostedService<VippsWebhookRegistrationService<TContext>>();
        return services;
    }
}

internal sealed class VippsWebhookOptionsValidator : IValidateOptions<VippsWebhookOptions>
{
    public ValidateOptionsResult Validate(string? name, VippsWebhookOptions options) =>
        options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}

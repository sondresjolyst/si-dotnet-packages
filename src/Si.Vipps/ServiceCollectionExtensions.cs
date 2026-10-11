using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Si.Vipps;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IVippsClient"/> with its own <see cref="HttpClient"/>. Calls go to the live
    /// environment unless the app registers its own <see cref="IVippsEnvironment"/>, before or after.
    /// </summary>
    public static IHttpClientBuilder AddVipps(this IServiceCollection services, Action<VippsOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<IVippsEnvironment>(new FixedVippsEnvironment(false));
        services.TryAddSingleton(TimeProvider.System);
        return services.AddHttpClient<IVippsClient, VippsClient>();
    }
}

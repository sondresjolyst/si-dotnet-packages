using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>Registers the token services.</summary>
public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IJwtTokenService"/> and <see cref="IRefreshTokenService"/>, with
    /// <see cref="AppKitAuthOptions"/> bound from <paramref name="jwtSection"/>, and a hosted service that
    /// deletes expired refresh tokens at startup and daily. The app fails to start when the key is
    /// shorter than 32 bytes, or when a lifetime or the token cap is not positive.
    /// </summary>
    public static IServiceCollection AddAppKitAuth<TContext>(this IServiceCollection services, IConfigurationSection jwtSection)
        where TContext : DbContext, IAuthDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(jwtSection);
        services.AddOptions<AppKitAuthOptions>()
            .Bind(jwtSection)
            .Validate(o => Encoding.UTF8.GetByteCount(o.Key) >= 32, $"{jwtSection.Path}:Key must be at least 32 bytes.")
            .Validate(o => o.AccessTokenLifetime > TimeSpan.Zero, "The access token lifetime must be positive.")
            .Validate(o => o.RefreshTokenLifetimeMonths > 0, "The refresh token lifetime must be positive.")
            .Validate(o => o.MaxLiveRefreshTokens > 0, "The refresh token cap must be positive.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJwtTokenService, JwtTokenService>();
        services.TryAddScoped<IRefreshTokenService, RefreshTokenService<TContext>>();
        services.AddHostedService<RefreshTokenCleanupService<TContext>>();
        return services;
    }
}

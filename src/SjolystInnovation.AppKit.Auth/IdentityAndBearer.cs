using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>The options for <see cref="IdentityAndBearerExtensions.AddAppKitJwtBearer{TUser}"/>.</summary>
public sealed class AppKitJwtBearerOptions
{
    /// <summary>
    /// The paths where the token may come as <c>?access_token=</c>, for example <c>/hubs</c> for SignalR,
    /// which cannot send a header when it opens a WebSocket. Paths match by whole segments. Defaults to empty.
    /// </summary>
    public IList<string> QueryTokenPaths { get; } = [];

    /// <summary>How long the result of the user check is cached per user. Defaults to 1 minute.</summary>
    public TimeSpan UserCheckCacheDuration { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Registers Identity and the JWT bearer scheme.</summary>
public static class IdentityAndBearerExtensions
{
    /// <summary>
    /// Registers ASP.NET Core Identity with <see cref="IdentityRole"/>, EF stores in
    /// <typeparamref name="TContext"/>, and the default token providers. Sign-in locks for 30 minutes
    /// after 5 failed attempts, including for new users. A password needs at least 8 characters with a
    /// digit, a lowercase letter, and an uppercase letter. A symbol is not required. Every email is
    /// unique. <paramref name="configure"/> runs after these defaults.
    /// </summary>
    public static IdentityBuilder AddAppKitIdentity<TUser, TContext>(this IServiceCollection services, Action<IdentityOptions>? configure = null)
        where TUser : IdentityUser
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddIdentity<TUser, IdentityRole>(options =>
            {
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(30);
                options.Lockout.AllowedForNewUsers = true;

                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;

                options.User.RequireUniqueEmail = true;
                configure?.Invoke(options);
            })
            .AddEntityFrameworkStores<TContext>()
            .AddDefaultTokenProviders();
    }

    /// <summary>
    /// Makes the JWT bearer scheme the default and validates the tokens that <see cref="IJwtTokenService"/>
    /// writes. It checks the signature with <see cref="AuthOptions.Key"/>, the issuer and audience against
    /// <see cref="AuthOptions.Issuer"/>, and the lifetime with 30 seconds of clock skew. Call it after
    /// <see cref="ServiceCollectionExtensions.AddAppKitAuth{TContext}"/>. The scheme refuses a token when
    /// its user no longer exists or <paramref name="isActive"/> returns false for the user, so a deleted
    /// account stops working within <see cref="AppKitJwtBearerOptions.UserCheckCacheDuration"/>. The same
    /// check replaces the token's role claims with the user's current roles, so a removed role stops
    /// counting within that time too.
    /// </summary>
    public static AuthenticationBuilder AddAppKitJwtBearer<TUser>(this IServiceCollection services,
        Func<TUser, bool>? isActive = null, Action<AppKitJwtBearerOptions>? configure = null)
        where TUser : IdentityUser
    {
        ArgumentNullException.ThrowIfNull(services);
        var appKit = new AppKitJwtBearerOptions();
        configure?.Invoke(appKit);
        services.AddMemoryCache();

        // Identity sets its cookie schemes as the defaults. PostConfigure runs after every Configure, so
        // these win whichever is registered first.
        services.PostConfigure<AuthenticationOptions>(options =>
        {
            options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultForbidScheme = JwtBearerDefaults.AuthenticationScheme;
        });

        var builder = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>, IHostEnvironment>((options, auth, environment) =>
            {
                options.RequireHttpsMetadata = !environment.IsDevelopment();
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = auth.Value.Issuer,
                    ValidAudience = auth.Value.Issuer,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.Value.Key)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // Outside these paths the token stays empty, and the handler reads the header instead.
                        if (appKit.QueryTokenPaths.Any(p => context.Request.Path.StartsWithSegments(p)))
                            context.Token = context.Request.Query["access_token"].ToString();
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context => CheckUserAsync(context, isActive, appKit.UserCheckCacheDuration),
                };
            });
        return builder;
    }

    /// <summary>Builds the cache key for a user's bearer check result. Callers remove it when the user's roles or account change.</summary>
    internal static string UserCheckKey(string userId) => $"SjolystInnovation.AppKit.Auth:user-check:{userId}";

    private static async Task CheckUserAsync<TUser>(TokenValidatedContext context, Func<TUser, bool>? isActive, TimeSpan cacheFor)
        where TUser : IdentityUser
    {
        var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(userId))
        {
            context.Fail("The token has no user id.");
            return;
        }

        var services = context.HttpContext.RequestServices;
        var cache = services.GetRequiredService<IMemoryCache>();
        var key = UserCheckKey(userId);
        if (!cache.TryGetValue(key, out IReadOnlyList<string>? roles))
        {
            var users = services.GetRequiredService<UserManager<TUser>>();
            var user = await users.FindByIdAsync(userId);
            // Null means the user is gone or not active.
            roles = user is not null && (isActive is null || isActive(user)) ? [.. await users.GetRolesAsync(user)] : null;
            cache.Set(key, roles, cacheFor);
        }
        if (roles is null)
        {
            context.Fail("The user no longer exists or is not active.");
            return;
        }

        // The roles in the token were correct when it was issued. The current roles replace them.
        if (context.Principal?.Identity is ClaimsIdentity identity)
        {
            foreach (var claim in identity.FindAll(identity.RoleClaimType).ToList())
                identity.RemoveClaim(claim);
            foreach (var role in roles)
                identity.AddClaim(new Claim(identity.RoleClaimType, role));
        }
    }
}

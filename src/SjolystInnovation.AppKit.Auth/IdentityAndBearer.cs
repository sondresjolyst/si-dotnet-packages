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
    /// writes. It checks the signature with <see cref="AppKitAuthOptions.Key"/>, the issuer and audience against
    /// <see cref="AppKitAuthOptions.Issuer"/>, and the lifetime with 30 seconds of clock skew. Call it after
    /// <see cref="AuthServiceCollectionExtensions.AddAppKitAuth{TContext}"/>. The scheme refuses a token when
    /// its user no longer exists or <paramref name="isActive"/> returns false for the user, so a deleted
    /// account stops working within <see cref="AppKitJwtBearerOptions.UserCheckCacheDuration"/>. The same
    /// check replaces the token's role claims with the user's current roles, so a removed role stops
    /// counting within that time too. An app that called
    /// <see cref="SelfRegistrationServiceCollectionExtensions.AddAppKitSelfRegistration{TUser, TContext}"/> also gets the facts
    /// that <see cref="AppKitClaims.IsEmailVerified"/> and <see cref="AppKitClaims.GetPendingConsents"/> read, refreshed in
    /// the same way. In such an app a token whose stamp differs from the cached one makes the check read the user once more and
    /// replace the cache entry, so a token from a verify or a reset that another instance served is not refused. This happens
    /// at most once per user every 5 seconds. Until the cache entry expires, an instance may still accept a token from before
    /// a verify or a reset that another instance served.
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
            .Configure<IOptions<AppKitAuthOptions>, IHostEnvironment>((options, auth, environment) =>
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

    // What the bearer check keeps per user: the current roles, the hash of the security stamp, whether the email is confirmed
    // and the documents to accept again. The last two are only filled for an app that opted in to sign-up. Null roles mean
    // the user is gone or not active.
    private sealed record UserCheck(IReadOnlyList<string>? Roles, string? StampHash, bool EmailVerified, IReadOnlyList<PendingConsent> Pending);

    // The shortest time between two reads of a user caused by a token whose stamp differs from the cached one.
    private static readonly TimeSpan StampRefreshInterval = TimeSpan.FromSeconds(5);

    // True when no such read happened for the user inside the interval, and then notes this one. The note is kept in the cache and
    // expires on its own.
    private static bool StampRefreshAllowed(IServiceProvider services, IMemoryCache cache, string userId, TimeSpan cacheFor)
    {
        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        var noteKey = $"SjolystInnovation.AppKit.Auth:stamp-refresh:{userId}";
        if (cache.TryGetValue(noteKey, out DateTimeOffset last) && now - last < StampRefreshInterval)
            return false;
        cache.Set(noteKey, now, cacheFor > StampRefreshInterval ? cacheFor : StampRefreshInterval);
        return true;
    }

    // Reads the user, the roles and, for an app that opted in, the pending documents, and keeps the result in the cache.
    private static async Task<UserCheck> LoadUserCheckAsync<TUser>(TokenValidatedContext context, Func<TUser, bool>? isActive, IMemoryCache cache, string key, TimeSpan cacheFor, string userId)
        where TUser : IdentityUser
    {
        var services = context.HttpContext.RequestServices;
        var users = services.GetRequiredService<UserManager<TUser>>();
        var user = await users.FindByIdAsync(userId);
        // Null roles mean the user is gone or not active.
        var active = user is not null && (isActive is null || isActive(user));
        // An app that did not opt in has no reader, so it reads nothing from the consent tables.
        var pending = active && services.GetService<IPendingConsentReader>() is { } reader
            ? await reader.GetPendingAsync(user!.Id, context.HttpContext.RequestAborted)
            : [];
        var check = new UserCheck(active ? [.. await users.GetRolesAsync(user!)] : null, user?.SecurityStamp is { } stamp ? StampClaim.Hash(stamp) : null,
            user?.EmailConfirmed ?? false, pending);
        cache.Set(key, check, cacheFor);
        return check;
    }

    private static async Task CheckUserAsync<TUser>(TokenValidatedContext context, Func<TUser, bool>? isActive, TimeSpan cacheFor)
        where TUser : IdentityUser
    {
        var userId = context.Principal?.GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            context.Fail("The token has no user id.");
            return;
        }

        var services = context.HttpContext.RequestServices;
        var cache = services.GetRequiredService<IMemoryCache>();
        var key = UserCheckKey(userId);
        var loadedNow = false;
        if (!cache.TryGetValue(key, out UserCheck? check) || check is null)
        {
            check = await LoadUserCheckAsync(context, isActive, cache, key, cacheFor, userId);
            loadedNow = true;
        }
        if (check.Roles is not { } roles)
        {
            context.Fail("The user no longer exists or is not active.");
            return;
        }

        // Only an app that opted in to sign-up compares stamps. A token with no stamp claim was issued before the
        // opt-in, so it stays valid until it expires and does not sign every user out at deploy.
        if (services.GetService<SelfRegistrationRegistered>() is not null
            && context.Principal?.FindFirstValue(StampClaim.Type) is { } tokenStamp
            && !string.Equals(tokenStamp, check.StampHash, StringComparison.Ordinal))
        {
            // The cached stamp may be older than the token, for example when another instance served the verify or the reset.
            // The user is read once more and the cache entry replaced, at most once per user in each StampRefreshInterval,
            // so a flood of tokens with a wrong stamp cannot cause a read per request.
            if (!loadedNow && StampRefreshAllowed(services, cache, userId, cacheFor))
            {
                check = await LoadUserCheckAsync(context, isActive, cache, key, cacheFor, userId);
                if (check.Roles is null)
                {
                    context.Fail("The user no longer exists or is not active.");
                    return;
                }
            }
            if (!string.Equals(tokenStamp, check.StampHash, StringComparison.Ordinal))
            {
                context.Fail("The security stamp of the user has changed since the token was issued.");
                return;
            }
        }

        // The roles in the token were correct when it was issued. The current roles replace them.
        if (context.Principal?.Identity is ClaimsIdentity identity)
        {
            foreach (var claim in identity.FindAll(identity.RoleClaimType).ToList())
                identity.RemoveClaim(claim);
            foreach (var role in roles)
                identity.AddClaim(new Claim(identity.RoleClaimType, role));

            // The sign-up facts travel the same way. Only an app that opted in has them.
            if (services.GetService<SelfRegistrationRegistered>() is not null)
            {
                foreach (var claim in identity.FindAll(c => c.Type is AppKitClaims.EmailVerifiedClaim or AppKitClaims.PendingConsentClaim or AppKitClaims.PendingCheckedClaim).ToList())
                    identity.RemoveClaim(claim);
                identity.AddClaim(new Claim(AppKitClaims.EmailVerifiedClaim, check.EmailVerified ? "true" : "false"));
                identity.AddClaim(new Claim(AppKitClaims.PendingCheckedClaim, "true"));
                foreach (var pending in check.Pending)
                    identity.AddClaim(new Claim(AppKitClaims.PendingConsentClaim, AppKitClaims.PendingToClaim(pending)));
            }
        }
    }
}

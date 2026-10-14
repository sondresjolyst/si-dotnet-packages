using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public class IdentityTestUser : IdentityUser, IPasswordResetUser, IAppKitAccount
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public string? PasswordResetCodeHash { get; set; }
    public DateTime? PasswordResetCodeExpiration { get; set; }
    public int PasswordResetAttempts { get; set; }
}

public class IdentityTestDbContext(DbContextOptions<IdentityTestDbContext> options)
    : IdentityDbContext<IdentityTestUser>(options), IAuthDbContext
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.AddAppKitRefreshTokens<IdentityTestUser>();
    }
}

public sealed class IdentityAndBearerTests : IAsyncDisposable
{
    private const string Key = "bearer-test-signing-key-0123456789abcdef";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private IHost? _host;

    public IdentityAndBearerTests() => _connection.Open();

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await _connection.DisposeAsync();
    }

    private async Task<HttpClient> StartAsync(Func<IdentityTestUser, bool>? isActive = null, Action<AppKitJwtBearerOptions>? configure = null,
        bool bearerFirst = false, string issuer = "appkit")
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .UseEnvironment("Production")
                .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = Key,
                    ["Jwt:Issuer"] = issuer,
                }))
                .ConfigureServices((context, s) =>
                {
                    s.AddDbContext<IdentityTestDbContext>(o => o.UseSqlite(_connection));
                    s.AddAppKitAuth<IdentityTestDbContext>(context.Configuration.GetSection("Jwt"));
                    if (bearerFirst)
                        s.AddAppKitJwtBearer(isActive, configure);
                    s.AddAppKitIdentity<IdentityTestUser, IdentityTestDbContext>();
                    if (!bearerFirst)
                        s.AddAppKitJwtBearer(isActive, configure);
                    s.AddAuthorization();
                    s.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e =>
                    {
                        e.MapGet("/me", (ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)).RequireAuthorization();
                        e.MapGet("/hubs/devices", (ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)).RequireAuthorization();
                    });
                }))
            .Build();
        using (var scope = _host.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>().Database.EnsureCreated();
        await _host.StartAsync();
        return _host.GetTestClient();
    }

    private async Task<string> TokenForNewUserAsync(string id, bool deleted = false)
    {
        using var scope = _host!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        var user = new IdentityTestUser { Id = id, UserName = id, Email = $"{id}@example.invalid", IsDeleted = deleted };
        Assert.True((await users.CreateAsync(user)).Succeeded);
        return scope.ServiceProvider.GetRequiredService<IJwtTokenService>().Build(user, []);
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client.SendAsync(request);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_token_for_an_existing_user_is_accepted_whichever_is_registered_first(bool bearerFirst)
    {
        var client = await StartAsync(bearerFirst: bearerFirst);
        var token = await TokenForNewUserAsync("u1");

        var response = await GetAsync(client, "/me", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("u1", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_token_gets_401_not_a_login_redirect(bool bearerFirst)
    {
        var client = await StartAsync(bearerFirst: bearerFirst);

        var response = await GetAsync(client, "/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_for_a_user_that_is_gone_is_refused()
    {
        var client = await StartAsync();
        string token;
        using (var scope = _host!.Services.CreateScope())
        {
            token = scope.ServiceProvider.GetRequiredService<IJwtTokenService>()
                .Build(new IdentityTestUser { Id = "ghost", UserName = "ghost" }, []);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/me", token)).StatusCode);
    }

    [Fact]
    public async Task A_signed_token_without_a_user_id_is_refused()
    {
        var client = await StartAsync();
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var token = handler.WriteToken(handler.CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("name", "nobody")]),
            Issuer = "appkit",
            Audience = "appkit",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(Key)),
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256Signature),
        }));

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/hubs/devices", token)).StatusCode);
    }

    [Fact]
    public async Task On_a_listed_path_the_header_still_works_without_a_query_token()
    {
        var client = await StartAsync(configure: o => o.QueryTokenPaths.Add("/hubs"));
        var token = await TokenForNewUserAsync("u1");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/hubs/devices", token)).StatusCode);
    }

    [Fact]
    public async Task A_token_for_an_inactive_user_is_refused()
    {
        var client = await StartAsync(isActive: u => !u.IsDeleted);
        var token = await TokenForNewUserAsync("deleted", deleted: true);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/me", token)).StatusCode);
    }

    [Fact]
    public async Task Deleting_an_account_stops_its_token_once_the_cached_answer_expires()
    {
        var client = await StartAsync(isActive: u => !u.IsDeleted, configure: o => o.UserCheckCacheDuration = TimeSpan.FromMilliseconds(300));
        var token = await TokenForNewUserAsync("u1");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/me", token)).StatusCode);

        using (var scope = _host!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>();
            (await db.Users.SingleAsync(u => u.Id == "u1")).IsDeleted = true;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/me", token)).StatusCode);
        await Task.Delay(400);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/me", token)).StatusCode);
    }

    [Fact]
    public async Task Each_user_is_checked_on_its_own()
    {
        var client = await StartAsync(isActive: u => !u.IsDeleted);
        var deleted = await TokenForNewUserAsync("gone", deleted: true);
        var active = await TokenForNewUserAsync("here");

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/me", deleted)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/me", active)).StatusCode);
    }

    [Fact]
    public async Task The_cache_lasts_a_minute_by_default()
    {
        await StartAsync();

        Assert.Equal(TimeSpan.FromMinutes(1), new AppKitJwtBearerOptions().UserCheckCacheDuration);
    }

    [Fact]
    public async Task A_token_from_another_issuer_is_refused()
    {
        var client = await StartAsync();
        var token = await TokenForNewUserAsync("u1");
        await _host!.StopAsync();
        _host.Dispose();
        _host = null;
        var other = await StartAsync(issuer: "someone-else");

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(other, "/me", token)).StatusCode);
    }

    [Fact]
    public async Task The_query_token_works_only_on_the_listed_paths()
    {
        var client = await StartAsync(configure: o => o.QueryTokenPaths.Add("/hubs"));
        var token = await TokenForNewUserAsync("u1");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, $"/hubs/devices?access_token={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, $"/me?access_token={token}")).StatusCode);
    }

    [Fact]
    public async Task Without_listed_paths_the_query_token_is_ignored()
    {
        var client = await StartAsync();
        var token = await TokenForNewUserAsync("u1");

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, $"/hubs/devices?access_token={token}")).StatusCode);
    }

    [Fact]
    public async Task The_validation_matches_what_the_token_service_writes()
    {
        await StartAsync();
        var bearer = _host!.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
        var parameters = bearer.TokenValidationParameters;

        Assert.Equal(("appkit", "appkit"), (parameters.ValidIssuer, parameters.ValidAudience));
        Assert.True(parameters.ValidateIssuer && parameters.ValidateAudience && parameters.ValidateLifetime && parameters.ValidateIssuerSigningKey);
        Assert.Equal(TimeSpan.FromSeconds(30), parameters.ClockSkew);
        Assert.True(bearer.RequireHttpsMetadata);
    }

    [Fact]
    public void Identity_gets_the_shared_rules_and_the_override_runs_last()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDbContext<IdentityTestDbContext>(o => o.UseSqlite(_connection));
        services.AddAppKitIdentity<IdentityTestUser, IdentityTestDbContext>(o => o.Password.RequiredLength = 12);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<IdentityOptions>>().Value;
        Assert.Equal(5, options.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(30), options.Lockout.DefaultLockoutTimeSpan);
        Assert.True(options.Lockout.AllowedForNewUsers);
        Assert.Equal(12, options.Password.RequiredLength);
        Assert.True(options.Password.RequireDigit && options.Password.RequireLowercase && options.Password.RequireUppercase);
        Assert.False(options.Password.RequireNonAlphanumeric);
        Assert.True(options.User.RequireUniqueEmail);
        Assert.NotNull(provider.CreateScope().ServiceProvider.GetService<IUserStore<IdentityTestUser>>());
        Assert.NotNull(provider.CreateScope().ServiceProvider.GetService<RoleManager<IdentityRole>>());
    }

    [Fact]
    public async Task Identity_can_issue_password_reset_tokens()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDbContext<IdentityTestDbContext>(o => o.UseSqlite(_connection));
        services.AddAppKitIdentity<IdentityTestUser, IdentityTestDbContext>();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>().Database.EnsureCreated();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        var user = new IdentityTestUser { UserName = "u1", Email = "u1@example.invalid" };
        await users.CreateAsync(user);

        var token = await users.GeneratePasswordResetTokenAsync(user);

        Assert.True((await users.ResetPasswordAsync(user, token, "NewPassword1")).Succeeded);
    }

    [Fact]
    public void Identity_defaults_to_a_minimum_of_8_characters()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDbContext<IdentityTestDbContext>(o => o.UseSqlite(_connection));
        services.AddAppKitIdentity<IdentityTestUser, IdentityTestDbContext>();
        using var provider = services.BuildServiceProvider();

        Assert.Equal(8, provider.GetRequiredService<IOptions<IdentityOptions>>().Value.Password.RequiredLength);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public sealed class ResetConfirmsEmailTests : IAsyncDisposable
{
    private sealed class ResetEmails : IAuthEmails<IdentityTestUser>
    {
        public List<(string UserId, string Code)> Resets { get; } = [];

        public Task SendPasswordResetAsync(IdentityTestUser user, string code, CancellationToken cancellationToken)
        {
            Resets.Add((user.Id, code));
            return Task.CompletedTask;
        }

        public Task SendInviteAsync(IdentityTestUser user, string code, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingVerifiedHandler(List<(string Id, bool Confirmed)> handled) : IEmailVerifiedHandler<IdentityTestUser>
    {
        public Task OnEmailVerifiedAsync(IdentityTestUser user, CancellationToken cancellationToken)
        {
            handled.Add((user.Id, user.EmailConfirmed));
            return Task.CompletedTask;
        }
    }

    private sealed class QuietRegistrationEmails : IRegistrationEmails<IdentityTestUser>
    {
        public Task SendVerificationAsync(IdentityTestUser user, string code, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAlreadyRegisteredAsync(IdentityTestUser user, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendRemovalReminderAsync(IdentityTestUser user, string code, DateTime removalAt, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private const string Key = "reset-test-signing-key-0123456789abcdef";
    private const string Password = "Password1";
    private const string NewPassword = "NewPassword2";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly ResetEmails _emails = new();
    private readonly List<(string Id, bool Confirmed)> _handled = [];
    private IHost? _host;
    private HttpClient _client = default!;

    public ResetConfirmsEmailTests() => _connection.Open();

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await _connection.DisposeAsync();
    }

    private async Task StartAsync(bool optIn)
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .UseEnvironment("Production")
                .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = Key, ["Jwt:Issuer"] = "appkit" }))
                .ConfigureServices((context, s) =>
                {
                    s.AddSingleton<TimeProvider>(_time);
                    s.AddDbContext<RegistrationTestDbContext>(o => o.UseSqlite(_connection));
                    s.AddAppKitIdentity<IdentityTestUser, RegistrationTestDbContext>();
                    s.AddAppKitAuth<RegistrationTestDbContext>(context.Configuration.GetSection("Jwt"));
                    s.AddAppKitJwtBearer<IdentityTestUser>(u => !u.IsDeleted);
                    s.AddAppKitAccounts<IdentityTestUser, RegistrationTestDbContext>();
                    s.AddAuthorization();
                    s.AddRouting();
                    s.AddSingleton<IAuthEmails<IdentityTestUser>>(_emails);
                    s.AddScoped<IEmailVerifiedHandler<IdentityTestUser>>(_ => new RecordingVerifiedHandler(_handled));
                    if (optIn)
                    {
                        s.AddAppKitSelfRegistration<IdentityTestUser, RegistrationTestDbContext>(o =>
                        {
                            o.NewUser = r => new IdentityTestUser { Email = r.Email, UserName = r.UserName };
                            o.RunCleanupInBackground = false;
                        });
                        s.AddSingleton<IRegistrationEmails<IdentityTestUser>>(new QuietRegistrationEmails());
                    }
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e =>
                    {
                        e.MapAppKitAuth<IdentityTestUser, RegistrationTestDbContext>();
                        e.MapGet("/me", () => "ok").RequireAuthorization();
                    });
                }))
            .Build();
        using (var scope = _host.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>().Database.EnsureCreated();
        await _host.StartAsync();
        _client = _host.GetTestClient();
    }

    private async Task SeedAsync(string id, bool verified)
    {
        using var scope = _host!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        var user = new IdentityTestUser { Id = id, UserName = id, Email = $"{id}@example.invalid", EmailConfirmed = verified };
        if (!verified)
            EmailVerificationCodes.Issue(user, _time);
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
    }

    private IdentityTestUser Load(string id)
    {
        using var scope = _host!.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>().Users.AsNoTracking().Single(u => u.Id == id);
    }

    private async Task<(string Token, string RefreshToken)> LoginAsync(string id, string password = Password)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email = $"{id}@example.invalid", password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonElement.Parse(await response.Content.ReadAsStringAsync());
        return (json.GetProperty("token").GetString()!, json.GetProperty("refreshToken").GetString()!);
    }

    private async Task<HttpStatusCode> MeAsync(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await _client.SendAsync(request)).StatusCode;
    }

    private async Task<string> RequestCodeAsync(string id)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/request-password-reset", new { email = $"{id}@example.invalid" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return _emails.Resets.Last(r => r.UserId == id).Code;
    }

    private Task<HttpResponseMessage> ResetAsync(string id, string code) =>
        _client.PostAsJsonAsync("/api/auth/reset-password", new { email = $"{id}@example.invalid", code, newPassword = NewPassword });

    // request-password-reset

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reset_code_can_be_requested_for_an_unverified_account(bool optIn)
    {
        await StartAsync(optIn);
        await SeedAsync("u1", verified: false);

        var code = await RequestCodeAsync("u1");

        Assert.Equal(6, code.Length);
        Assert.NotNull(Load("u1").PasswordResetCodeHash);
        Assert.False(Load("u1").EmailConfirmed);
    }

    // reset-password for an app that opted in

    [Fact]
    public async Task A_reset_on_an_unverified_account_confirms_the_email_runs_the_handlers_and_ends_every_session()
    {
        await StartAsync(optIn: true);
        await SeedAsync("u1", verified: false);
        var (token, refreshToken) = await LoginAsync("u1");
        Assert.Equal(HttpStatusCode.OK, await MeAsync(token));
        var stampBefore = Load("u1").SecurityStamp;
        var code = await RequestCodeAsync("u1");

        var response = await ResetAsync("u1", code);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = Load("u1");
        Assert.True(user.EmailConfirmed);
        Assert.Null(user.EmailVerificationCodeHash);
        Assert.Null(user.EmailVerificationCodeExpiration);
        Assert.Equal(0, user.EmailVerificationAttempts);
        Assert.Null(user.PasswordResetCodeHash);
        Assert.Equal([("u1", true)], _handled);
        Assert.NotEqual(stampBefore, user.SecurityStamp);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(token));
        var refreshed = await _client.PostAsJsonAsync("/api/auth/refresh-token", new { token, refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
        var login = await _client.PostAsJsonAsync("/api/auth/login", new { email = "u1@example.invalid", password = NewPassword });
        Assert.True(JsonElement.Parse(await login.Content.ReadAsStringAsync()).GetProperty("emailVerified").GetBoolean());
    }

    [Fact]
    public async Task A_reset_on_a_verified_account_runs_no_verified_handler_but_still_ends_the_sessions()
    {
        await StartAsync(optIn: true);
        await SeedAsync("u1", verified: true);
        var (token, _) = await LoginAsync("u1");
        Assert.Equal(HttpStatusCode.OK, await MeAsync(token));
        var code = await RequestCodeAsync("u1");

        Assert.Equal(HttpStatusCode.OK, (await ResetAsync("u1", code)).StatusCode);

        Assert.Empty(_handled);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(token));
    }

    [Fact]
    public async Task A_wrong_code_confirms_nothing()
    {
        await StartAsync(optIn: true);
        await SeedAsync("u1", verified: false);
        await RequestCodeAsync("u1");

        var response = await ResetAsync("u1", "WRONG1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Load("u1").EmailConfirmed);
        Assert.Empty(_handled);
    }

    [Fact]
    public async Task A_password_that_is_refused_confirms_nothing()
    {
        await StartAsync(optIn: true);
        await SeedAsync("u1", verified: false);
        var code = await RequestCodeAsync("u1");

        var response = await _client.PostAsJsonAsync("/api/auth/reset-password", new { email = "u1@example.invalid", code, newPassword = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Load("u1").EmailConfirmed);
        Assert.Empty(_handled);
    }

    // reset-password for an app that did not opt in

    [Fact]
    public async Task An_app_that_did_not_opt_in_resets_the_password_and_leaves_the_email_and_the_access_tokens_as_before()
    {
        await StartAsync(optIn: false);
        await SeedAsync("u1", verified: false);
        var (token, refreshToken) = await LoginAsync("u1");
        var code = await RequestCodeAsync("u1");

        var response = await ResetAsync("u1", code);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = Load("u1");
        Assert.False(user.EmailConfirmed);
        Assert.NotNull(user.EmailVerificationCodeHash);
        Assert.Empty(_handled);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(token));
        var refreshed = await _client.PostAsJsonAsync("/api/auth/refresh-token", new { token, refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/auth/login", new { email = "u1@example.invalid", password = NewPassword })).StatusCode);
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Net;
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
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public sealed class AuthEndpointsTests : IAsyncDisposable
{
    private sealed class FakeEmails : IAuthEmails<IdentityTestUser>
    {
        public List<(string UserId, string Code)> Sent { get; } = [];

        public Task SendPasswordResetAsync(IdentityTestUser user, string code, CancellationToken cancellationToken)
        {
            Sent.Add((user.Id, code));
            return Task.CompletedTask;
        }

        public Task SendInviteAsync(IdentityTestUser user, string code, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private const string Password = "Password1";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeEmails _emails = new();
    private IHost? _host;
    private HttpClient _client = default!;

    public AuthEndpointsTests() => _connection.Open();

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await _connection.DisposeAsync();
    }

    private async Task StartAsync(Action<AppKitAuthEndpointOptions<IdentityTestUser>>? configure = null, string prefix = "/api/auth")
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = "endpoints-test-signing-key-0123456789abcdef",
                    ["Jwt:Issuer"] = "appkit",
                }))
                .ConfigureServices((context, s) =>
                {
                    s.AddDbContext<IdentityTestDbContext>(o => o.UseSqlite(_connection));
                    s.AddAppKitIdentity<IdentityTestUser, IdentityTestDbContext>();
                    s.AddAppKitAuth<IdentityTestDbContext>(context.Configuration.GetSection("Jwt"));
                    s.AddSingleton<IAuthEmails<IdentityTestUser>>(_emails);
                    s.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapAppKitAuth<IdentityTestUser, IdentityTestDbContext>(prefix, configure));
                }))
            .Build();
        using (var scope = _host.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>().Database.EnsureCreated();
        await _host.StartAsync();
        _client = _host.GetTestClient();
    }

    private async Task<IdentityTestUser> UserAsync(string id = "u1", bool deleted = false, string password = Password)
    {
        using var scope = _host!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        var user = new IdentityTestUser { Id = id, UserName = id, Email = $"{id}@example.invalid", IsDeleted = deleted };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        return user;
    }

    private T Db<T>(Func<IdentityTestDbContext, T> read)
    {
        using var scope = _host!.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>());
    }

    private Task<HttpResponseMessage> PostAsync(string path, object body) => _client.PostAsJsonAsync($"/api/auth/{path}", body);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> MessageAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("message").GetString()!;

    private static async Task<string[]> ErrorFieldsAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name).Order().ToArray();

    private async Task<(string Token, string RefreshToken)> LoginAsync(string id = "u1", string password = Password)
    {
        var json = await JsonAsync(await PostAsync("login", new { email = $"{id}@example.invalid", password }));
        return (json.GetProperty("token").GetString()!, json.GetProperty("refreshToken").GetString()!);
    }

    // Login

    [Fact]
    public async Task Login_gives_a_token_for_the_user_and_saves_a_refresh_token()
    {
        await StartAsync();
        await UserAsync();

        var response = await PostAsync("login", new { email = "u1@example.invalid", password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(json.GetProperty("token").GetString());
        Assert.Equal("u1", jwt.Subject);
        Assert.False(string.IsNullOrEmpty(json.GetProperty("refreshToken").GetString()));
        Assert.Equal(1, Db(db => db.RefreshTokens.Count(t => t.UserId == "u1" && t.Revoked == null)));
    }

    [Fact]
    public async Task Login_with_a_wrong_password_is_refused_and_counted()
    {
        await StartAsync();
        await UserAsync();

        var response = await PostAsync("login", new { email = "u1@example.invalid", password = "Wrong1234" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Invalid email or password.", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.InvalidCredentials, await ErrorJson.CodeAsync(response));
        Assert.Equal(1, Db(db => db.Users.Single().AccessFailedCount));
        Assert.Equal(0, Db(db => db.RefreshTokens.Count()));
    }

    [Fact]
    public async Task Login_for_an_unknown_email_gets_the_same_answer()
    {
        await StartAsync();

        var response = await PostAsync("login", new { email = "ghost@example.invalid", password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Invalid email or password.", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.InvalidCredentials, await ErrorJson.CodeAsync(response));
    }

    [Fact]
    public async Task Login_for_an_inactive_user_gets_the_same_answer_and_tries_no_password()
    {
        await StartAsync(o => o.IsActive = u => !u.IsDeleted);
        await UserAsync(deleted: true);

        var response = await PostAsync("login", new { email = "u1@example.invalid", password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Invalid email or password.", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.InvalidCredentials, await ErrorJson.CodeAsync(response));
        Assert.Equal(0, Db(db => db.Users.Single().AccessFailedCount));
    }

    [Fact]
    public async Task Login_locks_after_five_wrong_passwords_even_for_the_right_one()
    {
        await StartAsync();
        await UserAsync();
        for (var i = 0; i < 5; i++)
            await PostAsync("login", new { email = "u1@example.invalid", password = "Wrong1234" });

        var response = await PostAsync("login", new { email = "u1@example.invalid", password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Too many failed attempts. This login is temporarily locked, try again later.", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.LockedOut, await ErrorJson.CodeAsync(response));
    }

    [Theory]
    [InlineData(null, null, "Email=required", "Password=required")]
    [InlineData(" ", "x", "Email=required")]
    [InlineData("not-an-email", "", "Email=invalidEmail", "Password=required")]
    public async Task Login_field_errors_carry_a_code_for_each_message(string? email, string? password, params string[] codes)
    {
        await StartAsync();

        var response = await PostAsync("login", new { email, password });

        Assert.Equal(codes, await ErrorJson.FieldCodesAsync(response));
        Assert.Empty(await ErrorJson.LimitsAsync(response));
    }

    [Fact]
    public async Task Field_errors_keep_their_English_messages()
    {
        await StartAsync();

        var response = await PostAsync("login", new { email = "nope" });

        Assert.Equal(["Not a valid email address."], await ErrorJson.MessagesAsync(response, "Email"));
        Assert.Equal(["Required."], await ErrorJson.MessagesAsync(response, "Password"));
    }

    [Theory]
    [InlineData(null, null, "Email,Password")]
    [InlineData("", " ", "Email,Password")]
    [InlineData("not-an-email", "x", "Email")]
    [InlineData("@example.invalid", "x", "Email")]
    [InlineData("u1@", "x", "Email")]
    [InlineData("a@b@c", "x", "Email")]
    public async Task Login_with_missing_or_malformed_fields_is_a_validation_problem(string? email, string? password, string fields)
    {
        await StartAsync();

        var response = await PostAsync("login", new { email, password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(fields.Split(','), await ErrorFieldsAsync(response));
    }

    [Fact]
    public async Task The_prefix_can_be_changed()
    {
        await StartAsync(prefix: "/auth");
        await UserAsync();

        var response = await _client.PostAsJsonAsync("/auth/login", new { email = "u1@example.invalid", password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Refresh

    [Fact]
    public async Task Refresh_rotates_the_refresh_token()
    {
        await StartAsync();
        await UserAsync();
        var (token, refreshToken) = await LoginAsync();

        var response = await PostAsync("refresh-token", new { token, refreshToken });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.NotEqual(refreshToken, json.GetProperty("refreshToken").GetString());
        Assert.Equal("u1", new JwtSecurityTokenHandler().ReadJwtToken(json.GetProperty("token").GetString()).Subject);
        Assert.Equal(1, Db(db => db.RefreshTokens.Count(t => t.Revoked != null)));
    }

    [Fact]
    public async Task Refresh_with_a_token_this_app_did_not_sign_is_a_bad_request()
    {
        await StartAsync();

        var response = await PostAsync("refresh-token", new { token = "not.a.jwt", refreshToken = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid token", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.InvalidToken, await ErrorJson.CodeAsync(response));
    }

    [Fact]
    public async Task Refresh_for_a_user_that_is_gone_or_inactive_is_refused()
    {
        await StartAsync(o => o.IsActive = u => !u.IsDeleted);
        await UserAsync();
        var (token, refreshToken) = await LoginAsync();
        Db(db => { db.Users.Single().IsDeleted = true; return db.SaveChanges(); });

        var response = await PostAsync("refresh-token", new { token, refreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("User not found", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.UserNotFound, await ErrorJson.CodeAsync(response));
    }

    [Fact]
    public async Task Refresh_with_a_wrong_refresh_token_is_refused()
    {
        await StartAsync();
        await UserAsync();
        var (token, _) = await LoginAsync();

        var response = await PostAsync("refresh-token", new { token, refreshToken = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Invalid or expired refresh token", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.InvalidRefreshToken, await ErrorJson.CodeAsync(response));
    }

    [Fact]
    public async Task Refresh_with_a_used_token_ends_every_session()
    {
        await StartAsync();
        await UserAsync();
        var (token, refreshToken) = await LoginAsync();
        await PostAsync("refresh-token", new { token, refreshToken });

        var replay = await PostAsync("refresh-token", new { token, refreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(0, Db(db => db.RefreshTokens.Count(t => t.Revoked == null)));
    }

    [Fact]
    public async Task Refresh_with_missing_fields_is_a_validation_problem()
    {
        await StartAsync();

        var response = await PostAsync("refresh-token", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["RefreshToken", "Token"], await ErrorFieldsAsync(response));
        Assert.Equal(["RefreshToken=required", "Token=required"], await ErrorJson.FieldCodesAsync(response));
    }

    // Logout

    [Fact]
    public async Task Logout_deletes_the_refresh_token()
    {
        await StartAsync();
        await UserAsync();
        var (_, refreshToken) = await LoginAsync();

        var response = await PostAsync("logout", new { refreshToken });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, Db(db => db.RefreshTokens.Count()));
    }

    [Fact]
    public async Task Logout_with_an_unknown_token_answers_the_same()
    {
        await StartAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync("logout", new { refreshToken = "unknown" })).StatusCode);
    }

    [Fact]
    public async Task Logout_without_a_token_is_a_validation_problem()
    {
        await StartAsync();

        var response = await PostAsync("logout", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["RefreshToken"], await ErrorFieldsAsync(response));
        Assert.Equal(["RefreshToken=required"], await ErrorJson.FieldCodesAsync(response));
    }

    // Password reset

    [Fact]
    public async Task Requesting_a_reset_emails_a_code_and_stores_only_its_hash()
    {
        await StartAsync();
        await UserAsync();

        var response = await PostAsync("request-password-reset", new { email = "u1@example.invalid" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("If this email is registered, a reset code has been sent.", await MessageAsync(response));
        var (userId, code) = Assert.Single(_emails.Sent);
        Assert.Equal("u1", userId);
        Assert.Equal(VerificationCodes.Hash(code), Db(db => db.Users.Single().PasswordResetCodeHash));
    }

    [Fact]
    public async Task Requesting_a_reset_for_an_unknown_or_inactive_user_answers_the_same_and_sends_nothing()
    {
        await StartAsync(o => o.IsActive = u => !u.IsDeleted);
        await UserAsync(deleted: true);

        var unknown = await PostAsync("request-password-reset", new { email = "ghost@example.invalid" });
        var inactive = await PostAsync("request-password-reset", new { email = "u1@example.invalid" });

        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, inactive.StatusCode);
        Assert.Equal(await MessageAsync(unknown), await MessageAsync(inactive));
        Assert.Empty(_emails.Sent);
        Assert.Null(Db(db => db.Users.Single().PasswordResetCodeHash));
    }

    [Fact]
    public async Task Requesting_a_reset_with_a_malformed_email_is_a_validation_problem()
    {
        await StartAsync();

        var response = await PostAsync("request-password-reset", new { email = "nope" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["Email=invalidEmail"], await ErrorJson.FieldCodesAsync(response));
    }

    private async Task<string> CodeForAsync(string id = "u1")
    {
        await PostAsync("request-password-reset", new { email = $"{id}@example.invalid" });
        return _emails.Sent.Last().Code;
    }

    [Fact]
    public async Task Resetting_with_the_code_sets_the_password_and_ends_every_session()
    {
        await StartAsync();
        await UserAsync();
        await LoginAsync();
        await LoginAsync();
        var code = await CodeForAsync();

        var response = await PostAsync("reset-password", new { email = "u1@example.invalid", code, newPassword = "NewPassword2" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Password reset successfully.", await MessageAsync(response));
        Assert.Equal(0, Db(db => db.RefreshTokens.Count(t => t.Revoked == null)));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("login", new { email = "u1@example.invalid", password = "NewPassword2" })).StatusCode);
        Assert.Null(Db(db => db.Users.Single().PasswordResetCodeHash));
    }

    [Fact]
    public async Task A_reset_lifts_the_lockout_from_failed_logins()
    {
        await StartAsync();
        await UserAsync();
        for (var i = 0; i < 5; i++)
            await PostAsync("login", new { email = "u1@example.invalid", password = "Wrong1234" });
        var code = await CodeForAsync();

        await PostAsync("reset-password", new { email = "u1@example.invalid", code, newPassword = "NewPassword2" });

        Assert.Equal(HttpStatusCode.OK, (await PostAsync("login", new { email = "u1@example.invalid", password = "NewPassword2" })).StatusCode);
    }

    [Fact]
    public async Task A_code_works_only_once()
    {
        await StartAsync();
        await UserAsync();
        var code = await CodeForAsync();
        await PostAsync("reset-password", new { email = "u1@example.invalid", code, newPassword = "NewPassword2" });

        var again = await PostAsync("reset-password", new { email = "u1@example.invalid", code, newPassword = "NewPassword3" });

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_counted()
    {
        await StartAsync();
        await UserAsync();
        await CodeForAsync();

        var response = await PostAsync("reset-password", new { email = "u1@example.invalid", code = "WRONG1", newPassword = "NewPassword2" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid or expired reset code.", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.InvalidResetCode, await ErrorJson.CodeAsync(response));
        Assert.Equal(1, Db(db => db.Users.Single().PasswordResetAttempts));
    }

    [Fact]
    public async Task After_five_wrong_codes_even_the_right_one_is_refused()
    {
        await StartAsync();
        await UserAsync();
        var code = await CodeForAsync();
        for (var i = 0; i < 5; i++)
            await PostAsync("reset-password", new { email = "u1@example.invalid", code = "WRONG1", newPassword = "NewPassword2" });

        var response = await PostAsync("reset-password", new { email = "u1@example.invalid", code, newPassword = "NewPassword2" });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("Too many attempts. Request a new reset code.", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.TooManyResetAttempts, await ErrorJson.CodeAsync(response));
    }

    [Fact]
    public async Task A_weak_password_is_a_validation_problem_and_keeps_the_code_and_sessions()
    {
        await StartAsync();
        await UserAsync();
        await LoginAsync();
        var code = await CodeForAsync();

        var weak = await PostAsync("reset-password", new { email = "u1@example.invalid", code, newPassword = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal(["NewPassword=passwordTooShort,passwordRequiresDigit,passwordRequiresUpper"], await ErrorJson.FieldCodesAsync(weak));
        Assert.Equal(["NewPassword=8"], await ErrorJson.LimitsAsync(weak));
        Assert.Contains("Passwords must be at least 8 characters.", await ErrorJson.MessagesAsync(weak, "NewPassword"));
        Assert.Equal(1, Db(db => db.RefreshTokens.Count(t => t.Revoked == null)));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("reset-password", new { email = "u1@example.invalid", code, newPassword = "NewPassword2" })).StatusCode);
    }

    [Fact]
    public async Task Resetting_for_an_unknown_or_inactive_user_is_refused_like_a_wrong_code()
    {
        await StartAsync(o => o.IsActive = u => !u.IsDeleted);
        await UserAsync();
        var code = await CodeForAsync();
        Db(db => { db.Users.Single().IsDeleted = true; return db.SaveChanges(); });

        var inactive = await PostAsync("reset-password", new { email = "u1@example.invalid", code, newPassword = "NewPassword2" });
        var unknown = await PostAsync("reset-password", new { email = "ghost@example.invalid", code, newPassword = "NewPassword2" });

        Assert.Equal(HttpStatusCode.BadRequest, inactive.StatusCode);
        Assert.Equal("Invalid or expired reset code.", await MessageAsync(inactive));
        Assert.Equal(AuthErrorCodes.InvalidResetCode, await ErrorJson.CodeAsync(inactive));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("Invalid or expired reset code.", await MessageAsync(unknown));
        Assert.Equal(AuthErrorCodes.InvalidResetCode, await ErrorJson.CodeAsync(unknown));
    }

    [Fact]
    public async Task A_password_without_a_lowercase_letter_has_its_own_code_and_no_limit()
    {
        await StartAsync();
        await UserAsync();
        var code = await CodeForAsync();

        var weak = await PostAsync("reset-password", new { email = "u1@example.invalid", code, newPassword = "PASSWORD12" });

        Assert.Equal(["NewPassword=passwordRequiresLower"], await ErrorJson.FieldCodesAsync(weak));
        Assert.Empty(await ErrorJson.LimitsAsync(weak));
    }

    [Fact]
    public async Task Resetting_with_missing_fields_is_a_validation_problem()
    {
        await StartAsync();

        var response = await PostAsync("reset-password", new { email = "u1@example.invalid" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["Code=required", "NewPassword=required"], await ErrorJson.FieldCodesAsync(response));
    }

    [Fact]
    public async Task Every_endpoint_is_anonymous_and_a_post()
    {
        await StartAsync();

        foreach (var path in new[] { "login", "refresh-token", "logout", "request-password-reset", "reset-password" })
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _client.GetAsync($"/api/auth/{path}")).StatusCode);
    }
}

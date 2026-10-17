using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public sealed class AccountTests : IAsyncDisposable
{
    /// <summary>Records what it erased, and fails when asked to, inside the deletion transaction.</summary>
    private sealed class TestEraser(IdentityTestDbContext db, AccountTests test) : IAccountDataEraser<IdentityTestUser>
    {
        public async Task EraseAsync(IdentityTestUser user, CancellationToken cancellationToken)
        {
            test.Erased.Add(user.Id);
            // A change of the app's own, which must commit or roll back with the rest.
            db.RefreshTokens.Add(new RefreshToken { Token = $"erased-{user.Id}", UserId = user.Id, Created = DateTime.UtcNow, Expires = DateTime.UtcNow.AddDays(1), Revoked = DateTime.UtcNow });
            await Task.CompletedTask;
            if (test.FailErasing)
                throw new InvalidOperationException("eraser failed");
            if (test.CollideErasing)
                throw new Microsoft.EntityFrameworkCore.DbUpdateException("save failed", new FakeDbException("40001"));
        }
    }

    private sealed class TestHandler(AccountTests test) : IAccountDeletedHandler<IdentityTestUser>
    {
        public Task OnDeletedAsync(IdentityTestUser user, CancellationToken cancellationToken)
        {
            test.Handled.Add((user.Id, user.IsDeleted));
            return Task.CompletedTask;
        }
    }

    private sealed class TestExport : IAccountDataExporter<IdentityTestUser>
    {
        public Task<object?> ExportAsync(IdentityTestUser user, CancellationToken cancellationToken) =>
            Task.FromResult<object?>(new { garages = 2, owner = user.Id });
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeLogCollector _logs = new();
    private IHost? _host;
    private HttpClient _client = default!;
    internal List<string> Erased { get; } = [];
    internal List<(string Id, bool WasDeleted)> Handled { get; } = [];
    internal bool FailErasing { get; set; }
    internal bool CollideErasing { get; set; }

    private sealed class FakeDbException(string sqlState) : System.Data.Common.DbException("db failed")
    {
        public override string SqlState => sqlState;
    }

    public AccountTests() => _connection.Open();

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await _connection.DisposeAsync();
    }

    /// <summary>Refuses every user, so the update at the end of a deletion fails.</summary>
    private sealed class RefusingValidator : IUserValidator<IdentityTestUser>
    {
        public Task<IdentityResult> ValidateAsync(UserManager<IdentityTestUser> manager, IdentityTestUser user) =>
            Task.FromResult(user.IsDeleted ? IdentityResult.Failed(new IdentityError { Code = "No", Description = "Refused." }) : IdentityResult.Success);
    }

    private bool _refuseUpdates;

    private async Task StartAsync(bool withExport = false, bool withHooks = true)
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddLogging(b => b.AddProvider(new FakeLoggerProvider(_logs)));
                    s.AddDbContext<IdentityTestDbContext>(o => o.UseSqlite(_connection));
                    s.AddAppKitIdentity<IdentityTestUser, IdentityTestDbContext>();
                    s.AddAppKitAccounts<IdentityTestUser, IdentityTestDbContext>();
                    if (_refuseUpdates)
                        s.AddScoped<IUserValidator<IdentityTestUser>, RefusingValidator>();
                    if (withHooks)
                    {
                        s.AddScoped<IAccountDataEraser<IdentityTestUser>>(sp => new TestEraser(sp.GetRequiredService<IdentityTestDbContext>(), this));
                        s.AddScoped<IAccountDeletedHandler<IdentityTestUser>>(_ => new TestHandler(this));
                    }
                    if (withExport)
                        s.AddScoped<IAccountDataExporter<IdentityTestUser>, TestExport>();
                    s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    s.PostConfigure<AuthenticationOptions>(o =>
                    {
                        o.DefaultScheme = "Test";
                        o.DefaultAuthenticateScheme = "Test";
                        o.DefaultChallengeScheme = "Test";
                        o.DefaultForbidScheme = "Test";
                    });
                    s.AddAuthorization();
                    s.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapAppKitAccount<IdentityTestUser>());
                }))
            .Build();
        using (var scope = _host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>().Database.EnsureCreated();
            await scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>().CreateAsync(new IdentityRole("Admin"));
        }
        await _host.StartAsync();
        _client = _host.GetTestClient();
    }

    private async Task UserAsync(string id, bool admin = false, bool deleted = false, string? password = "Password1")
    {
        using var scope = _host!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        var user = new IdentityTestUser
        {
            Id = id, UserName = id, Email = $"{id}@example.invalid", FirstName = "Kari", LastName = "Nordmann", IsDeleted = deleted,
            EmailConfirmed = true, PhoneNumber = "+4712345678", PhoneNumberConfirmed = true, TwoFactorEnabled = true,
            LockoutEnabled = false, AccessFailedCount = 3,
            PasswordResetCodeHash = "h", PasswordResetCodeExpiration = DateTime.UtcNow.AddHours(1), PasswordResetAttempts = 2,
            EmailVerificationCodeHash = "v", EmailVerificationCodeExpiration = DateTime.UtcNow.AddHours(1), EmailVerificationAttempts = 1,
        };
        Assert.True((password is null ? await users.CreateAsync(user) : await users.CreateAsync(user, password)).Succeeded);
        // Identity turns lockout on for new users, so it is turned off again to see the deleter turn it on.
        await users.SetLockoutEnabledAsync(user, false);
        if (admin)
            await users.AddToRoleAsync(user, "Admin");
        var db = scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>();
        db.RefreshTokens.Add(new RefreshToken { Token = $"live-{id}", UserId = id, Created = DateTime.UtcNow, Expires = DateTime.UtcNow.AddDays(30) });
        await db.SaveChangesAsync();
    }

    private IdentityTestUser Load(string id)
    {
        using var scope = _host!.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>().Users.AsNoTracking().Single(u => u.Id == id);
    }

    private int Tokens(string id)
    {
        using var scope = _host!.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>().RefreshTokens.Count(t => t.UserId == id);
    }

    private async Task DeleteDirectAsync(string id)
    {
        using var scope = _host!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        await scope.ServiceProvider.GetRequiredService<IAccountDeleter<IdentityTestUser>>().DeleteAsync((await users.FindByIdAsync(id))!);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string caller = "u1", string roles = "")
    {
        var request = new HttpRequestMessage(method, path);
        if (caller.Length > 0)
        {
            request.Headers.Add("X-Test-User", caller);
            request.Headers.Add("X-Test-Roles", roles);
        }
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    // The deleter

    [Fact]
    public async Task Deleting_scrubs_the_person_and_keeps_the_row()
    {
        await StartAsync();
        await UserAsync("u1");
        var stampBefore = Load("u1").SecurityStamp;

        await DeleteDirectAsync("u1");

        var u = Load("u1");
        Assert.Equal(("Deleted", "User"), (u.FirstName, u.LastName));
        Assert.Equal(("deleted-u1@deleted.invalid", "DELETED-U1@DELETED.INVALID"), (u.Email, u.NormalizedEmail));
        Assert.Equal(("deleted-u1", "DELETED-U1"), (u.UserName, u.NormalizedUserName));
        Assert.False(u.EmailConfirmed);
        Assert.Null(u.PhoneNumber);
        Assert.False(u.PhoneNumberConfirmed);
        Assert.False(u.TwoFactorEnabled);
        Assert.Null(u.PasswordHash);
        Assert.True(u.LockoutEnabled);
        Assert.Equal(DateTimeOffset.MaxValue, u.LockoutEnd);
        Assert.Equal(0, u.AccessFailedCount);
        Assert.Equal((null, null, 0), (u.PasswordResetCodeHash, u.PasswordResetCodeExpiration, u.PasswordResetAttempts));
        Assert.Equal((null, null, 0), (u.EmailVerificationCodeHash, u.EmailVerificationCodeExpiration, u.EmailVerificationAttempts));
        Assert.True(u.IsDeleted);
        Assert.InRange(u.DeletedAt!.Value, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.NotEqual(stampBefore, u.SecurityStamp);
    }

    [Fact]
    public async Task Deleting_removes_the_refresh_tokens_and_commits_the_erasers_work()
    {
        await StartAsync();
        await UserAsync("u1");

        await DeleteDirectAsync("u1");

        Assert.Equal(["u1"], Erased);
        using var scope = _host!.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>().RefreshTokens.Where(t => t.UserId == "u1").Select(t => t.Token).ToList();
        Assert.Equal(["erased-u1"], tokens);
    }

    [Fact]
    public async Task The_handlers_run_after_the_deletion_committed()
    {
        await StartAsync();
        await UserAsync("u1");

        await DeleteDirectAsync("u1");

        Assert.Equal([("u1", true)], Handled);
    }

    [Fact]
    public async Task A_failing_eraser_rolls_everything_back_and_runs_no_handler()
    {
        await StartAsync();
        await UserAsync("u1");
        FailErasing = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => DeleteDirectAsync("u1"));

        var u = Load("u1");
        Assert.False(u.IsDeleted);
        Assert.Equal("u1@example.invalid", u.Email);
        Assert.Equal(1, Tokens("u1"));
        Assert.Empty(Handled);
    }

    [Fact]
    public async Task A_save_Identity_refuses_rolls_everything_back()
    {
        _refuseUpdates = true;
        await StartAsync();
        await UserAsync("u1");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DeleteDirectAsync("u1"));

        Assert.Contains("Refused.", error.Message, StringComparison.Ordinal);
        Assert.False(Load("u1").IsDeleted);
        Assert.Equal(1, Tokens("u1"));
        Assert.Empty(Handled);
    }

    [Fact]
    public async Task A_collision_with_another_change_is_a_conflict_and_rolls_back()
    {
        await StartAsync();
        await UserAsync("u1");
        CollideErasing = true;

        var response = await SendAsync(HttpMethod.Delete, "/api/users/u1/account");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Another change to the accounts happened at the same time. Try again.", (await JsonAsync(response)).GetProperty("message").GetString());
        Assert.Equal(AuthErrorCodes.AccountChangeConflict, await ErrorJson.CodeAsync(response));
        Assert.False(Load("u1").IsDeleted);
        Assert.Empty(Handled);
    }

    [Theory]
    [InlineData("40001", true)]
    [InlineData("40P01", false)]
    [InlineData("23505", false)]
    public void Only_a_serialization_failure_counts_as_a_collision(string sqlState, bool expected)
    {
        Assert.Equal(expected, AccountDeleter<IdentityTestUser, IdentityTestDbContext>.IsSerializationFailure(new FakeDbException(sqlState)));
        Assert.Equal(expected, AccountDeleter<IdentityTestUser, IdentityTestDbContext>.IsSerializationFailure(
            new InvalidOperationException("outer", new Microsoft.EntityFrameworkCore.DbUpdateException("middle", new FakeDbException(sqlState)))));
    }

    [Fact]
    public void Other_failures_are_not_collisions()
    {
        Assert.False(AccountDeleter<IdentityTestUser, IdentityTestDbContext>.IsSerializationFailure(new InvalidOperationException("x")));
        Assert.False(AccountDeleter<IdentityTestUser, IdentityTestDbContext>.IsSerializationFailure(null));
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_deleted()
    {
        await StartAsync();
        await UserAsync("last", admin: true);
        await UserAsync("gone", admin: true, deleted: true);

        await Assert.ThrowsAsync<LastAdminException>(() => DeleteDirectAsync("last"));

        Assert.False(Load("last").IsDeleted);
        Assert.Empty(Erased);
    }

    [Fact]
    public async Task One_of_two_admins_can_be_deleted()
    {
        await StartAsync();
        await UserAsync("a1", admin: true);
        await UserAsync("a2", admin: true);

        await DeleteDirectAsync("a1");

        Assert.True(Load("a1").IsDeleted);
    }

    [Fact]
    public async Task The_admin_role_name_comes_from_the_options()
    {
        var services = new ServiceCollection();
        services.AddAppKitAccounts<IdentityTestUser, IdentityTestDbContext>(o => o.AdminRole = "Owner");

        using var provider = services.BuildServiceProvider();
        Assert.Equal("Owner", provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppKitAccountOptions>>().Value.AdminRole);
    }

    [Fact]
    public async Task Deleting_forgets_the_bearer_check_so_a_token_stops_at_once()
    {
        await StartAsync();
        await UserAsync("u1");
        var cache = _host!.Services.GetRequiredService<IMemoryCache>();
        cache.Set("SjolystInnovation.AppKit.Auth:user-check:u1", (IReadOnlyList<string>)["Admin"]);

        await DeleteDirectAsync("u1");

        Assert.False(cache.TryGetValue("SjolystInnovation.AppKit.Auth:user-check:u1", out _));
    }

    [Fact]
    public async Task Deleting_logs_the_user_id_and_no_address()
    {
        await StartAsync();
        await UserAsync("u1");

        await DeleteDirectAsync("u1");

        var mine = _logs.GetSnapshot().Where(r => r.Category!.StartsWith("SjolystInnovation.AppKit.Auth.AccountDeleter", StringComparison.Ordinal)).ToList();
        Assert.Contains(mine, r => r.Message == "Account u1 deleted.");
        Assert.DoesNotContain(mine, r => r.Message.Contains('@', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deleting_works_without_erasers_or_handlers()
    {
        await StartAsync(withHooks: false);
        await UserAsync("u1");

        await DeleteDirectAsync("u1");

        Assert.True(Load("u1").IsDeleted);
        Assert.Equal(0, Tokens("u1"));
    }

    // The endpoints

    [Fact]
    public async Task Every_route_needs_a_signed_in_user()
    {
        await StartAsync();

        foreach (var (method, path) in new[] { (HttpMethod.Get, "/api/users/u1/profile"), (HttpMethod.Put, "/api/users/u1/profile"),
            (HttpMethod.Put, "/api/users/u1/password"), (HttpMethod.Get, "/api/users/u1/export"), (HttpMethod.Delete, "/api/users/u1/account") })
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(method, path, new { }, caller: "")).StatusCode);
    }

    [Fact]
    public async Task The_user_reads_their_profile()
    {
        await StartAsync();
        await UserAsync("u1");

        var json = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/users/u1/profile"));

        Assert.Equal(("u1", "u1", "Kari", "Nordmann", "u1@example.invalid"), (json.GetProperty("id").GetString(), json.GetProperty("userName").GetString(),
            json.GetProperty("firstName").GetString(), json.GetProperty("lastName").GetString(), json.GetProperty("email").GetString()));
        Assert.True(json.TryGetProperty("createdAt", out _));
    }

    [Fact]
    public async Task Another_user_is_forbidden_and_an_admin_is_let_in()
    {
        await StartAsync();
        await UserAsync("u1");

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/users/u1/profile", caller: "u2")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/users/u1/profile", caller: "boss", roles: "Admin")).StatusCode);
    }

    [Fact]
    public async Task A_missing_or_deleted_account_is_not_found()
    {
        await StartAsync();
        await UserAsync("gone", deleted: true);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/api/users/ghost/profile", caller: "ghost")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/api/users/gone/profile", caller: "gone")).StatusCode);
    }

    [Fact]
    public async Task The_user_updates_their_name()
    {
        await StartAsync();
        await UserAsync("u1");

        var response = await SendAsync(HttpMethod.Put, "/api/users/u1/profile", new { firstName = "Ola", lastName = "Hansen" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ola", (await JsonAsync(response)).GetProperty("firstName").GetString());
        Assert.Equal(("Ola", "Hansen"), (Load("u1").FirstName, Load("u1").LastName));
    }

    [Theory]
    [InlineData(null, "Hansen", "FirstName", "required", null)]
    [InlineData("Ola", " ", "LastName", "required", null)]
    [InlineData("Ola", "x51", "LastName", "tooLong", 50)]
    [InlineData("x51", "Hansen", "FirstName", "tooLong", 50)]
    public async Task A_bad_name_is_a_validation_problem(string? first, string? last, string field, string code, int? limit)
    {
        await StartAsync();
        await UserAsync("u1");

        string? Long(string? v) => v == "x51" ? new string('x', 51) : v;
        var response = await SendAsync(HttpMethod.Put, "/api/users/u1/profile", new { firstName = Long(first), lastName = Long(last) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([field], (await JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name));
        Assert.Equal([$"{field}={code}"], await ErrorJson.FieldCodesAsync(response));
        Assert.Equal(limit is null ? [] : [$"{field}={limit}"], await ErrorJson.LimitsAsync(response));
        Assert.Equal([limit is null ? "Required." : "At most 50 characters."], await ErrorJson.MessagesAsync(response, field));
        Assert.Equal("Kari", Load("u1").FirstName);
    }

    [Fact]
    public async Task Another_user_cannot_update_the_profile()
    {
        await StartAsync();
        await UserAsync("u1");

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, "/api/users/u1/profile", new { firstName = "X", lastName = "Y" }, caller: "u2")).StatusCode);
    }

    [Fact]
    public async Task The_user_changes_their_password()
    {
        await StartAsync();
        await UserAsync("u1");

        var response = await SendAsync(HttpMethod.Put, "/api/users/u1/password", new { currentPassword = "Password1", newPassword = "NewPassword2" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Password changed.", (await JsonAsync(response)).GetProperty("message").GetString());
        using var scope = _host!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        Assert.True(await users.CheckPasswordAsync((await users.FindByIdAsync("u1"))!, "NewPassword2"));
    }

    [Fact]
    public async Task A_wrong_current_password_is_a_validation_problem_on_the_current_one()
    {
        await StartAsync();
        await UserAsync("u1");

        var response = await SendAsync(HttpMethod.Put, "/api/users/u1/password", new { currentPassword = "Wrong1234", newPassword = "NewPassword2" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["CurrentPassword=wrongPassword"], await ErrorJson.FieldCodesAsync(response));
        Assert.Equal(["Incorrect password."], await ErrorJson.MessagesAsync(response, "CurrentPassword"));
    }

    [Fact]
    public async Task A_weak_new_password_is_a_validation_problem_with_a_code_per_rule()
    {
        await StartAsync();
        await UserAsync("u1");

        var response = await SendAsync(HttpMethod.Put, "/api/users/u1/password", new { currentPassword = "Password1", newPassword = "abc" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["NewPassword=passwordTooShort,passwordRequiresDigit,passwordRequiresUpper"], await ErrorJson.FieldCodesAsync(response));
        Assert.Equal(["NewPassword=8"], await ErrorJson.LimitsAsync(response));
    }

    [Fact]
    public async Task Only_the_user_changes_their_password_not_even_an_admin()
    {
        await StartAsync();
        await UserAsync("u1");

        var response = await SendAsync(HttpMethod.Put, "/api/users/u1/password", new { currentPassword = "Password1", newPassword = "NewPassword2" }, caller: "boss", roles: "Admin");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_without_both_fields_is_a_validation_problem()
    {
        await StartAsync();
        await UserAsync("u1");

        var response = await SendAsync(HttpMethod.Put, "/api/users/u1/password", new { });

        Assert.Equal(["CurrentPassword", "NewPassword"], (await JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(["CurrentPassword=required", "NewPassword=required"], await ErrorJson.FieldCodesAsync(response));
    }

    [Fact]
    public async Task The_export_holds_the_account_and_roles_and_no_data_section_by_default()
    {
        await StartAsync();
        await UserAsync("u1", admin: true);

        var json = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/users/u1/export"));

        Assert.Equal("u1@example.invalid", json.GetProperty("account").GetProperty("email").GetString());
        Assert.Equal(["Admin"], json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.True(json.TryGetProperty("exportedAt", out _));
        Assert.False(json.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task The_export_adds_the_apps_data()
    {
        await StartAsync(withExport: true);
        await UserAsync("u1");

        var json = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/users/u1/export"));

        Assert.Equal(2, json.GetProperty("data").GetProperty("garages").GetInt32());
        Assert.Equal("u1", json.GetProperty("data").GetProperty("owner").GetString());
    }

    [Fact]
    public async Task Another_user_cannot_export()
    {
        await StartAsync();
        await UserAsync("u1");

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/users/u1/export", caller: "u2")).StatusCode);
    }

    [Fact]
    public async Task The_user_deletes_their_account()
    {
        await StartAsync();
        await UserAsync("u1");

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/api/users/u1/account")).StatusCode);
        Assert.True(Load("u1").IsDeleted);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, "/api/users/u1/account")).StatusCode);
    }

    [Fact]
    public async Task An_admin_deletes_someone_and_another_user_cannot()
    {
        await StartAsync();
        await UserAsync("u1");
        await UserAsync("u2");

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, "/api/users/u1/account", caller: "u2")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/api/users/u1/account", caller: "boss", roles: "Admin")).StatusCode);
        Assert.True(Load("u1").IsDeleted);
    }

    [Fact]
    public async Task Deleting_the_last_admin_is_a_conflict()
    {
        await StartAsync();
        await UserAsync("last", admin: true);

        var response = await SendAsync(HttpMethod.Delete, "/api/users/last/account", caller: "last", roles: "Admin");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.StartsWith("The last admin account cannot be deleted", (await JsonAsync(response)).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(AuthErrorCodes.LastAdmin, await ErrorJson.CodeAsync(response));
        Assert.False(Load("last").IsDeleted);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

/// <summary>Signs a request in as the user in <c>X-Test-User</c> with the roles in <c>X-Test-Roles</c>.</summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.ToString()) };
        claims.AddRange(Request.Headers["X-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
    }
}

public sealed class UserAdminTests : IAsyncDisposable
{
    private sealed class FakeEmails : IAuthEmails<IdentityTestUser>
    {
        public List<(string UserId, string Code)> Invites { get; } = [];
        public Task SendPasswordResetAsync(IdentityTestUser user, string code, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendInviteAsync(IdentityTestUser user, string code, CancellationToken cancellationToken)
        {
            Invites.Add((user.Id, code));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler(UserAdminTests test) : IUserRolesChangedHandler<IdentityTestUser>
    {
        public async Task OnRolesChangedAsync(IdentityTestUser user, CancellationToken cancellationToken)
        {
            // The change is saved before the handler runs.
            using var scope = test._host!.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
            test.Changed.Add((user.Id, string.Join(",", (await users.GetRolesAsync((await users.FindByIdAsync(user.Id))!)).Order())));
            if (test.HandlerThrows)
                throw new InvalidOperationException("handler failed");
        }
    }

    private List<(string UserId, string Roles)> Changed { get; } = [];
    private bool HandlerThrows { get; set; }

    private static readonly string[] Roles = ["Admin", "Editor", "Default"];
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeEmails _emails = new();
    private IHost? _host;
    private HttpClient _client = default!;

    public UserAdminTests() => _connection.Open();

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await _connection.DisposeAsync();
    }

    private static IdentityTestUser NewUser(AppKitNewUser user) => new() { FirstName = user.FirstName, LastName = user.LastName };

    private async Task StartAsync(Action<AppKitUserAdminOptions<IdentityTestUser>>? configure = null, string adminRole = "Admin")
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddDbContext<IdentityTestDbContext>(o => o.UseSqlite(_connection));
                    s.AddAppKitIdentity<IdentityTestUser, IdentityTestDbContext>();
                    s.AddAppKitAccounts<IdentityTestUser, IdentityTestDbContext>(o => o.AdminRole = adminRole);
                    s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    s.PostConfigure<AuthenticationOptions>(o =>
                    {
                        o.DefaultScheme = "Test";
                        o.DefaultAuthenticateScheme = "Test";
                        o.DefaultChallengeScheme = "Test";
                        o.DefaultForbidScheme = "Test";
                    });
                    s.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("Admin")));
                    s.AddSingleton<IAuthEmails<IdentityTestUser>>(_emails);
                    s.AddScoped<IUserRolesChangedHandler<IdentityTestUser>>(_ => new RecordingHandler(this));
                    s.AddScoped<IUserRolesChangedHandler<IdentityTestUser>>(_ => new RecordingHandler(this));
                    s.AddMemoryCache();
                    s.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapAppKitUserAdmin<IdentityTestUser, IdentityTestDbContext>("/api/users", o =>
                    {
                        o.Roles = Roles;
                        o.NewUser = NewUser;
                        configure?.Invoke(o);
                    }));
                }))
            .Build();
        using (var scope = _host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>().Database.EnsureCreated();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            foreach (var role in Roles)
                await roles.CreateAsync(new IdentityRole(role));
        }
        await _host.StartAsync();
        _client = _host.GetTestClient();
    }

    private Task UserAsync(string id, bool deleted = false, params string[] roles) => UserAsync(id, $"{id}@example.invalid", deleted, roles);

    private async Task UserAsync(string id, string email, bool deleted, params string[] roles)
    {
        using var scope = _host!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        var user = new IdentityTestUser { Id = id, UserName = id, Email = email, FirstName = $"F-{id}", LastName = $"L-{id}", IsDeleted = deleted };
        Assert.True((await users.CreateAsync(user)).Succeeded);
        foreach (var role in roles)
            await users.AddToRoleAsync(user, role);
    }

    private async Task<string[]> RolesOfAsync(string id)
    {
        using var scope = _host!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        return (await users.GetRolesAsync((await users.FindByIdAsync(id))!)).Order().ToArray();
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string caller = "boss", string roles = "Admin")
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

    private static async Task<string> MessageAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("message").GetString()!;

    private void Cache(string id) => _host!.Services.GetRequiredService<IMemoryCache>().Set(IdentityAndBearerExtensions.UserCheckKey(id), (IReadOnlyList<string>)["Admin"]);

    private bool Cached(string id) => _host!.Services.GetRequiredService<IMemoryCache>().TryGetValue(IdentityAndBearerExtensions.UserCheckKey(id), out _);

    // Role change handlers

    [Fact]
    public async Task Giving_a_role_runs_every_handler_after_saving_and_forgets_the_cached_roles()
    {
        await StartAsync();
        await UserAsync("u1", false, "Default");
        Cache("u1");
        Cache("u2");

        var response = await SendAsync(HttpMethod.Post, "/api/users/u1/roles", new { role = "Editor" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([("u1", "Default,Editor"), ("u1", "Default,Editor")], Changed);
        Assert.False(Cached("u1"));
        Assert.True(Cached("u2"));
    }

    [Fact]
    public async Task Taking_a_role_runs_every_handler_after_saving_and_forgets_the_cached_roles()
    {
        await StartAsync();
        await UserAsync("u1", false, "Default", "Editor");
        Cache("u1");

        var response = await SendAsync(HttpMethod.Delete, "/api/users/u1/roles/Editor");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal([("u1", "Default"), ("u1", "Default")], Changed);
        Assert.False(Cached("u1"));
    }

    [Fact]
    public async Task An_invite_runs_the_handlers_for_the_new_user()
    {
        await StartAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/users/invite", new { email = "new@example.invalid", firstName = "N", lastName = "U", role = "Editor" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (userId, roles) = Assert.Single(Changed.Distinct());
        Assert.Equal("Editor", roles);
        Assert.Equal(_emails.Invites.Single().UserId, userId);
    }

    [Fact]
    public async Task A_change_that_does_not_happen_runs_no_handler_and_keeps_the_cache()
    {
        await StartAsync();
        await UserAsync("boss", false, "Admin");
        await UserAsync("u1", false, "Editor");
        Cache("u1");
        Cache("boss");

        await SendAsync(HttpMethod.Post, "/api/users/u1/roles", new { role = "Editor" });
        await SendAsync(HttpMethod.Post, "/api/users/u1/roles", new { role = "Nope" });
        await SendAsync(HttpMethod.Delete, "/api/users/u1/roles/Default");
        await SendAsync(HttpMethod.Delete, "/api/users/boss/roles/Admin", caller: "other");
        await SendAsync(HttpMethod.Post, "/api/users/invite", new { email = "u1@example.invalid", firstName = "N", lastName = "U", role = "Editor" });

        Assert.Empty(Changed);
        Assert.True(Cached("u1"));
        Assert.True(Cached("boss"));
    }

    [Fact]
    public async Task A_failing_handler_fails_the_request_but_the_role_change_stays()
    {
        await StartAsync();
        await UserAsync("u1", false, "Default");
        HandlerThrows = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(HttpMethod.Post, "/api/users/u1/roles", new { role = "Editor" }));

        Assert.Equal(["Default", "Editor"], await RolesOfAsync("u1"));
    }

    // Access

    [Fact]
    public async Task Every_endpoint_needs_the_policy()
    {
        await StartAsync();

        foreach (var (method, path) in new[] { (HttpMethod.Get, "/api/users"), (HttpMethod.Post, "/api/users/x/roles"), (HttpMethod.Delete, "/api/users/x/roles/Editor"), (HttpMethod.Post, "/api/users/invite") })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(method, path, new { }, caller: "")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(method, path, new { }, roles: "Default")).StatusCode);
        }
    }

    [Fact]
    public async Task The_policy_can_be_changed()
    {
        await StartAsync(o => o.Policy = "Nobody");

        await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(HttpMethod.Get, "/api/users"));
    }

    [Fact]
    public void Mapping_without_a_user_factory_fails()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        var app = builder.Build();

        Assert.Throws<InvalidOperationException>(() => app.MapAppKitUserAdmin<IdentityTestUser, IdentityTestDbContext>());
    }

    [Fact]
    public void Mapping_without_the_account_options_fails_and_names_the_missing_call()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        var app = builder.Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            app.MapAppKitUserAdmin<IdentityTestUser, IdentityTestDbContext>(configure: o => o.NewUser = NewUser));
        Assert.Contains("AddAppKitAccounts", error.Message, StringComparison.Ordinal);
    }

    // List

    [Fact]
    public async Task Lists_active_users_by_email_with_their_roles()
    {
        await StartAsync();
        await UserAsync("bea", false, "Editor", "Admin");
        await UserAsync("al", "zed@example.invalid", false);
        await UserAsync("gone", true, "Editor");

        var json = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/users"));

        // By email, so al with zed@ comes last although its id sorts first.
        var users = json.EnumerateArray().ToList();
        Assert.Equal(["bea", "al"], users.Select(u => u.GetProperty("id").GetString()));
        var bea = users[0];
        Assert.Equal("bea@example.invalid", bea.GetProperty("email").GetString());
        Assert.Equal("bea", bea.GetProperty("userName").GetString());
        Assert.Equal("F-bea", bea.GetProperty("firstName").GetString());
        Assert.Equal("L-bea", bea.GetProperty("lastName").GetString());
        Assert.False(bea.GetProperty("isDeleted").GetBoolean());
        Assert.True(bea.TryGetProperty("createdAt", out _));
        Assert.Equal(["Admin", "Editor"], bea.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Empty(users[1].GetProperty("roles").EnumerateArray());
    }

    [Fact]
    public async Task Lists_deleted_users_too_when_asked()
    {
        await StartAsync();
        await UserAsync("al");
        await UserAsync("gone", true, "Editor");

        var json = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/users?includeDeleted=true"));

        var gone = json.EnumerateArray().Single(u => u.GetProperty("id").GetString() == "gone");
        Assert.True(gone.GetProperty("isDeleted").GetBoolean());
        Assert.Equal(["Editor"], gone.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    // Add role

    [Fact]
    public async Task Gives_a_known_role()
    {
        await StartAsync();
        await UserAsync("al");

        var response = await SendAsync(HttpMethod.Post, "/api/users/al/roles", new { role = "Editor" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Role assigned.", await MessageAsync(response));
        Assert.Equal(["Editor"], await RolesOfAsync("al"));
    }

    [Fact]
    public async Task Giving_a_role_the_user_has_says_so()
    {
        await StartAsync();
        await UserAsync("al", false, "Editor");

        var response = await SendAsync(HttpMethod.Post, "/api/users/al/roles", new { role = "Editor" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("User already has this role.", await MessageAsync(response));
    }

    [Fact]
    public async Task Refuses_an_unknown_role_even_if_it_exists_in_the_database()
    {
        await StartAsync(o => o.Roles = ["Admin", "Default"]);
        await UserAsync("al");

        var response = await SendAsync(HttpMethod.Post, "/api/users/al/roles", new { role = "Editor" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Unknown role.", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.UnknownRole, await ErrorJson.CodeAsync(response));
        Assert.Empty(await RolesOfAsync("al"));
    }

    [Fact]
    public async Task Giving_a_role_without_one_is_a_validation_problem()
    {
        await StartAsync();
        await UserAsync("al");

        var response = await SendAsync(HttpMethod.Post, "/api/users/al/roles", new { role = " " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["Role=required"], await ErrorJson.FieldCodesAsync(response));
    }

    [Fact]
    public async Task Giving_a_role_to_a_missing_or_deleted_user_is_not_found()
    {
        await StartAsync();
        await UserAsync("gone", true);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, "/api/users/ghost/roles", new { role = "Editor" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, "/api/users/gone/roles", new { role = "Editor" })).StatusCode);
    }

    // Remove role

    [Fact]
    public async Task Takes_a_role()
    {
        await StartAsync();
        await UserAsync("al", false, "Editor");

        var response = await SendAsync(HttpMethod.Delete, "/api/users/al/roles/Editor");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await RolesOfAsync("al"));
    }

    [Fact]
    public async Task Taking_an_unknown_role_is_refused()
    {
        await StartAsync();
        await UserAsync("al");

        var response = await SendAsync(HttpMethod.Delete, "/api/users/al/roles/Root");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Unknown role.", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.UnknownRole, await ErrorJson.CodeAsync(response));
    }

    [Fact]
    public async Task Nobody_takes_their_own_admin_role()
    {
        await StartAsync();
        await UserAsync("boss", false, "Admin");
        await UserAsync("other", false, "Admin");

        var response = await SendAsync(HttpMethod.Delete, "/api/users/boss/roles/Admin");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("You cannot remove your own admin role.", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.OwnAdminRole, await ErrorJson.CodeAsync(response));
        Assert.Equal(["Admin"], await RolesOfAsync("boss"));
    }

    [Fact]
    public async Task The_last_active_admin_keeps_the_role()
    {
        await StartAsync();
        await UserAsync("last", false, "Admin");
        await UserAsync("gone", true, "Admin");

        var response = await SendAsync(HttpMethod.Delete, "/api/users/last/roles/Admin");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("The last admin keeps the admin role. Give someone else the role first.", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.LastAdmin, await ErrorJson.CodeAsync(response));
        Assert.Equal(["Admin"], await RolesOfAsync("last"));
    }

    [Fact]
    public async Task One_of_two_admins_can_lose_the_role()
    {
        await StartAsync();
        await UserAsync("a1", false, "Admin");
        await UserAsync("a2", false, "Admin");

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/api/users/a1/roles/Admin")).StatusCode);
        Assert.Empty(await RolesOfAsync("a1"));
    }

    [Fact]
    public async Task A_deleted_admin_can_lose_the_role_and_the_last_holder_of_another_role_too()
    {
        await StartAsync();
        await UserAsync("live", false, "Admin");
        await UserAsync("gone", true, "Admin");
        await UserAsync("ed", false, "Editor");

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/api/users/gone/roles/Admin")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/api/users/ed/roles/Editor")).StatusCode);
    }

    [Fact]
    public async Task Taking_the_admin_role_from_a_user_without_it_is_not_blocked_by_the_guard()
    {
        await StartAsync();
        await UserAsync("last", false, "Admin");
        await UserAsync("al");

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Delete, "/api/users/al/roles/Admin")).StatusCode);
    }

    [Fact]
    public async Task The_admin_role_name_can_be_changed()
    {
        await StartAsync(adminRole: "Editor");
        await UserAsync("only", false, "Editor");

        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Delete, "/api/users/only/roles/Editor")).StatusCode);
    }

    [Fact]
    public async Task Taking_a_role_from_a_missing_user_is_not_found()
    {
        await StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, "/api/users/ghost/roles/Editor")).StatusCode);
    }

    // Invite

    [Fact]
    public async Task Invites_a_user_without_a_password_and_emails_a_code()
    {
        await StartAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/users/invite", new { email = "new@example.invalid", firstName = "Kari", lastName = "N", role = "Editor" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Invitation sent.", await MessageAsync(response));
        using var scope = _host!.Services.CreateScope();
        var user = scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>().Users.Single();
        Assert.Equal(("new@example.invalid", "new@example.invalid", "Kari", "N"), (user.Email, user.UserName, user.FirstName, user.LastName));
        Assert.True(user.EmailConfirmed);
        Assert.Null(user.PasswordHash);
        Assert.Equal(["Editor"], await RolesOfAsync(user.Id));
        var (userId, code) = Assert.Single(_emails.Invites);
        Assert.Equal(user.Id, userId);
        Assert.Equal(VerificationCodes.Hash(code), user.PasswordResetCodeHash);
        Assert.InRange(user.PasswordResetCodeExpiration!.Value, DateTime.UtcNow.AddDays(7).AddMinutes(-1), DateTime.UtcNow.AddDays(7).AddMinutes(1));
    }

    [Fact]
    public async Task The_invite_lifetime_can_be_changed()
    {
        await StartAsync(o => o.InviteLifetime = TimeSpan.FromHours(2));

        await SendAsync(HttpMethod.Post, "/api/users/invite", new { email = "new@example.invalid", firstName = "Kari", lastName = "N", role = "Editor" });

        using var scope = _host!.Services.CreateScope();
        var expires = scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>().Users.Single().PasswordResetCodeExpiration!.Value;
        Assert.InRange(expires, DateTime.UtcNow.AddHours(2).AddMinutes(-1), DateTime.UtcNow.AddHours(2).AddMinutes(1));
    }

    [Fact]
    public async Task An_invite_whose_role_cannot_be_given_leaves_no_account_and_sends_nothing()
    {
        await StartAsync(o => o.Roles = ["Admin", "Missing"]);

        var response = await SendAsync(HttpMethod.Post, "/api/users/invite", new { email = "new@example.invalid", firstName = "Kari", lastName = "N", role = "Missing" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Failed to assign role.", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.RoleAssignFailed, await ErrorJson.CodeAsync(response));
        using var scope = _host!.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>().Users);
        Assert.Empty(_emails.Invites);
    }

    [Fact]
    public async Task Inviting_an_email_with_an_account_is_a_conflict()
    {
        await StartAsync();
        await UserAsync("al");

        var response = await SendAsync(HttpMethod.Post, "/api/users/invite", new { email = "al@example.invalid", firstName = "A", lastName = "L", role = "Editor" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("That email already has an account.", await MessageAsync(response));
        Assert.Equal(AuthErrorCodes.EmailTaken, await ErrorJson.CodeAsync(response));
        Assert.Empty(_emails.Invites);
    }

    [Theory]
    [InlineData(null, "Kari", "N", "Editor", "Email", "required")]
    [InlineData("nope", "Kari", "N", "Editor", "Email", "invalidEmail")]
    [InlineData("a@b.invalid", " ", "N", "Editor", "FirstName", "required")]
    [InlineData("a@b.invalid", "Kari", "", "Editor", "LastName", "required")]
    [InlineData("a@b.invalid", "Kari", "N", "Root", "Role", "unknownRole")]
    [InlineData("a@b.invalid", "Kari", "N", null, "Role", "required")]
    public async Task Inviting_with_a_bad_field_is_a_validation_problem(string? email, string? first, string? last, string? role, string field, string code)
    {
        await StartAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/users/invite", new { email, firstName = first, lastName = last, role });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([field], (await JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name));
        Assert.Equal([$"{field}={code}"], await ErrorJson.FieldCodesAsync(response));
        Assert.Empty(_emails.Invites);
    }

    [Theory]
    [InlineData(51, 1, "FirstName")]
    [InlineData(1, 51, "LastName")]
    public async Task Names_longer_than_50_are_refused(int first, int last, string field)
    {
        await StartAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/users/invite", new { email = "a@b.invalid", firstName = new string('x', first), lastName = new string('y', last), role = "Editor" });

        Assert.Equal([field], (await JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name));
        Assert.Equal([$"{field}=tooLong"], await ErrorJson.FieldCodesAsync(response));
        Assert.Equal([$"{field}=50"], await ErrorJson.LimitsAsync(response));
    }

    [Fact]
    public async Task An_email_longer_than_200_is_refused()
    {
        await StartAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/users/invite", new { email = new string('a', 190) + "@example.invalid", firstName = "K", lastName = "N", role = "Editor" });

        Assert.Equal(["Email=invalidEmail"], await ErrorJson.FieldCodesAsync(response));
    }

    // Seeding

    private async Task<(IServiceProvider Services, FakeLogCollector Logs)> SeedHostAsync(string? adminRole = "Admin")
    {
        var logs = new FakeLogCollector();
        var services = new ServiceCollection().AddLogging(b => b.AddProvider(new FakeLoggerProvider(logs)));
        services.AddDbContext<IdentityTestDbContext>(o => o.UseSqlite(_connection));
        services.AddAppKitIdentity<IdentityTestUser, IdentityTestDbContext>();
        if (adminRole is not null)
            services.AddAppKitAccounts<IdentityTestUser, IdentityTestDbContext>(o => o.AdminRole = adminRole);
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>().Database.EnsureCreated();
        await scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>().CreateAsync(new IdentityRole(adminRole ?? "Admin"));
        return (provider, logs);
    }

    private static IConfiguration Seed(string? email = "first@example.invalid", string? password = "Password1", string? first = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:AdminEmail"] = email,
            ["Seed:AdminPassword"] = password,
            ["Seed:AdminFirstName"] = first,
        }).Build();

    private static async Task<(int Users, int Admins)> CountsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        return (users.Users.Count(), (await users.GetUsersInRoleAsync("Admin")).Count);
    }

    [Fact]
    public async Task Seeding_creates_the_first_admin_with_the_password()
    {
        var (services, logs) = await SeedHostAsync();

        await services.SeedAppKitFirstAdminAsync(Seed(first: "Ola"), NewUser);

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        var admin = (await users.GetUsersInRoleAsync("Admin")).Single();
        Assert.Equal(("first@example.invalid", "first@example.invalid", "Ola"), (admin.Email, admin.UserName, admin.FirstName));
        Assert.True(admin.EmailConfirmed);
        Assert.True(await users.CheckPasswordAsync(admin, "Password1"));
        var seedLogs = logs.GetSnapshot().Where(r => r.Category == "SjolystInnovation.AppKit.Auth.Seed").ToList();
        Assert.Contains(seedLogs, r => r.Message.StartsWith("Created the first admin", StringComparison.Ordinal));
        Assert.DoesNotContain(seedLogs, r => r.Message.Contains('@', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Seeding_names_the_admin_Admin_by_default()
    {
        var (services, _) = await SeedHostAsync();

        await services.SeedAppKitFirstAdminAsync(Seed(), NewUser);

        using var scope = services.CreateScope();
        Assert.Equal("Admin", scope.ServiceProvider.GetRequiredService<IdentityTestDbContext>().Users.Single().FirstName);
    }

    [Fact]
    public async Task Seeding_gives_an_existing_user_with_the_email_the_role()
    {
        var (services, _) = await SeedHostAsync();
        using (var scope = services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>().CreateAsync(new IdentityTestUser { UserName = "first", Email = "first@example.invalid" });

        await services.SeedAppKitFirstAdminAsync(Seed(), NewUser);

        Assert.Equal((1, 1), await CountsAsync(services));
    }

    [Fact]
    public async Task Seeding_does_nothing_while_an_admin_exists()
    {
        var (services, _) = await SeedHostAsync();
        await services.SeedAppKitFirstAdminAsync(Seed(email: "one@example.invalid"), NewUser);

        await services.SeedAppKitFirstAdminAsync(Seed(email: "two@example.invalid"), NewUser);

        Assert.Equal((1, 1), await CountsAsync(services));
    }

    [Theory]
    [InlineData(null, "Password1")]
    [InlineData("first@example.invalid", "")]
    public async Task Seeding_without_an_email_or_a_password_does_nothing(string? email, string? password)
    {
        var (services, logs) = await SeedHostAsync();

        await services.SeedAppKitFirstAdminAsync(Seed(email, password), NewUser);

        Assert.Equal((0, 0), await CountsAsync(services));
        Assert.DoesNotContain(logs.GetSnapshot(), r => r.Category == "SjolystInnovation.AppKit.Auth.Seed");
    }

    [Fact]
    public async Task Seeding_uses_the_admin_role_from_the_account_options()
    {
        var (services, _) = await SeedHostAsync("Owner");

        await services.SeedAppKitFirstAdminAsync(Seed(), NewUser);

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        Assert.Equal("first@example.invalid", (await users.GetUsersInRoleAsync("Owner")).Single().Email);
    }

    [Fact]
    public async Task Seeding_without_the_account_options_fails_and_names_the_missing_call()
    {
        var (services, _) = await SeedHostAsync(adminRole: null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => services.SeedAppKitFirstAdminAsync(Seed(), NewUser));
        Assert.Contains("AddAppKitAccounts", error.Message, StringComparison.Ordinal);
        Assert.Equal((0, 0), await CountsAsync(services));
    }

    [Fact]
    public async Task Seeding_with_a_weak_password_logs_and_creates_nobody()
    {
        var (services, logs) = await SeedHostAsync();

        await services.SeedAppKitFirstAdminAsync(Seed(password: "weak"), NewUser);

        Assert.Equal((0, 0), await CountsAsync(services));
        Assert.Contains(logs.GetSnapshot(), r => r.Level == LogLevel.Error && r.Message.StartsWith("Could not create the seed admin", StringComparison.Ordinal));
    }

    private static string[] RoleNames(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return [.. scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>().Roles.Select(r => r.Name!).OrderBy(n => n)];
    }

    [Fact]
    public async Task Seeding_roles_creates_only_the_missing_ones()
    {
        var (services, logs) = await SeedHostAsync();

        await services.SeedAppKitRolesAsync(["Admin", "Editor", "Viewer"]);

        Assert.Equal(["Admin", "Editor", "Viewer"], RoleNames(services));
        var seedLogs = logs.GetSnapshot().Where(r => r.Category == "SjolystInnovation.AppKit.Auth.Seed").Select(r => r.Message);
        Assert.Equal(["Created the role Editor.", "Created the role Viewer."], seedLogs);
    }

    [Fact]
    public async Task Seeding_roles_twice_changes_nothing_the_second_time()
    {
        var (services, logs) = await SeedHostAsync();
        await services.SeedAppKitRolesAsync(["Admin", "Editor"]);
        logs.Clear();

        await services.SeedAppKitRolesAsync(["Admin", "Editor"]);

        Assert.Equal(["Admin", "Editor"], RoleNames(services));
        Assert.DoesNotContain(logs.GetSnapshot(), r => r.Category == "SjolystInnovation.AppKit.Auth.Seed");
    }
}

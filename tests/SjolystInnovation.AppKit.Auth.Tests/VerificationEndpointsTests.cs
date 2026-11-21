using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public sealed class VerificationEndpointsTests : IAsyncDisposable
{
    private sealed class FakeEmails : IRegistrationEmails<IdentityTestUser>
    {
        public List<(string UserId, string Code)> Verifications { get; } = [];
        public List<string> AlreadyRegistered { get; } = [];

        public Task SendVerificationAsync(IdentityTestUser user, string code, string? locale, CancellationToken cancellationToken)
        {
            Verifications.Add((user.Id, code));
            return Task.CompletedTask;
        }

        public Task SendAlreadyRegisteredAsync(IdentityTestUser user, string? locale, CancellationToken cancellationToken)
        {
            AlreadyRegistered.Add(user.Id);
            return Task.CompletedTask;
        }

        public Task SendRemovalReminderAsync(IdentityTestUser user, string code, DateTime removalAt, string? locale, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    // Makes the next saves of a user fail like a lost race on the concurrency stamp.
    internal sealed class LostRace : SaveChangesInterceptor
    {
        public volatile bool Active;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Active && eventData.Context!.ChangeTracker.Entries<IdentityTestUser>().Any(e => e.State == EntityState.Modified)
                ? throw new DbUpdateConcurrencyException("Lost the race.")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private sealed class VerifiedLog
    {
        public List<(string UserId, bool Confirmed, bool InTransaction)> Calls { get; } = [];
    }

    private sealed class VerifiedHandler(RegistrationTestDbContext db, VerifiedLog log) : IEmailVerifiedHandler<IdentityTestUser>
    {
        public Task OnEmailVerifiedAsync(IdentityTestUser user, CancellationToken cancellationToken)
        {
            log.Calls.Add((user.Id, user.EmailConfirmed, db.Database.CurrentTransaction is not null));
            return Task.CompletedTask;
        }
    }

    private const string Key = "verify-test-signing-key-0123456789abcdef";
    private const string Password = "Password1";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    // Tokens are checked against the real clock, so the fake one starts now and tokens are made before it moves.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeEmails _emails = new();
    private readonly VerifiedLog _verified = new();
    private readonly LostRace _lostRace = new();
    private string? _file;
    private IHost? _host;
    private HttpClient _client = default!;

    public VerificationEndpointsTests() => _connection.Open();

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await _connection.DisposeAsync();
        if (_file is not null)
        {
            SqliteConnection.ClearAllPools();
            File.Delete(_file);
        }
    }

    private async Task StartAsync(Action<AppKitRegistrationOptions<IdentityTestUser>>? configure = null, bool fileDb = false)
    {
        // Parallel requests need their own connections, so they run on a database file.
        if (fileDb)
            _file = Path.Combine(Path.GetTempPath(), $"appkit-auth-{Guid.NewGuid():N}.db");
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .UseEnvironment("Production")
                .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = Key, ["Jwt:Issuer"] = "appkit" }))
                .ConfigureServices((context, s) =>
                {
                    s.AddSingleton<TimeProvider>(_time);
                    s.AddDbContext<RegistrationTestDbContext>(o =>
                    {
                        if (_file is null)
                            o.UseSqlite(_connection);
                        else
                            o.UseSqlite($"Data Source={_file};Pooling=False");
                        o.AddInterceptors(_lostRace);
                    });
                    s.AddAppKitIdentity<IdentityTestUser, RegistrationTestDbContext>();
                    s.AddAppKitAuth<RegistrationTestDbContext>(context.Configuration.GetSection("Jwt"));
                    s.AddAppKitJwtBearer<IdentityTestUser>(u => !u.IsDeleted);
                    s.AddAuthorization();
                    s.AddRouting();
                    s.AddAppKitSelfRegistration<IdentityTestUser, RegistrationTestDbContext>(o =>
                    {
                        o.NewUser = r => new IdentityTestUser { Email = r.Email, UserName = r.UserName };
                        configure?.Invoke(o);
                    });
                    s.AddSingleton<IRegistrationEmails<IdentityTestUser>>(_emails);
                    s.AddSingleton(_verified);
                    s.AddScoped<IEmailVerifiedHandler<IdentityTestUser>, VerifiedHandler>();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e =>
                    {
                        e.MapAppKitRegistration<IdentityTestUser, RegistrationTestDbContext>();
                        e.MapAppKitAuth<IdentityTestUser, RegistrationTestDbContext>();
                        e.MapGet("/me", (ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)).RequireAuthorization();
                    });
                }))
            .Build();
        using (var scope = _host.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>().Database.EnsureCreated();
        await _host.StartAsync();
        _client = _host.GetTestClient();
    }

    private T Db<T>(Func<RegistrationTestDbContext, T> read)
    {
        using var scope = _host!.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>());
    }

    private IdentityTestUser Stored(string id) => Db(db => db.Users.AsNoTracking().Single(u => u.Id == id));

    private async Task<string> SeedAsync(string id, bool verified = false, bool deleted = false)
    {
        using var scope = _host!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        var user = new IdentityTestUser { Id = id, UserName = id, Email = $"{id}@example.invalid", EmailConfirmed = verified, IsDeleted = deleted };
        var code = verified ? string.Empty : EmailVerificationCodes.Issue(user, _time);
        Assert.True((await users.CreateAsync(user, "Original1")).Succeeded);
        return code;
    }

    private async Task<string> AccessTokenAsync(string id)
    {
        using var scope = _host!.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>().FindByIdAsync(id);
        return scope.ServiceProvider.GetRequiredService<IJwtTokenService>().Build(user!, []);
    }

    private async Task<string> RefreshTokenAsync(string id)
    {
        using var scope = _host!.Services.CreateScope();
        var raw = await scope.ServiceProvider.GetRequiredService<IRefreshTokenService>().IssueAsync(id);
        await scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>().SaveChangesAsync();
        return raw;
    }

    // A token the app's key signed, with a stamp claim of the test's choosing, or none.
    private static string Craft(string id, string? stampClaim)
    {
        var claims = new List<Claim> { new("sub", id), new("unique_name", id), new("email", $"{id}@example.invalid") };
        if (stampClaim is not null)
            claims.Add(new Claim("sst", stampClaim));
        var handler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
        var now = DateTime.UtcNow;
        return handler.WriteToken(handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddHours(1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256Signature),
            Issuer = "appkit",
            Audience = "appkit",
        }));
    }

    private Task<HttpResponseMessage> SendAsync(string path, object? body, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> VerifyAsync(string? code, string? bearer) => SendAsync("/api/auth/verify-email", new { code }, bearer);

    private Task<HttpResponseMessage> ResendAsync(string? bearer) => SendAsync("/api/auth/resend-verification", null, bearer);

    private async Task<HttpStatusCode> MeAsync(string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return (await _client.SendAsync(request)).StatusCode;
    }

    private Task<HttpResponseMessage> RegisterAsync(string email) => _client.PostAsJsonAsync("/api/auth/register", new Dictionary<string, object?>
    {
        ["email"] = email, ["password"] = Password, ["ageConfirmed"] = true, ["termsAccepted"] = true, ["termsVersion"] = "v1",
    });

    // verify-email

    [Fact]
    public async Task A_right_code_confirms_the_email_and_answers_200_with_a_message_and_no_tokens()
    {
        await StartAsync();
        var code = await SeedAsync("u1");

        var response = await VerifyAsync(code, await AccessTokenAsync("u1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("message", text, StringComparison.Ordinal);
        Assert.Contains("sign in again", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        var user = Stored("u1");
        Assert.True(user.EmailConfirmed);
        Assert.Null(user.EmailVerificationCodeHash);
    }

    [Theory]
    [InlineData("verify-email")]
    [InlineData("resend-verification")]
    public async Task Without_a_token_both_endpoints_answer_401_and_change_nothing(string route)
    {
        await StartAsync();
        var code = await SeedAsync("u1");
        var before = Stored("u1");

        var response = await SendAsync($"/api/auth/{route}", new { code }, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var after = Stored("u1");
        Assert.False(after.EmailConfirmed);
        Assert.Equal((before.EmailVerificationCodeHash, before.EmailVerificationAttempts), (after.EmailVerificationCodeHash, after.EmailVerificationAttempts));
        Assert.Empty(_emails.Verifications);
    }

    [Fact]
    public async Task A_token_for_another_user_cannot_verify_the_account_of_the_code()
    {
        await StartAsync();
        var code = await SeedAsync("victim");
        await SeedAsync("other");

        var response = await VerifyAsync(code, await AccessTokenAsync("other"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Stored("victim").EmailConfirmed);
        Assert.False(Stored("other").EmailConfirmed);
    }

    [Fact]
    public async Task A_missing_code_is_a_validation_problem()
    {
        await StartAsync();
        await SeedAsync("u1");

        var response = await VerifyAsync(null, await AccessTokenAsync("u1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["Code=required"], await ErrorJson.FieldCodesAsync(response));
    }

    [Fact]
    public async Task A_wrong_code_answers_400_invalidCode_and_counts_an_attempt()
    {
        await StartAsync();
        await SeedAsync("u1");

        var response = await VerifyAsync("WRONG1", await AccessTokenAsync("u1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalidCode", await ErrorJson.CodeAsync(response));
        var user = Stored("u1");
        Assert.False(user.EmailConfirmed);
        Assert.Equal(1, user.EmailVerificationAttempts);
        Assert.Empty(_verified.Calls);
    }

    [Fact]
    public async Task An_expired_code_answers_400_invalidCode()
    {
        await StartAsync();
        var code = await SeedAsync("u1");
        var token = await AccessTokenAsync("u1");
        _time.Advance(EmailVerificationCodes.Lifetime + TimeSpan.FromMinutes(1));

        var response = await VerifyAsync(code, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalidCode", await ErrorJson.CodeAsync(response));
        Assert.False(Stored("u1").EmailConfirmed);
    }

    [Fact]
    public async Task A_deleted_account_cannot_use_either_endpoint()
    {
        await StartAsync();
        var code = await SeedAsync("u1");
        var token = await AccessTokenAsync("u1");
        using (var scope = _host!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>();
            db.Users.Single(u => u.Id == "u1").IsDeleted = true;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(code, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ResendAsync(token)).StatusCode);
        Assert.False(Stored("u1").EmailConfirmed);
    }

    [Fact]
    public async Task A_verified_account_answers_409_alreadyVerified()
    {
        await StartAsync();
        await SeedAsync("u1", verified: true);

        var response = await VerifyAsync("ABC123", await AccessTokenAsync("u1"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("alreadyVerified", await ErrorJson.CodeAsync(response));
        Assert.Empty(_verified.Calls);
    }

    [Fact]
    public async Task Five_wrong_codes_then_429_even_for_the_right_one()
    {
        await StartAsync();
        var code = await SeedAsync("u1");
        var token = await AccessTokenAsync("u1");
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await VerifyAsync("WRONG1", token)).StatusCode);

        var response = await VerifyAsync(code, token);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("tooManyAttempts", await ErrorJson.CodeAsync(response));
        Assert.False(Stored("u1").EmailConfirmed);
        Assert.Empty(_verified.Calls);
    }

    [Fact]
    public async Task The_handler_runs_once_and_only_after_the_commit()
    {
        await StartAsync();
        var code = await SeedAsync("u1");
        var token = await AccessTokenAsync("u1");
        await VerifyAsync("WRONG1", token);
        Assert.Empty(_verified.Calls);

        await VerifyAsync(code, token);

        var call = Assert.Single(_verified.Calls);
        Assert.Equal(("u1", true, false), call);
    }

    // A check whose write is lost

    [Fact]
    public async Task A_wrong_code_whose_attempt_cannot_be_saved_answers_409_tryAgain()
    {
        await StartAsync();
        await SeedAsync("u1");
        var token = await AccessTokenAsync("u1");
        _lostRace.Active = true;

        var response = await VerifyAsync("WRONG1", token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("tryAgain", await ErrorJson.CodeAsync(response));
        Assert.Equal(0, Stored("u1").EmailVerificationAttempts);
    }

    [Fact]
    public async Task A_right_code_whose_confirmation_cannot_be_saved_answers_409_tryAgain()
    {
        await StartAsync();
        var code = await SeedAsync("u1");
        var token = await AccessTokenAsync("u1");
        _lostRace.Active = true;

        var response = await VerifyAsync(code, token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("tryAgain", await ErrorJson.CodeAsync(response));
        Assert.False(Stored("u1").EmailConfirmed);
        Assert.Empty(_verified.Calls);
    }

    [Fact]
    public async Task Parallel_wrong_guesses_never_get_more_invalidCode_answers_than_stored_attempts()
    {
        await StartAsync(fileDb: true);
        await SeedAsync("u1");
        var token = await AccessTokenAsync("u1");
        var invalid = 0;
        for (var round = 0; round < 30 && Stored("u1").EmailVerificationAttempts < VerificationCodes.MaxAttempts; round++)
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => VerifyAsync($"WRONG{i}", token)));
            invalid += responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest);
            Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Conflict, HttpStatusCode.TooManyRequests }));
            Assert.True(invalid <= Stored("u1").EmailVerificationAttempts);
        }

        Assert.Equal(VerificationCodes.MaxAttempts, Stored("u1").EmailVerificationAttempts);
        var after = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => VerifyAsync($"LATE{i}", token)));
        Assert.All(after, r => Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode));
    }

    // Sessions

    [Fact]
    public async Task Verifying_ends_every_refresh_token_of_the_user_and_no_one_elses()
    {
        await StartAsync();
        var code = await SeedAsync("u1");
        await SeedAsync("u2");
        var access = await AccessTokenAsync("u1");
        var refresh = await RefreshTokenAsync("u1");
        await RefreshTokenAsync("u1");
        await RefreshTokenAsync("u2");

        await VerifyAsync(code, access);

        Assert.Equal(0, Db(db => db.RefreshTokens.Count(t => t.UserId == "u1")));
        Assert.Equal(1, Db(db => db.RefreshTokens.Count(t => t.UserId == "u2")));
        var refreshed = await _client.PostAsJsonAsync("/api/auth/refresh-token", new { token = access, refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
        Assert.Equal("invalidRefreshToken", await ErrorJson.CodeAsync(refreshed));
    }

    [Fact]
    public async Task An_access_token_from_before_the_verify_is_refused_after_it_and_one_from_after_works()
    {
        await StartAsync();
        var code = await SeedAsync("u1");
        var before = await AccessTokenAsync("u1");
        Assert.Equal(HttpStatusCode.OK, await MeAsync(before));

        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(code, before)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(before));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(await AccessTokenAsync("u1")));
    }

    [Fact]
    public async Task Verifying_rotates_the_stamp_and_removes_the_cache_key()
    {
        await StartAsync();
        var code = await SeedAsync("u1");
        var stamp = Stored("u1").SecurityStamp;
        var token = await AccessTokenAsync("u1");
        await MeAsync(token);
        var cache = _host!.Services.GetRequiredService<IMemoryCache>();
        Assert.True(cache.TryGetValue(IdentityAndBearerExtensions.UserCheckKey("u1"), out _));

        await VerifyAsync(code, token);

        Assert.NotEqual(stamp, Stored("u1").SecurityStamp);
        Assert.False(cache.TryGetValue(IdentityAndBearerExtensions.UserCheckKey("u1"), out _));
    }

    [Fact]
    public async Task A_wrong_code_ends_no_session()
    {
        await StartAsync();
        await SeedAsync("u1");
        var token = await AccessTokenAsync("u1");
        var stamp = Stored("u1").SecurityStamp;
        await RefreshTokenAsync("u1");

        await VerifyAsync("WRONG1", token);

        Assert.Equal(stamp, Stored("u1").SecurityStamp);
        Assert.Equal(1, Db(db => db.RefreshTokens.Count()));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(token));
    }

    [Fact]
    public async Task The_token_carries_a_short_hash_of_the_stamp_and_never_the_stamp()
    {
        await StartAsync();
        await SeedAsync("u1");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(await AccessTokenAsync("u1"));

        var claim = jwt.Claims.Single(c => c.Type == "sst").Value;
        Assert.NotEqual(Stored("u1").SecurityStamp, claim);
        Assert.True(claim.Length <= 24);
        Assert.DoesNotContain(Stored("u1").SecurityStamp!, jwt.RawPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_with_a_wrong_stamp_is_refused_and_one_with_no_stamp_is_accepted_for_an_opted_in_app()
    {
        await StartAsync();
        await SeedAsync("u1");

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(Craft("u1", "not-the-stamp")));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(Craft("u1", null)));
    }

    // resend-verification

    [Fact]
    public async Task A_resend_after_the_cooldown_replaces_the_code_and_sends_it()
    {
        await StartAsync();
        var old = await SeedAsync("u1");
        var token = await AccessTokenAsync("u1");
        var before = Stored("u1");
        _time.Advance(TimeSpan.FromMinutes(2));

        var response = await ResendAsync(token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (sentTo, fresh) = Assert.Single(_emails.Verifications);
        Assert.Equal("u1", sentTo);
        Assert.NotEqual(old, fresh);
        var after = Stored("u1");
        Assert.Equal(VerificationCodes.Hash(fresh), after.EmailVerificationCodeHash);
        Assert.NotEqual(before.EmailVerificationCodeHash, after.EmailVerificationCodeHash);
        Assert.Equal(HttpStatusCode.BadRequest, (await VerifyAsync(old, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(fresh, token)).StatusCode);
    }

    [Fact]
    public async Task A_resend_inside_the_cooldown_answers_429_tooManyRequests_and_sends_nothing()
    {
        await StartAsync(o => o.ResendCooldown = TimeSpan.FromMinutes(5));
        await SeedAsync("u1");
        var token = await AccessTokenAsync("u1");
        var before = Stored("u1").EmailVerificationCodeHash;
        _time.Advance(TimeSpan.FromMinutes(4));

        var response = await ResendAsync(token);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("tooManyRequests", await ErrorJson.CodeAsync(response));
        Assert.Empty(_emails.Verifications);
        Assert.Equal(before, Stored("u1").EmailVerificationCodeHash);

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.OK, (await ResendAsync(token)).StatusCode);
        Assert.Single(_emails.Verifications);
    }

    [Fact]
    public async Task A_resend_over_the_daily_cap_of_the_address_answers_200_and_sends_nothing()
    {
        await StartAsync(o => { o.ResendCooldown = TimeSpan.Zero; o.MaxEmailsPerAddressPerDay = 2; });
        await SeedAsync("u1");
        var token = await AccessTokenAsync("u1");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            statuses.Add((await ResendAsync(token)).StatusCode);

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(2, _emails.Verifications.Count);
    }

    [Fact]
    public async Task A_resend_for_a_verified_account_answers_409_and_sends_nothing()
    {
        await StartAsync();
        await SeedAsync("u1", verified: true);

        var response = await ResendAsync(await AccessTokenAsync("u1"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("alreadyVerified", await ErrorJson.CodeAsync(response));
        Assert.Empty(_emails.Verifications);
    }

    [Fact]
    public async Task After_too_many_attempts_no_new_code_is_issued_until_the_code_has_expired()
    {
        await StartAsync();
        await SeedAsync("u1");
        var token = await AccessTokenAsync("u1");
        for (var i = 0; i < 5; i++)
            await VerifyAsync("WRONG1", token);
        var locked = Stored("u1").EmailVerificationCodeHash;

        _time.Advance(TimeSpan.FromMinutes(30));
        var early = await ResendAsync(token);

        Assert.Equal(HttpStatusCode.TooManyRequests, early.StatusCode);
        Assert.Equal("tooManyAttempts", await ErrorJson.CodeAsync(early));
        Assert.Empty(_emails.Verifications);
        Assert.Equal(locked, Stored("u1").EmailVerificationCodeHash);

        _time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.OK, (await ResendAsync(token)).StatusCode);
        Assert.Single(_emails.Verifications);
        Assert.Equal(0, Stored("u1").EmailVerificationAttempts);
    }

    // register

    [Fact]
    public async Task Register_for_an_unverified_email_issues_no_code_and_changes_nothing()
    {
        await StartAsync();
        await SeedAsync("u1");
        var before = Stored("u1");
        _time.Advance(TimeSpan.FromMinutes(5));

        var response = await RegisterAsync("u1@example.invalid");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = Stored("u1");
        Assert.Equal((before.EmailVerificationCodeHash, before.EmailVerificationCodeExpiration, before.EmailVerificationAttempts, before.PasswordHash, before.SecurityStamp),
            (after.EmailVerificationCodeHash, after.EmailVerificationCodeExpiration, after.EmailVerificationAttempts, after.PasswordHash, after.SecurityStamp));
        Assert.Empty(_emails.Verifications);
        Assert.Equal(["u1"], _emails.AlreadyRegistered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Register_sends_the_notice_at_most_once_per_cooldown(bool verified)
    {
        await StartAsync(o => o.ResendCooldown = TimeSpan.FromMinutes(5));
        await SeedAsync("u1", verified);

        for (var i = 0; i < 4; i++)
            await RegisterAsync("u1@example.invalid");
        Assert.Single(_emails.AlreadyRegistered);

        _time.Advance(TimeSpan.FromMinutes(6));
        await RegisterAsync("u1@example.invalid");
        await RegisterAsync("u1@example.invalid");

        Assert.Equal(2, _emails.AlreadyRegistered.Count);
        Assert.Empty(_emails.Verifications);
    }

    [Fact]
    public async Task Register_answers_the_same_body_when_the_notice_is_held_back()
    {
        await StartAsync();
        await SeedAsync("u1");

        var first = await (await RegisterAsync("u1@example.invalid")).Content.ReadAsStringAsync();
        var second = await (await RegisterAsync("u1@example.invalid")).Content.ReadAsStringAsync();
        var fresh = await (await RegisterAsync("new@example.invalid")).Content.ReadAsStringAsync();

        Assert.Equal(first, second);
        Assert.Equal(first, fresh);
    }

    [Fact]
    public async Task A_person_can_register_after_someone_tried_to_take_their_email_as_a_user_name()
    {
        await StartAsync(o => o.AllowUserName = true);

        var squatter = await _client.PostAsJsonAsync("/api/auth/register", new Dictionary<string, object?>
        {
            ["email"] = "squatter@example.invalid", ["userName"] = "victim@example.invalid", ["password"] = Password,
            ["ageConfirmed"] = true, ["termsAccepted"] = true, ["termsVersion"] = "v1",
        });
        var victim = await RegisterAsync("victim@example.invalid");

        Assert.Equal(HttpStatusCode.BadRequest, squatter.StatusCode);
        Assert.Equal(["UserName=invalidUserName"], await ErrorJson.FieldCodesAsync(squatter));
        Assert.Equal(HttpStatusCode.OK, victim.StatusCode);
        Assert.Equal(["victim@example.invalid"], Db(db => db.Users.Select(u => u.UserName!).ToList()));
        Assert.Single(_emails.Verifications);
    }

    [Fact]
    public async Task Mapping_adds_the_two_authorized_routes_under_the_prefix()
    {
        await StartAsync();

        var routes = _host!.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(r => r.RoutePattern.RawText).Where(p => p!.Contains("verif", StringComparison.Ordinal)).Order().ToList();

        Assert.Equal(["/api/auth/resend-verification", "/api/auth/verify-email"], routes);
    }
}

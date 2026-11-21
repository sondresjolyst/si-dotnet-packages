using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

/// <summary>Records every command and can refuse the ones that touch the consent tables.</summary>
internal sealed class CommandLog : DbCommandInterceptor
{
    private readonly List<string> _texts = [];

    public volatile bool FailOnConsentTables;

    // The SELECT commands that read the user table, which is what a user check does on a cache miss.
    public int UserReads
    {
        get
        {
            lock (_texts)
                return _texts.Count(t => t.StartsWith("SELECT", StringComparison.Ordinal) && t.Contains("\"AspNetUsers\"", StringComparison.Ordinal));
        }
    }

    public void Clear()
    {
        lock (_texts)
            _texts.Clear();
    }

    public int ConsentCommands
    {
        get
        {
            lock (_texts)
                return _texts.Count(IsConsent);
        }
    }

    private static bool IsConsent(string text) =>
        text.Contains("AccountConsents", StringComparison.Ordinal) || text.Contains("ConsentRequirements", StringComparison.Ordinal);

    private void Record(DbCommand command)
    {
        lock (_texts)
            _texts.Add(command.CommandText);
        if (FailOnConsentTables && IsConsent(command.CommandText))
            throw new InvalidOperationException("A consent table was queried: " + command.CommandText);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}

// A second authentication scheme, standing for any sign-in the bearer check never sees. It signs in whoever sends the header.
internal sealed class OtherSchemeHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Name = "Other";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(Request.Headers.ContainsKey("X-Other")
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "other")], Name)), Name))
            : AuthenticateResult.NoResult());
}

public sealed partial class TermsAndFiltersTests : IAsyncDisposable
{
    private sealed class CurrentVersion : IConsentVersionSource
    {
        public string? Current { get; set; } = "v2";

        public List<string?> Locales { get; } = [];

        public Task<string?> GetCurrentVersionAsync(string document, string? locale, CancellationToken cancellationToken)
        {
            Locales.Add(locale);
            return Task.FromResult(Current);
        }
    }

    private sealed class QuietEmails : IRegistrationEmails<IdentityTestUser>
    {
        public Task SendVerificationAsync(IdentityTestUser user, string code, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAlreadyRegisteredAsync(IdentityTestUser user, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendRemovalReminderAsync(IdentityTestUser user, string code, DateTime removalAt, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private const string Key = "terms-test-signing-key-0123456789abcdef";
    private const string Password = "Password1";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    // Tokens are checked against the real clock, so tokens are made before the fake clock moves.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly CurrentVersion _version = new();
    private readonly CommandLog _commands = new();
    private readonly DateTime _start;
    private string? _file;
    private IHost? _host;
    private HttpClient _client = default!;

    public TermsAndFiltersTests()
    {
        _start = _time.GetUtcNow().UtcDateTime;
        _connection.Open();
    }

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

    private async Task StartAsync(bool optIn = true, bool fileDb = false, bool filters = true, Action<AppKitRegistrationOptions<IdentityTestUser>>? configure = null)
    {
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
                        o.AddInterceptors(_commands);
                    });
                    s.AddAppKitIdentity<IdentityTestUser, RegistrationTestDbContext>();
                    s.AddAppKitAuth<RegistrationTestDbContext>(context.Configuration.GetSection("Jwt"));
                    s.AddAppKitJwtBearer<IdentityTestUser>(u => !u.IsDeleted);
                    s.AddAppKitAccounts<IdentityTestUser, RegistrationTestDbContext>();
                    s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, OtherSchemeHandler>(OtherSchemeHandler.Name, _ => { });
                    s.AddAuthorization();
                    s.AddRouting();
                    if (optIn)
                    {
                        s.AddAppKitSelfRegistration<IdentityTestUser, RegistrationTestDbContext>(o =>
                        {
                            o.NewUser = r => new IdentityTestUser { Email = r.Email, UserName = r.UserName };
                            // The hosted cleanup would otherwise read the shared connection while the test uses it.
                            o.RunCleanupInBackground = false;
                            o.ResendCooldown = TimeSpan.Zero;
                            configure?.Invoke(o);
                        });
                        s.AddSingleton<IRegistrationEmails<IdentityTestUser>>(new QuietEmails());
                        s.AddSingleton<IConsentVersionSource>(_version);
                    }
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e =>
                    {
                        if (optIn)
                            e.MapAppKitRegistration<IdentityTestUser, RegistrationTestDbContext>();
                        e.MapAppKitAuth<IdentityTestUser, RegistrationTestDbContext>();
                        e.MapAppKitAccount<IdentityTestUser>();
                        e.MapGet("/me", () => "ok").RequireAuthorization();
                        if (filters)
                        {
                            e.MapGet("/verified", () => "ok").RequireAuthorization().RequireVerifiedEmail();
                            e.MapGet("/terms", () => "ok").RequireAuthorization().RequireCurrentTerms();
                            e.MapGet("/both", () => "ok").RequireAuthorization().RequireVerifiedEmail().RequireCurrentTerms();
                            e.MapGet("/other", () => "ok").RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = OtherSchemeHandler.Name }).RequireCurrentTerms();
                            e.MapGet("/open", () => "ok").AllowAnonymous().RequireVerifiedEmail().RequireCurrentTerms();
                            var group = e.MapGroup("/group").RequireAuthorization().RequireVerifiedEmail();
                            group.MapGet("a", () => "ok");
                            group.MapGet("b", () => "ok");
                        }
                        e.MapGet("/facts", (ClaimsPrincipal user) => new { verified = user.IsEmailVerified(), pending = user.GetPendingConsents() }).RequireAuthorization();
                    });
                }))
            .Build();
        using (var scope = _host.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>().Database.EnsureCreated();
        await _host.StartAsync();
        _client = _host.GetTestClient();
        _commands.Clear();
    }

    private T Db<T>(Func<RegistrationTestDbContext, T> read)
    {
        using var scope = _host!.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>());
    }

    private List<AccountConsent> Rows(string userId) =>
        Db(db => db.AccountConsents.AsNoTracking().Where(c => c.UserId == userId).OrderBy(c => c.Id).ToList());

    // Creates the user with the password, and returns the verification code when the email is not confirmed.
    private async Task<string> SeedAsync(string id, bool verified = true)
    {
        using var scope = _host!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        var user = new IdentityTestUser { Id = id, UserName = id, Email = $"{id}@example.invalid", EmailConfirmed = verified };
        var code = verified ? string.Empty : EmailVerificationCodes.Issue(user, _time);
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        return code;
    }

    private async Task<string> TokenAsync(string id)
    {
        using var scope = _host!.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>().FindByIdAsync(id);
        return scope.ServiceProvider.GetRequiredService<IJwtTokenService>().Build(user!, []);
    }

    private async Task RequireAsync(string version, DateTime effectiveFrom, string document = "terms")
    {
        using var scope = _host!.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IConsentRequirements>().RequireAcceptanceAsync(document, version, effectiveFrom);
    }

    private void Accept(string userId, string version, DateTime at, string document = "terms")
    {
        using var scope = _host!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>();
        db.AccountConsents.Add(new AccountConsent { UserId = userId, Document = document, Version = version, Source = "register", AcceptedAt = at });
        db.SaveChanges();
    }

    // The bearer check keeps its result for a minute. This is what the passing of that minute does.
    private void ExpireUserCheck(string userId) =>
        _host!.Services.GetRequiredService<IMemoryCache>().Remove(IdentityAndBearerExtensions.UserCheckKey(userId));

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? bearer)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> GetAsync(string path, string? bearer = null) => SendAsync(HttpMethod.Get, path, null, bearer);

    private Task<HttpResponseMessage> AcceptTermsAsync(object? body, string? bearer) => SendAsync(HttpMethod.Post, "/api/auth/accept-terms", body, bearer);

    private async Task<string> LoginBodyAsync(string id)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email = $"{id}@example.invalid", password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<JsonElement> LoginAsync(string id) => JsonElement.Parse(await LoginBodyAsync(id));

    private static string[] Pending(JsonElement login) =>
        login.GetProperty("pendingConsents").EnumerateArray().Select(p => $"{p.GetProperty("document").GetString()}={p.GetProperty("version").GetString()}").ToArray();

    // accept-terms

    [Fact]
    public async Task Accepting_the_current_version_records_a_reaccept_row_and_answers_204()
    {
        await StartAsync();
        await SeedAsync("u1");
        var token = await TokenAsync("u1");
        _time.Advance(TimeSpan.FromHours(2));

        var response = await AcceptTermsAsync(new { termsVersion = "v2", locale = "nb" }, token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var row = Assert.Single(Rows("u1"));
        Assert.Equal(("terms", "v2", "reaccept", "nb"), (row.Document, row.Version, row.Source, row.Locale));
        Assert.Equal(_time.GetUtcNow().UtcDateTime, row.AcceptedAt);
    }

    [Fact]
    public async Task Accepting_asks_the_version_source_without_a_locale_and_stores_the_locale_as_sent()
    {
        await StartAsync();
        await SeedAsync("u1");

        await AcceptTermsAsync(new { termsVersion = "v2", locale = "nb" }, await TokenAsync("u1"));

        Assert.NotEmpty(_version.Locales);
        Assert.All(_version.Locales, Assert.Null);
        Assert.Equal("nb", Assert.Single(Rows("u1")).Locale);
    }

    [Fact]
    public async Task Accepting_the_same_version_again_adds_no_row_and_still_answers_204()
    {
        await StartAsync();
        await SeedAsync("u1");
        var token = await TokenAsync("u1");
        Assert.Equal(HttpStatusCode.NoContent, (await AcceptTermsAsync(new { termsVersion = "v2" }, token)).StatusCode);

        var again = await AcceptTermsAsync(new { termsVersion = "v2" }, token);

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Single(Rows("u1"));
    }

    [Fact]
    public async Task A_version_that_is_not_the_current_one_answers_409_termsChanged_and_records_nothing()
    {
        await StartAsync();
        await SeedAsync("u1");

        var response = await AcceptTermsAsync(new { termsVersion = "v1" }, await TokenAsync("u1"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("termsChanged", await ErrorJson.CodeAsync(response));
        Assert.Empty(Rows("u1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_version_is_a_required_field_error(string? version)
    {
        await StartAsync();
        await SeedAsync("u1");

        var response = await AcceptTermsAsync(new { termsVersion = version }, await TokenAsync("u1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["TermsVersion=required"], await ErrorJson.FieldCodesAsync(response));
        Assert.Empty(Rows("u1"));
    }

    [Fact]
    public async Task Without_a_token_accept_terms_answers_401()
    {
        await StartAsync();
        await SeedAsync("u1");

        var response = await AcceptTermsAsync(new { termsVersion = "v2" }, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(Rows("u1"));
    }

    [Fact]
    public async Task Accepting_removes_the_user_check_from_the_cache()
    {
        await StartAsync();
        await SeedAsync("u1");
        var token = await TokenAsync("u1");
        await GetAsync("/me", token);
        var cache = _host!.Services.GetRequiredService<IMemoryCache>();
        Assert.True(cache.TryGetValue(IdentityAndBearerExtensions.UserCheckKey("u1"), out _));

        await AcceptTermsAsync(new { termsVersion = "v2" }, token);

        Assert.False(cache.TryGetValue(IdentityAndBearerExtensions.UserCheckKey("u1"), out _));
    }

    [Fact]
    public async Task Parallel_accepts_of_one_user_all_answer_204_and_leave_one_row()
    {
        await StartAsync(fileDb: true);
        await SeedAsync("u1");
        var token = await TokenAsync("u1");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AcceptTermsAsync(new { termsVersion = "v2" }, token)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Single(Rows("u1"));
    }

    [Fact]
    public async Task With_no_source_version_and_no_requirement_there_is_nothing_to_accept()
    {
        await StartAsync();
        await SeedAsync("u1");
        _version.Current = null;

        var response = await AcceptTermsAsync(new { termsVersion = "anything" }, await TokenAsync("u1"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("noTermsToAccept", await ErrorJson.CodeAsync(response));
        Assert.Empty(Rows("u1"));
    }

    [Fact]
    public async Task With_a_requirement_in_effect_and_no_source_version_only_the_required_version_is_accepted()
    {
        await StartAsync();
        await SeedAsync("u1");
        _version.Current = null;
        await RequireAsync("v3", _start);
        var token = await TokenAsync("u1");

        var wrong = await AcceptTermsAsync(new { termsVersion = "anything" }, token);

        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("termsChanged", await ErrorJson.CodeAsync(wrong));
        Assert.Empty(Rows("u1"));
        Assert.Equal(HttpStatusCode.NoContent, (await AcceptTermsAsync(new { termsVersion = "v3" }, token)).StatusCode);
        Assert.Equal(["v3"], Rows("u1").Select(r => r.Version));
    }

    [Fact]
    public async Task A_requirement_in_effect_wins_over_a_source_version_that_differs()
    {
        await StartAsync();
        await SeedAsync("u1");
        await RequireAsync("v3", _start);
        var token = await TokenAsync("u1");

        var fromSource = await AcceptTermsAsync(new { termsVersion = "v2" }, token);

        Assert.Equal(HttpStatusCode.Conflict, fromSource.StatusCode);
        Assert.Equal("termsChanged", await ErrorJson.CodeAsync(fromSource));
        Assert.Empty(Rows("u1"));
        Assert.Equal(HttpStatusCode.NoContent, (await AcceptTermsAsync(new { termsVersion = "v3" }, token)).StatusCode);
        Assert.Equal(["v3"], Rows("u1").Select(r => r.Version));
        Assert.Empty(Pending(await LoginAsync("u1")));
    }

    [Fact]
    public async Task A_requirement_in_effect_is_met_only_by_its_own_version_while_the_source_serves_an_older_one()
    {
        await StartAsync();
        _version.Current = "v1";
        await SeedAsync("u1");
        Accept("u1", "v1", _start);
        await RequireAsync("v2", _start);
        var token = await TokenAsync("u1");

        Assert.Equal(["terms=v2"], Pending(await LoginAsync("u1")));
        var oldFromAccept = await AcceptTermsAsync(new { termsVersion = "v1" }, token);
        Assert.Equal(HttpStatusCode.Conflict, oldFromAccept.StatusCode);
        Assert.Equal("termsChanged", await ErrorJson.CodeAsync(oldFromAccept));
        Assert.Equal(["terms=v2"], Pending(await LoginAsync("u1")));

        var oldFromRegister = await _client.PostAsJsonAsync("/api/auth/register", new { email = "new@example.invalid", password = Password, ageConfirmed = true, termsAccepted = true, termsVersion = "v1" });
        Assert.Equal(HttpStatusCode.Conflict, oldFromRegister.StatusCode);
        Assert.Equal("termsChanged", await ErrorJson.CodeAsync(oldFromRegister));
        Assert.Empty(Db(db => db.Users.Where(u => u.Email == "new@example.invalid").ToList()));

        var registered = await _client.PostAsJsonAsync("/api/auth/register", new { email = "new@example.invalid", password = Password, ageConfirmed = true, termsAccepted = true, termsVersion = "v2" });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var created = Assert.Single(Db(db => db.Users.Where(u => u.Email == "new@example.invalid").ToList()));
        Assert.Equal(["v2"], Rows(created.Id).Where(r => r.Document == "terms").Select(r => r.Version));

        Assert.Equal(HttpStatusCode.NoContent, (await AcceptTermsAsync(new { termsVersion = "v2" }, token)).StatusCode);
        Assert.Equal(["v1", "v2"], Rows("u1").Select(r => r.Version));
        Assert.Empty(Pending(await LoginAsync("u1")));
    }

    [Fact]
    public async Task A_requirement_that_is_not_in_effect_yet_can_be_accepted_early_when_the_source_has_no_version()
    {
        await StartAsync();
        await SeedAsync("u1");
        _version.Current = null;
        await RequireAsync("v3", _start.AddDays(7));
        var token = await TokenAsync("u1");

        Assert.Equal("termsChanged", await ErrorJson.CodeAsync(await AcceptTermsAsync(new { termsVersion = "v2" }, token)));
        Assert.Equal(HttpStatusCode.NoContent, (await AcceptTermsAsync(new { termsVersion = "v3" }, token)).StatusCode);

        _time.Advance(TimeSpan.FromDays(8));
        ExpireUserCheck("u1");
        Assert.Empty(Pending(await LoginAsync("u1")));
    }

    [Fact]
    public async Task An_acceptance_of_some_other_version_does_not_clear_a_pending_requirement()
    {
        await StartAsync();
        await SeedAsync("u1");
        await RequireAsync("v3", _start);
        _time.Advance(TimeSpan.FromHours(1));
        Accept("u1", "made-up", _start.AddHours(1));
        ExpireUserCheck("u1");

        Assert.Equal(["terms=v3"], Pending(await LoginAsync("u1")));
    }

    [Fact]
    public async Task An_acceptance_of_the_required_version_clears_it()
    {
        await StartAsync();
        await SeedAsync("u1");
        await RequireAsync("v3", _start);
        _time.Advance(TimeSpan.FromHours(1));
        Accept("u1", "v3", _start.AddHours(1));
        ExpireUserCheck("u1");

        Assert.Empty(Pending(await LoginAsync("u1")));
    }

    [Fact]
    public async Task An_acceptance_of_the_source_version_does_not_clear_a_requirement_of_another_version()
    {
        await StartAsync();
        await SeedAsync("u1");
        await RequireAsync("v3", _start);
        _time.Advance(TimeSpan.FromHours(1));
        Accept("u1", "v2", _start.AddHours(1));
        ExpireUserCheck("u1");

        Assert.Equal(["terms=v3"], Pending(await LoginAsync("u1")));
    }

    [Fact]
    public async Task A_version_accepted_before_the_app_marked_it_does_not_leave_the_user_pending()
    {
        await StartAsync();
        await SeedAsync("u1");
        Accept("u1", "v2", _start.AddDays(-30));
        var token = await TokenAsync("u1");
        _time.Advance(TimeSpan.FromDays(1));

        await RequireAsync("v2", _start);
        ExpireUserCheck("u1");

        Assert.Empty(Pending(await LoginAsync("u1")));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/terms", token)).StatusCode);
    }

    [Fact]
    public async Task A_user_who_accepted_only_an_older_version_is_pending_and_accept_terms_clears_it()
    {
        await StartAsync();
        await SeedAsync("u1");
        Accept("u1", "v1", _start.AddDays(-30));
        var token = await TokenAsync("u1");
        _time.Advance(TimeSpan.FromDays(1));

        await RequireAsync("v2", _start);
        ExpireUserCheck("u1");
        Assert.Equal(["terms=v2"], Pending(await LoginAsync("u1")));

        Assert.Equal(HttpStatusCode.NoContent, (await AcceptTermsAsync(new { termsVersion = "v2" }, token)).StatusCode);
        Assert.Empty(Pending(await LoginAsync("u1")));
    }

    [Fact]
    public async Task An_acceptance_made_before_the_requirement_takes_effect_counts()
    {
        await StartAsync();
        await SeedAsync("u1");
        Accept("u1", "v2", _start.AddDays(-1));
        await RequireAsync("v2", _start.AddDays(7));

        _time.Advance(TimeSpan.FromDays(8));
        ExpireUserCheck("u1");

        Assert.Empty(Pending(await LoginAsync("u1")));
    }

    // The default IConsentRequirements

    [Fact]
    public async Task Requiring_acceptance_writes_a_row_marked_now_and_a_second_call_changes_nothing()
    {
        await StartAsync();
        var effective = _start.AddDays(7);
        await RequireAsync("v2", effective);
        _time.Advance(TimeSpan.FromHours(1));

        await RequireAsync("v2", effective.AddDays(30));

        var row = Assert.Single(Db(db => db.ConsentRequirements.AsNoTracking().ToList()));
        Assert.Equal(("terms", "v2", _start, effective), (row.Document, row.Version, row.MarkedAt, row.EffectiveFrom));
    }

    [Fact]
    public async Task Requiring_acceptance_clears_the_cached_requirement_rows()
    {
        await StartAsync();
        await SeedAsync("u1");
        var token = await TokenAsync("u1");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/terms", token)).StatusCode);
        ExpireUserCheck("u1");

        await RequireAsync("v2", _start);
        ExpireUserCheck("u1");

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/terms", token)).StatusCode);
    }

    // The terms filter

    [Fact]
    public async Task A_requirement_before_its_effective_time_blocks_nobody_and_after_it_the_filter_answers_403_and_login_lists_it()
    {
        await StartAsync();
        await SeedAsync("u1");
        Accept("u1", "v1", _start);
        var token = await TokenAsync("u1");
        _time.Advance(TimeSpan.FromDays(1));
        await RequireAsync("v2", _start.AddDays(8));

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/terms", token)).StatusCode);
        Assert.Empty(Pending(await LoginAsync("u1")));

        _time.Advance(TimeSpan.FromDays(8));
        ExpireUserCheck("u1");
        var response = await GetAsync("/terms", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("termsNotAccepted", await ErrorJson.CodeAsync(response));
        Assert.Equal(["terms=v2"], Pending(await LoginAsync("u1")));
    }

    [Fact]
    public async Task The_terms_answer_has_a_message_and_the_code_and_nothing_else()
    {
        await StartAsync();
        await SeedAsync("u1");
        await RequireAsync("v2", _start);

        var response = await GetAsync("/terms", await TokenAsync("u1"));

        var json = JsonElement.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["message", "code"], json.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task A_user_with_no_acceptance_at_all_is_pending_once_a_requirement_is_in_effect()
    {
        await StartAsync();
        await SeedAsync("u1");
        var token = await TokenAsync("u1");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/terms", token)).StatusCode);
        ExpireUserCheck("u1");

        await RequireAsync("v2", _start);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/terms", token)).StatusCode);
    }

    [Fact]
    public async Task An_acceptance_made_between_the_marking_and_the_effective_time_counts()
    {
        await StartAsync();
        await SeedAsync("u1");
        var token = await TokenAsync("u1");
        await RequireAsync("v2", _start.AddDays(7));
        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(HttpStatusCode.NoContent, (await AcceptTermsAsync(new { termsVersion = "v2" }, token)).StatusCode);

        _time.Advance(TimeSpan.FromDays(7));
        ExpireUserCheck("u1");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/terms", token)).StatusCode);
        Assert.Empty(Pending(await LoginAsync("u1")));
    }

    [Fact]
    public async Task Accepting_clears_the_pending_state_at_once()
    {
        await StartAsync();
        await SeedAsync("u1");
        await RequireAsync("v2", _start);
        var token = await TokenAsync("u1");
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/terms", token)).StatusCode);
        Assert.Equal(["terms=v2"], Pending(await LoginAsync("u1")));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, (await AcceptTermsAsync(new { termsVersion = "v2" }, token)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/terms", token)).StatusCode);
        Assert.Empty(Pending(await LoginAsync("u1")));
    }

    [Fact]
    public async Task Two_requirements_for_one_document_use_the_newest()
    {
        await StartAsync();
        await SeedAsync("u1");
        _version.Current = "v3";
        Accept("u1", "v1", _start);
        var token = await TokenAsync("u1");
        _time.Advance(TimeSpan.FromDays(1));
        await RequireAsync("v2", _start);
        Accept("u1", "v2", _start.AddDays(1).AddHours(12));
        _time.Advance(TimeSpan.FromDays(1));
        await RequireAsync("v3", _start);

        Assert.Equal(["terms=v3"], Pending(await LoginAsync("u1")));
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/terms", token)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await AcceptTermsAsync(new { termsVersion = "v3" }, token)).StatusCode);
        Assert.Empty(Pending(await LoginAsync("u1")));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/terms", token)).StatusCode);
    }

    // The email filter

    [Fact]
    public async Task An_unverified_user_gets_403_emailNotVerified_and_a_verified_user_passes_right_after_verifying()
    {
        await StartAsync();
        var code = await SeedAsync("u1", verified: false);
        var before = await TokenAsync("u1");
        var blocked = await GetAsync("/verified", before);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("emailNotVerified", await ErrorJson.CodeAsync(blocked));
        var json = JsonElement.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Equal(["message", "code"], json.EnumerateObject().Select(p => p.Name).ToArray());

        var verified = await SendAsync(HttpMethod.Post, "/api/auth/verify-email", new { code }, before);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/verified", await TokenAsync("u1"))).StatusCode);
    }

    [Fact]
    public async Task A_verified_user_passes_the_email_filter_and_the_helpers_say_so()
    {
        await StartAsync();
        await SeedAsync("u1");
        var token = await TokenAsync("u1");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/verified", token)).StatusCode);
        var facts = JsonElement.Parse(await (await GetAsync("/facts", token)).Content.ReadAsStringAsync());
        Assert.True(facts.GetProperty("verified").GetBoolean());
        Assert.Equal(0, facts.GetProperty("pending").GetArrayLength());
    }

    [Fact]
    public async Task The_helpers_give_the_two_facts_without_a_filter()
    {
        await StartAsync();
        await SeedAsync("u1", verified: false);
        await RequireAsync("v2", _start);

        var facts = JsonElement.Parse(await (await GetAsync("/facts", await TokenAsync("u1"))).Content.ReadAsStringAsync());

        Assert.False(facts.GetProperty("verified").GetBoolean());
        var pending = Assert.Single(facts.GetProperty("pending").EnumerateArray());
        Assert.Equal(("terms", "v2"), (pending.GetProperty("document").GetString(), pending.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task An_anonymous_request_is_left_to_the_normal_authorization_answer()
    {
        await StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/verified")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/terms")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/open")).StatusCode);
    }

    // The user check keeps a user for a minute, so after a verify or a reset another instance may still hold the old stamp.

    private async Task RotateStampAsync(string userId)
    {
        using var scope = _host!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        Assert.True((await users.UpdateSecurityStampAsync((await users.FindByIdAsync(userId))!)).Succeeded);
    }

    private async Task<HttpStatusCode> MeAsync(string token) => (await GetAsync("/me", token)).StatusCode;

    [Fact]
    public async Task A_fresh_token_is_not_refused_by_a_cache_entry_with_an_older_stamp()
    {
        await StartAsync();
        await SeedAsync("u1");
        Assert.Equal(HttpStatusCode.OK, await MeAsync(await TokenAsync("u1")));
        await RotateStampAsync("u1");

        Assert.Equal(HttpStatusCode.OK, await MeAsync(await TokenAsync("u1")));
    }

    [Fact]
    public async Task A_token_with_an_older_stamp_is_still_refused_after_the_refresh()
    {
        await StartAsync();
        await SeedAsync("u1");
        var old = await TokenAsync("u1");
        Assert.Equal(HttpStatusCode.OK, await MeAsync(old));
        await RotateStampAsync("u1");
        Assert.Equal(HttpStatusCode.OK, await MeAsync(await TokenAsync("u1")));

        _time.Advance(TimeSpan.FromSeconds(6));
        _commands.Clear();
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(old));
        Assert.Equal(1, _commands.UserReads);
    }

    [Fact]
    public async Task A_stale_cache_entry_is_looked_at_again_once_per_interval_and_not_more_often()
    {
        await StartAsync();
        await SeedAsync("u1");
        var old = await TokenAsync("u1");
        Assert.Equal(HttpStatusCode.OK, await MeAsync(old));
        await RotateStampAsync("u1");
        Assert.Equal(HttpStatusCode.OK, await MeAsync(await TokenAsync("u1")));

        // A burst of old tokens inside the interval reads nothing. After it, the first reads once and the rest read nothing.
        _commands.Clear();
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(old));
        Assert.Equal(0, _commands.UserReads);
        _time.Advance(TimeSpan.FromSeconds(6));
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(old));
        Assert.Equal(1, _commands.UserReads);

        // The stamp changes again inside the interval, so the fresh token waits for the next look.
        await RotateStampAsync("u1");
        var third = await TokenAsync("u1");
        _commands.Clear();
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(third));
        Assert.Equal(0, _commands.UserReads);
        _time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(third));
        Assert.Equal(1, _commands.UserReads);
    }

    [Fact]
    public async Task A_token_with_the_current_stamp_causes_no_extra_read()
    {
        await StartAsync();
        await SeedAsync("u1");
        var token = await TokenAsync("u1");
        Assert.Equal(HttpStatusCode.OK, await MeAsync(token));
        _commands.Clear();

        Assert.Equal(HttpStatusCode.OK, await MeAsync(token));
        Assert.Equal(0, _commands.UserReads);
    }

    [Fact]
    public async Task An_app_that_did_not_opt_in_does_no_extra_read_for_a_token_with_another_stamp()
    {
        await StartAsync(optIn: false, filters: false);
        await SeedAsync("u1");
        var token = Craft("u1", "not-the-stamp");
        Assert.Equal(HttpStatusCode.OK, await MeAsync(token));
        _commands.Clear();

        Assert.Equal(HttpStatusCode.OK, await MeAsync(token));
        Assert.Equal(0, _commands.UserReads);
    }

    private static string Craft(string id, string stampClaim)
    {
        var handler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
        var now = DateTime.UtcNow;
        return handler.WriteToken(handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", id), new Claim("sst", stampClaim)]),
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddHours(1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256Signature),
            Issuer = "appkit",
            Audience = "appkit",
        }));
    }

    [Fact]
    public async Task A_signed_in_principal_the_bearer_check_never_saw_is_refused_by_the_terms_filter()
    {
        await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/other");
        request.Headers.Add("X-Other", "1");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("termsNotAccepted", await ErrorJson.CodeAsync(response));
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/other")).StatusCode);
    }

    [Fact]
    public async Task A_user_the_bearer_check_saw_with_nothing_pending_passes_the_terms_filter()
    {
        await StartAsync();
        await SeedAsync("u1");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/terms", await TokenAsync("u1"))).StatusCode);
    }

    [Theory]
    [InlineData("/group/a")]
    [InlineData("/group/b")]
    public async Task A_filter_on_a_group_covers_its_endpoints(string path)
    {
        await StartAsync();
        await SeedAsync("u1", verified: false);

        var response = await GetAsync(path, await TokenAsync("u1"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("emailNotVerified", await ErrorJson.CodeAsync(response));
    }

    [Fact]
    public async Task A_filter_in_an_app_that_did_not_opt_in_fails_with_a_clear_message_when_the_endpoints_are_built()
    {
        // The endpoints are built when the host starts, or at the first request at the latest.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await StartAsync(optIn: false);
            await GetAsync("/me");
        });

        Assert.Contains("AddAppKitSelfRegistration", error.ToString(), StringComparison.Ordinal);
    }

    // Login and refresh

    [GeneratedRegex("""^\{"token":"[^"]+","refreshToken":"[^"]+"\}$""")]
    private static partial Regex OldLoginBody();

    [GeneratedRegex("""^\{"token":"[^"]+","refreshToken":"[^"]+","emailVerified":(true|false),"pendingConsents":\[\]\}$""")]
    private static partial Regex NewLoginBodyWithoutPending();

    [Fact]
    public async Task An_app_that_did_not_opt_in_gets_the_old_login_and_refresh_body_and_touches_no_consent_table()
    {
        await StartAsync(optIn: false, filters: false);
        await SeedAsync("u1");
        _commands.FailOnConsentTables = true;

        var body = await LoginBodyAsync("u1");

        Assert.Matches(OldLoginBody(), body);
        var login = JsonElement.Parse(body);
        var token = login.GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/me", token)).StatusCode);
        var refreshed = await _client.PostAsJsonAsync("/api/auth/refresh-token", new { token, refreshToken = login.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.Matches(OldLoginBody(), await refreshed.Content.ReadAsStringAsync());
        Assert.Equal(0, _commands.ConsentCommands);
    }

    [Fact]
    public async Task An_opted_in_app_adds_the_two_fields_to_login_and_refresh()
    {
        await StartAsync();
        await SeedAsync("u1");

        var body = await LoginBodyAsync("u1");

        Assert.Matches(NewLoginBodyWithoutPending(), body);
        var login = JsonElement.Parse(body);
        var token = login.GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/me", token)).StatusCode);
        Assert.True(_commands.ConsentCommands > 0);
        var refreshed = await _client.PostAsJsonAsync("/api/auth/refresh-token", new { token, refreshToken = login.GetProperty("refreshToken").GetString() });
        Assert.Matches(NewLoginBodyWithoutPending(), await refreshed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_and_refresh_tell_an_unverified_user_with_a_pending_document_both_facts()
    {
        await StartAsync();
        await SeedAsync("u1", verified: false);
        await RequireAsync("v2", _start);

        var login = await LoginAsync("u1");

        Assert.False(login.GetProperty("emailVerified").GetBoolean());
        Assert.Equal(["terms=v2"], Pending(login));
        var refreshed = await _client.PostAsJsonAsync("/api/auth/refresh-token",
            new { token = login.GetProperty("token").GetString(), refreshToken = login.GetProperty("refreshToken").GetString() });
        var after = JsonElement.Parse(await refreshed.Content.ReadAsStringAsync());
        Assert.False(after.GetProperty("emailVerified").GetBoolean());
        Assert.Equal(["terms=v2"], Pending(after));
    }

    // What a pending or unverified user can still do

    [Theory]
    [InlineData("accept-terms")]
    [InlineData("verify-email")]
    [InlineData("resend-verification")]
    [InlineData("refresh-token")]
    [InlineData("logout")]
    [InlineData("export")]
    [InlineData("delete")]
    public async Task An_unverified_user_who_must_accept_terms_can_still_call(string route)
    {
        await StartAsync();
        var code = await SeedAsync("u1", verified: false);
        await RequireAsync("v2", _start);
        var login = await LoginAsync("u1");
        var token = login.GetProperty("token").GetString()!;
        var refreshToken = login.GetProperty("refreshToken").GetString()!;
        var emailBlock = await GetAsync("/verified", token);
        Assert.Equal("emailNotVerified", await ErrorJson.CodeAsync(emailBlock));
        Assert.Equal("termsNotAccepted", await ErrorJson.CodeAsync(await GetAsync("/terms", token)));

        var (expected, response) = route switch
        {
            "accept-terms" => (HttpStatusCode.NoContent, await AcceptTermsAsync(new { termsVersion = "v2" }, token)),
            "verify-email" => (HttpStatusCode.OK, await SendAsync(HttpMethod.Post, "/api/auth/verify-email", new { code }, token)),
            "resend-verification" => (HttpStatusCode.OK, await SendAsync(HttpMethod.Post, "/api/auth/resend-verification", null, token)),
            "refresh-token" => (HttpStatusCode.OK, await SendAsync(HttpMethod.Post, "/api/auth/refresh-token", new { token, refreshToken }, null)),
            "logout" => (HttpStatusCode.NoContent, await SendAsync(HttpMethod.Post, "/api/auth/logout", new { refreshToken }, null)),
            "export" => (HttpStatusCode.OK, await GetAsync("/api/users/u1/export", token)),
            _ => (HttpStatusCode.NoContent, await SendAsync(HttpMethod.Delete, "/api/users/u1/account", null, token)),
        };

        Assert.Equal(expected, response.StatusCode);
    }
}

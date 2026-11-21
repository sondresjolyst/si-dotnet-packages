using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public class ProfileRow
{
    public int Id { get; set; }
    public string UserId { get; set; } = default!;
}

public class RegistrationTestDbContext(DbContextOptions<RegistrationTestDbContext> options)
    : IdentityDbContext<IdentityTestUser>(options), IAuthDbContext, IAccountConsentDbContext
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AccountConsent> AccountConsents => Set<AccountConsent>();
    public DbSet<ConsentRequirement> ConsentRequirements => Set<ConsentRequirement>();
    public DbSet<SignUpMailLog> SignUpMailLogs => Set<SignUpMailLog>();
    public DbSet<ProfileRow> Profiles => Set<ProfileRow>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.AddAppKitRefreshTokens<IdentityTestUser>();
        builder.AddAppKitAccountConsents<IdentityTestUser>();
    }
}

public sealed class RegistrationEndpointsTests : IAsyncDisposable
{
    private sealed class FakeRegistrationEmails : IRegistrationEmails<IdentityTestUser>
    {
        public List<(string UserId, string Code, string? Locale)> Verifications { get; } = [];
        public List<string> AlreadyRegistered { get; } = [];

        public Task SendVerificationAsync(IdentityTestUser user, string code, string? locale, CancellationToken cancellationToken)
        {
            Verifications.Add((user.Id, code, locale));
            return Task.CompletedTask;
        }

        public Task SendAlreadyRegisteredAsync(IdentityTestUser user, string? locale, CancellationToken cancellationToken)
        {
            AlreadyRegistered.Add(user.Id);
            return Task.CompletedTask;
        }

        public Task SendRemovalReminderAsync(IdentityTestUser user, string code, DateTime removalAt, string? locale, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public int Total => Verifications.Count + AlreadyRegistered.Count;
    }

    private sealed class ProfileHandler(RegistrationTestDbContext db) : IRegistrationHandler<IdentityTestUser>
    {
        public Task OnRegisteringAsync(IdentityTestUser user, AppKitRegistration registration, CancellationToken cancellationToken)
        {
            db.Profiles.Add(new ProfileRow { UserId = user.Id });
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler(RegistrationTestDbContext db) : IRegistrationHandler<IdentityTestUser>
    {
        public async Task OnRegisteringAsync(IdentityTestUser user, AppKitRegistration registration, CancellationToken cancellationToken)
        {
            db.Profiles.Add(new ProfileRow { UserId = user.Id });
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("The app's handler failed.");
        }
    }

    private sealed class FixedVersions(string? version) : IConsentVersionSource
    {
        public Task<string?> GetCurrentVersionAsync(string document, string? locale, CancellationToken cancellationToken) =>
            Task.FromResult(document == "terms" ? version : null);
    }

    private sealed class CountingHasher : PasswordHasher<IdentityTestUser>
    {
        public int Hashes;

        public override string HashPassword(IdentityTestUser user, string password)
        {
            Interlocked.Increment(ref Hashes);
            return base.HashPassword(user, password);
        }
    }

    // Inserts a verified user with the same email just before the sign-up transaction starts, like a sign-up that raced.
    private sealed class RaceInterceptor : DbTransactionInterceptor
    {
        public string? Email;

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (Email is { } email)
            {
                Email = null;
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO AspNetUsers (Id, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed, " +
                    "PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount, FirstName, LastName, CreatedAt, " +
                    "IsDeleted, EmailVerificationAttempts, PasswordResetAttempts) VALUES ('racer', $e, $n, $e, $n, 1, 0, 0, 1, 0, '', '', '2026-01-01', 0, 0, 0)";
                var e = command.CreateParameter();
                e.ParameterName = "$e";
                e.Value = email;
                var n = command.CreateParameter();
                n.ParameterName = "$n";
                n.Value = email.ToUpperInvariant();
                command.Parameters.Add(e);
                command.Parameters.Add(n);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            return await base.TransactionStartingAsync(connection, eventData, result, cancellationToken);
        }
    }

    private const string Password = "Password1";
    private const string NewEmail = "new@example.invalid";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeRegistrationEmails _emails = new();
    private readonly CountingHasher _hasher = new();
    private readonly RaceInterceptor _race = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeLogCollector _logs = new();
    private IHost? _host;
    private HttpClient _client = default!;

    public RegistrationEndpointsTests() => _connection.Open();

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await _connection.DisposeAsync();
    }

    private async Task StartAsync(
        Action<AppKitRegistrationOptions<IdentityTestUser>>? configure = null,
        string? termsVersion = "v1", bool versionSource = true, bool handler = false, bool throwingHandler = false,
        bool optIn = true, IPAddress? remoteIp = null, bool userValidator = true)
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddLogging(b => b.AddProvider(new FakeLoggerProvider(_logs)));
                    s.AddSingleton<TimeProvider>(_time);
                    s.AddDbContext<RegistrationTestDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_race));
                    s.AddAppKitIdentity<IdentityTestUser, RegistrationTestDbContext>();
                    s.AddSingleton<IPasswordHasher<IdentityTestUser>>(_hasher);
                    if (!userValidator)
                        s.RemoveAll<IUserValidator<IdentityTestUser>>();
                    s.AddRouting();
                    if (!optIn)
                        return;
                    s.AddAppKitSelfRegistration<IdentityTestUser, RegistrationTestDbContext>(o =>
                    {
                        o.NewUser = r => new IdentityTestUser { Email = r.Email, UserName = r.UserName, FirstName = r.FirstName, LastName = r.LastName };
                        configure?.Invoke(o);
                    });
                    s.AddSingleton<IRegistrationEmails<IdentityTestUser>>(_emails);
                    if (versionSource)
                        s.AddSingleton<IConsentVersionSource>(new FixedVersions(termsVersion));
                    if (handler)
                        s.AddScoped<IRegistrationHandler<IdentityTestUser>, ProfileHandler>();
                    if (throwingHandler)
                        s.AddScoped<IRegistrationHandler<IdentityTestUser>, ThrowingHandler>();
                })
                .Configure(app =>
                {
                    if (remoteIp is not null)
                        app.Use((http, next) =>
                        {
                            http.Connection.RemoteIpAddress = remoteIp;
                            return next(http);
                        });
                    app.UseRouting();
                    app.UseEndpoints(e =>
                    {
                        if (optIn)
                            e.MapAppKitRegistration<IdentityTestUser, RegistrationTestDbContext>();
                    });
                }))
            .Build();
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>();
            db.Database.EnsureCreated();
            db.Roles.Add(new IdentityRole("Member") { NormalizedName = "MEMBER" });
            db.SaveChanges();
        }
        await _host.StartAsync();
        _client = _host.GetTestClient();
    }

    private static Dictionary<string, object?> Body(string email = NewEmail, params (string Key, object? Value)[] changes)
    {
        var body = new Dictionary<string, object?>
        {
            ["email"] = email,
            ["password"] = Password,
            ["ageConfirmed"] = true,
            ["termsAccepted"] = true,
            ["termsVersion"] = "v1",
            ["locale"] = "nb",
        };
        foreach (var (key, value) in changes)
            body[key] = value;
        return body;
    }

    private Task<HttpResponseMessage> RegisterAsync(Dictionary<string, object?>? body = null) =>
        _client.PostAsJsonAsync("/api/auth/register", body ?? Body());

    private T Db<T>(Func<RegistrationTestDbContext, T> read)
    {
        using var scope = _host!.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>());
    }

    private async Task<IdentityTestUser> SeedAsync(string id, bool verified, bool deleted = false, string? code = null)
    {
        using var scope = _host!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        var user = new IdentityTestUser
        {
            Id = id, UserName = id, Email = $"{id}@example.invalid", EmailConfirmed = verified, IsDeleted = deleted,
        };
        if (code is not null)
            EmailVerificationCodes.Issue(user);
        Assert.True((await users.CreateAsync(user, "Original1")).Succeeded);
        _hasher.Hashes = 0;
        return user;
    }

    private IdentityTestUser Stored(string id) => Db(db => db.Users.AsNoTracking().Single(u => u.Id == id));

    private static async Task<string> TextAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    // Success

    [Fact]
    public async Task A_new_email_creates_an_unverified_user_with_consents_role_handler_rows_and_one_email()
    {
        await StartAsync(o => o.Role = "Member", handler: true);

        var response = await RegisterAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = Db(db => db.Users.Single());
        Assert.Equal(NewEmail, user.Email);
        Assert.False(user.EmailConfirmed);
        Assert.NotNull(user.PasswordHash);
        Assert.Equal(["Member"], Db(db => (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == user.Id select r.Name).ToList()));
        Assert.Equal(user.Id, Db(db => db.Profiles.Single().UserId));
        var consents = Db(db => db.AccountConsents.OrderBy(c => c.Document).ToList());
        Assert.Equal([("age", "16"), ("terms", "v1")], consents.Select(c => (c.Document, c.Version)));
        Assert.All(consents, c =>
        {
            Assert.Equal(user.Id, c.UserId);
            Assert.Equal("register", c.Source);
            Assert.Equal("nb", c.Locale);
            Assert.Null(c.Ip);
        });
        var (sentTo, code, locale) = Assert.Single(_emails.Verifications);
        Assert.Equal(user.Id, sentTo);
        Assert.Equal("nb", locale);
        Assert.Equal(VerificationCodes.Hash(code), user.EmailVerificationCodeHash);
        Assert.Equal(1, _emails.Total);
    }

    [Fact]
    public async Task The_age_row_takes_its_version_from_the_minimum_age()
    {
        await StartAsync(o => o.MinimumAge = 18);

        await RegisterAsync();

        Assert.Equal("18", Db(db => db.AccountConsents.Single(c => c.Document == "age").Version));
    }

    [Fact]
    public async Task The_terms_row_uses_the_terms_document_option()
    {
        await StartAsync(o => o.TermsDocument = "tos");

        await RegisterAsync();

        Assert.Equal(["age", "tos"], Db(db => db.AccountConsents.Select(c => c.Document).OrderBy(d => d).ToList()));
    }

    [Fact]
    public async Task A_throwing_handler_leaves_nothing_behind_and_sends_no_email()
    {
        await StartAsync(throwingHandler: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RegisterAsync());

        Assert.Equal(0, Db(db => db.Users.Count()));
        Assert.Equal(0, Db(db => db.Profiles.Count()));
        Assert.Equal(0, Db(db => db.AccountConsents.Count()));
        Assert.Equal(0, _emails.Total);
    }

    [Fact]
    public async Task The_names_and_user_name_reach_the_new_user_when_the_options_ask_for_them()
    {
        await StartAsync(o => { o.RequireNames = true; o.AllowUserName = true; });

        var response = await RegisterAsync(Body(NewEmail, ("firstName", "Ada"), ("lastName", "Lovelace"), ("userName", "ada")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = Db(db => db.Users.Single());
        Assert.Equal(("Ada", "Lovelace", "ada"), (user.FirstName, user.LastName, user.UserName));
    }

    [Fact]
    public async Task Without_a_user_name_the_email_is_the_user_name_and_names_are_ignored_when_not_required()
    {
        await StartAsync();

        await RegisterAsync(Body(NewEmail, ("firstName", "Ada"), ("userName", "ada")));

        var user = Db(db => db.Users.Single());
        Assert.Equal((NewEmail, ""), (user.UserName, user.FirstName));
    }

    // Field errors

    public static TheoryData<string, object?, string, string?> FieldCases => new()
    {
        { "email", null, "Email=required", null },
        { "email", " ", "Email=required", null },
        { "email", "nope", "Email=invalidEmail", null },
        { "email", "a@a.a@a.a", "Email=invalidEmail", null },
        { "password", null, "Password=required", null },
        { "termsVersion", null, "TermsVersion=required", null },
        { "termsVersion", new string('v', 65), "TermsVersion=tooLong", "TermsVersion=64" },
        { "locale", new string('n', 17), "Locale=tooLong", "Locale=16" },
        { "password", new string('A', 129), "Password=tooLong", "Password=128" },
        { "ageConfirmed", false, "AgeConfirmed=ageNotConfirmed", null },
        { "termsAccepted", false, "TermsAccepted=termsNotAccepted", null },
    };

    [Theory]
    [MemberData(nameof(FieldCases))]
    public async Task Each_field_error_carries_its_code_and_creates_nothing(string field, object? value, string code, string? limit)
    {
        await StartAsync();

        var response = await RegisterAsync(Body(NewEmail, (field, value)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([code], await ErrorJson.FieldCodesAsync(response));
        Assert.Equal(limit is null ? [] : new[] { limit }, await ErrorJson.LimitsAsync(response));
        Assert.Equal(0, Db(db => db.Users.Count()));
        Assert.Equal(0, _emails.Total);
    }

    [Fact]
    public async Task An_email_longer_than_200_characters_is_invalid()
    {
        await StartAsync();

        var response = await RegisterAsync(Body(new string('a', 196) + "@a.no"));

        Assert.Equal(["Email=invalidEmail"], await ErrorJson.FieldCodesAsync(response));
    }

    [Fact]
    public async Task The_age_and_terms_messages_are_in_English()
    {
        await StartAsync();

        var response = await RegisterAsync(Body(NewEmail, ("ageConfirmed", false), ("termsAccepted", false)));

        Assert.Equal(["AgeConfirmed=ageNotConfirmed", "TermsAccepted=termsNotAccepted"], await ErrorJson.FieldCodesAsync(response));
        Assert.Equal(["You must confirm that you are at least 16 years old."], await ErrorJson.MessagesAsync(response, "AgeConfirmed"));
        Assert.Equal(["You must accept the terms."], await ErrorJson.MessagesAsync(response, "TermsAccepted"));
    }

    [Fact]
    public async Task Names_are_checked_only_when_the_option_asks_for_them()
    {
        await StartAsync(o => o.RequireNames = true);

        var response = await RegisterAsync(Body(NewEmail, ("lastName", new string('x', 51))));

        Assert.Equal(["FirstName=required", "LastName=tooLong"], await ErrorJson.FieldCodesAsync(response));
        Assert.Equal(["LastName=50"], await ErrorJson.LimitsAsync(response));
    }

    [Fact]
    public async Task A_long_user_name_is_refused_only_when_the_option_allows_user_names()
    {
        await StartAsync(o => o.AllowUserName = true);

        var response = await RegisterAsync(Body(NewEmail, ("userName", new string('x', 51))));

        Assert.Equal(["UserName=tooLong"], await ErrorJson.FieldCodesAsync(response));
        Assert.Equal(["UserName=50"], await ErrorJson.LimitsAsync(response));
    }

    [Fact]
    public async Task A_user_name_nobody_asked_for_is_not_checked()
    {
        await StartAsync();

        var response = await RegisterAsync(Body(NewEmail, ("userName", new string('x', 51))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_weak_password_gets_Identitys_errors_with_the_existing_password_codes()
    {
        await StartAsync();

        var response = await RegisterAsync(Body(NewEmail, ("password", "short")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["Password=passwordTooShort,passwordRequiresDigit,passwordRequiresUpper"], await ErrorJson.FieldCodesAsync(response));
        Assert.Equal(["Password=8"], await ErrorJson.LimitsAsync(response));
        Assert.Equal(0, Db(db => db.Users.Count()));
    }

    [Fact]
    public async Task Password_errors_read_the_same_for_a_new_and_an_existing_email()
    {
        await StartAsync();
        await SeedAsync("taken", verified: true);

        var forNew = await RegisterAsync(Body(NewEmail, ("password", "short")));
        var forExisting = await RegisterAsync(Body("taken@example.invalid", ("password", "short")));

        Assert.Equal(HttpStatusCode.BadRequest, forNew.StatusCode);
        Assert.Equal(forNew.StatusCode, forExisting.StatusCode);
        Assert.Equal(await TextAsync(forNew), await TextAsync(forExisting));
        Assert.Equal(0, _emails.Total);
    }

    [Fact]
    public async Task All_the_checks_without_an_account_report_together_before_the_terms_version_is_read()
    {
        await StartAsync(termsVersion: "v2");

        var response = await RegisterAsync(Body(NewEmail, ("password", "short"), ("ageConfirmed", false)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["AgeConfirmed=ageNotConfirmed", "Password=passwordTooShort,passwordRequiresDigit,passwordRequiresUpper"], await ErrorJson.FieldCodesAsync(response));
    }

    // Terms version

    [Fact]
    public async Task A_terms_version_that_differs_from_the_current_one_gives_409_termsChanged()
    {
        await StartAsync(termsVersion: "v2");

        var response = await RegisterAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(AuthErrorCodes.TermsChanged, await ErrorJson.CodeAsync(response));
        Assert.Equal("termsChanged", await ErrorJson.CodeAsync(response));
        Assert.False(string.IsNullOrWhiteSpace(JsonElement.Parse(await TextAsync(response)).GetProperty("message").GetString()));
        Assert.Equal(0, Db(db => db.Users.Count()));
        Assert.Equal(0, _emails.Total);
    }

    [Fact]
    public async Task The_current_terms_version_is_accepted_and_stored()
    {
        await StartAsync(termsVersion: "v2");

        var response = await RegisterAsync(Body(NewEmail, ("termsVersion", "v2")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("v2", Db(db => db.AccountConsents.Single(c => c.Document == "terms").Version));
    }

    [Fact]
    public async Task The_terms_version_is_checked_for_an_existing_email_too()
    {
        await StartAsync(termsVersion: "v2");
        await SeedAsync("taken", verified: true);

        var response = await RegisterAsync(Body("taken@example.invalid"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, _emails.Total);
    }

    [Fact]
    public async Task A_source_that_returns_null_accepts_any_version_as_sent()
    {
        await StartAsync(termsVersion: null);

        var response = await RegisterAsync(Body(NewEmail, ("termsVersion", "whatever")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("whatever", Db(db => db.AccountConsents.Single(c => c.Document == "terms").Version));
    }

    [Fact]
    public async Task No_version_source_is_treated_as_null()
    {
        await StartAsync(versionSource: false);

        var response = await RegisterAsync(Body(NewEmail, ("termsVersion", "any")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("any", Db(db => db.AccountConsents.Single(c => c.Document == "terms").Version));
    }

    private async Task RequireAsync(string version, TimeSpan effectiveIn, string document = "terms")
    {
        using var scope = _host!.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IConsentRequirements>().RequireAcceptanceAsync(document, version, _time.GetUtcNow().UtcDateTime + effectiveIn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_requirement_in_effect_decides_the_terms_version_when_the_source_has_none(bool versionSource)
    {
        await StartAsync(termsVersion: null, versionSource: versionSource);
        await RequireAsync("v3", TimeSpan.Zero);

        var wrong = await RegisterAsync(Body(NewEmail, ("termsVersion", "v1")));

        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal(AuthErrorCodes.TermsChanged, await ErrorJson.CodeAsync(wrong));
        Assert.Equal(0, Db(db => db.Users.Count()));

        var right = await RegisterAsync(Body(NewEmail, ("termsVersion", "v3")));

        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Equal("v3", Db(db => db.AccountConsents.Single(c => c.Document == "terms").Version));
    }

    [Fact]
    public async Task The_newest_requirement_in_effect_decides_when_there_are_several()
    {
        await StartAsync(versionSource: false);
        await RequireAsync("v2", TimeSpan.Zero);
        _time.Advance(TimeSpan.FromMinutes(1));
        await RequireAsync("v3", TimeSpan.Zero);

        Assert.Equal(HttpStatusCode.Conflict, (await RegisterAsync(Body(NewEmail, ("termsVersion", "v2")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(Body(NewEmail, ("termsVersion", "v3")))).StatusCode);
    }

    [Fact]
    public async Task A_requirement_that_is_not_in_effect_yet_does_not_decide_the_terms_version()
    {
        await StartAsync(versionSource: false);
        await RequireAsync("v3", TimeSpan.FromDays(7));

        var response = await RegisterAsync(Body(NewEmail, ("termsVersion", "whatever")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_requirement_for_another_document_does_not_decide_the_terms_version()
    {
        await StartAsync(versionSource: false);
        await RequireAsync("p2", TimeSpan.Zero, "privacy");

        var response = await RegisterAsync(Body(NewEmail, ("termsVersion", "whatever")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_requirement_check_runs_for_an_existing_email_too()
    {
        await StartAsync(versionSource: false);
        await SeedAsync("taken", verified: true);
        await RequireAsync("v3", TimeSpan.Zero);

        var response = await RegisterAsync(Body("taken@example.invalid", ("termsVersion", "v1")));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, _emails.Total);
    }

    // Existing emails

    [Fact]
    public async Task A_new_email_and_every_kind_of_existing_email_get_the_same_answer()
    {
        await StartAsync();
        await SeedAsync("verified", verified: true);
        await SeedAsync("unverified", verified: false);
        await SeedAsync("deleted", verified: true, deleted: true);

        var created = await RegisterAsync();
        var bodies = new List<string> { await TextAsync(created) };
        foreach (var id in new[] { "verified", "unverified", "deleted" })
        {
            var response = await RegisterAsync(Body($"{id}@example.invalid"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            bodies.Add(await TextAsync(response));
        }

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Single(bodies.Distinct());
        Assert.Contains("message", bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_verified_active_account_is_not_changed_and_hears_that_someone_tried()
    {
        await StartAsync();
        await SeedAsync("taken", verified: true);
        var before = Stored("taken");

        await RegisterAsync(Body("taken@example.invalid"));

        var after = Stored("taken");
        Assert.Equal((before.PasswordHash, before.SecurityStamp, before.EmailVerificationCodeHash), (after.PasswordHash, after.SecurityStamp, after.EmailVerificationCodeHash));
        Assert.Equal(["taken"], _emails.AlreadyRegistered);
        Assert.Empty(_emails.Verifications);
        Assert.Equal(1, Db(db => db.Users.Count()));
        Assert.Equal(0, Db(db => db.AccountConsents.Count()));
    }

    [Fact]
    public async Task An_unverified_active_account_is_not_changed_and_hears_that_someone_tried()
    {
        await StartAsync();
        await SeedAsync("pending", verified: false, code: "x");
        var before = Stored("pending");

        await RegisterAsync(Body("pending@example.invalid", ("password", "Different1")));

        var after = Stored("pending");
        Assert.Equal((before.PasswordHash, before.EmailVerificationCodeHash, before.EmailVerificationCodeExpiration, before.EmailVerificationAttempts, before.SecurityStamp),
            (after.PasswordHash, after.EmailVerificationCodeHash, after.EmailVerificationCodeExpiration, after.EmailVerificationAttempts, after.SecurityStamp));
        Assert.False(after.EmailConfirmed);
        Assert.Empty(_emails.Verifications);
        Assert.Equal(["pending"], _emails.AlreadyRegistered);
        Assert.Equal(1, Db(db => db.Users.Count()));
        Assert.Equal(0, Db(db => db.AccountConsents.Count()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_inactive_account_is_not_changed_and_gets_no_email(bool verified)
    {
        await StartAsync();
        await SeedAsync("gone", verified, deleted: true);
        var before = Stored("gone");

        await RegisterAsync(Body("gone@example.invalid"));

        var after = Stored("gone");
        Assert.Equal((before.PasswordHash, before.EmailConfirmed, before.EmailVerificationCodeHash), (after.PasswordHash, after.EmailConfirmed, after.EmailVerificationCodeHash));
        Assert.Equal(0, _emails.Total);
        Assert.Equal(1, Db(db => db.Users.Count()));
    }

    [Fact]
    public async Task An_email_with_other_letter_case_finds_the_same_account()
    {
        await StartAsync();
        await SeedAsync("taken", verified: true);

        await RegisterAsync(Body("  TAKEN@Example.Invalid "));

        Assert.Equal(["taken"], _emails.AlreadyRegistered);
        Assert.Equal(1, Db(db => db.Users.Count()));
    }

    public static TheoryData<string, object?> SameAnswerCases => new()
    {
        { "password", "short" },
        { "password", new string('A', 129) },
        { "termsVersion", new string('v', 65) },
        { "locale", new string('n', 17) },
        { "ageConfirmed", false },
        { "termsAccepted", false },
        { "userName", new string('x', 51) },
        { "userName", "with@sign" },
        { "userName", "bad name" },
        { "userName", "taken" },
        { "userName", "TAKEN" },
    };

    [Theory]
    [MemberData(nameof(SameAnswerCases))]
    public async Task A_field_error_reads_the_same_for_a_new_and_an_existing_email(string field, object? value)
    {
        await StartAsync(o => o.AllowUserName = true);
        await SeedAsync("taken", verified: true);

        var forNew = await RegisterAsync(Body(NewEmail, (field, value)));
        var forExisting = await RegisterAsync(Body("taken@example.invalid", (field, value)));

        Assert.Equal(HttpStatusCode.BadRequest, forNew.StatusCode);
        Assert.Equal(forNew.StatusCode, forExisting.StatusCode);
        Assert.Equal(await TextAsync(forNew), await TextAsync(forExisting));
        Assert.Equal(0, _emails.Total);
    }

    [Fact]
    public async Task A_user_name_with_bad_characters_is_an_invalidUserName_error_for_any_email()
    {
        await StartAsync(o => o.AllowUserName = true);
        await SeedAsync("taken", verified: true);

        var forExisting = await RegisterAsync(Body("taken@example.invalid", ("userName", "bad name")));

        Assert.Equal(HttpStatusCode.BadRequest, forExisting.StatusCode);
        Assert.Equal(["UserName=invalidUserName"], await ErrorJson.FieldCodesAsync(forExisting));
    }

    [Fact]
    public async Task A_taken_user_name_is_a_userNameTaken_error_for_any_email()
    {
        await StartAsync(o => o.AllowUserName = true);
        await SeedAsync("taken", verified: true);

        var forNew = await RegisterAsync(Body(NewEmail, ("userName", "taken")));
        var forExisting = await RegisterAsync(Body("taken@example.invalid", ("userName", "taken")));

        Assert.Equal(["UserName=userNameTaken"], await ErrorJson.FieldCodesAsync(forNew));
        Assert.Equal(["UserName=userNameTaken"], await ErrorJson.FieldCodesAsync(forExisting));
    }

    [Fact]
    public async Task User_name_errors_report_together_with_the_other_field_errors_without_an_account()
    {
        await StartAsync(o => o.AllowUserName = true);

        var response = await RegisterAsync(Body(NewEmail, ("userName", "bad name"), ("ageConfirmed", false)));

        Assert.Equal(["AgeConfirmed=ageNotConfirmed", "UserName=invalidUserName"], await ErrorJson.FieldCodesAsync(response));
    }

    // With the user validator, Identity finds the duplicate. Without it, the unique index in the database does.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_duplicate_that_appears_while_signing_up_is_handled_as_an_existing_email(bool userValidator)
    {
        await StartAsync(handler: true, userValidator: userValidator);
        _race.Email = NewEmail;

        var response = await RegisterAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["racer"], Db(db => db.Users.Select(u => u.Id).ToList()));
        Assert.Equal(0, Db(db => db.Profiles.Count()));
        Assert.Equal(0, Db(db => db.AccountConsents.Count()));
        Assert.Equal(["racer"], _emails.AlreadyRegistered);
        Assert.Empty(_emails.Verifications);
    }

    [Fact]
    public async Task Every_case_runs_exactly_one_password_hash()
    {
        await StartAsync();
        await SeedAsync("verified", verified: true);
        await SeedAsync("pending", verified: false);
        await SeedAsync("gone", verified: false, deleted: true);

        foreach (var email in new[] { NewEmail, "verified@example.invalid", "pending@example.invalid", "gone@example.invalid" })
        {
            _hasher.Hashes = 0;
            await RegisterAsync(Body(email));
            Assert.Equal(1, _hasher.Hashes);
        }
    }

    // The cap and the cooldown

    [Fact]
    public async Task Notices_to_one_existing_address_stop_at_the_daily_cap_and_the_answers_stay_the_same()
    {
        await StartAsync(o => { o.ResendCooldown = TimeSpan.Zero; o.MaxEmailsPerAddressPerDay = 2; });
        await SeedAsync("taken", verified: true);

        var answers = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var response = await RegisterAsync(Body("taken@example.invalid"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            answers.Add(await TextAsync(response));
        }

        Assert.Equal(2, _emails.AlreadyRegistered.Count);
        Assert.Single(answers.Distinct());
    }

    [Fact]
    public async Task The_cap_counts_the_verification_email_and_the_notice_together()
    {
        await StartAsync(o => { o.ResendCooldown = TimeSpan.Zero; o.MaxEmailsPerAddressPerDay = 2; });

        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.OK, (await RegisterAsync()).StatusCode);

        Assert.Single(_emails.Verifications);
        Assert.Single(_emails.AlreadyRegistered);
    }

    [Fact]
    public async Task A_new_account_over_the_cap_is_created_without_an_email_and_answers_the_same()
    {
        await StartAsync(o => { o.ResendCooldown = TimeSpan.Zero; o.MaxEmailsPerAddressPerDay = 1; });
        var first = await RegisterAsync(Body("name+one@example.invalid"));
        var second = await RegisterAsync(Body("name+two@example.invalid"));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(await TextAsync(first), await TextAsync(second));
        Assert.Equal(2, Db(db => db.Users.Count()));
        Assert.Single(_emails.Verifications);
    }

    [Fact]
    public async Task The_cap_starts_again_the_next_day()
    {
        await StartAsync(o => { o.ResendCooldown = TimeSpan.Zero; o.MaxEmailsPerAddressPerDay = 1; });
        await SeedAsync("taken", verified: true);
        await RegisterAsync(Body("taken@example.invalid"));
        await RegisterAsync(Body("taken@example.invalid"));
        Assert.Single(_emails.AlreadyRegistered);

        _time.Advance(TimeSpan.FromDays(1));
        await RegisterAsync(Body("taken@example.invalid"));

        Assert.Equal(2, _emails.AlreadyRegistered.Count);
    }

    [Fact]
    public async Task The_notice_cooldown_is_kept_in_the_database_and_ends_with_the_cooldown()
    {
        await StartAsync(o => o.ResendCooldown = TimeSpan.FromMinutes(5));
        await SeedAsync("taken", verified: true);

        await RegisterAsync(Body("taken@example.invalid"));
        _time.Advance(TimeSpan.FromMinutes(4));
        await RegisterAsync(Body("taken@example.invalid"));
        Assert.Single(_emails.AlreadyRegistered);
        Assert.Equal(1, Db(db => db.SignUpMailLogs.Count()));

        _time.Advance(TimeSpan.FromMinutes(2));
        await RegisterAsync(Body("taken@example.invalid"));
        Assert.Equal(2, _emails.AlreadyRegistered.Count);
    }

    [Fact]
    public async Task The_log_holds_a_hash_and_never_the_address()
    {
        await StartAsync();

        await RegisterAsync(Body("Someone+Tag@Example.invalid"));

        var row = Db(db => db.SignUpMailLogs.Single());
        Assert.Equal(64, row.AddressHash.Length);
        Assert.DoesNotContain("someone", row.AddressHash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("verification", row.Kind);
        Assert.Equal(1, row.Count);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.Date, row.Day);
    }

    [Fact]
    public async Task Addresses_that_differ_only_in_case_or_a_plus_tag_share_one_count()
    {
        await StartAsync(o => { o.ResendCooldown = TimeSpan.Zero; o.MaxEmailsPerAddressPerDay = 2; });

        foreach (var email in new[] { "name@example.invalid", "NAME+a@example.invalid", "name+b@Example.invalid", "name+c@example.invalid" })
            await RegisterAsync(Body(email));

        Assert.Equal(2, _emails.Verifications.Count);
        Assert.Equal(1, Db(db => db.SignUpMailLogs.Count()));
    }

    [Fact]
    public async Task The_notice_counter_is_shared_by_every_spelling_of_the_address()
    {
        await StartAsync(o => { o.ResendCooldown = TimeSpan.Zero; o.MaxEmailsPerAddressPerDay = 1; });
        await SeedAsync("taken", verified: true);

        await RegisterAsync(Body("taken@example.invalid"));
        await RegisterAsync(Body("  TAKEN@Example.Invalid "));
        await RegisterAsync(Body("Taken@example.invalid"));

        Assert.Single(_emails.AlreadyRegistered);
        Assert.Single(Db(db => db.SignUpMailLogs.Select(l => l.AddressHash).Distinct().ToList()));
    }

    [Fact]
    public async Task The_counter_key_of_a_new_account_is_the_hash_of_the_normalised_address()
    {
        await StartAsync();

        await RegisterAsync(Body("  Case@Example.Invalid "));

        var user = Db(db => db.Users.Single());
        Assert.Equal(SignUpMailAddress.Hash(user.NormalizedEmail!), Db(db => db.SignUpMailLogs.Single().AddressHash));
        Assert.Equal("CASE@EXAMPLE.INVALID", user.NormalizedEmail);
    }

    [Fact]
    public async Task The_verification_and_the_notice_share_the_counter_of_the_account_whatever_the_spelling()
    {
        await StartAsync(o => { o.ResendCooldown = TimeSpan.Zero; o.MaxEmailsPerAddressPerDay = 2; });

        await RegisterAsync(Body("name@example.invalid"));
        await RegisterAsync(Body(" NAME@example.invalid"));
        await RegisterAsync(Body("Name@EXAMPLE.invalid"));

        Assert.Single(_emails.Verifications);
        Assert.Single(_emails.AlreadyRegistered);
        Assert.Single(Db(db => db.SignUpMailLogs.Select(l => l.AddressHash).Distinct().ToList()));
    }

    // IP

    [Fact]
    public async Task The_shortened_ip_is_stored_on_both_consent_rows_when_the_option_is_on()
    {
        await StartAsync(o => o.RecordTruncatedIp = true, remoteIp: IPAddress.Parse("203.0.113.77"));

        await RegisterAsync();

        Assert.Equal(["203.0.113.0", "203.0.113.0"], Db(db => db.AccountConsents.Select(c => c.Ip!).ToList()));
    }

    [Fact]
    public async Task No_ip_is_stored_when_the_option_is_off()
    {
        await StartAsync(remoteIp: IPAddress.Parse("203.0.113.77"));

        await RegisterAsync();

        Assert.All(Db(db => db.AccountConsents.Select(c => c.Ip).ToList()), Assert.Null);
    }

    // Logs

    [Fact]
    public async Task Logs_hold_the_user_id_and_never_an_email_a_code_or_a_password()
    {
        await StartAsync();
        await SeedAsync("pending", verified: false);

        await RegisterAsync();
        await RegisterAsync(Body("pending@example.invalid", ("password", "Secret9999")));

        var text = string.Join('\n', _logs.GetSnapshot().Select(l => l.Message));
        Assert.DoesNotContain("example.invalid", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret9999", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        Assert.All(_emails.Verifications, v => Assert.DoesNotContain(v.Code, text, StringComparison.Ordinal));
        Assert.Contains(Db(db => db.Users.Single(u => u.Email == NewEmail)).Id, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_and_an_existing_email_log_the_same_event_shape()
    {
        await StartAsync();
        await SeedAsync("pending", verified: false);

        await RegisterAsync();
        await RegisterAsync(Body("pending@example.invalid"));

        var events = _logs.GetSnapshot().Where(l => l.Category == "SjolystInnovation.AppKit.Auth.Registration").ToList();
        Assert.Equal(2, events.Count);
        Assert.Single(events.Select(e => e.Id.Id).Distinct());
        Assert.Single(events.Select(e => e.Level).Distinct());
    }

    // Opt-in

    [Fact]
    public async Task Without_the_opt_in_the_route_answers_404()
    {
        await StartAsync(optIn: false);

        var response = await RegisterAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Mapping_without_AddAppKitSelfRegistration_throws_a_message_that_names_the_call()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        using var app = builder.Build();

        var e = Assert.Throws<InvalidOperationException>(() => app.MapAppKitRegistration<IdentityTestUser, RegistrationTestDbContext>());

        Assert.Contains("AddAppKitSelfRegistration", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapping_with_the_opt_in_maps_four_routes_under_the_prefix()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAppKitSelfRegistration<IdentityTestUser, RegistrationTestDbContext>(o => o.NewUser = _ => new IdentityTestUser());
        using var app = builder.Build();

        app.MapAppKitRegistration<IdentityTestUser, RegistrationTestDbContext>("/custom");

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Select(r => r.RoutePattern.RawText).ToList();
        Assert.Equal(["/custom/accept-terms", "/custom/register", "/custom/resend-verification", "/custom/verify-email"], routes.Order());
    }
}

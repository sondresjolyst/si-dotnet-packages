using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public sealed class UnverifiedAccountCleanupTests : IAsyncDisposable
{
    private sealed class RecordingEmails : IRegistrationEmails<IdentityTestUser>
    {
        public List<(string UserId, string Code, DateTime RemovalAt)> Reminders { get; } = [];
        public List<string?> ReminderLocales { get; } = [];
        public bool Fail { get; set; }

        public Task SendVerificationAsync(IdentityTestUser user, string code, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendAlreadyRegisteredAsync(IdentityTestUser user, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendRemovalReminderAsync(IdentityTestUser user, string code, DateTime removalAt, string? locale, CancellationToken cancellationToken)
        {
            if (Fail)
                throw new InvalidOperationException("The mail server is down.");
            Reminders.Add((user.Id, code, removalAt));
            ReminderLocales.Add(locale);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEraser(List<string> erased, string? failFor = null, Exception? throws = null) : IAccountDataEraser<IdentityTestUser>
    {
        public async Task EraseAsync(IdentityTestUser user, CancellationToken cancellationToken)
        {
            if (user.Id == failFor)
                throw throws ?? new InvalidOperationException("The eraser failed.");
            erased.Add(user.Id);
            await Task.CompletedTask;
        }
    }

    private sealed class InviteEmails : IAuthEmails<IdentityTestUser>
    {
        public Task SendPasswordResetAsync(IdentityTestUser user, string code, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendInviteAsync(IdentityTestUser user, string code, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeTimeProvider _time = new(Start);
    private readonly RecordingEmails _emails = new();
    private readonly VerificationEndpointsTests.LostRace _lostRace = new();
    private readonly List<string> _erased = [];
    private Exception? _eraserThrows;
    private IHost? _host;
    private HttpClient _client = default!;

    public UnverifiedAccountCleanupTests() => _connection.Open();

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await _connection.DisposeAsync();
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private async Task StartAsync(Action<AppKitRegistrationOptions<IdentityTestUser>>? configure = null, string? failEraserFor = null, bool start = true, Action<IHost>? seed = null)
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .UseEnvironment("Production")
                .ConfigureServices(s =>
                {
                    s.AddLogging(b => b.AddFakeLogging());
                    s.AddSingleton<TimeProvider>(_time);
                    s.AddDbContext<RegistrationTestDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_lostRace));
                    s.AddAppKitIdentity<IdentityTestUser, RegistrationTestDbContext>();
                    s.Configure<AppKitAuthOptions>(o => o.Key = MailKeys.SigningKey);
                    s.AddAppKitAccounts<IdentityTestUser, RegistrationTestDbContext>();
                    s.AddAppKitSelfRegistration<IdentityTestUser, RegistrationTestDbContext>(o =>
                    {
                        o.NewUser = r => new IdentityTestUser { Email = r.Email, UserName = r.UserName };
                        o.RunCleanupInBackground = false;
                        configure?.Invoke(o);
                    });
                    s.AddSingleton<IRegistrationEmails<IdentityTestUser>>(_emails);
                    s.AddSingleton<IAuthEmails<IdentityTestUser>>(new InviteEmails());
                    s.AddScoped<IAccountDataEraser<IdentityTestUser>>(_ => new RecordingEraser(_erased, failEraserFor, _eraserThrows));
                    s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    s.PostConfigure<AuthenticationOptions>(o => o.DefaultScheme = o.DefaultAuthenticateScheme = o.DefaultChallengeScheme = o.DefaultForbidScheme = "Test");
                    s.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("Admin")));
                    s.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapAppKitUserAdmin<IdentityTestUser, RegistrationTestDbContext>("/api/users", o =>
                    {
                        o.Roles = ["Editor"];
                        o.NewUser = u => new IdentityTestUser { FirstName = u.FirstName, LastName = u.LastName };
                    }));
                }))
            .Build();
        using (var scope = _host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>().Database.EnsureCreated();
            await scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>().CreateAsync(new IdentityRole("Editor"));
        }
        seed?.Invoke(_host);
        if (start)
            await _host.StartAsync();
        _client = _host.GetTestClient();
    }

    private IUnverifiedAccountCleanup Cleanup => _host!.Services.GetRequiredService<IUnverifiedAccountCleanup>();

    private Task<UnverifiedCleanupResult> RunAsync() => Cleanup.RunOnceAsync();

    private static void SeedUser(IHost host, string id, TimeSpan age, DateTime now, bool verified, bool deleted = false, string? code = null, TimeSpan? codeExpiresIn = null, string? consentSource = "register")
    {
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>();
        var user = new IdentityTestUser
        {
            Id = id, UserName = id, Email = $"{id}@example.invalid", EmailConfirmed = verified, IsDeleted = deleted, CreatedAt = now - age,
        };
        if (code is not null)
        {
            user.EmailVerificationCodeHash = VerificationCodes.Hash(code);
            user.EmailVerificationCodeExpiration = now + (codeExpiresIn ?? TimeSpan.FromMinutes(30));
        }
        Assert.True(users.CreateAsync(user).GetAwaiter().GetResult().Succeeded);
        var db = scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>();
        db.RefreshTokens.Add(new RefreshToken { Token = $"token-{id}", UserId = id, Created = now - age, Expires = now.AddDays(30) });
        if (consentSource is not null)
            db.AccountConsents.Add(new AccountConsent { UserId = id, Document = "terms", Version = "v1", Source = consentSource, AcceptedAt = now - age });
        db.SaveChanges();
    }

    private Task SeedAsync(string id, TimeSpan age, bool verified = false, bool deleted = false, string? code = null, TimeSpan? codeExpiresIn = null, string? consentSource = "register")
    {
        SeedUser(_host!, id, age, Now, verified, deleted, code, codeExpiresIn, consentSource);
        return Task.CompletedTask;
    }

    private T Db<T>(Func<RegistrationTestDbContext, T> read)
    {
        using var scope = _host!.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>());
    }

    private void Update(string id, Action<IdentityTestUser> change)
    {
        using var scope = _host!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>();
        change(db.Users.Single(u => u.Id == id));
        db.SaveChanges();
    }

    private bool Exists(string id) => Db(db => db.Users.Any(u => u.Id == id));

    private IdentityTestUser Load(string id) => Db(db => db.Users.AsNoTracking().Single(u => u.Id == id));

    private string[] Logs() => [.. _host!.Services.GetFakeLogCollector().GetSnapshot().Select(r => r.Message)];

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    // Removal

    [Fact]
    public async Task An_unverified_account_older_than_the_lifetime_is_removed_with_its_tokens_and_consents_and_the_email_is_free()
    {
        await StartAsync();
        await SeedAsync("old", Lifetime + TimeSpan.FromHours(1));

        var result = await RunAsync();

        Assert.Equal(new UnverifiedCleanupResult(1, 0, 0), result);
        Assert.False(Exists("old"));
        Assert.Empty(Db(db => db.RefreshTokens.Where(t => t.UserId == "old").ToList()));
        Assert.Empty(Db(db => db.AccountConsents.Where(c => c.UserId == "old").ToList()));
        using var scope = _host!.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<UserManager<IdentityTestUser>>().FindByEmailAsync("old@example.invalid"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("migration")]
    [InlineData("reaccept")]
    public async Task An_old_unverified_account_without_a_sign_up_consent_is_never_touched(string? consentSource)
    {
        await StartAsync();
        await SeedAsync("older", Lifetime + TimeSpan.FromDays(400), consentSource: consentSource);

        var result = await RunAsync();

        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), result);
        Assert.True(Exists("older"));
        Assert.Empty(_erased);
    }

    [Fact]
    public async Task A_young_unverified_account_without_a_sign_up_consent_gets_no_reminder()
    {
        await StartAsync();
        await SeedAsync("u1", TimeSpan.Zero, consentSource: null);
        _time.Advance(TimeSpan.FromDays(16));

        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), await RunAsync());
        Assert.Empty(_emails.Reminders);
    }

    [Fact]
    public async Task Every_eraser_runs_before_the_user_is_deleted()
    {
        await StartAsync();
        await SeedAsync("old", Lifetime + TimeSpan.FromDays(2));

        await RunAsync();

        Assert.Equal(["old"], _erased);
        Assert.False(Exists("old"));
    }

    [Fact]
    public async Task An_account_younger_than_the_lifetime_is_left_alone()
    {
        await StartAsync(o => o.RemindBefore = []);
        await SeedAsync("young", Lifetime - TimeSpan.FromMinutes(1));

        var result = await RunAsync();

        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), result);
        Assert.True(Exists("young"));
        Assert.Empty(_erased);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(4000)]
    public async Task A_verified_account_is_never_touched_whatever_its_age(int days)
    {
        await StartAsync();
        await SeedAsync("verified", TimeSpan.FromDays(days), verified: true);

        var result = await RunAsync();

        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), result);
        Assert.True(Exists("verified"));
        Assert.Empty(_erased);
        Assert.Empty(_emails.Reminders);
        Assert.Single(Db(db => db.RefreshTokens.Where(t => t.UserId == "verified").ToList()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(4000)]
    public async Task An_invited_account_is_never_touched_whatever_its_age(int days)
    {
        await StartAsync();
        var invite = new HttpRequestMessage(HttpMethod.Post, "/api/users/invite") { Content = JsonContent.Create(new { email = "invited@example.invalid", firstName = "Kari", lastName = "N", role = "Editor" }) };
        invite.Headers.Add("X-Test-User", "boss");
        invite.Headers.Add("X-Test-Roles", "Admin");
        Assert.True((await _client.SendAsync(invite)).IsSuccessStatusCode);
        var id = Db(db => db.Users.Single(u => u.Email == "invited@example.invalid")).Id;
        Assert.True(Load(id).EmailConfirmed);
        Update(id, u => u.CreatedAt = Now.AddDays(-days));

        var result = await RunAsync();
        _time.Advance(TimeSpan.FromDays(60));
        var later = await RunAsync();

        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), result);
        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), later);
        Assert.True(Exists(id));
        Assert.Empty(_erased);
        Assert.Empty(_emails.Reminders);
    }

    [Fact]
    public async Task An_account_that_was_deleted_by_its_owner_is_not_selected()
    {
        await StartAsync();
        await SeedAsync("gone", Lifetime + TimeSpan.FromDays(5), deleted: true);

        var result = await RunAsync();

        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), result);
        Assert.True(Exists("gone"));
    }

    [Fact]
    public async Task An_account_that_is_confirmed_when_it_is_loaded_again_is_skipped()
    {
        await StartAsync();
        await SeedAsync("late", Lifetime + TimeSpan.FromDays(2));
        Update("late", u => u.EmailConfirmed = true);
        var cleanup = (UnverifiedAccountCleanup<IdentityTestUser, RegistrationTestDbContext>)Cleanup;

        var outcome = await cleanup.RemoveAsync("late", CancellationToken.None);

        Assert.Equal(CleanupOutcome.Skipped, outcome);
        Assert.True(Exists("late"));
        Assert.Empty(_erased);
    }

    [Fact]
    public async Task One_failing_account_is_logged_by_id_and_does_not_stop_the_run()
    {
        await StartAsync(failEraserFor: "bad");
        await SeedAsync("bad", Lifetime + TimeSpan.FromDays(2));
        await SeedAsync("good", Lifetime + TimeSpan.FromDays(3));

        var result = await RunAsync();

        Assert.Equal(new UnverifiedCleanupResult(1, 0, 1), result);
        Assert.True(Exists("bad"));
        Assert.False(Exists("good"));
        Assert.Single(Db(db => db.RefreshTokens.Where(t => t.UserId == "bad").ToList()));
        var logs = Logs();
        Assert.Contains(logs, l => l.Contains("bad", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.Contains("example.invalid", StringComparison.Ordinal));
    }

    // An owner in the middle of verifying keeps the account a little longer, but asking for codes cannot keep it for good.

    [Fact]
    public async Task A_live_code_keeps_an_account_just_past_the_lifetime_until_the_code_expires()
    {
        await StartAsync();
        await SeedAsync("typing", Lifetime + TimeSpan.FromMinutes(10), code: "AB12CD", codeExpiresIn: TimeSpan.FromMinutes(50));

        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), await RunAsync());
        Assert.True(Exists("typing"));

        _time.Advance(TimeSpan.FromMinutes(51));

        Assert.Equal(new UnverifiedCleanupResult(1, 0, 0), await RunAsync());
        Assert.False(Exists("typing"));
    }

    [Fact]
    public async Task Asking_for_codes_cannot_keep_an_account_beyond_one_day_past_the_lifetime()
    {
        await StartAsync();
        await SeedAsync("squatter", Lifetime + TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1), code: "AB12CD", codeExpiresIn: TimeSpan.FromMinutes(59));

        var result = await RunAsync();

        Assert.Equal(new UnverifiedCleanupResult(1, 0, 0), result);
        Assert.False(Exists("squatter"));
    }

    // Reminders

    [Fact]
    public async Task The_reminder_gets_the_locale_of_the_latest_consent_row_of_the_user()
    {
        await StartAsync();
        await SeedAsync("u1", TimeSpan.Zero);
        Db(db =>
        {
            db.AccountConsents.Add(new AccountConsent { UserId = "u1", Document = "terms", Version = "v1", Source = "register", AcceptedAt = Now.AddMinutes(1), Locale = "nb" });
            db.AccountConsents.Add(new AccountConsent { UserId = "u1", Document = "terms", Version = "v1", Source = "reaccept", AcceptedAt = Now.AddMinutes(2), Locale = "en" });
            db.SaveChanges();
            return 0;
        });
        _time.Advance(TimeSpan.FromDays(16));

        Assert.Equal(new UnverifiedCleanupResult(0, 1, 0), await RunAsync());

        Assert.Equal(["en"], _emails.ReminderLocales);
    }

    [Fact]
    public async Task The_reminder_for_an_account_with_no_stored_locale_gets_none()
    {
        await StartAsync();
        await SeedAsync("u1", TimeSpan.Zero);
        _time.Advance(TimeSpan.FromDays(16));

        Assert.Equal(new UnverifiedCleanupResult(0, 1, 0), await RunAsync());

        Assert.Equal([null], _emails.ReminderLocales);
    }

    [Fact]
    public async Task Each_reminder_is_sent_once_at_its_offset_before_the_removal_with_a_fresh_code()
    {
        await StartAsync();
        await SeedAsync("u1", TimeSpan.Zero);
        var created = Load("u1").CreatedAt;
        var removalAt = created + Lifetime;

        _time.Advance(TimeSpan.FromDays(15));
        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), await RunAsync());

        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new UnverifiedCleanupResult(0, 1, 0), await RunAsync());
        var (id, code, at) = Assert.Single(_emails.Reminders);
        Assert.Equal(("u1", removalAt), (id, at));
        var first = Load("u1");
        Assert.Equal(Now, first.RemovalReminderSentAt);
        Assert.Equal(VerificationCodes.Hash(code), first.EmailVerificationCodeHash);
        Assert.Equal(Now + EmailVerificationCodes.Lifetime, first.EmailVerificationCodeExpiration);

        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), await RunAsync());
        _time.Advance(TimeSpan.FromDays(10));
        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), await RunAsync());

        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new UnverifiedCleanupResult(0, 1, 0), await RunAsync());
        Assert.Equal(2, _emails.Reminders.Count);
        Assert.NotEqual(code, _emails.Reminders[1].Code);

        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), await RunAsync());
        Assert.Equal(2, _emails.Reminders.Count);
        Assert.True(Exists("u1"));
    }

    // Seeds the rows that one reservation of the kind would have written, count times over.
    private void SeedMailCount(string email, string kind, DateTime day, int count)
    {
        using var scope = _host!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RegistrationTestDbContext>();
        var normal = email.ToUpperInvariant();
        var mailbox = SignUpMailAddress.MailboxHash(MailKeys.Key, normal);
        var owner = SignUpMailKinds.IsOwner(kind);
        var hash = owner ? SignUpMailAddress.Hash(MailKeys.Key, normal) : mailbox;
        foreach (var (rowHash, rowKind) in new[] { (hash, kind), (hash, owner ? SignUpMailKinds.OwnerBudget : SignUpMailKinds.AnonymousBudget), (mailbox, SignUpMailKinds.Total) })
            db.SignUpMailLogs.Add(new SignUpMailLog { AddressHash = rowHash, MailboxHash = mailbox, Kind = rowKind, Day = day, Count = count, LastSentAt = day });
        db.SaveChanges();
    }

    [Fact]
    public async Task A_reminder_over_the_daily_cap_of_the_address_is_held_back_and_goes_out_the_next_day()
    {
        await StartAsync(o => o.MaxOwnerEmailsPerAddressPerDay = 1);
        await SeedAsync("u1", TimeSpan.Zero);
        _time.Advance(TimeSpan.FromDays(16));
        SeedMailCount("u1@example.invalid", "resend", Now.Date, 1);

        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), await RunAsync());
        Assert.Empty(_emails.Reminders);

        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new UnverifiedCleanupResult(0, 1, 0), await RunAsync());
        Assert.Single(_emails.Reminders);
    }

    [Fact]
    public async Task A_reminder_refused_by_the_cap_leaves_the_stored_code_and_attempts_as_they_were_and_the_next_run_tries_again()
    {
        await StartAsync(o => o.MaxOwnerEmailsPerAddressPerDay = 1);
        await SeedAsync("u1", TimeSpan.Zero);
        _time.Advance(TimeSpan.FromDays(16));
        Update("u1", u =>
        {
            u.EmailVerificationCodeHash = VerificationCodes.Hash("OLD123");
            u.EmailVerificationCodeExpiration = Now.AddMinutes(30);
            u.EmailVerificationAttempts = 2;
        });
        SeedMailCount("u1@example.invalid", "resend", Now.Date, 1);
        var before = Load("u1");
        var cleanup = (UnverifiedAccountCleanup<IdentityTestUser, RegistrationTestDbContext>)Cleanup;

        Assert.Equal(CleanupOutcome.Skipped, await cleanup.RemindAsync("u1", CancellationToken.None));

        var after = Load("u1");
        Assert.Equal(before.EmailVerificationCodeHash, after.EmailVerificationCodeHash);
        Assert.Equal(before.EmailVerificationCodeExpiration, after.EmailVerificationCodeExpiration);
        Assert.Equal(2, after.EmailVerificationAttempts);
        Assert.Null(after.RemovalReminderSentAt);
        Assert.Empty(_emails.Reminders);
        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), await RunAsync());

        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new UnverifiedCleanupResult(0, 1, 0), await RunAsync());
        Assert.Single(_emails.Reminders);
    }

    [Fact]
    public async Task A_reminder_refused_by_the_cap_never_writes_the_code_of_the_user()
    {
        await StartAsync(o => o.MaxOwnerEmailsPerAddressPerDay = 1);
        await SeedAsync("u1", TimeSpan.Zero);
        _time.Advance(TimeSpan.FromDays(16));
        Update("u1", u =>
        {
            u.EmailVerificationCodeHash = VerificationCodes.Hash("OLD123");
            u.EmailVerificationCodeExpiration = Now.AddMinutes(30);
            u.EmailVerificationAttempts = 2;
        });
        SeedMailCount("u1@example.invalid", "resend", Now.Date, 1);
        var before = Load("u1");
        var cleanup = (UnverifiedAccountCleanup<IdentityTestUser, RegistrationTestDbContext>)Cleanup;
        _lostRace.Active = true;

        Assert.Equal(CleanupOutcome.Skipped, await cleanup.RemindAsync("u1", CancellationToken.None));

        _lostRace.Active = false;
        var after = Load("u1");
        Assert.Equal(before.EmailVerificationCodeHash, after.EmailVerificationCodeHash);
        Assert.Equal(before.EmailVerificationCodeExpiration, after.EmailVerificationCodeExpiration);
        Assert.Equal(2, after.EmailVerificationAttempts);
    }

    [Fact]
    public async Task A_reminder_that_loses_on_the_concurrency_stamp_is_lost_and_leaves_the_mail_counts_and_the_stored_code_as_they_were()
    {
        await StartAsync();
        await SeedAsync("u1", TimeSpan.Zero);
        _time.Advance(TimeSpan.FromDays(16));
        Update("u1", u =>
        {
            u.EmailVerificationCodeHash = VerificationCodes.Hash("OLD123");
            u.EmailVerificationCodeExpiration = Now.AddMinutes(30);
        });
        var before = Load("u1");
        var cleanup = (UnverifiedAccountCleanup<IdentityTestUser, RegistrationTestDbContext>)Cleanup;
        _lostRace.Active = true;

        Assert.Equal(CleanupOutcome.Lost, await cleanup.RemindAsync("u1", CancellationToken.None));

        _lostRace.Active = false;
        Assert.Equal(0, Db(db => db.SignUpMailLogs.Sum(l => l.Count)));
        Assert.Equal(before.EmailVerificationCodeHash, Load("u1").EmailVerificationCodeHash);
        Assert.Empty(_emails.Reminders);
    }

    [Fact]
    public async Task A_reminder_whose_time_save_loses_on_the_concurrency_stamp_saves_the_time_again_and_counts_as_sent()
    {
        await StartAsync();
        await SeedAsync("u1", TimeSpan.Zero);
        _time.Advance(TimeSpan.FromDays(16));
        // The first save of the user is the code, and the second is the time. Only the second loses.
        _lostRace.PassThenFailOnce(1);

        Assert.Equal(new UnverifiedCleanupResult(0, 1, 0), await RunAsync());

        _lostRace.Active = false;
        Assert.Single(_emails.Reminders);
        Assert.Equal(Now, Load("u1").RemovalReminderSentAt);
        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), await RunAsync());
        Assert.Single(_emails.Reminders);
    }

    [Fact]
    public async Task An_eraser_that_throws_a_concurrency_exception_leaves_the_account_counts_it_as_skipped_and_logs_a_warning()
    {
        _eraserThrows = new DbUpdateConcurrencyException("The eraser lost a race.");
        await StartAsync(failEraserFor: "old");
        await SeedAsync("old", Lifetime + TimeSpan.FromDays(2));

        var result = await RunAsync();

        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0, 1), result);
        Assert.True(Exists("old"));
        var records = _host!.Services.GetFakeLogCollector().GetSnapshot();
        var warning = Assert.Single(records, r => r.Level == LogLevel.Warning && r.Message.Contains("old", StringComparison.Ordinal));
        Assert.IsType<DbUpdateConcurrencyException>(warning.Exception);
    }

    [Fact]
    public async Task Sign_up_emails_to_the_address_do_not_hold_back_the_reminder()
    {
        await StartAsync(o => o.MaxOwnerEmailsPerAddressPerDay = 1);
        await SeedAsync("u1", TimeSpan.Zero);
        _time.Advance(TimeSpan.FromDays(16));
        SeedMailCount("u1@example.invalid", "notice", Now.Date, 5);

        Assert.Equal(new UnverifiedCleanupResult(0, 1, 0), await RunAsync());
    }

    [Fact]
    public async Task A_reminder_counts_toward_the_daily_cap_of_the_address()
    {
        await StartAsync();
        await SeedAsync("u1", TimeSpan.Zero);
        _time.Advance(TimeSpan.FromDays(16));

        await RunAsync();

        var row = Db(db => db.SignUpMailLogs.Single(l => l.Kind == SignUpMailKinds.Reminder));
        Assert.Equal(("reminder", 1, Now.Date), (row.Kind, row.Count, row.Day));
        Assert.Equal(SignUpMailAddress.Hash(MailKeys.Key, "U1@EXAMPLE.INVALID"), row.AddressHash);
    }

    [Fact]
    public async Task The_run_removes_mail_log_rows_older_than_yesterday()
    {
        await StartAsync();
        SeedMailCount("a@example.invalid", "notice", Now.Date.AddDays(-5), 3);
        SeedMailCount("b@example.invalid", "notice", Now.Date.AddDays(-1), 1);
        SeedMailCount("c@example.invalid", "notice", Now.Date, 1);

        await RunAsync();

        Assert.Equal([Now.Date.AddDays(-1), Now.Date], Db(db => db.SignUpMailLogs.Where(l => l.Kind == SignUpMailKinds.Notice).OrderBy(l => l.Day).Select(l => l.Day).ToList()));
    }

    [Fact]
    public async Task After_missed_runs_one_reminder_goes_out_and_not_one_per_missed_offset()
    {
        await StartAsync();
        await SeedAsync("u1", TimeSpan.Zero);

        _time.Advance(TimeSpan.FromDays(28));

        Assert.Equal(new UnverifiedCleanupResult(0, 1, 0), await RunAsync());
        Assert.Single(_emails.Reminders);
        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), await RunAsync());
    }

    [Fact]
    public async Task A_locked_account_gets_no_reminder_until_the_lock_has_passed_and_is_then_reminded()
    {
        await StartAsync();
        await SeedAsync("locked", TimeSpan.Zero);
        Update("locked", u =>
        {
            u.EmailVerificationAttempts = 5;
            u.EmailVerificationCodeHash = "x";
            u.EmailVerificationCodeExpiration = Now.AddDays(16).AddMinutes(30);
        });
        _time.Advance(TimeSpan.FromDays(16));

        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), await RunAsync());
        Assert.Empty(_emails.Reminders);
        Assert.Null(Load("locked").RemovalReminderSentAt);

        _time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(new UnverifiedCleanupResult(0, 1, 0), await RunAsync());
        Assert.Single(_emails.Reminders);
    }

    [Fact]
    public async Task A_reminder_that_cannot_be_sent_is_not_marked_sent_and_does_not_stop_the_run()
    {
        await StartAsync();
        await SeedAsync("u1", TimeSpan.Zero);
        await SeedAsync("old", Lifetime + TimeSpan.FromDays(2));
        _time.Advance(TimeSpan.FromDays(16));
        _emails.Fail = true;

        var failed = await RunAsync();

        Assert.Equal(new UnverifiedCleanupResult(1, 0, 1), failed);
        Assert.Null(Load("u1").RemovalReminderSentAt);

        _emails.Fail = false;
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(new UnverifiedCleanupResult(0, 1, 0), await RunAsync());
        Assert.NotNull(Load("u1").RemovalReminderSentAt);
        Assert.DoesNotContain(Logs(), l => l.Contains("example.invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verified_and_young_accounts_get_no_reminder()
    {
        await StartAsync();
        await SeedAsync("verified", TimeSpan.FromDays(20), verified: true);
        await SeedAsync("young", TimeSpan.FromDays(2));

        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), await RunAsync());
        Assert.Empty(_emails.Reminders);
    }

    [Fact]
    public async Task No_offsets_means_no_reminders()
    {
        await StartAsync(o => o.RemindBefore = []);
        await SeedAsync("u1", TimeSpan.FromDays(29));

        Assert.Equal(new UnverifiedCleanupResult(0, 0, 0), await RunAsync());
        Assert.Empty(_emails.Reminders);
    }

    [Fact]
    public void The_cleanup_logs_a_warning_when_the_user_type_cannot_store_reminders()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddFakeLogging());
        services.AddAppKitSelfRegistration<TestUser, ConsentTestDbContext>(o => o.NewUser = r => new TestUser { Email = r.Email });
        using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<IUnverifiedAccountCleanup>();

        var warning = Assert.Single(provider.GetFakeLogCollector().GetSnapshot(), r => r.Level == LogLevel.Warning);
        Assert.Contains("IRemovalReminderUser", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cleanup_logs_no_warning_when_reminders_are_off_for_a_user_type_without_the_column()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddFakeLogging());
        services.AddAppKitSelfRegistration<TestUser, ConsentTestDbContext>(o => { o.NewUser = r => new TestUser { Email = r.Email }; o.RemindBefore = []; });
        using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<IUnverifiedAccountCleanup>();

        Assert.Empty(provider.GetFakeLogCollector().GetSnapshot());
    }

    // The hosted run

    [Fact]
    public async Task The_hosted_service_runs_the_cleanup_when_the_app_starts()
    {
        await StartAsync(o => o.RunCleanupInBackground = true, seed: h => SeedUser(h, "old", Lifetime + TimeSpan.FromDays(2), Start.UtcDateTime, verified: false));

        await EventuallyAsync(() => !Exists("old"));
    }

    [Fact]
    public async Task The_hosted_service_runs_again_a_day_later()
    {
        await StartAsync(o => o.RunCleanupInBackground = true);
        await SeedAsync("old", Lifetime + TimeSpan.FromDays(2));

        await EventuallyAsync(() =>
        {
            _time.Advance(TimeSpan.FromDays(1));
            return !Exists("old");
        });
    }

    [Fact]
    public async Task The_hosted_service_does_nothing_when_the_option_is_off()
    {
        await StartAsync(o => o.RunCleanupInBackground = false, seed: h => SeedUser(h, "old", Lifetime + TimeSpan.FromDays(2), Start.UtcDateTime, verified: false));

        await Task.Delay(300);
        _time.Advance(TimeSpan.FromDays(2));
        await Task.Delay(300);

        Assert.True(Exists("old"));
    }

    [Fact]
    public async Task The_cleanup_is_registered_for_an_app_that_schedules_it_itself()
    {
        await StartAsync();

        Assert.NotNull(_host!.Services.GetService<IUnverifiedAccountCleanup>());
    }

    // The hosted service works on another thread and shares the test's one SQLite connection, so a look that
    // lands in the middle of its work can throw. Such a look counts as not yet. The wait is long, since the
    // whole suite may run on a busy machine.
    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 600; i++)
        {
            try
            {
                if (condition())
                    return;
            }
            catch (Exception e) when (e is InvalidOperationException or SqliteException or NullReferenceException)
            {
            }
            await Task.Delay(50);
        }
        Assert.True(condition());
    }
}

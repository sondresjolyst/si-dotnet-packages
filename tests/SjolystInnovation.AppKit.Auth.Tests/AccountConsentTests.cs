using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public class ConsentTestDbContext(DbContextOptions<ConsentTestDbContext> options) : DbContext(options), IAccountConsentDbContext
{
    public DbSet<TestUser> Users => Set<TestUser>();
    public DbSet<AccountConsent> AccountConsents => Set<AccountConsent>();
    public DbSet<ConsentRequirement> ConsentRequirements => Set<ConsentRequirement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddAppKitAccountConsents<TestUser>();
}

public sealed class AccountConsentTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Start));

    public AccountConsentTests()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Users.AddRange(new TestUser { Id = "ada", UserName = "ada" }, new TestUser { Id = "bob", UserName = "bob" });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private ConsentTestDbContext NewContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ConsentTestDbContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options);

    private AccountConsentService<ConsentTestDbContext> Service(ConsentTestDbContext db) => new(db, _time);

    private static bool MustAccept(DateTime? latest, DateTime now, params ConsentRequirement[] requirements) =>
        MustAccept(latest is { } at ? [("v2", at)] : [], null, now, requirements);

    private static bool MustAccept(IReadOnlyList<(string Version, DateTime At)> acceptances, string? current, DateTime now, params ConsentRequirement[] requirements) =>
        AccountConsentService<ConsentTestDbContext>.MustAcceptAgain(acceptances, current, requirements, now);

    private static ConsentRequirement Requirement(string version, DateTime markedAt, DateTime effectiveFrom) =>
        new() { Document = "terms", Version = version, MarkedAt = markedAt, EffectiveFrom = effectiveFrom };

    private List<AccountConsent> Rows(string userId)
    {
        using var db = NewContext();
        return db.AccountConsents.AsNoTracking().Where(c => c.UserId == userId).OrderBy(c => c.Id).ToList();
    }

    private void Require(string document, string version, DateTime markedAt, DateTime effectiveFrom)
    {
        using var db = NewContext();
        db.ConsentRequirements.Add(new ConsentRequirement { Document = document, Version = version, MarkedAt = markedAt, EffectiveFrom = effectiveFrom });
        db.SaveChanges();
    }

    [Fact]
    public async Task Recording_an_acceptance_stores_a_row_with_the_time_from_the_clock()
    {
        await using var db = NewContext();
        var added = await Service(db).RecordAsync("ada", "terms", "2026-11-01", "register", "nb", "203.0.113.0");

        Assert.True(added);
        var row = Assert.Single(Rows("ada"));
        Assert.Equal("terms", row.Document);
        Assert.Equal("2026-11-01", row.Version);
        Assert.Equal("register", row.Source);
        Assert.Equal("nb", row.Locale);
        Assert.Equal("203.0.113.0", row.Ip);
        Assert.Equal(Start, row.AcceptedAt);
    }

    [Fact]
    public async Task Recording_the_same_user_document_and_version_twice_keeps_one_row_and_the_first_time()
    {
        await using (var db = NewContext())
            Assert.True(await Service(db).RecordAsync("ada", "terms", "v1", "register"));
        _time.Advance(TimeSpan.FromHours(1));
        await using (var db = NewContext())
            Assert.False(await Service(db).RecordAsync("ada", "terms", "v1", "reaccept"));

        var row = Assert.Single(Rows("ada"));
        Assert.Equal(Start, row.AcceptedAt);
        Assert.Equal("register", row.Source);
    }

    [Fact]
    public async Task A_new_version_or_a_new_document_or_a_new_user_is_a_new_row()
    {
        await using var db = NewContext();
        var service = Service(db);
        Assert.True(await service.RecordAsync("ada", "terms", "v1", "register"));
        Assert.True(await service.RecordAsync("ada", "terms", "v2", "reaccept"));
        Assert.True(await service.RecordAsync("ada", "age", "16", "register"));
        Assert.True(await service.RecordAsync("bob", "terms", "v1", "register"));

        Assert.Equal(3, Rows("ada").Count);
        Assert.Single(Rows("bob"));
    }

    [Fact]
    public async Task A_race_on_the_unique_index_counts_as_already_recorded()
    {
        await using var db = NewContext(new RivalInsert(() => NewContext()));

        var added = await Service(db).RecordAsync("ada", "terms", "v1", "register");

        Assert.False(added);
        var row = Assert.Single(Rows("ada"));
        Assert.Equal("rival", row.Source);
    }

    [Fact]
    public async Task A_failure_that_is_not_a_duplicate_is_not_swallowed()
    {
        await using var db = NewContext();

        // There is no user "ghost", so the foreign key fails and no row exists for the key.
        await Assert.ThrowsAsync<DbUpdateException>(() => Service(db).RecordAsync("ghost", "terms", "v1", "register"));
    }

    [Fact]
    public async Task The_unique_index_refuses_a_duplicate_row_written_around_the_service()
    {
        await using var db = NewContext();
        db.AccountConsents.Add(new AccountConsent { UserId = "ada", Document = "terms", Version = "v1", Source = "register", AcceptedAt = Start });
        db.AccountConsents.Add(new AccountConsent { UserId = "ada", Document = "terms", Version = "v1", Source = "register", AcceptedAt = Start });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Hard_deleting_the_user_deletes_the_consent_rows()
    {
        await using (var db = NewContext())
            await Service(db).RecordAsync("ada", "terms", "v1", "register");

        await using (var db = NewContext())
        {
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == "ada"));
            await db.SaveChangesAsync();
        }

        Assert.Empty(Rows("ada"));
    }

    [Fact]
    public async Task The_acceptances_of_a_user_list_every_document_version_and_time()
    {
        await using (var db = NewContext())
        {
            var service = Service(db);
            await service.RecordAsync("ada", "terms", "v1", "register");
            await service.RecordAsync("ada", "age", "16", "register");
            _time.Advance(TimeSpan.FromDays(10));
            await service.RecordAsync("ada", "terms", "v2", "reaccept");
            await service.RecordAsync("bob", "terms", "v1", "register");
        }

        await using var read = NewContext();
        var acceptances = await Service(read).AcceptancesAsync("ada");

        Assert.Equal(2, acceptances.Count);
        Assert.Equal([("v1", Start), ("v2", Start.AddDays(10))], acceptances["terms"].OrderBy(a => a.At));
        Assert.Equal([("16", Start)], acceptances["age"]);
    }

    [Fact]
    public async Task A_user_with_no_acceptances_has_an_empty_acceptance_map()
    {
        await using var db = NewContext();
        Assert.Empty(await Service(db).AcceptancesAsync("ada"));
    }

    [Fact]
    public void With_no_requirements_nobody_must_accept()
    {
        Assert.False(MustAccept(Start, Start));
        Assert.False(MustAccept(null, Start));
    }

    [Fact]
    public void A_requirement_that_is_not_in_effect_yet_does_not_bind()
    {
        Assert.False(MustAccept(Start.AddDays(-30), Start, Requirement("v2", Start, Start.AddDays(1))));
    }

    [Fact]
    public void A_requirement_in_effect_binds_a_user_whose_latest_acceptance_is_older_than_it_was_marked()
    {
        Assert.True(MustAccept(Start.AddSeconds(-1), Start, Requirement("v2", Start, Start)));
    }

    [Fact]
    public void An_acceptance_at_or_after_the_marked_time_satisfies_the_requirement()
    {
        var requirement = Requirement("v2", Start, Start);

        Assert.False(MustAccept(Start, Start, requirement));
        Assert.False(MustAccept(Start.AddMinutes(5), Start, requirement));
    }

    [Fact]
    public void A_user_who_never_accepted_must_accept_once_a_requirement_is_in_effect()
    {
        Assert.True(MustAccept(null, Start, Requirement("v2", Start, Start)));
    }

    [Fact]
    public void Accepting_early_satisfies_a_requirement_before_it_comes_into_effect()
    {
        Assert.False(MustAccept(Start.AddDays(1), Start.AddDays(20), Requirement("v2", Start, Start.AddDays(14))));
    }

    [Fact]
    public void The_newest_marked_requirement_in_effect_decides()
    {
        var older = Requirement("v2", Start, Start);
        var newer = Requirement("v3", Start.AddDays(30), Start.AddDays(30));
        var now = Start.AddDays(31);

        Assert.False(MustAccept(Start.AddDays(1), now, older));
        Assert.True(MustAccept(Start.AddDays(1), now, older, newer));
    }

    [Fact]
    public void An_acceptance_of_another_version_does_not_satisfy_the_requirement()
    {
        var requirement = Requirement("v2", Start, Start);

        Assert.True(MustAccept([("v1", Start.AddMinutes(5))], null, Start.AddMinutes(10), requirement));
        Assert.True(MustAccept([("v1", Start.AddMinutes(5))], "v3", Start.AddMinutes(10), requirement));
    }

    [Fact]
    public void An_acceptance_of_the_current_source_version_satisfies_the_requirement_when_it_is_not_older_than_the_marking()
    {
        var requirement = Requirement("v2", Start, Start);

        Assert.False(MustAccept([("v3", Start)], "v3", Start.AddMinutes(10), requirement));
        Assert.True(MustAccept([("v3", Start.AddSeconds(-1))], "v3", Start.AddMinutes(10), requirement));
    }

    [Fact]
    public void Only_an_acceptance_at_or_after_the_marking_counts_even_when_an_older_one_has_the_right_version()
    {
        var requirement = Requirement("v2", Start, Start);

        Assert.True(MustAccept([("v2", Start.AddDays(-1)), ("v1", Start.AddDays(1))], null, Start.AddDays(2), requirement));
        Assert.False(MustAccept([("v2", Start.AddDays(-1)), ("v2", Start.AddDays(1))], null, Start.AddDays(2), requirement));
    }

    [Fact]
    public async Task The_pending_documents_follow_the_requirement_rows_and_the_clock()
    {
        await using (var db = NewContext())
        {
            var service = Service(db);
            await service.RecordAsync("ada", "terms", "v1", "register");
            await service.RecordAsync("ada", "age", "16", "register");
        }
        _time.Advance(TimeSpan.FromDays(1));
        var marked = _time.GetUtcNow().UtcDateTime;
        Require("terms", "v2", marked, marked.AddDays(7));

        await using (var db = NewContext())
            Assert.Empty(await Service(db).PendingDocumentsAsync("ada"));

        _time.Advance(TimeSpan.FromDays(7));
        await using (var db = NewContext())
            Assert.Equal(["terms"], await Service(db).PendingDocumentsAsync("ada"));

        await using (var db = NewContext())
            await Service(db).RecordAsync("ada", "terms", "v2", "reaccept");
        await using (var db = NewContext())
            Assert.Empty(await Service(db).PendingDocumentsAsync("ada"));
    }

    [Fact]
    public void A_requirement_is_unique_per_document_and_version()
    {
        Require("terms", "v2", Start, Start);

        Assert.Throws<DbUpdateException>(() => Require("terms", "v2", Start, Start));
    }

    // Writes the same consent through another context just before the save, like a second request would.
    private sealed class RivalInsert(Func<ConsentTestDbContext> open) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await using var other = open();
                other.AccountConsents.Add(new AccountConsent { UserId = "ada", Document = "terms", Version = "v1", Source = "rival", AcceptedAt = Start });
                await other.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }
}

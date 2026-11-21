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
    public DbSet<SignUpMailLog> SignUpMailLogs => Set<SignUpMailLog>();

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

    private static bool MustAccept(DateTime? acceptedAt, DateTime now, params ConsentRequirement[] requirements) =>
        MustAccept(acceptedAt is { } at ? [("v2", at)] : [], now, requirements);

    private static bool MustAccept(IReadOnlyList<(string Version, DateTime At)> acceptances, DateTime now, params ConsentRequirement[] requirements) =>
        AccountConsentService<ConsentTestDbContext>.MustAcceptAgain(acceptances, requirements, now);

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
        Assert.False(MustAccept((DateTime?)null, Start));
    }

    [Fact]
    public void A_requirement_that_is_not_in_effect_yet_does_not_bind()
    {
        Assert.False(MustAccept(Start.AddDays(-30), Start, Requirement("v2", Start, Start.AddDays(1))));
    }

    [Fact]
    public void A_requirement_in_effect_binds_a_user_whose_latest_acceptance_is_before_the_mark_whatever_its_version()
    {
        var requirement = Requirement("v2", Start, Start);

        Assert.True(MustAccept([("v1", Start.AddSeconds(-1))], Start.AddDays(2), requirement));
        Assert.True(MustAccept([("v2", Start.AddSeconds(-1))], Start.AddDays(2), requirement));
        Assert.True(MustAccept([(AccountConsent.Unversioned, Start.AddSeconds(-1))], Start.AddDays(2), requirement));
    }

    [Fact]
    public void An_acceptance_at_or_after_the_marked_time_satisfies_the_requirement_whatever_its_version()
    {
        var requirement = Requirement("v2", Start, Start);

        Assert.False(MustAccept(Start, Start, requirement));
        Assert.False(MustAccept(Start.AddMinutes(5), Start, requirement));
        Assert.False(MustAccept([("v1", Start)], Start.AddDays(2), requirement));
        Assert.False(MustAccept([("v0", Start.AddDays(-2)), (AccountConsent.Unversioned, Start.AddDays(1))], Start.AddDays(2), requirement));
    }

    [Fact]
    public void The_latest_acceptance_decides_and_not_the_version_the_requirement_records()
    {
        var requirement = Requirement("v2", Start, Start);

        Assert.True(MustAccept([("v2", Start.AddDays(-5)), ("v1", Start.AddDays(-3))], Start.AddDays(2), requirement));
        Assert.False(MustAccept([("v2", Start.AddDays(-5)), ("v1", Start.AddDays(1))], Start.AddDays(2), requirement));
    }

    [Fact]
    public void A_user_who_never_accepted_must_accept_once_a_requirement_is_in_effect()
    {
        Assert.True(MustAccept((DateTime?)null, Start, Requirement("v2", Start, Start)));
    }

    [Fact]
    public void Accepting_early_satisfies_a_requirement_before_it_comes_into_effect()
    {
        Assert.False(MustAccept(Start.AddDays(1), Start.AddDays(20), Requirement("v2", Start, Start.AddDays(14))));
    }

    [Fact]
    public void The_requirement_with_the_latest_effective_time_not_after_now_is_in_force_whatever_the_order_of_the_marks()
    {
        var first = Requirement("v2", Start, Start.AddDays(10));
        var second = Requirement("v3", Start.AddDays(1), Start.AddDays(5));
        var acceptances = new[] { ("v1", Start.AddHours(12)) };

        Assert.False(MustAccept(acceptances, Start.AddDays(2), first, second));
        Assert.True(MustAccept(acceptances, Start.AddDays(6), first, second));
        Assert.False(MustAccept(acceptances, Start.AddDays(11), first, second));
    }

    [Fact]
    public void Two_requirements_with_one_effective_time_are_told_apart_by_the_mark()
    {
        var older = Requirement("v2", Start, Start);
        var newer = Requirement("v3", Start.AddDays(2), Start);
        var acceptances = new[] { ("v1", Start.AddDays(1)) };

        Assert.False(MustAccept(acceptances, Start.AddDays(3), older));
        Assert.True(MustAccept(acceptances, Start.AddDays(3), older, newer));
    }

    [Fact]
    public async Task Recording_a_version_again_after_a_requirement_is_marked_adds_a_row_and_a_third_time_adds_none()
    {
        await using (var db = NewContext())
            Assert.True(await Service(db).RecordAsync("ada", "terms", "v1", "register"));
        _time.Advance(TimeSpan.FromDays(1));
        Require("terms", "v1", _time.GetUtcNow().UtcDateTime, _time.GetUtcNow().UtcDateTime);
        _time.Advance(TimeSpan.FromHours(1));

        await using (var db = NewContext())
            Assert.True(await Service(db).RecordAsync("ada", "terms", "v1", "reaccept"));
        await using (var db = NewContext())
            Assert.False(await Service(db).RecordAsync("ada", "terms", "v1", "reaccept"));

        Assert.Equal(["register", "reaccept"], Rows("ada").Select(r => r.Source));
    }

    [Fact]
    public async Task Recording_a_version_that_was_accepted_after_the_mark_adds_no_row()
    {
        Require("terms", "v1", Start, Start);
        _time.Advance(TimeSpan.FromHours(1));
        await using (var db = NewContext())
            Assert.True(await Service(db).RecordAsync("ada", "terms", "v1", "register"));

        await using (var db = NewContext())
            Assert.False(await Service(db).RecordAsync("ada", "terms", "v1", "reaccept"));

        Assert.Single(Rows("ada"));
    }

    [Fact]
    public async Task Recording_a_held_version_before_the_requirement_takes_effect_adds_a_row()
    {
        await using (var db = NewContext())
            Assert.True(await Service(db).RecordAsync("ada", "terms", "v1", "register"));
        _time.Advance(TimeSpan.FromDays(1));
        Require("terms", "v1", _time.GetUtcNow().UtcDateTime, _time.GetUtcNow().UtcDateTime.AddDays(7));
        _time.Advance(TimeSpan.FromHours(1));

        await using (var db = NewContext())
            Assert.True(await Service(db).RecordAsync("ada", "terms", "v1", "reaccept"));
        _time.Advance(TimeSpan.FromDays(7));

        await using var read = NewContext();
        Assert.Empty(await Service(read).PendingDocumentsAsync("ada"));
    }

    [Fact]
    public async Task A_race_between_two_acceptances_for_one_requirement_leaves_one_new_row()
    {
        await using (var db = NewContext())
            Assert.True(await Service(db).RecordAsync("ada", "terms", "v1", "register"));
        _time.Advance(TimeSpan.FromDays(1));
        Require("terms", "v1", _time.GetUtcNow().UtcDateTime, _time.GetUtcNow().UtcDateTime);
        _time.Advance(TimeSpan.FromHours(1));
        int requirementId;
        await using (var db = NewContext())
            requirementId = (await db.ConsentRequirements.SingleAsync()).Id;

        await using (var db = NewContext(new RivalInsert(() => NewContext(), requirementId)))
            Assert.False(await Service(db).RecordAsync("ada", "terms", "v1", "reaccept"));

        Assert.Equal(["register", "rival"], Rows("ada").Select(r => r.Source));
    }

    [Fact]
    public async Task A_user_who_accepted_the_marked_version_before_the_mark_is_pending_until_they_accept_again()
    {
        await using (var db = NewContext())
            await Service(db).RecordAsync("ada", "terms", "v2", "register");
        _time.Advance(TimeSpan.FromDays(1));
        Require("terms", "v2", _time.GetUtcNow().UtcDateTime, _time.GetUtcNow().UtcDateTime);

        await using (var read = NewContext())
            Assert.Equal(["terms"], await Service(read).PendingDocumentsAsync("ada"));

        _time.Advance(TimeSpan.FromHours(1));
        await using (var db = NewContext())
            await Service(db).RecordAsync("ada", "terms", "v2", "reaccept");
        await using (var read = NewContext())
            Assert.Empty(await Service(read).PendingDocumentsAsync("ada"));
    }

    [Fact]
    public async Task A_user_who_accepted_only_an_older_version_is_pending_until_a_new_acceptance()
    {
        await using (var db = NewContext())
            await Service(db).RecordAsync("ada", "terms", "v1", "register");
        _time.Advance(TimeSpan.FromDays(1));
        Require("terms", "v2", _time.GetUtcNow().UtcDateTime, _time.GetUtcNow().UtcDateTime);

        await using (var read = NewContext())
            Assert.Equal(["terms"], await Service(read).PendingDocumentsAsync("ada"));

        await using (var db = NewContext())
            await Service(db).RecordAsync("ada", "terms", "v2", "reaccept");
        await using (var read = NewContext())
            Assert.Empty(await Service(read).PendingDocumentsAsync("ada"));
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
    public void A_requirement_is_unique_per_document_and_effective_time()
    {
        Require("terms", "v2", Start, Start);

        Assert.Throws<DbUpdateException>(() => Require("terms", "v3", Start.AddDays(1), Start));
        Require("terms", "v2", Start, Start.AddDays(1));
        Require("privacy", "v2", Start, Start);
    }

    // Writes the same consent through another context just before the save, like a second request would.
    private sealed class RivalInsert(Func<ConsentTestDbContext> open, int requirementId = 0) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await using var other = open();
                other.AccountConsents.Add(new AccountConsent { UserId = "ada", Document = "terms", Version = "v1", Source = "rival", AcceptedAt = Start, RequirementId = requirementId });
                await other.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }
}

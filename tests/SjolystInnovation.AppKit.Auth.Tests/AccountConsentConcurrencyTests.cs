using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

/// <summary>Fires parallel acceptances of one user on a SQLite file database, where each one has its own connection.</summary>
public sealed class AccountConsentConcurrencyTests : IDisposable
{
    private const int Requests = 8;
    private static readonly DateTime Start = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"appkit-consent-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Start));

    public AccountConsentConcurrencyTests()
    {
        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Users.Add(new TestUser { Id = "ada", UserName = "ada" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private ConsentTestDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ConsentTestDbContext>().UseSqlite($"Data Source={_file};Pooling=False").Options);

    [Fact]
    public async Task Parallel_acceptances_of_a_pending_user_leave_the_user_not_pending_with_between_one_and_eight_rows()
    {
        await using (var db = NewContext())
        {
            Assert.True(await new AccountConsentService<ConsentTestDbContext>(db, _time).RecordAsync("ada", "terms", "v1", "register"));
            db.ConsentRequirements.Add(new ConsentRequirement { Document = "terms", Version = "v1", MarkedAt = Start.AddDays(1), EffectiveFrom = Start.AddDays(1) });
            await db.SaveChangesAsync();
        }
        _time.SetUtcNow(new DateTimeOffset(Start.AddDays(2)));

        await Task.WhenAll(Enumerable.Range(0, Requests).Select(_ => Task.Run(async () =>
        {
            await using var db = NewContext();
            await new AccountConsentService<ConsentTestDbContext>(db, _time).RecordAsync("ada", "terms", "v1", "reaccept");
        })));

        await using var read = NewContext();
        var service = new AccountConsentService<ConsentTestDbContext>(read, _time);
        Assert.Empty(await service.PendingDocumentsAsync("ada"));
        var rows = await read.AccountConsents.CountAsync(c => c.UserId == "ada" && c.Document == "terms" && c.Version == "v1" && c.Source == "reaccept");
        Assert.InRange(rows, 1, Requests);
    }
}

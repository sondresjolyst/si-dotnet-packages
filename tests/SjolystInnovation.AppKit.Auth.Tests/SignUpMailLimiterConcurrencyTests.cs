using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

/// <summary>Fires many reservations at once on a SQLite file database, where each one has its own connection.</summary>
public sealed class SignUpMailLimiterConcurrencyTests : IDisposable
{
    private const int Requests = 30;
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"appkit-auth-{Guid.NewGuid():N}.db");

    public SignUpMailLimiterConcurrencyTests()
    {
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private RegistrationTestDbContext NewContext() =>
        new(new DbContextOptionsBuilder<RegistrationTestDbContext>().UseSqlite($"Data Source={_file};Pooling=False").Options);

    private async Task<bool> ReserveAsync(string email, string kind, int anonymousCap, int ownerCap)
    {
        await using var db = NewContext();
        var limiter = new SignUpMailLimiter<RegistrationTestDbContext>(db, TimeProvider.System, new UpperInvariantLookupNormalizer());
        return await limiter.TryReserveAsync(email, null, kind, anonymousCap, ownerCap, TimeSpan.Zero, CancellationToken.None);
    }

    private async Task<int> SucceededAsync(IEnumerable<(string Email, string Kind)> requests, int anonymousCap, int ownerCap)
    {
        var results = await Task.WhenAll(requests.Select(r => Task.Run(() => ReserveAsync(r.Email, r.Kind, anonymousCap, ownerCap))));
        return results.Count(r => r);
    }

    [Fact]
    public async Task Parallel_reservations_for_one_mailbox_never_pass_the_anonymous_cap()
    {
        var requests = Enumerable.Range(0, Requests).Select(i => ("name@example.invalid", i % 2 == 0 ? SignUpMailKinds.Notice : SignUpMailKinds.Verification));

        Assert.Equal(5, await SucceededAsync(requests, anonymousCap: 5, ownerCap: 5));
    }

    [Fact]
    public async Task Parallel_reservations_for_one_account_never_pass_the_owner_cap()
    {
        var requests = Enumerable.Range(0, Requests).Select(i => ("name@example.invalid", i % 2 == 0 ? SignUpMailKinds.Resend : SignUpMailKinds.Reminder));

        Assert.Equal(5, await SucceededAsync(requests, anonymousCap: 5, ownerCap: 5));
    }

    [Fact]
    public async Task Parallel_reservations_of_mixed_kinds_never_pass_the_total_of_the_mailbox()
    {
        var accounts = new[] { "name+a@example.invalid", "name+b@example.invalid", "name+c@example.invalid", "name+d@example.invalid" };
        var requests = Enumerable.Range(0, Requests).Select(i => i % 3 == 0
            ? ("name@example.invalid", SignUpMailKinds.Notice)
            : (accounts[i % accounts.Length], SignUpMailKinds.Resend));

        Assert.Equal(5, await SucceededAsync(requests, anonymousCap: 2, ownerCap: 3));
        await using var db = NewContext();
        Assert.Equal(5, await db.SignUpMailLogs.Where(l => l.Kind == SignUpMailKinds.Total).SumAsync(l => l.Count));
    }
}

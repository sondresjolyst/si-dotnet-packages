using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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

    private static SignUpMailLimiter<RegistrationTestDbContext> NewLimiter(RegistrationTestDbContext db) =>
        new(db, TimeProvider.System, Options.Create(new AppKitAuthOptions { Key = MailKeys.SigningKey }), new UpperInvariantLookupNormalizer());

    private async Task<bool> ReserveAsync(string email, string kind, int anonymousCap, int ownerCap, TimeSpan cooldown = default)
    {
        await using var db = NewContext();
        return await NewLimiter(db).TryReserveAsync(email, null, kind, anonymousCap, ownerCap, cooldown, CancellationToken.None);
    }

    private async Task<bool> CountNetworkAsync(string network, int cap)
    {
        await using var db = NewContext();
        return await NewLimiter(db).TryCountNetworkAsync(network, cap, CancellationToken.None);
    }

    private async Task<int> CountOfAsync(string kind)
    {
        await using var db = NewContext();
        return await db.SignUpMailLogs.Where(l => l.Kind == kind).SumAsync(l => l.Count);
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

    [Fact]
    public async Task Parallel_sign_ups_from_one_network_stop_exactly_at_the_cap()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => CountNetworkAsync("203.0.113.0", 20))));

        Assert.Equal(20, results.Count(r => r));
        Assert.Equal(20, await CountOfAsync(SignUpMailKinds.Network));
    }

    [Fact]
    public async Task Parallel_sign_ups_from_one_network_below_the_cap_are_all_counted()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => Task.Run(() => CountNetworkAsync("203.0.113.0", 100))));

        Assert.All(results, Assert.True);
        Assert.Equal(60, await CountOfAsync(SignUpMailKinds.Network));
    }

    [Fact]
    public async Task Parallel_reservations_with_a_cooldown_let_exactly_one_through()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() => ReserveAsync("name@example.invalid", SignUpMailKinds.Notice, 50, 50, TimeSpan.FromHours(1)))));

        Assert.Single(results, r => r);
        Assert.Equal(1, await CountOfAsync(SignUpMailKinds.Total));
    }

    [Fact]
    public async Task A_refusal_by_the_budget_leaves_the_total_unchanged()
    {
        Assert.True(await ReserveAsync("name@example.invalid", SignUpMailKinds.Notice, 2, 5));
        Assert.True(await ReserveAsync("name@example.invalid", SignUpMailKinds.Verification, 2, 5));

        Assert.False(await ReserveAsync("name@example.invalid", SignUpMailKinds.Notice, 2, 5));

        Assert.Equal(2, await CountOfAsync(SignUpMailKinds.Total));
        Assert.Equal(2, await CountOfAsync(SignUpMailKinds.AnonymousBudget));
        Assert.Equal(2, await CountOfAsync(SignUpMailKinds.Notice) + await CountOfAsync(SignUpMailKinds.Verification));
    }

    [Fact]
    public async Task A_refusal_by_the_cooldown_leaves_the_total_and_the_budget_unchanged()
    {
        Assert.True(await ReserveAsync("name@example.invalid", SignUpMailKinds.Notice, 5, 5, TimeSpan.FromHours(1)));

        Assert.False(await ReserveAsync("name@example.invalid", SignUpMailKinds.Notice, 5, 5, TimeSpan.FromHours(1)));

        Assert.Equal(1, await CountOfAsync(SignUpMailKinds.Total));
        Assert.Equal(1, await CountOfAsync(SignUpMailKinds.AnonymousBudget));
        Assert.Equal(1, await CountOfAsync(SignUpMailKinds.Notice));
    }

    [Fact]
    public async Task A_cooldown_holds_across_midnight()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 23, 50, 0, TimeSpan.Zero));
        await using var db = NewContext();
        var limiter = new SignUpMailLimiter<RegistrationTestDbContext>(db, time, Options.Create(new AppKitAuthOptions { Key = MailKeys.SigningKey }), new UpperInvariantLookupNormalizer());
        Assert.True(await limiter.TryReserveAsync("name@example.invalid", null, SignUpMailKinds.Notice, 5, 5, TimeSpan.FromMinutes(30), CancellationToken.None));

        time.Advance(TimeSpan.FromMinutes(20));
        Assert.False(await limiter.TryReserveAsync("name@example.invalid", null, SignUpMailKinds.Notice, 5, 5, TimeSpan.FromMinutes(30), CancellationToken.None));

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.True(await limiter.TryReserveAsync("name@example.invalid", null, SignUpMailKinds.Notice, 5, 5, TimeSpan.FromMinutes(30), CancellationToken.None));
    }

    [Fact]
    public async Task Counting_on_a_row_that_another_context_made_leaves_no_entity_tracked()
    {
        await using var first = NewContext();
        await using var second = NewContext();
        var limiter = NewLimiter(second);

        // The first context makes the row, and the second one counts on the same row afterwards.
        Assert.True(await NewLimiter(first).TryCountNetworkAsync("203.0.113.0", 3, CancellationToken.None));
        Assert.True(await limiter.TryCountNetworkAsync("203.0.113.0", 3, CancellationToken.None));
        Assert.True(await limiter.TryCountNetworkAsync("203.0.113.0", 3, CancellationToken.None));
        Assert.False(await limiter.TryCountNetworkAsync("203.0.113.0", 3, CancellationToken.None));

        Assert.Empty(second.ChangeTracker.Entries());
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public sealed class RefreshTokenServiceTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Start));
    private readonly FakeLogger<RefreshTokenService<TestDbContext>> _logger = new();

    public RefreshTokenServiceTests()
    {
        using var db = _db.NewContext();
        db.Users.AddRange(new TestUser { Id = "ada", UserName = "ada" }, new TestUser { Id = "bob", UserName = "bob" });
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private RefreshTokenService<TestDbContext> Service(TestDbContext db) =>
        new(db, Options.Create(new AuthOptions { Key = "unused" }), _time, _logger);

    /// <summary>Adds a token row directly, as an earlier sign-in would have.</summary>
    private void Seed(string userId, string raw, DateTime created, DateTime? expires = null, DateTime? revoked = null)
    {
        using var db = _db.NewContext();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId, Token = VerificationCodes.Hash(raw), Created = created,
            Expires = expires ?? created.AddMonths(6), Revoked = revoked,
        });
        db.SaveChanges();
    }

    private List<RefreshToken> Tokens(string userId)
    {
        using var db = _db.NewContext();
        return db.RefreshTokens.AsNoTracking().Where(t => t.UserId == userId).OrderBy(t => t.Id).ToList();
    }

    private async Task<string> Issue(string userId)
    {
        await using var db = _db.NewContext();
        var raw = await Service(db).IssueAsync(userId);
        await db.SaveChangesAsync();
        return raw;
    }

    private async Task<RotationResult> Rotate(string userId, string raw)
    {
        await using var db = _db.NewContext();
        return await Service(db).RotateAsync(userId, raw);
    }

    [Fact]
    public async Task Issue_StoresOnlyTheHashWithASixMonthExpiry()
    {
        var raw = await Issue("ada");

        var row = Assert.Single(Tokens("ada"));
        Assert.Equal(44, raw.Length);
        Assert.Equal(VerificationCodes.Hash(raw), row.Token);
        Assert.NotEqual(raw, row.Token);
        Assert.Equal(Start, row.Created);
        Assert.Equal(Start.AddMonths(6), row.Expires);
        Assert.Null(row.Revoked);
    }

    [Fact]
    public async Task Issue_GivesEveryTokenItsOwnValue()
    {
        Assert.NotEqual(await Issue("ada"), await Issue("ada"));
    }

    [Fact]
    public async Task Issue_DeletesTheOldestLiveTokenAtTheCap()
    {
        for (var i = 0; i < 5; i++) Seed("ada", $"t{i}", Start.AddMinutes(-10 + i));

        await Issue("ada");

        var tokens = Tokens("ada");
        Assert.Equal(5, tokens.Count);
        Assert.DoesNotContain(tokens, t => t.Token == VerificationCodes.Hash("t0"));
        Assert.All(tokens, t => Assert.Null(t.Revoked));
    }

    [Fact]
    public async Task Issue_BreaksATieOnCreatedByTheOlderRow()
    {
        for (var i = 0; i < 5; i++) Seed("ada", $"t{i}", Start.AddMinutes(-10));

        await Issue("ada");

        Assert.DoesNotContain(Tokens("ada"), t => t.Token == VerificationCodes.Hash("t0"));
        Assert.Contains(Tokens("ada"), t => t.Token == VerificationCodes.Hash("t1"));
    }

    [Fact]
    public async Task Issue_TrimsBackToTheCapWhenAboveIt()
    {
        for (var i = 0; i < 7; i++) Seed("ada", $"t{i}", Start.AddMinutes(-10 + i));

        await Issue("ada");

        var tokens = Tokens("ada");
        Assert.Equal(5, tokens.Count);
        foreach (var gone in (string[])["t0", "t1", "t2"])
            Assert.DoesNotContain(tokens, t => t.Token == VerificationCodes.Hash(gone));
    }

    [Fact]
    public async Task Issue_LeavesExpiredRevokedAndOtherUsersTokensOutOfTheCount()
    {
        for (var i = 0; i < 4; i++) Seed("ada", $"t{i}", Start.AddMinutes(-10 + i));
        Seed("ada", "expired", Start.AddMonths(-7), expires: Start.AddMonths(-1));
        Seed("ada", "at-expiry", Start.AddMonths(-6), expires: Start);
        Seed("ada", "revoked", Start.AddDays(-1), revoked: Start.AddHours(-1));
        for (var i = 0; i < 5; i++) Seed("bob", $"b{i}", Start.AddMinutes(-10 + i));

        await Issue("ada");

        Assert.Equal(8, Tokens("ada").Count);
        Assert.Equal(5, Tokens("bob").Count);
    }

    [Fact]
    public async Task Issue_HonoursACustomCap()
    {
        for (var i = 0; i < 3; i++) Seed("ada", $"t{i}", Start.AddMinutes(-10 + i));
        await using var db = _db.NewContext();
        var service = new RefreshTokenService<TestDbContext>(
            db, Options.Create(new AuthOptions { Key = "unused", MaxLiveRefreshTokens = 2, RefreshTokenLifetimeMonths = 1 }), _time, _logger);

        await service.IssueAsync("ada");
        await db.SaveChangesAsync();

        Assert.Equal(2, Tokens("ada").Count);
        Assert.Contains(Tokens("ada"), t => t.Expires == Start.AddMonths(1));
    }

    [Fact]
    public async Task Issue_LeavesSavingToTheCaller()
    {
        await using (var db = _db.NewContext())
            await Service(db).IssueAsync("ada");

        Assert.Empty(Tokens("ada"));
    }

    [Fact]
    public async Task Rotate_SwapsTheTokenForANewOne()
    {
        Seed("ada", "old", Start.AddDays(-1));
        _time.Advance(TimeSpan.FromMinutes(1));

        var result = await Rotate("ada", "old");

        Assert.Equal(RotationStatus.Rotated, result.Status);
        var tokens = Tokens("ada");
        Assert.Equal(Start.AddMinutes(1), tokens.Single(t => t.Token == VerificationCodes.Hash("old")).Revoked);
        var fresh = tokens.Single(t => t.Token == VerificationCodes.Hash(result.RefreshToken!));
        Assert.Null(fresh.Revoked);
    }

    [Fact]
    public async Task Rotate_AtTheCapKeepsTheOtherDevicesSignedIn()
    {
        for (var i = 0; i < 5; i++) Seed("ada", $"t{i}", Start.AddMinutes(-10 + i));

        var result = await Rotate("ada", "t2");

        Assert.Equal(RotationStatus.Rotated, result.Status);
        var live = Tokens("ada").Where(t => t.Revoked == null).ToList();
        Assert.Equal(5, live.Count);
        foreach (var kept in (string[])["t0", "t1", "t3", "t4"])
            Assert.Contains(live, t => t.Token == VerificationCodes.Hash(kept));
    }

    [Fact]
    public async Task Rotate_RejectsAnUnknownToken()
    {
        Seed("ada", "real", Start.AddDays(-1));

        var result = await Rotate("ada", "made-up");

        Assert.Equal(new RotationResult(RotationStatus.Rejected), result);
        Assert.Null(Assert.Single(Tokens("ada")).Revoked);
    }

    [Fact]
    public async Task Rotate_RejectsATokenAtItsExpiry()
    {
        Seed("ada", "old", Start.AddMonths(-6), expires: Start);

        Assert.Equal(RotationStatus.Rejected, (await Rotate("ada", "old")).Status);
        Assert.Null(Assert.Single(Tokens("ada")).Revoked);
    }

    [Fact]
    public async Task Rotate_AcceptsATokenJustBeforeItsExpiry()
    {
        Seed("ada", "old", Start.AddMonths(-6), expires: Start.AddTicks(1));

        Assert.Equal(RotationStatus.Rotated, (await Rotate("ada", "old")).Status);
    }

    [Fact]
    public async Task Rotate_RejectsAnotherUsersToken()
    {
        Seed("bob", "bobs", Start.AddDays(-1));

        Assert.Equal(RotationStatus.Rejected, (await Rotate("ada", "bobs")).Status);
        Assert.Null(Assert.Single(Tokens("bob")).Revoked);
        Assert.Empty(Tokens("ada"));
    }

    [Fact]
    public async Task Rotate_RevokesEveryLiveTokenOfTheUserOnAReplay()
    {
        Seed("ada", "old", Start.AddDays(-1));
        var first = await Rotate("ada", "old");
        Seed("ada", "laptop", Start.AddHours(-1));
        Seed("bob", "bobs", Start.AddDays(-1));
        _time.Advance(TimeSpan.FromMinutes(5));

        var replay = await Rotate("ada", "old");

        Assert.Equal(new RotationResult(RotationStatus.Replayed), replay);
        Assert.All(Tokens("ada"), t => Assert.NotNull(t.Revoked));
        Assert.Equal(Start.AddMinutes(5), Tokens("ada").Single(t => t.Token == VerificationCodes.Hash(first.RefreshToken!)).Revoked);
        Assert.Equal(Start, Tokens("ada").Single(t => t.Token == VerificationCodes.Hash("old")).Revoked);
        Assert.Null(Assert.Single(Tokens("bob")).Revoked);
        var log = Assert.Single(_logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal("Refresh token replayed for user ada. Revoking 2 live token(s).", log.Message);
    }

    /// <summary>Uses a token from another request just before the first save, as a parallel refresh would.</summary>
    private sealed class ParallelUse(string hashedToken, DateTime at) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await eventData.Context!.Database.ExecuteSqlAsync(
                    $"UPDATE RefreshTokens SET Revoked = {at} WHERE Token = {hashedToken}", cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task Rotate_LosingARaceWithAParallelUseCountsAsAReplay()
    {
        Seed("ada", "old", Start.AddDays(-1));
        Seed("ada", "laptop", Start.AddHours(-1));
        await using var db = _db.NewContext(new ParallelUse(VerificationCodes.Hash("old"), Start));

        var result = await Service(db).RotateAsync("ada", "old");

        Assert.Equal(new RotationResult(RotationStatus.Replayed), result);
        Assert.Equal(2, Tokens("ada").Count);
        Assert.All(Tokens("ada"), t => Assert.NotNull(t.Revoked));
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Rotate_RevokingAfterAReplayTriesAgainWhenATokenChangesUnderIt()
    {
        Seed("ada", "old", Start.AddDays(-1), revoked: Start.AddHours(-1));
        Seed("ada", "laptop", Start.AddHours(-1));
        await using var db = _db.NewContext(new ParallelUse(VerificationCodes.Hash("laptop"), Start));

        var result = await Service(db).RotateAsync("ada", "old");

        Assert.Equal(RotationStatus.Replayed, result.Status);
        Assert.All(Tokens("ada"), t => Assert.NotNull(t.Revoked));
    }

    private async Task<bool> Revoke(string raw)
    {
        await using var db = _db.NewContext();
        return await Service(db).RevokeAsync(raw);
    }

    [Fact]
    public async Task Revoke_DeletesTheLiveTokenAndLeavesTheOthers()
    {
        Seed("ada", "phone", Start.AddDays(-1));
        Seed("ada", "laptop", Start.AddDays(-1));
        Seed("bob", "bobs", Start.AddDays(-1));

        Assert.True(await Revoke("phone"));

        Assert.Equal([VerificationCodes.Hash("laptop")], Tokens("ada").Select(t => t.Token));
        Assert.Null(Tokens("ada").Single().Revoked);
        Assert.Single(Tokens("bob"));
    }

    [Fact]
    public async Task Revoke_AfterwardsTheTokenIsRefusedAsUnknownNotAsAReplay()
    {
        Seed("ada", "phone", Start.AddDays(-1));
        Seed("ada", "laptop", Start.AddDays(-1));
        await Revoke("phone");

        var result = await Rotate("ada", "phone");

        Assert.Equal(RotationStatus.Rejected, result.Status);
        Assert.Null(Tokens("ada").Single().Revoked);
        Assert.Empty(_logger.Collector.GetSnapshot());
    }

    [Fact]
    public async Task Revoke_LeavesAUsedTokenSoItStillCountsAsAReplay()
    {
        Seed("ada", "used", Start.AddDays(-1), revoked: Start.AddHours(-1));
        Seed("ada", "laptop", Start.AddDays(-1));

        Assert.False(await Revoke("used"));
        Assert.Equal(2, Tokens("ada").Count);
        Assert.Equal(RotationStatus.Replayed, (await Rotate("ada", "used")).Status);
    }

    [Fact]
    public async Task Revoke_AnUnknownTokenChangesNothing()
    {
        Seed("ada", "phone", Start.AddDays(-1));

        Assert.False(await Revoke("made-up"));
        Assert.Single(Tokens("ada"));
    }

    [Fact]
    public async Task Revoke_LosingARaceWithARefreshLeavesTheTokenUsed()
    {
        Seed("ada", "phone", Start.AddDays(-1));
        await using var db = _db.NewContext(new ParallelUse(VerificationCodes.Hash("phone"), Start));

        Assert.False(await Service(db).RevokeAsync("phone"));
        Assert.NotNull(Assert.Single(Tokens("ada")).Revoked);
        Assert.Empty(db.ChangeTracker.Entries<RefreshToken>());
    }

    private async Task<int> RevokeAll(string userId, params IInterceptor[] interceptors)
    {
        await using var db = _db.NewContext(interceptors);
        return await Service(db).RevokeAllForUserAsync(userId);
    }

    [Fact]
    public async Task RevokeAll_DeletesEveryLiveTokenOfTheUserOnly()
    {
        Seed("ada", "phone", Start.AddDays(-1));
        Seed("ada", "laptop", Start.AddDays(-1));
        Seed("ada", "used", Start.AddDays(-2), revoked: Start.AddDays(-1));
        Seed("bob", "bobs", Start.AddDays(-1));

        Assert.Equal(2, await RevokeAll("ada"));

        Assert.Equal([VerificationCodes.Hash("used")], Tokens("ada").Select(t => t.Token));
        Assert.Single(Tokens("bob"));
    }

    [Fact]
    public async Task RevokeAll_AfterwardsAnOldTokenIsRefusedAsUnknown()
    {
        Seed("ada", "phone", Start.AddDays(-1));
        await RevokeAll("ada");

        Assert.Equal(RotationStatus.Rejected, (await Rotate("ada", "phone")).Status);
        Assert.Empty(_logger.Collector.GetSnapshot());
    }

    [Fact]
    public async Task RevokeAll_WithNothingLiveReturnsZero()
    {
        Seed("ada", "used", Start.AddDays(-2), revoked: Start.AddDays(-1));

        Assert.Equal(0, await RevokeAll("ada"));
        Assert.Single(Tokens("ada"));
    }

    [Fact]
    public async Task RevokeAll_TriesAgainWhenARefreshUsesATokenMeanwhile()
    {
        Seed("ada", "phone", Start.AddDays(-1));
        Seed("ada", "laptop", Start.AddDays(-1));

        var count = await RevokeAll("ada", new ParallelUse(VerificationCodes.Hash("phone"), Start));

        Assert.Equal(1, count);
        Assert.Equal([VerificationCodes.Hash("phone")], Tokens("ada").Select(t => t.Token));
        Assert.NotNull(Tokens("ada").Single().Revoked);
    }

    [Fact]
    public async Task Rotate_ReplayWithNothingLeftToRevokeLogsNothing()
    {
        Seed("ada", "old", Start.AddDays(-1), revoked: Start.AddHours(-1));

        Assert.Equal(RotationStatus.Replayed, (await Rotate("ada", "old")).Status);
        Assert.Empty(_logger.Collector.GetSnapshot());
    }

    [Fact]
    public async Task Rotate_TheNewTokenRotatesAgain()
    {
        Seed("ada", "old", Start.AddDays(-1));
        var first = await Rotate("ada", "old");

        var second = await Rotate("ada", first.RefreshToken!);

        Assert.Equal(RotationStatus.Rotated, second.Status);
        Assert.Single(Tokens("ada"), t => t.Revoked == null);
    }

    [Fact]
    public async Task Rotate_CommitsSoAnotherRequestSeesTheChange()
    {
        Seed("ada", "old", Start.AddDays(-1));
        await using var db = _db.NewContext();

        await Service(db).RotateAsync("ada", "old");

        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(2, Tokens("ada").Count);
    }

    [Fact]
    public async Task Rotate_EndsTheTransactionAfterARejection()
    {
        await using var db = _db.NewContext();

        await Service(db).RotateAsync("ada", "made-up");

        Assert.Null(db.Database.CurrentTransaction);
    }
}

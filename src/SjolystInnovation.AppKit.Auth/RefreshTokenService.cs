using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>Issues and rotates refresh tokens.</summary>
public interface IRefreshTokenService
{
    /// <summary>
    /// Adds a new refresh token for the user and returns the raw token. Live tokens over
    /// <see cref="AuthOptions.MaxLiveRefreshTokens"/> are deleted, oldest first. The caller saves.
    /// </summary>
    Task<string> IssueAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Swaps a refresh token for a new one, in one transaction that this call saves and commits. A
    /// token that was already used revokes every live token of the user.
    /// </summary>
    Task<RotationResult> RotateAsync(string userId, string rawToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a live refresh token, as at sign-out, and returns whether there was one. A later use of
    /// it is refused as unknown rather than taken for a replay. A token already used is left alone, so
    /// presenting it still counts as a replay. This call saves.
    /// </summary>
    Task<bool> RevokeAsync(string rawToken, CancellationToken cancellationToken = default);
}

public enum RotationStatus
{
    /// <summary>The token was swapped. <see cref="RotationResult.RefreshToken"/> holds the new one.</summary>
    Rotated,

    /// <summary>The token is unknown, expired or belongs to another user. Nothing changed.</summary>
    Rejected,

    /// <summary>The token was already used. Every live token of the user is now revoked.</summary>
    Replayed,
}

public sealed record RotationResult(RotationStatus Status, string? RefreshToken = null);

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh token replayed for user {UserId}. Revoking {RevokedCount} live token(s).")]
    public static partial void Replayed(ILogger logger, string userId, int revokedCount);
}

internal sealed class RefreshTokenService<TContext>(
    TContext db,
    IOptions<AuthOptions> options,
    TimeProvider time,
    ILogger<RefreshTokenService<TContext>> logger) : IRefreshTokenService
    where TContext : DbContext, IAuthDbContext
{
    private readonly AuthOptions _options = options.Value;

    public async Task<string> IssueAsync(string userId, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var live = (await db.RefreshTokens
                .Where(t => t.UserId == userId && t.Revoked == null && t.Expires > now)
                .OrderBy(t => t.Created)
                .ThenBy(t => t.Id)
                .ToListAsync(cancellationToken))
            // The database still reports a token live that a rotation revoked without saving yet.
            .Where(t => t.Revoked == null)
            .ToList();

        // Delete rather than revoke. Presenting a revoked token counts as a replay, so an evicted
        // device's next refresh would otherwise revoke every session the user has.
        foreach (var stale in live.SkipLast(_options.MaxLiveRefreshTokens - 1))
            db.RefreshTokens.Remove(stale);

        db.RefreshTokens.Add(new RefreshToken
        {
            Token = VerificationCodes.Hash(raw),
            UserId = userId,
            Created = now,
            Expires = now.AddMonths(_options.RefreshTokenLifetimeMonths),
        });
        return raw;
    }

    public async Task<RotationResult> RotateAsync(string userId, string rawToken, CancellationToken cancellationToken = default)
    {
        var hashed = VerificationCodes.Hash(rawToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var match = await db.RefreshTokens.FirstOrDefaultAsync(t => t.Token == hashed && t.UserId == userId, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;

        if (match == null || (match.Revoked == null && match.Expires <= now))
        {
            await tx.RollbackAsync(cancellationToken);
            return new RotationResult(RotationStatus.Rejected);
        }

        if (match.Revoked == null)
        {
            match.Revoked = now;
            var next = await IssueAsync(userId, cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return new RotationResult(RotationStatus.Rotated, next);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Revoked is a concurrency token, so a request that used the same token after this
                // one read it makes this save fail. The token was used twice, which is a replay.
            }
        }

        await tx.RollbackAsync(cancellationToken);
        await RevokeAllAsync(userId, cancellationToken);
        return new RotationResult(RotationStatus.Replayed);
    }

    public async Task<bool> RevokeAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        var hashed = VerificationCodes.Hash(rawToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.Token == hashed && t.Revoked == null, cancellationToken);
        if (token == null)
            return false;
        db.RefreshTokens.Remove(token);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // A refresh used the token in the meantime, so it is no longer live.
            db.Entry(token).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>Revokes every live token of the user, trying again while other requests change them.</summary>
    private async Task RevokeAllAsync(string userId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            foreach (var entry in db.ChangeTracker.Entries<RefreshToken>().ToList())
                entry.State = EntityState.Detached;
            var now = time.GetUtcNow().UtcDateTime;
            var live = await db.RefreshTokens.Where(t => t.UserId == userId && t.Revoked == null).ToListAsync(cancellationToken);
            foreach (var t in live) t.Revoked = now;
            // Logged before the save, so a revocation that fails to save is not silent.
            if (live.Count > 0)
                Log.Replayed(logger, userId, live.Count);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
            }
        }
    }
}

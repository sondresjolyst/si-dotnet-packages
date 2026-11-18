using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace SjolystInnovation.AppKit.Auth;

// What the bearer check and the sign-in answers need to know about a user's consents. Registered only when the app opted in to sign-up.
internal interface IPendingConsentReader
{
    // The documents the user must accept again right now, each with the version that is current, in document order.
    Task<IReadOnlyList<PendingConsent>> GetPendingAsync(string userId, CancellationToken cancellationToken = default);
}

// Records acceptances and tells whether a user must accept a document again. Time comes from the TimeProvider.
// The requirement rows are cached for a short time when a cache is given. Marking a requirement clears them.
internal sealed class AccountConsentService<TContext>(TContext db, TimeProvider time, IMemoryCache? cache = null) : IPendingConsentReader
    where TContext : DbContext, IAccountConsentDbContext
{
    internal const string RequirementsCacheKey = "SjolystInnovation.AppKit.Auth:consent-requirements";
    private static readonly TimeSpan RequirementsCacheDuration = TimeSpan.FromMinutes(1);

    // Adds the acceptance and saves. Returns false when the user already accepted this version of the document,
    // which includes a request that lost a race on the unique index. The first row stays as it was. The caller must
    // not hold a transaction around this call. A failed insert aborts a Postgres transaction, and then the check
    // for the row that won the race fails too.
    public async Task<bool> RecordAsync(
        string userId, string document, string version, string source,
        string? locale = null, string? ip = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentException.ThrowIfNullOrEmpty(document);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(source);

        if (await ExistsAsync(userId, document, version, cancellationToken))
            return false;

        var row = new AccountConsent
        {
            UserId = userId, Document = document, Version = version, Source = source,
            Locale = locale, Ip = ip, AcceptedAt = time.GetUtcNow().UtcDateTime,
        };
        db.AccountConsents.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Another request may have inserted the same key first. Anything else is a real failure.
            db.Entry(row).State = EntityState.Detached;
            if (await ExistsAsync(userId, document, version, cancellationToken))
                return false;
            throw;
        }
    }

    // The time of the newest acceptance for each document the user has accepted.
    public async Task<IReadOnlyDictionary<string, DateTime>> LatestAcceptancesAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        // Grouped in memory, because a user has few rows and not every provider translates MAX over a date.
        var rows = await db.AccountConsents.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new { c.Document, c.AcceptedAt })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.Document).ToDictionary(g => g.Key, g => g.Max(r => r.AcceptedAt));
    }

    // The documents the user must accept again right now, in name order.
    public async Task<IReadOnlyList<string>> PendingDocumentsAsync(string userId, CancellationToken cancellationToken = default) =>
        [.. (await GetPendingAsync(userId, cancellationToken)).Select(p => p.Document)];

    // Each document the user must accept again, with the version of its newest requirement in effect.
    public async Task<IReadOnlyList<PendingConsent>> GetPendingAsync(string userId, CancellationToken cancellationToken = default)
    {
        var latest = await LatestAcceptancesAsync(userId, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var requirements = await RequirementsAsync(cancellationToken);
        return [.. requirements
            .GroupBy(r => r.Document)
            .Where(g => MustAcceptAgain(latest.TryGetValue(g.Key, out var at) ? at : null, g, now))
            .Select(g => new PendingConsent(g.Key, g.Where(r => r.EffectiveFrom <= now).OrderByDescending(r => r.MarkedAt).ThenByDescending(r => r.Id).First().Version))
            .OrderBy(p => p.Document, StringComparer.Ordinal)];
    }

    private async Task<IReadOnlyList<ConsentRequirement>> RequirementsAsync(CancellationToken cancellationToken)
    {
        if (cache is not null && cache.TryGetValue(RequirementsCacheKey, out IReadOnlyList<ConsentRequirement>? cached) && cached is not null)
            return cached;
        IReadOnlyList<ConsentRequirement> rows = await db.ConsentRequirements.AsNoTracking().ToListAsync(cancellationToken);
        cache?.Set(RequirementsCacheKey, rows, RequirementsCacheDuration);
        return rows;
    }

    // A requirement binds from EffectiveFrom. The user is pending when the latest acceptance is older than the
    // newest requirement in effect was marked, or when there is no acceptance at all.
    internal static bool MustAcceptAgain(DateTime? latestAcceptedAt, IEnumerable<ConsentRequirement> requirements, DateTime now)
    {
        var markedAt = requirements.Where(r => r.EffectiveFrom <= now).Select(r => (DateTime?)r.MarkedAt).Max();
        return markedAt is { } marked && (latestAcceptedAt is null || latestAcceptedAt < marked);
    }

    private Task<bool> ExistsAsync(string userId, string document, string version, CancellationToken cancellationToken) =>
        db.AccountConsents.AsNoTracking().AnyAsync(c => c.UserId == userId && c.Document == document && c.Version == version, cancellationToken);
}

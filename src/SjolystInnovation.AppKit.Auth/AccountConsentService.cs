using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace SjolystInnovation.AppKit.Auth;

// What the bearer check and the sign-in answers need to know about a user's consents. Registered only when the app opted in to sign-up.
internal interface IPendingConsentReader
{
    // The documents the user must accept again right now, each with the version that is current, in document order.
    Task<IReadOnlyList<PendingConsent>> GetPendingAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>One consent row of the account export. The shortened IP address is never part of it.</summary>
internal sealed record ConsentExportRow(string Document, string Version, DateTime AcceptedAt, string Source, string? Locale);

// Lists the consent rows of a user for the account export. Registered only when the app opted in to sign-up.
internal interface IAccountConsentExporter
{
    Task<IReadOnlyList<ConsentExportRow>> ExportAsync(string userId, CancellationToken cancellationToken);
}

// Records acceptances and tells whether a user must accept a document again. Time comes from the TimeProvider.
// The requirement rows are cached for a short time when a cache is given. Marking a requirement clears them.
internal sealed class AccountConsentService<TContext>(TContext db, TimeProvider time, IMemoryCache? cache = null, IConsentVersionSource? versions = null) : IPendingConsentReader, IAccountConsentExporter
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

    // The time is stored in UTC, and the export says so, whatever kind the provider gives back.
    public async Task<IReadOnlyList<ConsentExportRow>> ExportAsync(string userId, CancellationToken cancellationToken)
    {
        var rows = await db.AccountConsents.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.AcceptedAt).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(c => new ConsentExportRow(c.Document, c.Version, DateTime.SpecifyKind(c.AcceptedAt, DateTimeKind.Utc), c.Source, c.Locale))];
    }

    // Every acceptance of the user, grouped by document.
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<(string Version, DateTime At)>>> AcceptancesAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var rows = await db.AccountConsents.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new { c.Document, c.Version, c.AcceptedAt })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.Document).ToDictionary(g => g.Key, g => (IReadOnlyList<(string Version, DateTime At)>)[.. g.Select(r => (r.Version, r.AcceptedAt))]);
    }

    // The documents the user must accept again right now, in name order.
    public async Task<IReadOnlyList<string>> PendingDocumentsAsync(string userId, CancellationToken cancellationToken = default) =>
        [.. (await GetPendingAsync(userId, cancellationToken)).Select(p => p.Document)];

    // Each document the user must accept again, with the version of its newest requirement in effect. The version source
    // is asked without a locale, because the requirement does not belong to one.
    public async Task<IReadOnlyList<PendingConsent>> GetPendingAsync(string userId, CancellationToken cancellationToken = default)
    {
        var accepted = await AcceptancesAsync(userId, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var pending = new List<PendingConsent>();
        foreach (var group in (await RequirementsAsync(cancellationToken)).GroupBy(r => r.Document).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (NewestInEffect(group, now) is not { } required)
                continue;
            if (MustAcceptAgain(accepted.TryGetValue(group.Key, out var own) ? own : [], group, now))
                pending.Add(new PendingConsent(group.Key, required.Version));
        }
        return pending;
    }

    // The version a user may accept for the document when the app has made a requirement for it, and whether that
    // requirement is in effect. The newest requirement in effect decides. Before any is in effect, the newest one
    // marked decides, so a user can accept early. Null when the document has no requirement.
    public async Task<(string Version, bool InEffect)?> RequiredVersionAsync(string document, CancellationToken cancellationToken = default)
    {
        var rows = (await RequirementsAsync(cancellationToken)).Where(r => r.Document == document).ToList();
        if (NewestInEffect(rows, time.GetUtcNow().UtcDateTime) is { } inEffect)
            return (inEffect.Version, true);
        return rows.Count == 0 ? null : (Newest(rows).Version, false);
    }

    // The version of the document that a person may accept now, as the server knows it, or null when there is none. The newest
    // requirement in effect decides. Without one, the version source decides. With none of them, a requirement that is not in
    // effect yet decides when notYetInEffect is true, so a user can accept it early. The source is asked without a locale,
    // because a requirement does not belong to one.
    public async Task<string?> AcceptableVersionAsync(string document, bool notYetInEffect, CancellationToken cancellationToken = default)
    {
        var requirement = await RequiredVersionAsync(document, cancellationToken);
        if (requirement is { InEffect: true } inEffect)
            return inEffect.Version;
        var current = versions is null ? null : await versions.GetCurrentVersionAsync(document, null, cancellationToken);
        return current ?? (notYetInEffect ? requirement?.Version : null);
    }

    private static ConsentRequirement Newest(IEnumerable<ConsentRequirement> requirements) =>
        requirements.OrderByDescending(r => r.MarkedAt).ThenByDescending(r => r.Id).First();

    private static ConsentRequirement? NewestInEffect(IEnumerable<ConsentRequirement> requirements, DateTime now) =>
        requirements.Where(r => r.EffectiveFrom <= now).OrderByDescending(r => r.MarkedAt).ThenByDescending(r => r.Id).FirstOrDefault();

    private async Task<IReadOnlyList<ConsentRequirement>> RequirementsAsync(CancellationToken cancellationToken)
    {
        if (cache is not null && cache.TryGetValue(RequirementsCacheKey, out IReadOnlyList<ConsentRequirement>? cached) && cached is not null)
            return cached;
        IReadOnlyList<ConsentRequirement> rows = await db.ConsentRequirements.AsNoTracking().ToListAsync(cancellationToken);
        cache?.Set(RequirementsCacheKey, rows, RequirementsCacheDuration);
        return rows;
    }

    // A requirement binds from EffectiveFrom. The newest one in effect decides. The user is pending unless they hold an
    // acceptance of exactly its version, whenever it was made. An acceptance of any other version does not count, and the
    // version the source serves plays no part.
    internal static bool MustAcceptAgain(IReadOnlyList<(string Version, DateTime At)> acceptances, IEnumerable<ConsentRequirement> requirements, DateTime now)
    {
        if (NewestInEffect(requirements, now) is not { } required)
            return false;
        return !acceptances.Any(a => a.Version != AccountConsent.Unversioned && string.Equals(a.Version, required.Version, StringComparison.Ordinal));
    }

    private Task<bool> ExistsAsync(string userId, string document, string version, CancellationToken cancellationToken) =>
        db.AccountConsents.AsNoTracking().AnyAsync(c => c.UserId == userId && c.Document == document && c.Version == version, cancellationToken);
}

// Runs inside the account deletion. The consent rows stay as proof of what the user accepted, but the shortened IP address
// and the locale are personal data and are cleared. Registered only when the app opted in to sign-up.
internal sealed class AccountConsentEraser<TUser, TContext>(TContext db) : IAccountDataEraser<TUser>
    where TUser : IdentityUser
    where TContext : DbContext, IAccountConsentDbContext
{
    public async Task EraseAsync(TUser user, CancellationToken cancellationToken)
    {
        foreach (var row in await db.AccountConsents.Where(c => c.UserId == user.Id && (c.Ip != null || c.Locale != null)).ToListAsync(cancellationToken))
        {
            row.Ip = null;
            row.Locale = null;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}

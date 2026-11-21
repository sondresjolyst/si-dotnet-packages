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

    // The version of the document as people see it now, or null when the app has no source or the source gives none. The
    // source is asked without a locale, because a requirement does not belong to one.
    public async Task<string?> CurrentVersionAsync(string document, CancellationToken cancellationToken = default)
    {
        var current = versions is null ? null : await versions.GetCurrentVersionAsync(document, null, cancellationToken);
        return string.IsNullOrEmpty(current) ? null : current;
    }

    // Adds the acceptance and saves. Returns false when the user already holds this version of the document in a way that
    // meets every requirement marked so far, which includes a request that lost a race on the unique index. The first row stays
    // as it was. A user who accepted the version before the newest mark gets a new row, so accepting again always works.
    // The unique index on user, document, version and requirement id is what two parallel requests meet: both read the same
    // newest requirement, so both insert the same key and one insert fails. The failed one is detached and the key is read
    // again on a connection that is still usable, which is why the caller must not hold a transaction around this call. A
    // failed insert aborts a Postgres transaction.
    public async Task<bool> RecordAsync(
        string userId, string document, string version, string source,
        string? locale = null, string? ip = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentException.ThrowIfNullOrEmpty(document);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(source);

        var newest = Newest((await RequirementsAsync(cancellationToken)).Where(r => r.Document == document));
        var requirementId = newest?.Id ?? 0;
        if (await HoldsAsync(userId, document, version, newest?.MarkedAt, cancellationToken))
            return false;

        var row = new AccountConsent
        {
            UserId = userId, Document = document, Version = version, Source = source, RequirementId = requirementId,
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
            if (await db.AccountConsents.AsNoTracking().AnyAsync(c => c.UserId == userId && c.Document == document && c.Version == version && c.RequirementId == requirementId, cancellationToken))
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

    // Each document the user must accept again, with the version that is current for it, or the unversioned marker when the
    // app serves none.
    public async Task<IReadOnlyList<PendingConsent>> GetPendingAsync(string userId, CancellationToken cancellationToken = default)
    {
        var accepted = await AcceptancesAsync(userId, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var pending = new List<PendingConsent>();
        foreach (var group in (await RequirementsAsync(cancellationToken)).GroupBy(r => r.Document).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (MustAcceptAgain(accepted.TryGetValue(group.Key, out var own) ? own : [], group, now))
                pending.Add(new PendingConsent(group.Key, await CurrentVersionAsync(group.Key, cancellationToken) ?? AccountConsent.Unversioned));
        }
        return pending;
    }

    // The requirement with the latest effective time that is not after now, and among those the latest mark. Null when none is.
    internal static ConsentRequirement? InForce(IEnumerable<ConsentRequirement> requirements, DateTime now) =>
        requirements.Where(r => r.EffectiveFrom <= now).OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.MarkedAt).ThenByDescending(r => r.Id).FirstOrDefault();

    // The requirement that was marked last, in force or not. An acceptance made at or after its mark meets every requirement.
    private static ConsentRequirement? Newest(IEnumerable<ConsentRequirement> requirements) =>
        requirements.OrderByDescending(r => r.MarkedAt).ThenByDescending(r => r.Id).FirstOrDefault();

    private async Task<IReadOnlyList<ConsentRequirement>> RequirementsAsync(CancellationToken cancellationToken)
    {
        if (cache is not null && cache.TryGetValue(RequirementsCacheKey, out IReadOnlyList<ConsentRequirement>? cached) && cached is not null)
            return cached;
        IReadOnlyList<ConsentRequirement> rows = await db.ConsentRequirements.AsNoTracking().ToListAsync(cancellationToken);
        cache?.Set(RequirementsCacheKey, rows, RequirementsCacheDuration);
        return rows;
    }

    // The requirement in force decides. The user is pending when their latest acceptance of the document, of any version, was
    // made before its mark, or when they have none. An acceptance at or after the mark meets it, also when it was made
    // before the requirement takes effect. No version is compared.
    internal static bool MustAcceptAgain(IReadOnlyList<(string Version, DateTime At)> acceptances, IEnumerable<ConsentRequirement> requirements, DateTime now)
    {
        if (InForce(requirements, now) is not { } required)
            return false;
        return acceptances.Count == 0 || acceptances.Max(a => a.At) < required.MarkedAt;
    }

    // Whether the user holds the version from at or after the mark, or at all when there is no requirement.
    private Task<bool> HoldsAsync(string userId, string document, string version, DateTime? markedAt, CancellationToken cancellationToken) =>
        markedAt is { } mark
            ? db.AccountConsents.AsNoTracking().AnyAsync(c => c.UserId == userId && c.Document == document && c.Version == version && c.AcceptedAt >= mark, cancellationToken)
            : db.AccountConsents.AsNoTracking().AnyAsync(c => c.UserId == userId && c.Document == document && c.Version == version, cancellationToken);
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

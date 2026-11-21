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

/// <summary>
/// Records acceptances and tells whether a user must accept a document again. Time comes from the TimeProvider. Acceptance and mark
/// times are compared, so instances must share a clock within the usual server tolerance. The requirement rows are cached for
/// 10 seconds when a cache is given, and marking a requirement clears the cache of that instance only. After a mark that takes
/// effect at once, a pending user may still pass on another instance for up to 10 seconds of requirement cache plus
/// <see cref="AppKitJwtBearerOptions.UserCheckCacheDuration"/> of user check cache, which is 70 seconds by default.
/// </summary>
internal sealed class AccountConsentService<TContext>(TContext db, TimeProvider time, IMemoryCache? cache = null, IConsentVersionSource? versions = null) : IPendingConsentReader, IAccountConsentExporter
    where TContext : DbContext, IAccountConsentDbContext
{
    internal const string RequirementsCacheKey = "SjolystInnovation.AppKit.Auth:consent-requirements";
    private static readonly TimeSpan RequirementsCacheDuration = TimeSpan.FromSeconds(10);

    // The version of the document as people see it now, or null when the app has no source or the source gives none. The
    // source is asked without a locale, because a requirement does not belong to one.
    public async Task<string?> CurrentVersionAsync(string document, CancellationToken cancellationToken = default)
    {
        var current = versions is null ? null : await versions.GetCurrentVersionAsync(document, null, cancellationToken);
        return string.IsNullOrEmpty(current) ? null : current;
    }

    /// <summary>
    /// Adds a row for the acceptance and saves, unless the user is not pending for the document and already holds an acceptance
    /// of this exact version. A pending user always gets a new row, so accepting again always works. Two parallel requests of one
    /// user may both add a row. That is accepted, because the pending rule reads the latest acceptance and an extra row is harmless.
    /// The caller passes <c>acceptedAt</c> when it read the current version first, taking the time before the read, so a request that
    /// read an older version never gets a time after a mark that came with a newer one. Null means now.
    /// </summary>
    /// <returns>True when a row was added.</returns>
    public async Task<bool> RecordAsync(
        string userId, string document, string version, string source,
        string? locale = null, string? ip = null, DateTime? acceptedAt = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentException.ThrowIfNullOrEmpty(document);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(source);

        var held = (await AcceptancesAsync(userId, cancellationToken)).TryGetValue(document, out var own) ? own : [];
        var requirements = (await RequirementsAsync(cancellationToken)).Where(r => r.Document == document);
        var now = time.GetUtcNow().UtcDateTime;
        if (!MustAcceptAgain(held, requirements, now) && held.Any(a => string.Equals(a.Version, version, StringComparison.Ordinal)))
            return false;

        db.AccountConsents.Add(new AccountConsent
        {
            UserId = userId, Document = document, Version = version, Source = source,
            Locale = locale, Ip = ip, AcceptedAt = acceptedAt ?? now,
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
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

    private async Task<IReadOnlyList<ConsentRequirement>> RequirementsAsync(CancellationToken cancellationToken)
    {
        if (cache is not null && cache.TryGetValue(RequirementsCacheKey, out IReadOnlyList<ConsentRequirement>? cached) && cached is not null)
            return cached;
        IReadOnlyList<ConsentRequirement> rows = await db.ConsentRequirements.AsNoTracking().ToListAsync(cancellationToken);
        cache?.Set(RequirementsCacheKey, rows, RequirementsCacheDuration);
        return rows;
    }

    // Every requirement whose effective time is not after now is in effect, and the newest mark among them decides. The user is
    // pending when there is one and the user's latest acceptance of the document, of any version, was made before that mark, or
    // the user has none. An acceptance at or after the mark meets it, also when it was made before the requirement takes effect.
    // A requirement that was marked earlier but takes effect later never replaces a newer mark. No version is compared.
    internal static bool MustAcceptAgain(IReadOnlyList<(string Version, DateTime At)> acceptances, IEnumerable<ConsentRequirement> requirements, DateTime now)
    {
        var inEffect = requirements.Where(r => r.EffectiveFrom <= now).ToList();
        if (inEffect.Count == 0)
            return false;
        return acceptances.Count == 0 || acceptances.Max(a => a.At) < inEffect.Max(r => r.MarkedAt);
    }
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

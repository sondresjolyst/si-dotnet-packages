using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace SjolystInnovation.AppKit.Auth;

// The default IConsentRequirements. A requirement is unique on document and version, so marking it twice changes nothing.
// The caller must not hold a transaction around the call, for the same reason as for the consent insert.
internal sealed class ConsentRequirements<TContext>(TContext db, TimeProvider time, IMemoryCache? cache = null) : IConsentRequirements
    where TContext : DbContext, IAccountConsentDbContext
{
    public async Task RequireAcceptanceAsync(string document, string version, DateTime effectiveFrom, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(document);
        ArgumentException.ThrowIfNullOrEmpty(version);

        if (!await ExistsAsync(document, version, cancellationToken))
        {
            var row = new ConsentRequirement { Document = document, Version = version, MarkedAt = time.GetUtcNow().UtcDateTime, EffectiveFrom = effectiveFrom };
            db.ConsentRequirements.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Another call may have marked the same requirement first. Anything else is a real failure.
                db.Entry(row).State = EntityState.Detached;
                if (!await ExistsAsync(document, version, cancellationToken))
                    throw;
            }
        }
        cache?.Remove(AccountConsentService<TContext>.RequirementsCacheKey);
    }

    private Task<bool> ExistsAsync(string document, string version, CancellationToken cancellationToken) =>
        db.ConsentRequirements.AsNoTracking().AnyAsync(r => r.Document == document && r.Version == version, cancellationToken);
}

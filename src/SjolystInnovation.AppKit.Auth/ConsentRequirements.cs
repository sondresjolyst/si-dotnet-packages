using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace SjolystInnovation.AppKit.Auth;

// The documents a requirement may name, from the sign-up options: the terms and the further documents.
internal sealed class ConsentDocuments(Func<string, bool> isListed)
{
    public bool IsListed(string document) => isListed(document);
}

// The default IConsentRequirements. A requirement is unique on document and effective time, so marking it twice changes nothing.
// The caller must not hold a transaction around the call, for the same reason as for the consent insert.
internal sealed class ConsentRequirements<TContext>(TContext db, TimeProvider time, ConsentDocuments documents, IMemoryCache? cache = null, IConsentVersionSource? versions = null) : IConsentRequirements
    where TContext : DbContext, IAccountConsentDbContext
{
    private const int MaxVersion = 64;

    public async Task RequireAcceptanceAsync(string document, DateTime effectiveFrom, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(document);
        // accept-terms takes only these names, so a requirement for any other document would leave every user pending for good.
        if (!documents.IsListed(document))
            throw new ArgumentException(
                $"The document {document} is neither the terms document nor in AdditionalDocuments, so no one could accept it. Add it to AdditionalDocuments first.",
                nameof(document));

        if (!await ExistsAsync(document, effectiveFrom, cancellationToken))
        {
            // The version is a record of what the source served at this moment. It is never compared.
            var current = versions is null ? null : await versions.GetCurrentVersionAsync(document, null, cancellationToken);
            var version = string.IsNullOrEmpty(current) ? AccountConsent.Unversioned : current.Length > MaxVersion ? current[..MaxVersion] : current;
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
                if (!await ExistsAsync(document, effectiveFrom, cancellationToken))
                    throw;
            }
        }
        cache?.Remove(AccountConsentService<TContext>.RequirementsCacheKey);
    }

    private Task<bool> ExistsAsync(string document, DateTime effectiveFrom, CancellationToken cancellationToken) =>
        db.ConsentRequirements.AsNoTracking().AnyAsync(r => r.Document == document && r.EffectiveFrom == effectiveFrom, cancellationToken);
}

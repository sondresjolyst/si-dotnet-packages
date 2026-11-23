using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Auth;

// The document is the terms when the request names none. The version is the one the person saw of that document.
internal sealed record AcceptTermsRequest(string? TermsVersion, string? Locale, string? Document = null);

internal static class ConsentHandlers<TUser, TContext>
    where TContext : DbContext, IAccountConsentDbContext
{
    private const int MaxTermsVersion = 64;
    private const int MaxLocale = 16;
    private const int MaxDocument = 32;

    private static T Get<T>(HttpContext http) where T : notnull => http.RequestServices.GetRequiredService<T>();

    // Records that the signed-in user accepted the current version of the terms, or of the document the request names. Two parallel
    // requests of one user may add two rows.
    public static async Task<IResult> AcceptTermsAsync(AcceptTermsRequest request, HttpContext http)
    {
        if (http.User.GetUserId() is not { } userId)
            return TypedResults.Unauthorized();
        var options = Get<IOptions<AppKitRegistrationOptions<TUser>>>(http).Value;
        var cancellationToken = http.RequestAborted;

        var version = request.TermsVersion?.Trim() ?? string.Empty;
        var locale = string.IsNullOrWhiteSpace(request.Locale) ? null : request.Locale.Trim();
        // A request without a document keeps the answers it always had. One that names a document gets that name back in a conflict.
        var named = string.IsNullOrWhiteSpace(request.Document) ? null : request.Document.Trim();
        var errors = new AuthFieldErrors();
        errors.Name("TermsVersion", version, MaxTermsVersion);
        if (locale is { Length: > MaxLocale })
            errors.Add("Locale", AuthErrorCodes.TooLong, $"At most {MaxLocale} characters.", MaxLocale);
        if (named is { Length: > MaxDocument })
            errors.Add("Document", AuthErrorCodes.TooLong, $"At most {MaxDocument} characters.", MaxDocument);
        else if (named is not null && !options.IsAcceptedDocument(named))
            errors.Add("Document", AuthErrorCodes.UnknownDocument, "Not a document to accept.");
        if (errors.Any)
            return errors.ToResult();
        var document = named ?? options.TermsDocument;

        // The time is taken before the source is read, so a mark that comes with a newer version is never older than the acceptance.
        var acceptedAt = Get<TimeProvider>(http).GetUtcNow().UtcDateTime;

        // The source decides. With no version from it, the marker is recorded and the version sent is ignored.
        var consents = Get<AccountConsentService<TContext>>(http);
        var current = await consents.CurrentVersionAsync(document, locale, cancellationToken);
        if (current is not null && !string.Equals(current, version, StringComparison.Ordinal))
            return named is null
                ? AuthResults.Error(AuthErrorCodes.TermsChanged, "The terms have changed. Read the current terms and accept them again.", StatusCodes.Status409Conflict)
                : AuthResults.DocumentChanged(named);

        var ip = options.RecordTruncatedIp ? IpShortening.Shorten(http.Connection.RemoteIpAddress) : null;
        await consents.RecordAsync(userId, document, current ?? AccountConsent.Unversioned, "reaccept", locale, ip, acceptedAt, cancellationToken);

        // The pending state clears at once on this instance, instead of when the cached user check expires.
        http.RequestServices.GetService<IMemoryCache>()?.Remove(IdentityAndBearerExtensions.UserCheckKey(userId));
        return TypedResults.NoContent();
    }
}

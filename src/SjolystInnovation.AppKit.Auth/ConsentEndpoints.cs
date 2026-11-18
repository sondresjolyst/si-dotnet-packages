using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Auth;

internal sealed record AcceptTermsRequest(string? TermsVersion, string? Locale);

internal static class ConsentHandlers<TUser, TContext>
    where TContext : DbContext, IAccountConsentDbContext
{
    private const int MaxTermsVersion = 64;
    private const int MaxLocale = 16;

    private static T Get<T>(HttpContext http) where T : notnull => http.RequestServices.GetRequiredService<T>();

    // Records that the signed-in user accepted the current terms. It runs without a transaction of its own, so a lost
    // race on the unique index is caught and checked on a connection that is still usable.
    public static async Task<IResult> AcceptTermsAsync(AcceptTermsRequest request, HttpContext http)
    {
        if (http.User.GetUserId() is not { } userId)
            return TypedResults.Unauthorized();
        var options = Get<IOptions<AppKitRegistrationOptions<TUser>>>(http).Value;
        var cancellationToken = http.RequestAborted;

        var version = request.TermsVersion?.Trim() ?? string.Empty;
        var locale = string.IsNullOrWhiteSpace(request.Locale) ? null : request.Locale.Trim();
        var errors = new AuthFieldErrors();
        errors.Name("TermsVersion", version, MaxTermsVersion);
        if (locale is { Length: > MaxLocale })
            errors.Add("Locale", AuthErrorCodes.TooLong, $"At most {MaxLocale} characters.", MaxLocale);
        if (errors.Any)
            return errors.ToResult();

        // A requirement in effect decides the version, so a stale or different source version cannot stand in for it.
        // Without one the source decides. Before any requirement is in effect and with no source version, the newest
        // requirement decides, so a user can accept it early.
        var consents = Get<AccountConsentService<TContext>>(http);
        var requirement = await consents.RequiredVersionAsync(options.TermsDocument, cancellationToken);
        var source = http.RequestServices.GetService<IConsentVersionSource>();
        var expected = requirement is { InEffect: true } required
            ? required.Version
            : (source is null ? null : await source.GetCurrentVersionAsync(options.TermsDocument, locale, cancellationToken)) ?? requirement?.Version;
        if (expected is null)
            return AuthResults.Error(AuthErrorCodes.NoTermsToAccept, "There are no terms to accept.", StatusCodes.Status409Conflict);
        if (!string.Equals(expected, version, StringComparison.Ordinal))
            return AuthResults.Error(AuthErrorCodes.TermsChanged, "The terms have changed. Read the current terms and accept them again.", StatusCodes.Status409Conflict);

        var ip = options.RecordTruncatedIp ? IpShortening.Shorten(http.Connection.RemoteIpAddress) : null;
        await consents.RecordAsync(userId, options.TermsDocument, version, "reaccept", locale, ip, cancellationToken);

        // The pending state clears at once on this instance, instead of when the cached user check expires.
        http.RequestServices.GetService<IMemoryCache>()?.Remove(IdentityAndBearerExtensions.UserCheckKey(userId));
        return TypedResults.NoContent();
    }
}

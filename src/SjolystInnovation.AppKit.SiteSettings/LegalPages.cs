using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SjolystInnovation.AppKit.SiteSettings;

/// <summary>The text of one legal page in one language, edited by an admin and rendered as markdown.</summary>
public class LegalPage
{
    /// <summary>The row id.</summary>
    public int Id { get; set; }
    /// <summary>The page key, for example <c>terms</c>.</summary>
    public required string Key { get; set; }
    /// <summary>The language the page is written in, for example <c>no</c>.</summary>
    public required string Locale { get; set; }
    /// <summary>The page title.</summary>
    public required string Title { get; set; }
    /// <summary>The page body as markdown.</summary>
    public string BodyMarkdown { get; set; } = string.Empty;
    /// <summary>When the page was last saved, in UTC.</summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>The starting text of a legal page, written while the page has none.</summary>
public sealed record LegalPageSeed(string Key, string Locale, string Title, string BodyMarkdown);

/// <summary>
/// The options for <see cref="LegalPagesExtensions.MapAppKitLegalPages{TContext}"/>. Seeding does not read them.
/// It writes the pages it is given.
/// </summary>
public sealed class AppKitLegalPagesOptions
{
    /// <summary>The page keys. Defaults to <c>terms</c>, <c>privacy</c>, and <c>cookies</c>.</summary>
    public IReadOnlyCollection<string> Keys { get; set; } = ["terms", "privacy", "cookies"];

    /// <summary>The languages a page can be written in. The first is the fallback. Defaults to <c>no</c>.</summary>
    public IReadOnlyList<string> Locales { get; set; } = ["no"];

    /// <summary>The authorization policy that listing and editing require. Defaults to <c>Admin</c>.</summary>
    public string Policy { get; set; } = "Admin";

    internal bool KnownKey(string? key) => key is not null && Keys.Contains(key, StringComparer.OrdinalIgnoreCase);

    internal bool KnownLocale(string? locale) => locale is not null && Locales.Contains(locale, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the supported locale a tag asks for. It tries an exact match first, then the tag's primary
    /// language, with <c>nb</c> and <c>nn</c> read as <c>no</c>. Anything else, or no tag, gets the fallback.
    /// </summary>
    internal string Normalize(string? locale) => LocaleTags.Match(Locales, Locales[0], locale);
}

/// <summary>A legal page as the endpoints return it.</summary>
public sealed record LegalPageInfo(string Key, string Locale, string Title, string BodyMarkdown, DateTime UpdatedAt);

/// <summary>The text an admin saves for a legal page.</summary>
public sealed record LegalPageBody(string? Title, string? BodyMarkdown);

/// <summary>Maps, seeds, and configures the legal pages.</summary>
public static class LegalPagesExtensions
{
    /// <summary>
    /// Maps <see cref="LegalPage"/> to the <c>LegalPages</c> table, with one page per key and locale.
    /// </summary>
    public static ModelBuilder AddAppKitLegalPages(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<LegalPage>(e =>
        {
            e.ToTable("LegalPages");
            e.HasKey(p => p.Id);
            e.Property(p => p.Key).IsRequired().HasMaxLength(40);
            e.Property(p => p.Locale).IsRequired().HasMaxLength(10);
            e.Property(p => p.Title).IsRequired().HasMaxLength(200);
            e.Property(p => p.BodyMarkdown).IsRequired();
            e.HasIndex(p => new { p.Key, p.Locale }).IsUnique();
        });
        return modelBuilder;
    }

    /// <summary>
    /// Maps the legal page endpoints under <paramref name="prefix"/>. <c>GET {key}</c> is open to everyone
    /// and returns the page in the requested <c>?locale=</c>, or else in the fallback language. <c>GET</c>
    /// lists every page and <c>PUT {key}/{locale}</c> saves one, both behind
    /// <see cref="AppKitLegalPagesOptions.Policy"/>.
    /// </summary>
    public static RouteGroupBuilder MapAppKitLegalPages<TContext>(this IEndpointRouteBuilder endpoints,
        string prefix = "/api/content/legal", Action<AppKitLegalPagesOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new AppKitLegalPagesOptions();
        configure?.Invoke(options);
        var handlers = new LegalPageHandlers<TContext>(options);

        var group = endpoints.MapGroup(prefix);
        group.MapGet("{key}", (string key, HttpContext http, string? locale = null) => handlers.GetAsync(key, locale, http)).AllowAnonymous();
        group.MapGet("", (Func<HttpContext, Task<IResult>>)LegalPageHandlers<TContext>.ListAsync).RequireAuthorization(options.Policy);
        group.MapPut("{key}/{locale}", (string key, string locale, LegalPageBody body, HttpContext http) => handlers.PutAsync(key, locale, body, http))
            .RequireAuthorization(options.Policy);
        return group;
    }

    /// <summary>
    /// Writes the starting text for every page and language in <paramref name="pages"/> that has none.
    /// Pages that an admin has written stay as they are. Keys and locales are stored in lower case, the
    /// form the endpoints look pages up by.
    /// </summary>
    public static async Task SeedAppKitLegalPagesAsync<TContext>(this IServiceProvider services, IEnumerable<LegalPageSeed> pages, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(pages);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = (scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var existing = (await db.Set<LegalPage>().Select(p => new { p.Key, p.Locale }).ToListAsync(cancellationToken))
            .Select(p => (p.Key.ToLowerInvariant(), p.Locale.ToLowerInvariant())).ToHashSet();
        foreach (var page in pages)
        {
            var key = page.Key.ToLowerInvariant();
            var locale = page.Locale.ToLowerInvariant();
            // Add returns false for a page that exists or that appears twice in the seed.
            if (existing.Add((key, locale)))
                db.Set<LegalPage>().Add(new LegalPage { Key = key, Locale = locale, Title = page.Title, BodyMarkdown = page.BodyMarkdown, UpdatedAt = now });
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class LegalPageHandlers<TContext>(AppKitLegalPagesOptions options)
    where TContext : DbContext
{
    // The column holds 200 characters, see AddAppKitLegalPages.
    private const int MaxTitleLength = 200;

    private static LegalPageInfo Info(LegalPage p) => new(p.Key, p.Locale, p.Title, p.BodyMarkdown, p.UpdatedAt);

    public async Task<IResult> GetAsync(string key, string? locale, HttpContext http)
    {
        if (!options.KnownKey(key))
            return TypedResults.NotFound();
        key = key.ToLowerInvariant();
        var pages = http.RequestServices.GetRequiredService<TContext>().Set<LegalPage>().AsNoTracking();
        var wanted = options.Normalize(locale);
        var fallback = options.Locales[0].ToLowerInvariant();
        var page = await pages.FirstOrDefaultAsync(p => p.Key == key && p.Locale == wanted, http.RequestAborted)
            ?? await pages.FirstOrDefaultAsync(p => p.Key == key && p.Locale == fallback, http.RequestAborted);
        return page is null ? TypedResults.NotFound() : TypedResults.Ok(Info(page));
    }

    public static async Task<IResult> ListAsync(HttpContext http)
    {
        var pages = await http.RequestServices.GetRequiredService<TContext>().Set<LegalPage>().AsNoTracking()
            .OrderBy(p => p.Key).ThenBy(p => p.Locale).ToListAsync(http.RequestAborted);
        return TypedResults.Ok(pages.Select(Info).ToList());
    }

    public async Task<IResult> PutAsync(string key, string locale, LegalPageBody body, HttpContext http)
    {
        if (!options.KnownKey(key))
            return TypedResults.Problem("Unknown legal page.", statusCode: StatusCodes.Status400BadRequest);
        if (!options.KnownLocale(locale))
            return TypedResults.Problem("Unsupported locale.", statusCode: StatusCodes.Status400BadRequest);

        var problem = new SiteSettingsProblem();
        if (string.IsNullOrWhiteSpace(body.Title))
            problem.Set("Title", SiteSettingsErrorCodes.Required, "Required.");
        else if (body.Title.Trim().Length > MaxTitleLength)
            problem.Set("Title", SiteSettingsErrorCodes.TooLong, $"At most {MaxTitleLength} characters.", MaxTitleLength);
        if (string.IsNullOrWhiteSpace(body.BodyMarkdown))
            problem.Set("BodyMarkdown", SiteSettingsErrorCodes.Required, "Required.");
        if (problem.Errors.Count > 0)
            return problem.ToResult();

        var db = http.RequestServices.GetRequiredService<TContext>();
        var now = (http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        key = key.ToLowerInvariant();
        locale = locale.ToLowerInvariant();
        var page = await db.Set<LegalPage>().FirstOrDefaultAsync(p => p.Key == key && p.Locale == locale, http.RequestAborted);
        if (page is null)
        {
            page = new LegalPage { Key = key, Locale = locale, Title = "" };
            db.Set<LegalPage>().Add(page);
        }
        page.Title = body.Title!.Trim();
        page.BodyMarkdown = body.BodyMarkdown!;
        page.UpdatedAt = now;
        await db.SaveChangesAsync(http.RequestAborted);
        return TypedResults.Ok(Info(page));
    }
}

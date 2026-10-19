using System.Security.Claims;
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

/// <summary>
/// One saved version of a legal page. Every save that changes the title or body adds one, so the text a
/// page had on any date can be looked up later. The package never changes or deletes a revision.
/// </summary>
public class LegalPageRevision
{
    /// <summary>The row id.</summary>
    public int Id { get; set; }
    /// <summary>The page key, for example <c>terms</c>.</summary>
    public required string Key { get; set; }
    /// <summary>The language of the page, for example <c>no</c>.</summary>
    public required string Locale { get; set; }
    /// <summary>The page title as saved.</summary>
    public required string Title { get; set; }
    /// <summary>The page body as Markdown, as saved.</summary>
    public string BodyMarkdown { get; set; } = string.Empty;
    /// <summary>When this version was saved, in UTC. It equals the page's <see cref="LegalPage.UpdatedAt"/> at that save.</summary>
    public DateTime SavedAt { get; set; }
    /// <summary>The user id of the admin who saved this version, or null for seeded text and text saved before history was kept.</summary>
    public string? SavedBy { get; set; }
}

/// <summary>The starting text of a legal page, written while the page has none.</summary>
/// <param name="Key">The page key, for example <c>terms</c>.</param>
/// <param name="Locale">The language of the text, for example <c>no</c>.</param>
/// <param name="Title">The page title.</param>
/// <param name="BodyMarkdown">The page body as Markdown.</param>
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
/// <param name="Key">The page key, in lowercase.</param>
/// <param name="Locale">The language of the page, in lowercase.</param>
/// <param name="Title">The page title.</param>
/// <param name="BodyMarkdown">The page body as Markdown.</param>
/// <param name="UpdatedAt">When the page was last saved, in UTC.</param>
public sealed record LegalPageInfo(string Key, string Locale, string Title, string BodyMarkdown, DateTime UpdatedAt);

/// <summary>A saved version of a legal page in the list of versions, without its body.</summary>
/// <param name="Id">The revision id.</param>
/// <param name="SavedAt">When the version was saved, in UTC.</param>
/// <param name="Title">The page title in that version.</param>
public sealed record LegalPageRevisionSummary(int Id, DateTime SavedAt, string Title);

/// <summary>A saved version of a legal page with its body.</summary>
/// <param name="Id">The revision id.</param>
/// <param name="Key">The page key, in lowercase.</param>
/// <param name="Locale">The language of the page, in lowercase.</param>
/// <param name="Title">The page title in that version.</param>
/// <param name="BodyMarkdown">The page body as Markdown in that version.</param>
/// <param name="SavedAt">When the version was saved, in UTC.</param>
/// <param name="SavedBy">The user id of the admin who saved it, or null when not known.</param>
public sealed record LegalPageRevisionInfo(int Id, string Key, string Locale, string Title, string BodyMarkdown, DateTime SavedAt, string? SavedBy);

/// <summary>The text an admin saves for a legal page.</summary>
/// <param name="Title">The page title. Required, at most 200 characters.</param>
/// <param name="BodyMarkdown">The page body as Markdown. Required.</param>
public sealed record LegalPageBody(string? Title, string? BodyMarkdown);

/// <summary>Maps, seeds, and configures the legal pages.</summary>
public static class LegalPagesExtensions
{
    /// <summary>
    /// Maps <see cref="LegalPage"/> to the <c>LegalPages</c> table, with one page per key and locale, and
    /// <see cref="LegalPageRevision"/> to the <c>LegalPageRevisions</c> table.
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
        modelBuilder.Entity<LegalPageRevision>(e =>
        {
            e.ToTable("LegalPageRevisions");
            e.HasKey(r => r.Id);
            e.Property(r => r.Key).IsRequired().HasMaxLength(40);
            e.Property(r => r.Locale).IsRequired().HasMaxLength(10);
            e.Property(r => r.Title).IsRequired().HasMaxLength(200);
            e.Property(r => r.BodyMarkdown).IsRequired();
            e.Property(r => r.SavedBy).HasMaxLength(450);
            e.HasIndex(r => new { r.Key, r.Locale, r.SavedAt });
        });
        return modelBuilder;
    }

    /// <summary>
    /// Maps the legal page endpoints under <paramref name="prefix"/>. <c>GET {key}</c> is open to everyone
    /// and returns the page in the requested <c>?locale=</c>, or else in the fallback language. <c>GET</c>
    /// lists every page, <c>PUT {key}/{locale}</c> saves one, <c>GET {key}/{locale}/revisions</c> lists its
    /// saved versions and <c>GET {key}/{locale}/revisions/{id}</c> returns one, all behind
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
        group.MapGet("{key}/{locale}/revisions", (string key, string locale, HttpContext http) => handlers.RevisionsAsync(key, locale, http))
            .RequireAuthorization(options.Policy);
        group.MapGet("{key}/{locale}/revisions/{id:int}", (string key, string locale, int id, HttpContext http) => handlers.RevisionAsync(key, locale, id, http))
            .RequireAuthorization(options.Policy);
        return group;
    }

    /// <summary>
    /// Writes the starting text for every page and language in <paramref name="pages"/> that has none.
    /// Pages that an admin has written stay as they are. Keys and locales are stored in lower case, the
    /// form the endpoints look pages up by. Each page it writes also gets its first revision.
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
            {
                var seeded = new LegalPage { Key = key, Locale = locale, Title = page.Title, BodyMarkdown = page.BodyMarkdown, UpdatedAt = now };
                db.Set<LegalPage>().Add(seeded);
                db.Set<LegalPageRevision>().Add(LegalPageHandlers<TContext>.Revision(seeded, savedBy: null));
            }
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

    internal static LegalPageRevision Revision(LegalPage p, string? savedBy) =>
        new() { Key = p.Key, Locale = p.Locale, Title = p.Title, BodyMarkdown = p.BodyMarkdown, SavedAt = p.UpdatedAt, SavedBy = savedBy };

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
        var title = body.Title!.Trim();
        var markdown = body.BodyMarkdown!;
        var revisions = db.Set<LegalPageRevision>();
        var page = await db.Set<LegalPage>().FirstOrDefaultAsync(p => p.Key == key && p.Locale == locale, http.RequestAborted);
        if (page is null)
        {
            page = new LegalPage { Key = key, Locale = locale, Title = "" };
            db.Set<LegalPage>().Add(page);
        }
        else
        {
            // A save that changes nothing keeps the page and its history as they are.
            if (page.Title == title && page.BodyMarkdown == markdown)
                return TypedResults.Ok(Info(page));
            // A page written before history was kept gets its current text as the first revision.
            if (!await revisions.AnyAsync(r => r.Key == key && r.Locale == locale, http.RequestAborted))
                revisions.Add(Revision(page, savedBy: null));
        }
        page.Title = title;
        page.BodyMarkdown = markdown;
        page.UpdatedAt = now;
        revisions.Add(Revision(page, SavedBy(http.User)));
        await db.SaveChangesAsync(http.RequestAborted);
        return TypedResults.Ok(Info(page));
    }

    public async Task<IResult> RevisionsAsync(string key, string locale, HttpContext http)
    {
        if (!options.KnownKey(key) || !options.KnownLocale(locale))
            return TypedResults.NotFound();
        key = key.ToLowerInvariant();
        locale = locale.ToLowerInvariant();
        var list = await http.RequestServices.GetRequiredService<TContext>().Set<LegalPageRevision>().AsNoTracking()
            .Where(r => r.Key == key && r.Locale == locale)
            .OrderByDescending(r => r.SavedAt).ThenByDescending(r => r.Id)
            .Select(r => new LegalPageRevisionSummary(r.Id, r.SavedAt, r.Title))
            .ToListAsync(http.RequestAborted);
        return TypedResults.Ok(list);
    }

    public async Task<IResult> RevisionAsync(string key, string locale, int id, HttpContext http)
    {
        if (!options.KnownKey(key) || !options.KnownLocale(locale))
            return TypedResults.NotFound();
        key = key.ToLowerInvariant();
        locale = locale.ToLowerInvariant();
        var r = await http.RequestServices.GetRequiredService<TContext>().Set<LegalPageRevision>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.Key == key && r.Locale == locale, http.RequestAborted);
        return r is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new LegalPageRevisionInfo(r.Id, r.Key, r.Locale, r.Title, r.BodyMarkdown, r.SavedAt, r.SavedBy));
    }

    // The user id claim that AppKit.Auth writes, read without depending on that package.
    private static string? SavedBy(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");

}

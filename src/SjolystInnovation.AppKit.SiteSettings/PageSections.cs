using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SjolystInnovation.AppKit.SiteSettings;

/// <summary>
/// The sections of one page in one language, edited by an admin. The sections are a JSON array that the
/// package stores as it is sent and never reads.
/// </summary>
public class PageSections
{
    /// <summary>The row id.</summary>
    public int Id { get; set; }
    /// <summary>
    /// The page key, for example <c>home</c>. It is not stored when the table is mapped without a key
    /// column, see <see cref="AppKitPageSectionsModelOptions.KeyColumn"/>.
    /// </summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>The language the sections are written in, for example <c>no</c>.</summary>
    public required string Locale { get; set; }
    /// <summary>The sections as a JSON array.</summary>
    public string SectionsJson { get; set; } = "[]";
    /// <summary>When the sections were last saved, in UTC.</summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>How <see cref="PageSectionsExtensions.AddAppKitPageSections"/> maps <see cref="PageSections"/> to a table.</summary>
public sealed class AppKitPageSectionsModelOptions
{
    /// <summary>The table name. Defaults to <c>PageSections</c>.</summary>
    public string TableName { get; set; } = "PageSections";

    /// <summary>
    /// Whether the table has a <c>Key</c> column, so it can hold several pages. Defaults to <c>true</c>. Set
    /// it to <c>false</c> to map an existing table that holds a single page with one row per language. The
    /// endpoints then need exactly one key in <see cref="AppKitPageSectionsOptions.Keys"/>.
    /// </summary>
    public bool KeyColumn { get; set; } = true;
}

/// <summary>The options for <see cref="PageSectionsExtensions.MapAppKitPageSections{TContext}"/>.</summary>
public sealed class AppKitPageSectionsOptions
{
    /// <summary>The page keys. Defaults to <c>home</c>.</summary>
    public IReadOnlyCollection<string> Keys { get; set; } = ["home"];

    /// <summary>The languages a page can be written in. Defaults to <c>no</c>.</summary>
    public IReadOnlyList<string> Locales { get; set; } = ["no"];

    /// <summary>
    /// The language a page falls back to when it has no sections in the requested one, and the language
    /// a save without <c>?locale=</c> writes. Defaults to the first of <see cref="Locales"/>.
    /// </summary>
    public string? DefaultLocale { get; set; }

    /// <summary>The authorization policy that saving requires. Defaults to <c>Admin</c>.</summary>
    public string Policy { get; set; } = "Admin";

    /// <summary>
    /// A function that runs after the sections of a page are saved. Use it to revalidate the pages that
    /// show them.
    /// </summary>
    public Func<PageSectionsSaved, Task>? OnSaved { get; set; }

    internal string Default => (DefaultLocale ?? Locales[0]).ToLowerInvariant();

    internal bool KnownKey(string? key) => key is not null && Keys.Contains(key, StringComparer.OrdinalIgnoreCase);

    internal bool KnownLocale(string? locale) => locale is not null && Locales.Contains(locale, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the supported locale a tag asks for, the same way the legal pages do, with
    /// <see cref="DefaultLocale"/> for anything else.
    /// </summary>
    internal string Normalize(string? locale) => LocaleTags.Match(Locales, Default, locale);
}

/// <summary>The sections of a page that are saved.</summary>
/// <param name="Key">The page key, in lower case.</param>
/// <param name="Locale">The language the sections were saved in, in lower case.</param>
/// <param name="SectionsJson">The saved sections as a JSON array.</param>
/// <param name="Http">The request that saved them.</param>
public sealed record PageSectionsSaved(string Key, string Locale, string SectionsJson, HttpContext Http);

/// <summary>Maps and configures the page sections.</summary>
public static class PageSectionsExtensions
{
    /// <summary>
    /// Maps <see cref="PageSections"/> to a table with one row per key and locale. By default the table is
    /// <c>PageSections</c> with a <c>Key</c> column. <paramref name="configure"/> can map an existing table
    /// instead, see <see cref="AppKitPageSectionsModelOptions"/>.
    /// </summary>
    public static ModelBuilder AddAppKitPageSections(this ModelBuilder modelBuilder, Action<AppKitPageSectionsModelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var options = new AppKitPageSectionsModelOptions();
        configure?.Invoke(options);
        modelBuilder.Entity<PageSections>(e =>
        {
            e.ToTable(options.TableName);
            e.HasKey(p => p.Id);
            e.Property(p => p.Locale).IsRequired().HasMaxLength(10);
            e.Property(p => p.SectionsJson).IsRequired();
            if (options.KeyColumn)
            {
                e.Property(p => p.Key).IsRequired().HasMaxLength(40);
                e.HasIndex(p => new { p.Key, p.Locale }).IsUnique();
            }
            else
            {
                e.Ignore(p => p.Key);
                e.HasIndex(p => p.Locale).IsUnique();
            }
        });
        return modelBuilder;
    }

    /// <summary>
    /// Maps the page sections endpoints under <paramref name="prefix"/>. <c>GET {key}</c> is open to
    /// everyone and returns the sections in the requested <c>?locale=</c>, or else in
    /// <see cref="AppKitPageSectionsOptions.DefaultLocale"/>, with a <c>Last-Modified</c> header. It returns
    /// an empty array while the page has none. <c>PUT {key}</c> saves a JSON array of sections, behind
    /// <see cref="AppKitPageSectionsOptions.Policy"/>.
    /// </summary>
    public static RouteGroupBuilder MapAppKitPageSections<TContext>(this IEndpointRouteBuilder endpoints,
        string prefix = "/api/content", Action<AppKitPageSectionsOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new AppKitPageSectionsOptions();
        configure?.Invoke(options);
        var handlers = new PageSectionsHandlers<TContext>(options);

        var group = endpoints.MapGroup(prefix);
        group.MapGet("{key}", (string key, HttpContext http, string? locale = null) => handlers.GetAsync(key, locale, http)).AllowAnonymous();
        group.MapPut("{key}", (string key, JsonElement sections, HttpContext http, string? locale = null) => handlers.PutAsync(key, locale, sections, http))
            .RequireAuthorization(options.Policy);
        return group;
    }
}

internal sealed class PageSectionsHandlers<TContext>(AppKitPageSectionsOptions options)
    where TContext : DbContext
{
    private const string Empty = "[]";

    public async Task<IResult> GetAsync(string key, string? locale, HttpContext http)
    {
        if (!options.KnownKey(key))
            return TypedResults.NotFound();
        var db = http.RequestServices.GetRequiredService<TContext>();
        var keyed = Keyed(db);
        key = key.ToLowerInvariant();
        var rows = db.Set<PageSections>().AsNoTracking();
        var wanted = options.Normalize(locale);
        var fallback = options.Default;

        // A language with no sections falls back to the default one rather than an empty page.
        var page = await Find(rows, keyed, key, wanted).FirstOrDefaultAsync(http.RequestAborted);
        if (page is null && wanted != fallback)
            page = await Find(rows, keyed, key, fallback).FirstOrDefaultAsync(http.RequestAborted);

        // Reports the row that is served, which may be the fallback language's.
        if (page is not null)
            http.Response.Headers.LastModified = AsUtc(page.UpdatedAt).ToString("R", CultureInfo.InvariantCulture);

        var json = string.IsNullOrWhiteSpace(page?.SectionsJson) ? Empty : page.SectionsJson;
        return TypedResults.Content(json, "application/json");
    }

    public async Task<IResult> PutAsync(string key, string? locale, JsonElement sections, HttpContext http)
    {
        if (!options.KnownKey(key))
            return TypedResults.NotFound();
        if (sections.ValueKind != JsonValueKind.Array)
            return TypedResults.Problem("Expected a JSON array of sections.", statusCode: StatusCodes.Status400BadRequest);
        if (locale is not null && !options.KnownLocale(locale))
            return TypedResults.Problem("Unsupported locale.", statusCode: StatusCodes.Status400BadRequest);

        var db = http.RequestServices.GetRequiredService<TContext>();
        var keyed = Keyed(db);
        var now = (http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        key = key.ToLowerInvariant();
        locale = locale?.ToLowerInvariant() ?? options.Default;
        var json = sections.GetRawText();

        var page = await Find(db.Set<PageSections>(), keyed, key, locale).FirstOrDefaultAsync(http.RequestAborted);
        if (page is null)
        {
            page = new PageSections { Key = key, Locale = locale };
            db.Set<PageSections>().Add(page);
        }
        page.SectionsJson = json;
        page.UpdatedAt = now;
        await db.SaveChangesAsync(http.RequestAborted);
        if (options.OnSaved is { } onSaved)
            await onSaved(new PageSectionsSaved(key, locale, json, http));
        return TypedResults.Content(json, "application/json");
    }

    private static IQueryable<PageSections> Find(IQueryable<PageSections> rows, bool keyed, string key, string locale) =>
        keyed ? rows.Where(p => p.Key == key && p.Locale == locale) : rows.Where(p => p.Locale == locale);

    /// <summary>
    /// Whether the table has a key column. A table without one holds a single page, so it can serve only
    /// one key.
    /// </summary>
    private bool Keyed(DbContext db)
    {
        var entity = db.Model.FindEntityType(typeof(PageSections))
            ?? throw new InvalidOperationException("PageSections is not in the model. Call AddAppKitPageSections in OnModelCreating.");
        var keyed = entity.FindProperty(nameof(PageSections.Key)) is not null;
        if (!keyed && options.Keys.Count != 1)
            throw new InvalidOperationException("The page sections table has no key column, so it holds one page. Set Keys to a single key.");
        return keyed;
    }

    // SQLite gives back an unspecified kind. The value is stored in UTC either way.
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
}

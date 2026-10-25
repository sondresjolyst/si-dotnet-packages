using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SjolystInnovation.AppKit.SiteSettings.Tests;

public class LegalPagesTests
{
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static Task<TestApp> StartAsync(Action<AppKitLegalPagesOptions>? legal = null) =>
        TestApp.StartAsync(legal: legal ?? (o => o.Locales = ["no", "en"]));

    private static async Task<List<LegalPage>> PagesAsync(TestApp app)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().LegalPages.AsNoTracking().OrderBy(p => p.Key).ThenBy(p => p.Locale).ToListAsync();
    }

    private static Task<HttpResponseMessage> PutAsync(TestApp app, string path, string? title = "Vilkår", string? body = "## Vilkår", string? roles = "Admin") =>
        app.SendAsync(HttpMethod.Put, path, JsonContent.Create(new { title, bodyMarkdown = body }), roles);

    // Access

    [Fact]
    public async Task Reading_a_page_is_public_and_listing_and_saving_need_the_policy()
    {
        await using var app = await StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await app.SendAsync(HttpMethod.Get, "/api/content/legal/terms", roles: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.SendAsync(HttpMethod.Get, "/api/content/legal", roles: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.SendAsync(HttpMethod.Get, "/api/content/legal", roles: "Default")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAsync(app, "/api/content/legal/terms/no", roles: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(app, "/api/content/legal/terms/no", roles: "Default")).StatusCode);
        Assert.Empty(await PagesAsync(app));
    }

    [Fact]
    public async Task The_policy_can_be_changed()
    {
        await using var app = await StartAsync(o => o.Policy = "Owner");

        Assert.Equal(HttpStatusCode.Forbidden, (await app.SendAsync(HttpMethod.Get, "/api/content/legal", roles: "Admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, "/api/content/legal/terms/no", roles: "Owner")).StatusCode);
    }

    // Saving

    [Fact]
    public async Task Saving_creates_the_page_then_updates_it_in_place()
    {
        await using var app = await StartAsync();

        var created = await JsonAsync(await PutAsync(app, "/api/content/legal/terms/no", "  Vilkår  ", "## Første"));
        app.Time.Advance(TimeSpan.FromHours(1));
        var updated = await JsonAsync(await PutAsync(app, "/api/content/legal/terms/no", "Vilkår 2", "## Andre"));

        Assert.Equal("Vilkår", created.GetProperty("title").GetString());
        Assert.Equal("2026-10-15T12:00:00Z", created.GetProperty("updatedAt").GetDateTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Vilkår 2", updated.GetProperty("title").GetString());
        Assert.Equal("## Andre", updated.GetProperty("bodyMarkdown").GetString());
        Assert.Equal("terms", updated.GetProperty("key").GetString());
        Assert.Equal("no", updated.GetProperty("locale").GetString());
        var page = Assert.Single(await PagesAsync(app));
        Assert.Equal("## Andre", page.BodyMarkdown);
        Assert.Equal(new DateTime(2026, 10, 15, 13, 0, 0), page.UpdatedAt);
    }

    [Fact]
    public async Task Keys_and_locales_are_stored_in_lower_case()
    {
        await using var app = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, "/api/content/legal/Terms/EN")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, "/api/content/legal/terms/en", "Terms")).StatusCode);

        var page = Assert.Single(await PagesAsync(app));
        Assert.Equal(("terms", "en", "Terms"), (page.Key, page.Locale, page.Title));
    }

    [Theory]
    [InlineData("/api/content/legal/refunds/no", "Unknown legal page.")]
    [InlineData("/api/content/legal/terms/de", "Unsupported locale.")]
    public async Task An_unknown_page_or_language_is_refused(string path, string detail)
    {
        await using var app = await StartAsync();

        var response = await PutAsync(app, path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(detail, (await JsonAsync(response)).GetProperty("detail").GetString());
        Assert.Empty(await PagesAsync(app));
    }

    [Theory]
    [InlineData(null, "## x", "Title")]
    [InlineData(" ", "## x", "Title")]
    [InlineData("Vilkår", null, "BodyMarkdown")]
    [InlineData("Vilkår", "  ", "BodyMarkdown")]
    public async Task A_missing_title_or_body_is_a_validation_problem(string? title, string? body, string field)
    {
        await using var app = await StartAsync();

        var response = await PutAsync(app, "/api/content/legal/terms/no", title, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal([field], json.GetProperty("errors").EnumerateObject().Select(p => p.Name));
        Assert.Equal("Required.", Assert.Single(json.GetProperty("errors").GetProperty(field).EnumerateArray()).GetString());
        Assert.Equal(field, Assert.Single(json.GetProperty("codes").EnumerateObject()).Name);
        Assert.Equal("required", Assert.Single(json.GetProperty("codes").GetProperty(field).EnumerateArray()).GetString());
        Assert.Empty(json.GetProperty("limits").EnumerateObject());
        Assert.Empty(await PagesAsync(app));
    }

    [Fact]
    public async Task A_title_over_200_characters_is_refused()
    {
        await using var app = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, "/api/content/legal/terms/no", new string('x', 200))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, "/api/content/legal/terms/no", $"  {new string('x', 200)}  ")).StatusCode);
        var response = await PutAsync(app, "/api/content/legal/terms/no", new string('x', 201));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal("At most 200 characters.", Assert.Single(json.GetProperty("errors").GetProperty("Title").EnumerateArray()).GetString());
        Assert.Equal("tooLong", Assert.Single(json.GetProperty("codes").GetProperty("Title").EnumerateArray()).GetString());
        var limit = Assert.Single(json.GetProperty("limits").EnumerateObject());
        Assert.Equal(("Title", 200), (limit.Name, limit.Value.GetInt32()));
    }

    [Fact]
    public async Task A_long_title_and_a_missing_body_get_a_code_each()
    {
        await using var app = await StartAsync();

        var json = await JsonAsync(await PutAsync(app, "/api/content/legal/terms/no", new string('x', 201), ""));

        Assert.Equal(["BodyMarkdown", "Title"], json.GetProperty("codes").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("tooLong", json.GetProperty("codes").GetProperty("Title")[0].GetString());
        Assert.Equal("required", json.GetProperty("codes").GetProperty("BodyMarkdown")[0].GetString());
        Assert.Equal("Title", Assert.Single(json.GetProperty("limits").EnumerateObject()).Name);
        Assert.Empty(await PagesAsync(app));
    }

    [Fact]
    public async Task The_pages_can_be_changed()
    {
        await using var app = await StartAsync(o => { o.Keys = ["terms", "refunds"]; o.Locales = ["en"]; });

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, "/api/content/legal/refunds/en")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(app, "/api/content/legal/privacy/en")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(app, "/api/content/legal/terms/no")).StatusCode);
    }

    // Reading

    [Theory]
    [InlineData(null, "no")]
    [InlineData("no", "no")]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("nb-NO", "no")]
    [InlineData("nn", "no")]
    [InlineData("de", "no")]
    [InlineData(" ", "no")]
    public async Task A_page_is_read_in_the_asked_language(string? locale, string expected)
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/terms/no", "Vilkår");
        await PutAsync(app, "/api/content/legal/terms/en", "Terms");

        var response = await app.SendAsync(HttpMethod.Get, "/api/content/legal/terms" + (locale is null ? "" : $"?locale={Uri.EscapeDataString(locale)}"), roles: null);

        Assert.Equal(expected, (await JsonAsync(response)).GetProperty("locale").GetString());
    }

    [Theory]
    [InlineData("nb-NO", "no")]
    [InlineData("nn", "no")]
    [InlineData(" no ", "no")]
    [InlineData("pt-BR", "pt-br")]
    [InlineData("pt", "pt")]
    [InlineData("pt-PT", "pt")]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("sv", "en")]
    public async Task Norwegian_tags_and_regions_are_matched_even_when_the_fallback_is_another_language(string? locale, string expected)
    {
        await using var app = await StartAsync(o => o.Locales = ["EN", "No", "pt-BR", "pt"]);
        foreach (var l in new[] { "en", "no", "pt-br", "pt" })
            await PutAsync(app, $"/api/content/legal/terms/{l}", $"Terms {l}");

        var response = await app.SendAsync(HttpMethod.Get, "/api/content/legal/terms" + (locale is null ? "" : $"?locale={Uri.EscapeDataString(locale)}"), roles: null);

        Assert.Equal(expected, (await JsonAsync(response)).GetProperty("locale").GetString());
    }

    [Fact]
    public async Task One_page_per_key_and_language_is_enforced_by_the_database()
    {
        await using var app = await StartAsync();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        db.LegalPages.Add(new LegalPage { Key = "terms", Locale = "no", Title = "A", BodyMarkdown = "a" });
        db.LegalPages.Add(new LegalPage { Key = "terms", Locale = "no", Title = "B", BodyMarkdown = "b" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_fallback_is_found_when_the_first_locale_is_configured_in_upper_case()
    {
        await using var app = await StartAsync(o => o.Locales = ["NO", "en"]);
        await PutAsync(app, "/api/content/legal/privacy/no", "Personvern");

        var page = await JsonAsync(await app.SendAsync(HttpMethod.Get, "/api/content/legal/privacy?locale=en", roles: null));

        Assert.Equal("Personvern", page.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_page_missing_in_the_asked_language_falls_back_to_the_first()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/privacy/no", "Personvern");

        var page = await JsonAsync(await app.SendAsync(HttpMethod.Get, "/api/content/legal/privacy?locale=en", roles: null));

        Assert.Equal("no", page.GetProperty("locale").GetString());
        Assert.Equal("Personvern", page.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_page_only_in_another_language_is_not_found()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/cookies/en", "Cookies");

        Assert.Equal(HttpStatusCode.NotFound, (await app.SendAsync(HttpMethod.Get, "/api/content/legal/cookies?locale=no", roles: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Get, "/api/content/legal/COOKIES?locale=en", roles: null)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_page_is_not_found_even_if_a_row_exists()
    {
        await using var app = await StartAsync();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            db.LegalPages.Add(new LegalPage { Key = "refunds", Locale = "no", Title = "Retur", BodyMarkdown = "x" });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound, (await app.SendAsync(HttpMethod.Get, "/api/content/legal/refunds", roles: null)).StatusCode);
    }

    [Fact]
    public async Task The_list_has_every_page_by_key_then_language()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/terms/no", "Vilkår");
        await PutAsync(app, "/api/content/legal/cookies/no", "Informasjonskapsler");
        await PutAsync(app, "/api/content/legal/cookies/en", "Cookies");

        var list = await JsonAsync(await app.SendAsync(HttpMethod.Get, "/api/content/legal"));

        Assert.Equal(["cookies/en", "cookies/no", "terms/no"],
            list.EnumerateArray().Select(p => $"{p.GetProperty("key").GetString()}/{p.GetProperty("locale").GetString()}"));
    }

    // Seeding

    [Fact]
    public async Task Seeding_writes_missing_pages_and_leaves_written_ones_alone()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/terms/no", "Mine vilkår", "## Mine");

        await app.Services.SeedAppKitLegalPagesAsync<TestDbContext>(
        [
            new("terms", "no", "Vilkår", "## Start"),
            new("privacy", "no", "Personvern", "## Start"),
            new("privacy", "en", "Privacy", "## Start"),
        ]);
        await app.Services.SeedAppKitLegalPagesAsync<TestDbContext>([new("privacy", "no", "Annen", "## Annen")]);

        var pages = await PagesAsync(app);
        Assert.Equal(["privacy/en/Privacy", "privacy/no/Personvern", "terms/no/Mine vilkår"], pages.Select(p => $"{p.Key}/{p.Locale}/{p.Title}"));
        Assert.All(pages, p => Assert.Equal(new DateTime(2026, 10, 15, 12, 0, 0), p.UpdatedAt));
    }

    [Fact]
    public async Task Seeding_stores_keys_and_locales_in_lower_case_so_the_pages_are_served()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/terms/no", "Mine vilkår", "## Mine");

        await app.Services.SeedAppKitLegalPagesAsync<TestDbContext>(
        [
            new("Terms", "NO", "Vilkår", "## Start"),
            new("PRIVACY", "En", "Privacy", "## Start"),
            new("privacy", "en", "Privacy again", "## Again"),
        ]);

        var pages = await PagesAsync(app);
        Assert.Equal(["privacy/en/Privacy", "terms/no/Mine vilkår"], pages.Select(p => $"{p.Key}/{p.Locale}/{p.Title}"));
        var served = await JsonAsync(await app.SendAsync(HttpMethod.Get, "/api/content/legal/privacy?locale=en", roles: null));
        Assert.Equal("Privacy", served.GetProperty("title").GetString());
    }

    // Revisions

    private static async Task<List<LegalPageRevision>> RevisionsAsync(TestApp app)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().Set<LegalPageRevision>().AsNoTracking().OrderBy(r => r.Id).ToListAsync();
    }

    private static string Utc(JsonElement value) =>
        value.GetDateTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Every_save_that_changes_the_page_adds_a_revision_listed_newest_first()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/terms/no", "Vilkår", "## Første");
        app.Time.Advance(TimeSpan.FromHours(1));
        await PutAsync(app, "/api/content/legal/terms/no", "Vilkår", "## Andre");
        await PutAsync(app, "/api/content/legal/terms/en", "Terms", "## First");

        var list = await JsonAsync(await app.SendAsync(HttpMethod.Get, "/api/content/legal/terms/no/revisions"));

        Assert.Equal(["2026-10-15T13:00:00Z", "2026-10-15T12:00:00Z"], list.EnumerateArray().Select(r => Utc(r.GetProperty("savedAt"))));
        Assert.All(list.EnumerateArray(), r => Assert.Equal("Vilkår", r.GetProperty("title").GetString()));
        Assert.Equal(["id", "savedAt", "title"], list[0].EnumerateObject().Select(p => p.Name));
        var stored = (await RevisionsAsync(app)).Where(r => r.Locale == "no").ToList();
        Assert.Equal(["## Første", "## Andre"], stored.Select(r => r.BodyMarkdown));
        Assert.All(stored, r => Assert.Equal("u1", r.SavedBy));
        var page = (await PagesAsync(app)).Single(p => p.Locale == "no");
        Assert.Equal(page.UpdatedAt, stored[^1].SavedAt);
    }

    [Fact]
    public async Task A_title_change_alone_adds_a_revision()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/terms/no", "Vilkår", "## Samme");
        await PutAsync(app, "/api/content/legal/terms/no", "Vilkår for kjøp", "## Samme");

        Assert.Equal(["Vilkår", "Vilkår for kjøp"], (await RevisionsAsync(app)).Select(r => r.Title));
    }

    [Fact]
    public async Task A_save_that_changes_nothing_adds_no_revision_and_keeps_the_saved_time()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/terms/no", "Vilkår", "## Samme");
        app.Time.Advance(TimeSpan.FromHours(1));

        var again = await JsonAsync(await PutAsync(app, "/api/content/legal/terms/no", "  Vilkår ", "## Samme"));

        Assert.Equal("2026-10-15T12:00:00Z", Utc(again.GetProperty("updatedAt")));
        Assert.Single(await RevisionsAsync(app));
        Assert.Equal(new DateTime(2026, 10, 15, 12, 0, 0), Assert.Single(await PagesAsync(app)).UpdatedAt);
    }

    [Fact]
    public async Task An_old_revision_opens_with_its_own_text()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/terms/no", "Gamle vilkår", "## Gammel tekst");
        app.Time.Advance(TimeSpan.FromDays(30));
        await PutAsync(app, "/api/content/legal/terms/no", "Nye vilkår", "## Ny tekst");
        var oldId = (await JsonAsync(await app.SendAsync(HttpMethod.Get, "/api/content/legal/terms/no/revisions")))[1].GetProperty("id").GetInt32();

        var old = await JsonAsync(await app.SendAsync(HttpMethod.Get, $"/api/content/legal/TERMS/No/revisions/{oldId}"));

        Assert.Equal(oldId, old.GetProperty("id").GetInt32());
        Assert.Equal(("terms", "no"), (old.GetProperty("key").GetString(), old.GetProperty("locale").GetString()));
        Assert.Equal("Gamle vilkår", old.GetProperty("title").GetString());
        Assert.Equal("## Gammel tekst", old.GetProperty("bodyMarkdown").GetString());
        Assert.Equal("2026-10-15T12:00:00Z", Utc(old.GetProperty("savedAt")));
        Assert.Equal("u1", old.GetProperty("savedBy").GetString());
    }

    [Fact]
    public async Task A_revision_is_only_found_under_its_own_page_and_language()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/terms/no", "Vilkår");
        var id = Assert.Single(await RevisionsAsync(app)).Id;

        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Get, $"/api/content/legal/terms/no/revisions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.SendAsync(HttpMethod.Get, $"/api/content/legal/terms/en/revisions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.SendAsync(HttpMethod.Get, $"/api/content/legal/privacy/no/revisions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.SendAsync(HttpMethod.Get, $"/api/content/legal/terms/no/revisions/{id + 1}")).StatusCode);
    }

    [Theory]
    [InlineData("/api/content/legal/refunds/no/revisions")]
    [InlineData("/api/content/legal/terms/de/revisions")]
    [InlineData("/api/content/legal/refunds/no/revisions/1")]
    [InlineData("/api/content/legal/terms/de/revisions/1")]
    public async Task Revisions_of_an_unknown_page_or_language_are_not_found(string path)
    {
        await using var app = await StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await app.SendAsync(HttpMethod.Get, path)).StatusCode);
    }

    [Fact]
    public async Task A_page_with_no_revisions_lists_none()
    {
        await using var app = await StartAsync();

        var list = await JsonAsync(await app.SendAsync(HttpMethod.Get, "/api/content/legal/privacy/en/revisions"));

        Assert.Empty(list.EnumerateArray());
    }

    [Theory]
    [InlineData("/api/content/legal/terms/no/revisions")]
    [InlineData("/api/content/legal/terms/no/revisions/1")]
    public async Task Revisions_need_the_policy(string path)
    {
        await using var app = await StartAsync(o => o.Policy = "Owner");
        await PutAsync(app, "/api/content/legal/terms/no", roles: "Owner");

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.SendAsync(HttpMethod.Get, path, roles: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.SendAsync(HttpMethod.Get, path, roles: "Admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Get, path, roles: "Owner")).StatusCode);
    }

    [Fact]
    public async Task Seeding_writes_the_first_revision_of_each_page_it_writes()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/terms/no", "Mine vilkår", "## Mine");

        await app.Services.SeedAppKitLegalPagesAsync<TestDbContext>(
        [
            new("terms", "no", "Vilkår", "## Start"),
            new("Privacy", "NO", "Personvern", "## Start"),
        ]);

        var revisions = await RevisionsAsync(app);
        Assert.Equal(["terms/no/Mine vilkår/u1", "privacy/no/Personvern/"], revisions.Select(r => $"{r.Key}/{r.Locale}/{r.Title}/{r.SavedBy}"));
        Assert.Equal(new DateTime(2026, 10, 15, 12, 0, 0), revisions[1].SavedAt);
    }

    [Fact]
    public async Task A_page_written_before_history_was_kept_keeps_its_old_text_on_the_next_save()
    {
        await using var app = await StartAsync();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            db.LegalPages.Add(new LegalPage { Key = "terms", Locale = "no", Title = "Gamle vilkår", BodyMarkdown = "## Gammel", UpdatedAt = new DateTime(2025, 1, 2, 3, 4, 5) });
            await db.SaveChangesAsync();
        }

        await PutAsync(app, "/api/content/legal/terms/no", "Nye vilkår", "## Ny");
        app.Time.Advance(TimeSpan.FromHours(1));
        await PutAsync(app, "/api/content/legal/terms/no", "Nyere vilkår", "## Nyere");

        var revisions = await RevisionsAsync(app);
        Assert.Equal(["## Gammel", "## Ny", "## Nyere"], revisions.Select(r => r.BodyMarkdown));
        Assert.Equal((new DateTime(2025, 1, 2, 3, 4, 5), null), (revisions[0].SavedAt, revisions[0].SavedBy));
        Assert.Equal("Gamle vilkår", revisions[0].Title);
    }

    // Current version

    private static async Task<LegalPage?> FindAsync(TestApp app, string key, string? locale, IReadOnlyList<string>? locales = null)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().FindCurrentLegalPageAsync(key, locale, locales ?? ["no", "en"]);
    }

    private static async Task<string?> VersionAsync(TestApp app, string key, string? locale)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().GetCurrentLegalPageVersionAsync(key, locale, ["no", "en"]);
    }

    [Fact]
    public async Task The_current_page_is_found_in_the_asked_language()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/terms/no", "Vilkår");
        await PutAsync(app, "/api/content/legal/terms/en", "Terms");

        var page = await FindAsync(app, "TERMS", "en-GB");

        Assert.Equal(("terms", "en", "Terms"), (page?.Key, page?.Locale, page?.Title));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData(null)]
    public async Task The_current_page_falls_back_to_the_first_locale(string? locale)
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/terms/no", "Vilkår");

        var page = await FindAsync(app, "terms", locale, ["NO", "en"]);

        Assert.Equal(("no", "Vilkår"), (page?.Locale, page?.Title));
    }

    [Theory]
    [InlineData("nb")]
    [InlineData("nb-NO")]
    [InlineData("nn")]
    public async Task Norwegian_tags_find_the_norwegian_page_even_when_the_fallback_is_another_language(string locale)
    {
        await using var app = await StartAsync(o => o.Locales = ["en", "no"]);
        await PutAsync(app, "/api/content/legal/terms/en", "Terms");
        await PutAsync(app, "/api/content/legal/terms/no", "Vilkår");

        Assert.Equal("no", (await FindAsync(app, "terms", locale, ["en", "no"]))?.Locale);
    }

    [Fact]
    public async Task A_page_with_no_text_has_no_current_version()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/legal/cookies/en", "Cookies");

        Assert.Null(await FindAsync(app, "refunds", "no"));
        Assert.Null(await FindAsync(app, "cookies", "no", ["no"]));
        Assert.Null(await VersionAsync(app, "terms", "no"));
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void The_version_is_the_saved_time_in_utc_to_the_microsecond(DateTimeKind kind)
    {
        var page = new LegalPage { Key = "terms", Locale = "no", Title = "Vilkår", UpdatedAt = new DateTime(2026, 10, 19, 12, 34, 56, kind).AddTicks(1_234_567) };

        Assert.Equal("2026-10-19T12:34:56.123456Z", LegalPagesExtensions.LegalPageVersion(page));
        Assert.Equal("2026-01-02T03:04:05.000000Z", LegalPagesExtensions.LegalPageVersion(new LegalPage { Key = "terms", Locale = "no", Title = "x", UpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, kind) }));
    }

    [Fact]
    public async Task Bad_arguments_are_refused()
    {
        await using var app = await StartAsync();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();

        await Assert.ThrowsAsync<ArgumentException>("locales", () => db.FindCurrentLegalPageAsync("terms", "no", []));
        await Assert.ThrowsAsync<ArgumentNullException>("locales", () => db.FindCurrentLegalPageAsync("terms", "no", null!));
        await Assert.ThrowsAsync<ArgumentNullException>("key", () => db.GetCurrentLegalPageVersionAsync(null!, "no", ["no"]));
        Assert.Throws<ArgumentNullException>("page", () => LegalPagesExtensions.LegalPageVersion(null!));
    }

    [Fact]
    public async Task A_page_answers_the_version_a_client_sends_back()
    {
        await using var app = await StartAsync();
        app.Time.Advance(TimeSpan.FromTicks(1_234_560));
        await PutAsync(app, "/api/content/legal/terms/no", "Vilkår");

        var page = await JsonAsync(await app.SendAsync(HttpMethod.Get, "/api/content/legal/terms?locale=en", roles: null));

        Assert.Equal("2026-10-15T12:00:00.123456Z", page.GetProperty("version").GetString());
        Assert.Equal(await VersionAsync(app, "terms", "en"), page.GetProperty("version").GetString());
        Assert.Equal(["key", "locale", "title", "bodyMarkdown", "updatedAt", "version"], page.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_save_that_changes_nothing_keeps_the_version()
    {
        await using var app = await StartAsync();
        var saved = await JsonAsync(await PutAsync(app, "/api/content/legal/terms/no", "Vilkår", "## Samme"));
        app.Time.Advance(TimeSpan.FromHours(1));

        var again = await JsonAsync(await PutAsync(app, "/api/content/legal/terms/no", "Vilkår", "## Samme"));
        var served = await JsonAsync(await app.SendAsync(HttpMethod.Get, "/api/content/legal/terms", roles: null));

        Assert.Equal("2026-10-15T12:00:00.000000Z", saved.GetProperty("version").GetString());
        Assert.Equal(saved.GetProperty("version").GetString(), again.GetProperty("version").GetString());
        Assert.Equal(saved.GetProperty("version").GetString(), served.GetProperty("version").GetString());
        Assert.Equal("2026-10-15T12:00:00.000000Z", await VersionAsync(app, "terms", "no"));

        await PutAsync(app, "/api/content/legal/terms/no", "Vilkår", "## Endret");
        Assert.Equal("2026-10-15T13:00:00.000000Z", await VersionAsync(app, "terms", "no"));
    }

    // Locale rules

    [Fact]
    public void The_defaults_are_the_three_pages_in_norwegian_behind_admin()
    {
        var options = new AppKitLegalPagesOptions();
        Assert.Equal(["terms", "privacy", "cookies"], options.Keys);
        Assert.Equal(["no"], options.Locales);
        Assert.Equal("Admin", options.Policy);
    }
}

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
        Assert.Equal([field], (await JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name));
        Assert.Empty(await PagesAsync(app));
    }

    [Fact]
    public async Task A_title_over_200_characters_is_refused()
    {
        await using var app = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, "/api/content/legal/terms/no", new string('x', 200))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(app, "/api/content/legal/terms/no", new string('x', 201))).StatusCode);
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

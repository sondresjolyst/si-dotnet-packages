using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SjolystInnovation.AppKit.SiteSettings.Tests;

public class PageSectionsTests
{
    private const string Hero = """[{"id":"1","type":"hero","heading":"Hei"}]""";

    private static Task<TestApp> StartAsync(Action<AppKitPageSectionsOptions>? sections = null) =>
        TestApp.StartAsync(sections: sections ?? (o => o.Locales = ["no", "en"]));

    private static async Task<List<PageSections>> RowsAsync<TContext>(TestApp app) where TContext : DbContext
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TContext>().Set<PageSections>().AsNoTracking().OrderBy(p => p.Locale).ToListAsync();
    }

    private static Task<List<PageSections>> RowsAsync(TestApp app) => RowsAsync<TestDbContext>(app);

    private static Task<HttpResponseMessage> PutAsync(TestApp app, string path, string json = Hero, string? roles = "Admin") =>
        app.SendAsync(HttpMethod.Put, path, new StringContent(json, Encoding.UTF8, "application/json"), roles);

    private static Task<HttpResponseMessage> GetAsync(TestApp app, string path) => app.SendAsync(HttpMethod.Get, path, roles: null);

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    private static string? LastModified(HttpResponseMessage response) =>
        response.Content.Headers.TryGetValues("Last-Modified", out var values) ? values.Single() : null;

    // Access

    [Fact]
    public async Task Reading_is_public_and_saving_needs_the_policy()
    {
        await using var app = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, "/api/content/home")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAsync(app, "/api/content/home", roles: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(app, "/api/content/home", roles: "Default")).StatusCode);
        Assert.Empty(await RowsAsync(app));
    }

    [Fact]
    public async Task Reading_stays_public_when_the_app_requires_sign_in_by_default()
    {
        await using var app = await StartAsync();
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText == "/api/content/{key}").ToList();

        var get = Assert.Single(endpoints, e => e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("GET"));
        var put = Assert.Single(endpoints, e => e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
        Assert.NotNull(get.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.Null(put.Metadata.GetMetadata<IAllowAnonymous>());
    }

    [Fact]
    public async Task The_policy_can_be_changed()
    {
        await using var app = await StartAsync(o => o.Policy = "Owner");

        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(app, "/api/content/home", roles: "Admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, "/api/content/home", roles: "Owner")).StatusCode);
    }

    // Reading

    [Fact]
    public async Task A_page_with_no_sections_is_an_empty_array_without_a_timestamp()
    {
        await using var app = await StartAsync();

        var response = await GetAsync(app, "/api/content/home");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await BodyAsync(response));
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Null(LastModified(response));
    }

    [Fact]
    public async Task Sections_are_served_exactly_as_they_were_saved()
    {
        await using var app = await StartAsync();
        const string json = """[ {"id": "1", "type": "hero", "heading": "Hei å"} , {"id":"2","type":"text","extra":{"n":1.50}} ]""";

        var saved = await PutAsync(app, "/api/content/home", json);
        var served = await GetAsync(app, "/api/content/home");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(json, await BodyAsync(saved));
        Assert.Equal("application/json", saved.Content.Headers.ContentType!.MediaType);
        Assert.Equal(json, await BodyAsync(served));
        Assert.Equal(json, Assert.Single(await RowsAsync(app)).SectionsJson);
    }

    [Fact]
    public async Task Each_language_keeps_its_own_sections()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/home?locale=no", """[{"heading":"Hei"}]""");
        await PutAsync(app, "/api/content/home?locale=en", """[{"heading":"Hello"}]""");

        Assert.Equal("""[{"heading":"Hei"}]""", await BodyAsync(await GetAsync(app, "/api/content/home?locale=no")));
        Assert.Equal("""[{"heading":"Hello"}]""", await BodyAsync(await GetAsync(app, "/api/content/home?locale=en")));
        Assert.Equal(["en", "no"], (await RowsAsync(app)).Select(r => r.Locale));
    }

    [Fact]
    public async Task A_language_with_no_sections_falls_back_to_the_default_and_reports_its_timestamp()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/home?locale=no", Hero);
        app.Time.Advance(TimeSpan.FromHours(2));

        var response = await GetAsync(app, "/api/content/home?locale=en");

        Assert.Equal(Hero, await BodyAsync(response));
        Assert.Equal("Thu, 15 Oct 2026 12:00:00 GMT", LastModified(response));
    }

    [Fact]
    public async Task A_page_only_in_another_language_is_an_empty_array()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/home?locale=en", Hero);

        var response = await GetAsync(app, "/api/content/home?locale=no");

        Assert.Equal("[]", await BodyAsync(response));
        Assert.Null(LastModified(response));
    }

    [Theory]
    [InlineData(null, "no")]
    [InlineData("no", "no")]
    [InlineData("EN", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("nb-NO", "no")]
    [InlineData("nn", "no")]
    [InlineData("de", "no")]
    [InlineData(" ", "no")]
    public async Task Sections_are_read_in_the_asked_language(string? locale, string expected)
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/home?locale=no", "[\"no\"]");
        await PutAsync(app, "/api/content/home?locale=en", "[\"en\"]");

        var response = await GetAsync(app, "/api/content/home" + (locale is null ? "" : $"?locale={Uri.EscapeDataString(locale)}"));

        Assert.Equal($"[\"{expected}\"]", await BodyAsync(response));
    }

    [Fact]
    public async Task The_timestamp_is_the_last_save_in_http_date_format()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/home", "[]");
        app.Time.Advance(TimeSpan.FromMinutes(90));
        await PutAsync(app, "/api/content/home", Hero);

        var response = await GetAsync(app, "/api/content/home");

        Assert.Equal("Thu, 15 Oct 2026 13:30:00 GMT", LastModified(response));
        var row = Assert.Single(await RowsAsync(app));
        Assert.Equal(new DateTime(2026, 10, 15, 13, 30, 0), row.UpdatedAt);
    }

    [Fact]
    public async Task A_stored_blank_value_is_served_as_an_empty_array()
    {
        await using var app = await StartAsync();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            db.PageSections.Add(new PageSections { Key = "home", Locale = "no", SectionsJson = "  ", UpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) });
            await db.SaveChangesAsync();
        }

        var response = await GetAsync(app, "/api/content/home");

        Assert.Equal("[]", await BodyAsync(response));
        Assert.Equal("Fri, 02 Jan 2026 03:04:05 GMT", LastModified(response));
    }

    // Saving

    [Fact]
    public async Task Saving_without_a_locale_writes_the_default_language_and_updates_in_place()
    {
        await using var app = await StartAsync();

        await PutAsync(app, "/api/content/home", "[1]");
        await PutAsync(app, "/api/content/home", "[2]");

        var row = Assert.Single(await RowsAsync(app));
        Assert.Equal(("home", "no", "[2]"), (row.Key, row.Locale, row.SectionsJson));
    }

    [Fact]
    public async Task An_empty_array_can_be_saved()
    {
        await using var app = await StartAsync();
        await PutAsync(app, "/api/content/home", Hero);

        var response = await PutAsync(app, "/api/content/home", "[]");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await BodyAsync(response));
        Assert.Equal("[]", Assert.Single(await RowsAsync(app)).SectionsJson);
    }

    [Theory]
    [InlineData("""{"foo":"bar"}""")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task Anything_but_a_json_array_is_refused(string json)
    {
        await using var app = await StartAsync();

        var response = await PutAsync(app, "/api/content/home", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Expected a JSON array of sections.", JsonDocument.Parse(await BodyAsync(response)).RootElement.GetProperty("detail").GetString());
        Assert.Empty(await RowsAsync(app));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("nb-NO")]
    [InlineData("")]
    public async Task Saving_in_an_unsupported_language_is_refused(string locale)
    {
        await using var app = await StartAsync();

        var response = await PutAsync(app, $"/api/content/home?locale={Uri.EscapeDataString(locale)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Unsupported locale.", JsonDocument.Parse(await BodyAsync(response)).RootElement.GetProperty("detail").GetString());
        Assert.Empty(await RowsAsync(app));
    }

    [Fact]
    public async Task Keys_and_locales_are_stored_in_lower_case()
    {
        await using var app = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, "/api/content/HOME?locale=EN", "[1]")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, "/api/content/home?locale=en", "[2]")).StatusCode);

        var row = Assert.Single(await RowsAsync(app));
        Assert.Equal(("home", "en", "[2]"), (row.Key, row.Locale, row.SectionsJson));
        Assert.Equal("[2]", await BodyAsync(await GetAsync(app, "/api/content/Home?locale=en")));
    }

    [Fact]
    public async Task An_unknown_page_is_not_found()
    {
        await using var app = await StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, "/api/content/about")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(app, "/api/content/about")).StatusCode);
        Assert.Empty(await RowsAsync(app));
    }

    [Fact]
    public async Task The_pages_can_be_changed_and_keep_their_own_sections()
    {
        await using var app = await StartAsync(o => o.Keys = ["home", "about"]);

        await PutAsync(app, "/api/content/home", "[\"home\"]");
        await PutAsync(app, "/api/content/about", "[\"about\"]");

        Assert.Equal("[\"home\"]", await BodyAsync(await GetAsync(app, "/api/content/home")));
        Assert.Equal("[\"about\"]", await BodyAsync(await GetAsync(app, "/api/content/about")));
        Assert.Equal(["about", "home"], (await RowsAsync(app)).Select(r => r.Key).Order());
    }

    [Fact]
    public async Task The_default_language_can_be_another_than_the_first()
    {
        await using var app = await StartAsync(o => { o.Locales = ["en", "no"]; o.DefaultLocale = "NO"; });

        await PutAsync(app, "/api/content/home", "[\"no\"]");

        Assert.Equal("no", Assert.Single(await RowsAsync(app)).Locale);
        Assert.Equal("[\"no\"]", await BodyAsync(await GetAsync(app, "/api/content/home?locale=en")));
        Assert.Equal("[\"no\"]", await BodyAsync(await GetAsync(app, "/api/content/home?locale=sv")));
    }

    [Fact]
    public async Task A_single_language_app_reads_any_tag_as_that_language()
    {
        await using var app = await StartAsync(o => o.Locales = ["en"]);

        await PutAsync(app, "/api/content/home", Hero);

        Assert.Equal("en", Assert.Single(await RowsAsync(app)).Locale);
        Assert.Equal(Hero, await BodyAsync(await GetAsync(app, "/api/content/home?locale=no")));
    }

    [Fact]
    public async Task OnSaved_runs_after_a_save_with_what_was_saved()
    {
        var saved = new List<PageSectionsSaved>();
        await using var app = await StartAsync(o =>
        {
            o.Locales = ["no", "en"];
            o.OnSaved = async s =>
            {
                // The row is saved before the hook runs.
                using var scope = s.Http.RequestServices.CreateScope();
                Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<TestDbContext>().PageSections.CountAsync());
                saved.Add(s);
            };
        });

        await PutAsync(app, "/api/content/HOME?locale=EN", Hero);
        await PutAsync(app, "/api/content/home", "{}");
        await PutAsync(app, "/api/content/home?locale=de", "[]");

        var s = Assert.Single(saved);
        Assert.Equal(("home", "en", Hero), (s.Key, s.Locale, s.SectionsJson));
    }

    [Fact]
    public async Task One_row_per_key_and_language_is_enforced_by_the_database()
    {
        await using var app = await StartAsync();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        db.PageSections.Add(new PageSections { Key = "home", Locale = "no" });
        db.PageSections.Add(new PageSections { Key = "home", Locale = "no" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_legal_pages_still_answer_next_to_the_sections()
    {
        await using var app = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Get, "/api/content/legal")).StatusCode);
        Assert.Equal("[]", await BodyAsync(await app.SendAsync(HttpMethod.Get, "/api/content/legal")));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, "/api/content/legal/terms")).StatusCode);
    }

    // The table

    [Fact]
    public void The_default_table_is_PageSections_with_a_key_column()
    {
        var table = Model<TestDbContext>();

        Assert.Equal("PageSections", table.GetTableName());
        Assert.Equal(["Id", "Key", "Locale", "SectionsJson", "UpdatedAt"], table.GetProperties().Select(p => p.GetColumnName()).Order());
        Assert.Equal(40, table.FindProperty("Key")!.GetMaxLength());
        Assert.False(table.FindProperty("Key")!.IsNullable);
        Assert.Equal(10, table.FindProperty("Locale")!.GetMaxLength());
        Assert.False(table.FindProperty("SectionsJson")!.IsNullable);
        var index = Assert.Single(table.GetIndexes());
        Assert.True(index.IsUnique);
        Assert.Equal(["Key", "Locale"], index.Properties.Select(p => p.Name));
    }

    [Fact]
    public void A_single_page_table_has_no_key_column_and_one_row_per_language()
    {
        var table = Model<SinglePageDbContext>();

        Assert.Equal("HomePageContents", table.GetTableName());
        Assert.Equal(["Id", "Locale", "SectionsJson", "UpdatedAt"], table.GetProperties().Select(p => p.GetColumnName()).Order());
        Assert.Equal("Id", Assert.Single(table.FindPrimaryKey()!.Properties).Name);
        var index = Assert.Single(table.GetIndexes());
        Assert.True(index.IsUnique);
        Assert.Equal("IX_HomePageContents_Locale", index.GetDatabaseName());
        Assert.Equal(["Locale"], index.Properties.Select(p => p.Name));
    }

    [Fact]
    public async Task A_single_page_table_serves_its_one_key()
    {
        await using var app = await TestApp.StartAsync<SinglePageDbContext>(sections: o => o.Locales = ["no", "en"]);

        await PutAsync(app, "/api/content/home?locale=en", "[\"en\"]");
        await PutAsync(app, "/api/content/home", "[\"no\"]");
        await PutAsync(app, "/api/content/home", "[\"no2\"]");

        Assert.Equal("[\"no2\"]", await BodyAsync(await GetAsync(app, "/api/content/home?locale=de")));
        Assert.Equal("[\"en\"]", await BodyAsync(await GetAsync(app, "/api/content/home?locale=en")));
        Assert.Equal(["en", "no"], (await RowsAsync<SinglePageDbContext>(app)).Select(r => r.Locale));
    }

    [Fact]
    public async Task A_single_page_table_refuses_to_serve_several_keys()
    {
        await using var app = await TestApp.StartAsync<SinglePageDbContext>(sections: o => o.Keys = ["home", "about"]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GetAsync(app, "/api/content/home"));
        Assert.Contains("Set Keys to a single key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_defaults_are_the_home_page_in_norwegian_behind_admin()
    {
        var options = new AppKitPageSectionsOptions();
        Assert.Equal(["home"], options.Keys);
        Assert.Equal(["no"], options.Locales);
        Assert.Null(options.DefaultLocale);
        Assert.Equal("no", options.Default);
        Assert.Equal("Admin", options.Policy);
        Assert.Null(options.OnSaved);
        var model = new AppKitPageSectionsModelOptions();
        Assert.Equal("PageSections", model.TableName);
        Assert.True(model.KeyColumn);
    }

    private static IEntityType Model<TContext>() where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>().UseSqlite("DataSource=:memory:").Options;
        using var db = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        return db.Model.FindEntityType(typeof(PageSections))!;
    }
}

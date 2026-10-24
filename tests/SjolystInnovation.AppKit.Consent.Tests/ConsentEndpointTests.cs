using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SjolystInnovation.AppKit.Consent.Tests;

public class ConsentEndpointTests
{
    private const string Id = "3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static object Body(string? consentId = Id, bool? marketing = true, string? noticeVersion = "2026-10-24", int? schemaVersion = 1,
        string? source = "banner") =>
        new { consentId, marketing, noticeVersion, schemaVersion, source };

    private static Task<HttpResponseMessage> PostAsync(TestApp app, object body, string? roles = null, IDictionary<string, string>? headers = null) =>
        app.SendAsync(HttpMethod.Post, "/api/consent", JsonContent.Create(body), roles, headers);

    private static Task<HttpResponseMessage> PostRawAsync(TestApp app, string json) =>
        app.SendAsync(HttpMethod.Post, "/api/consent", new StringContent(json, Encoding.UTF8, "application/json"));

    // Recording

    [Fact]
    public async Task An_anonymous_post_records_the_choice_and_answers_204()
    {
        await using var app = await TestApp.StartAsync();

        var response = await PostAsync(app, Body(marketing: true, noticeVersion: "v2.1_a-b", schemaVersion: 2, source: "settings"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        var row = Assert.Single(await app.RowsAsync());
        Assert.Equal(Guid.Parse(Id), row.ConsentId);
        Assert.True(row.Marketing);
        Assert.Equal("v2.1_a-b", row.NoticeVersion);
        Assert.Equal(2, row.SchemaVersion);
        Assert.Equal("settings", row.Source);
        Assert.Null(row.UserId);
    }

    [Fact]
    public async Task The_post_stays_open_when_the_app_requires_sign_in_by_default()
    {
        await using var app = await TestApp.StartAsync(signInByDefault: true);

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Body())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.SendAsync(HttpMethod.Get, $"/api/consent/{Id}")).StatusCode);
        Assert.Single(await app.RowsAsync());
    }

    [Fact]
    public async Task A_refused_choice_is_stored_as_false()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Body(marketing: false))).StatusCode);

        Assert.False(Assert.Single(await app.RowsAsync()).Marketing);
    }

    [Fact]
    public async Task The_server_time_is_stored_and_a_time_from_the_client_is_ignored()
    {
        await using var app = await TestApp.StartAsync();

        var json = $$"""{"consentId":"{{Id}}","marketing":true,"noticeVersion":"v1","schemaVersion":1,"source":"banner","decidedAt":"2001-01-01T00:00:00Z","DecidedAt":"2001-01-01T00:00:00Z"}""";
        Assert.Equal(HttpStatusCode.NoContent, (await PostRawAsync(app, json)).StatusCode);

        var row = Assert.Single(await app.RowsAsync());
        Assert.Equal(app.Time.GetUtcNow().UtcDateTime, row.DecidedAt);
    }

    [Fact]
    public async Task A_signed_in_visitor_is_recorded_with_the_user_id()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Body(), roles: "Default")).StatusCode);

        Assert.Equal("u1", Assert.Single(await app.RowsAsync()).UserId);
    }

    [Fact]
    public async Task A_signed_in_visitor_with_only_a_sub_claim_is_recorded_with_it()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Body(), roles: "Default", headers: new Dictionary<string, string> { ["X-Test-Sub"] = "s9" })).StatusCode);

        Assert.Equal("s9", Assert.Single(await app.RowsAsync()).UserId);
    }

    [Fact]
    public async Task A_user_whose_identity_is_not_authenticated_gets_no_user_id()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Body(), headers: new Dictionary<string, string> { ["X-Test-Anonymous"] = "1" })).StatusCode);

        Assert.Null(Assert.Single(await app.RowsAsync()).UserId);
    }

    [Fact]
    public async Task Every_post_adds_a_row_and_earlier_rows_stay_as_they_were()
    {
        await using var app = await TestApp.StartAsync();

        await PostAsync(app, Body(marketing: true, noticeVersion: "v1"));
        var first = Assert.Single(await app.RowsAsync());
        app.Time.Advance(TimeSpan.FromDays(2));
        await PostAsync(app, Body(marketing: false, noticeVersion: "v2", source: "settings"));
        await PostAsync(app, Body(marketing: false, noticeVersion: "v2", source: "settings"));

        var rows = await app.RowsAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equivalent(first, rows[0], strict: true);
        Assert.All(rows, r => Assert.Equal(Guid.Parse(Id), r.ConsentId));
        Assert.Equal([true, false, false], rows.Select(r => r.Marketing));
        Assert.Equal(first.DecidedAt.AddDays(2), rows[1].DecidedAt);
    }

    [Fact]
    public async Task There_is_no_way_to_change_or_delete_a_row()
    {
        await using var app = await TestApp.StartAsync();
        await PostAsync(app, Body());

        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            var response = await app.SendAsync(method, $"/api/consent/{Id}", JsonContent.Create(Body(marketing: false)), roles: "Admin");
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await app.SendAsync(method, "/api/consent", JsonContent.Create(Body(marketing: false)), roles: "Admin")).StatusCode);
        }
        Assert.True(Assert.Single(await app.RowsAsync()).Marketing);
    }

    [Fact]
    public async Task The_routes_sit_under_the_prefix()
    {
        await using var app = await TestApp.StartAsync(prefix: "/api/privacy");

        var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToList();
        Assert.Equal(["/api/privacy/consent", "/api/privacy/consent/{consentId:guid}"], routes.Order());
        Assert.Equal(HttpStatusCode.NoContent, (await app.SendAsync(HttpMethod.Post, "/api/privacy/consent", JsonContent.Create(Body()))).StatusCode);
        Assert.Single(await app.RowsAsync());
    }

    // Validation

    public static TheoryData<string, string, string> RefusedBodies => new()
    {
        { """{"marketing":true,"noticeVersion":"v1","schemaVersion":1,"source":"banner"}""", "ConsentId", "required" },
        { """{"consentId":"","marketing":true,"noticeVersion":"v1","schemaVersion":1,"source":"banner"}""", "ConsentId", "required" },
        { """{"consentId":"   ","marketing":true,"noticeVersion":"v1","schemaVersion":1,"source":"banner"}""", "ConsentId", "required" },
        { """{"consentId":"not-a-guid","marketing":true,"noticeVersion":"v1","schemaVersion":1,"source":"banner"}""", "ConsentId", "invalidConsentId" },
        { """{"consentId":"00000000-0000-0000-0000-000000000000","marketing":true,"noticeVersion":"v1","schemaVersion":1,"source":"banner"}""", "ConsentId", "invalidConsentId" },
        { """{"consentId":"3f2a6c1e0b7d4e8a9c551d2e3f4a5b6c","marketing":true,"noticeVersion":"v1","schemaVersion":1,"source":"banner"}""", "ConsentId", "invalidConsentId" },
        { """{"consentId":"{3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c}","marketing":true,"noticeVersion":"v1","schemaVersion":1,"source":"banner"}""", "ConsentId", "invalidConsentId" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","noticeVersion":"v1","schemaVersion":1,"source":"banner"}""", "Marketing", "required" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":null,"noticeVersion":"v1","schemaVersion":1,"source":"banner"}""", "Marketing", "required" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":true,"schemaVersion":1,"source":"banner"}""", "NoticeVersion", "required" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":true,"noticeVersion":"","schemaVersion":1,"source":"banner"}""", "NoticeVersion", "required" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":true,"noticeVersion":"v 1","schemaVersion":1,"source":"banner"}""", "NoticeVersion", "invalidNoticeVersion" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":true,"noticeVersion":"v1\n","schemaVersion":1,"source":"banner"}""", "NoticeVersion", "invalidNoticeVersion" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":true,"noticeVersion":"<script>","schemaVersion":1,"source":"banner"}""", "NoticeVersion", "invalidNoticeVersion" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":true,"noticeVersion":"versjon-æ","schemaVersion":1,"source":"banner"}""", "NoticeVersion", "invalidNoticeVersion" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":true,"noticeVersion":"v1","source":"banner"}""", "SchemaVersion", "required" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":true,"noticeVersion":"v1","schemaVersion":0,"source":"banner"}""", "SchemaVersion", "invalidSchemaVersion" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":true,"noticeVersion":"v1","schemaVersion":-1,"source":"banner"}""", "SchemaVersion", "invalidSchemaVersion" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":true,"noticeVersion":"v1","schemaVersion":1}""", "Source", "required" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":true,"noticeVersion":"v1","schemaVersion":1,"source":""}""", "Source", "required" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":true,"noticeVersion":"v1","schemaVersion":1,"source":"Banner"}""", "Source", "invalidSource" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":true,"noticeVersion":"v1","schemaVersion":1,"source":"banner "}""", "Source", "invalidSource" },
        { """{"consentId":"3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c","marketing":true,"noticeVersion":"v1","schemaVersion":1,"source":"api"}""", "Source", "invalidSource" },
    };

    [Theory]
    [MemberData(nameof(RefusedBodies))]
    public async Task A_refused_field_answers_400_with_its_code_and_stores_nothing(string json, string field, string code)
    {
        await using var app = await TestApp.StartAsync();

        var response = await PostRawAsync(app, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await JsonAsync(response);
        Assert.Equal(code, problem.GetProperty("codes").GetProperty(field)[0].GetString());
        Assert.Single(problem.GetProperty("codes").EnumerateObject());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("errors").GetProperty(field)[0].GetString()));
        Assert.Empty(await app.RowsAsync());
    }

    [Fact]
    public async Task Every_refused_field_is_reported_at_once()
    {
        await using var app = await TestApp.StartAsync();

        var response = await PostRawAsync(app, "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var codes = (await JsonAsync(response)).GetProperty("codes");
        Assert.Equal(["ConsentId", "Marketing", "NoticeVersion", "SchemaVersion", "Source"], codes.EnumerateObject().Select(p => p.Name).Order());
        Assert.All(codes.EnumerateObject(), p => Assert.Equal("required", p.Value[0].GetString()));
        Assert.Empty(await app.RowsAsync());
    }

    [Fact]
    public async Task A_notice_version_of_64_characters_is_allowed_and_65_is_too_long()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Body(noticeVersion: new string('a', 64)))).StatusCode);
        var response = await PostAsync(app, Body(noticeVersion: new string('a', 65)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await JsonAsync(response);
        Assert.Equal("tooLong", problem.GetProperty("codes").GetProperty("NoticeVersion")[0].GetString());
        Assert.Equal(64, problem.GetProperty("limits").GetProperty("NoticeVersion").GetInt32());
        Assert.Equal(new string('a', 64), Assert.Single(await app.RowsAsync()).NoticeVersion);
    }

    [Fact]
    public async Task Both_sources_and_a_schema_version_of_1_are_allowed()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Body(source: ConsentSources.Banner, schemaVersion: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Body(source: ConsentSources.Settings, schemaVersion: 1))).StatusCode);

        Assert.Equal(["banner", "settings"], (await app.RowsAsync()).Select(r => r.Source));
    }

    [Fact]
    public async Task An_upper_case_consent_id_is_allowed()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Body(consentId: Id.ToUpperInvariant()))).StatusCode);

        Assert.Equal(Guid.Parse(Id), Assert.Single(await app.RowsAsync()).ConsentId);
    }

    [Fact]
    public async Task A_body_that_is_not_json_or_has_wrong_types_is_refused()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await PostRawAsync(app, "not json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRawAsync(app, $$"""{"consentId":"{{Id}}","marketing":"yes","noticeVersion":"v1","schemaVersion":1,"source":"banner"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRawAsync(app, "")).StatusCode);
        Assert.Empty(await app.RowsAsync());
    }

    // Rate limiting

    [Fact]
    public async Task The_named_rate_limit_policy_is_applied_to_the_post()
    {
        await using var app = await TestApp.StartAsync(o => o.RateLimitPolicy = TestApp.RateLimitPolicy);

        for (var i = 0; i < TestApp.RateLimit; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Body())).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(app, Body())).StatusCode);

        Assert.Equal(TestApp.RateLimit, (await app.RowsAsync()).Count);
        var post = PostEndpoint(app);
        Assert.Equal(TestApp.RateLimitPolicy, post.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
    }

    [Fact]
    public async Task Without_a_policy_the_post_has_no_rate_limit_of_its_own()
    {
        await using var app = await TestApp.StartAsync();

        for (var i = 0; i < TestApp.RateLimit + 2; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Body())).StatusCode);

        Assert.Null(PostEndpoint(app).Metadata.GetMetadata<EnableRateLimitingAttribute>());
    }

    [Fact]
    public async Task The_history_is_not_rate_limited_by_the_post_policy()
    {
        await using var app = await TestApp.StartAsync(o => o.RateLimitPolicy = TestApp.RateLimitPolicy);

        var history = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/consent/{consentId:guid}");
        Assert.Null(history.Metadata.GetMetadata<EnableRateLimitingAttribute>());
    }

    private static RouteEndpoint PostEndpoint(TestApp app) =>
        app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/consent");

    // History

    [Fact]
    public async Task The_history_needs_the_policy()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.SendAsync(HttpMethod.Get, $"/api/consent/{Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.SendAsync(HttpMethod.Get, $"/api/consent/{Id}", roles: "Default")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Get, $"/api/consent/{Id}", roles: "Admin")).StatusCode);
    }

    [Fact]
    public async Task The_history_policy_can_be_changed()
    {
        await using var app = await TestApp.StartAsync(o => o.Policy = "Owner");

        Assert.Equal(HttpStatusCode.Forbidden, (await app.SendAsync(HttpMethod.Get, $"/api/consent/{Id}", roles: "Admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Get, $"/api/consent/{Id}", roles: "Owner")).StatusCode);
    }

    [Fact]
    public async Task The_history_lists_the_choices_of_one_consent_id_newest_first()
    {
        await using var app = await TestApp.StartAsync();
        await PostAsync(app, Body(marketing: true, noticeVersion: "v1"));
        app.Time.Advance(TimeSpan.FromMinutes(5));
        await PostAsync(app, Body(marketing: false, noticeVersion: "v2", source: "settings"), roles: "Default");
        await PostAsync(app, Body(consentId: "11111111-2222-3333-4444-555555555555"));

        var list = (await JsonAsync(await app.SendAsync(HttpMethod.Get, $"/api/consent/{Id}", roles: "Admin"))).EnumerateArray().ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal("v2", list[0].GetProperty("noticeVersion").GetString());
        Assert.False(list[0].GetProperty("marketing").GetBoolean());
        Assert.Equal("settings", list[0].GetProperty("source").GetString());
        Assert.Equal("u1", list[0].GetProperty("userId").GetString());
        Assert.Equal(1, list[0].GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Id, list[0].GetProperty("consentId").GetString());
        Assert.Equal(app.Time.GetUtcNow().UtcDateTime, list[0].GetProperty("decidedAt").GetDateTime());
        Assert.Equal("v1", list[1].GetProperty("noticeVersion").GetString());
        Assert.Equal(JsonValueKind.Null, list[1].GetProperty("userId").ValueKind);
        Assert.True(list[0].GetProperty("id").GetInt64() > list[1].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Choices_at_the_same_time_are_listed_by_id_newest_first()
    {
        await using var app = await TestApp.StartAsync();
        await PostAsync(app, Body(noticeVersion: "first"));
        await PostAsync(app, Body(noticeVersion: "second"));

        var list = (await JsonAsync(await app.SendAsync(HttpMethod.Get, $"/api/consent/{Id}", roles: "Admin"))).EnumerateArray().ToList();

        Assert.Equal(["second", "first"], list.Select(e => e.GetProperty("noticeVersion").GetString()));
    }

    [Fact]
    public async Task The_history_of_an_unknown_id_is_empty_and_a_non_guid_is_not_found()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Empty((await JsonAsync(await app.SendAsync(HttpMethod.Get, $"/api/consent/{Id}", roles: "Admin"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.SendAsync(HttpMethod.Get, "/api/consent/abc", roles: "Admin")).StatusCode);
    }
}

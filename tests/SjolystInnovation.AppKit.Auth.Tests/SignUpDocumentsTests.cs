using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

// More than one document at sign-up: the terms through the old fields and every further document through documents.
public sealed partial class TermsAndFiltersTests
{
    private Task StartWithPrivacyAsync() => StartAsync(configure: o => o.AdditionalDocuments = ["privacy"]);

    private Task<HttpResponseMessage> RegisterWithAsync(string email, object? documents, string termsVersion = "v2", string? locale = null) =>
        _client.PostAsJsonAsync("/api/auth/register", new { email, password = Password, ageConfirmed = true, termsAccepted = true, termsVersion, locale, documents });

    private string UserIdOf(string email) => Db(db => db.Users.AsNoTracking().Single(u => u.Email == email).Id);

    private static async Task<string?> DocumentOfAsync(HttpResponseMessage response) =>
        JsonElement.Parse(await response.Content.ReadAsStringAsync()).TryGetProperty("document", out var document) ? document.GetString() : null;

    [Fact]
    public async Task Two_documents_accepted_at_sign_up_give_a_row_each_next_to_the_age()
    {
        _version.PerDocument["privacy"] = "p1";
        await StartWithPrivacyAsync();

        var response = await RegisterWithAsync("a@example.invalid", new Dictionary<string, string> { ["privacy"] = "p1" }, locale: "nb");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = Rows(UserIdOf("a@example.invalid"));
        Assert.Equal(
            [("terms", "v2", "register", "nb"), ("age", "16", "register", "nb"), ("privacy", "p1", "register", "nb")],
            rows.Select(r => (r.Document, r.Version, r.Source, r.Locale!)).ToArray());
        Assert.Single(rows.Select(r => r.AcceptedAt).Distinct());
    }

    [Fact]
    public async Task A_sign_up_without_a_listed_document_is_a_field_error_naming_it_and_creates_nothing()
    {
        _version.PerDocument["privacy"] = "p1";
        await StartWithPrivacyAsync();

        var missing = await RegisterWithAsync("a@example.invalid", null);
        var other = await RegisterWithAsync("b@example.invalid", new Dictionary<string, string> { ["cookies"] = "c1" });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(["Documents.privacy=termsNotAccepted"], await ErrorJson.FieldCodesAsync(missing));
        Assert.Equal(["Documents.privacy=termsNotAccepted"], await ErrorJson.FieldCodesAsync(other));
        Assert.Equal(0, Db(db => db.Users.Count()));
    }

    [Theory]
    [InlineData(" ", "required")]
    [InlineData(null, "required")]
    public async Task A_blank_document_version_is_a_required_field_error(string? version, string code)
    {
        await StartWithPrivacyAsync();

        var response = await RegisterWithAsync("a@example.invalid", new Dictionary<string, string?> { ["privacy"] = version });

        Assert.Equal([$"Documents.privacy={code}"], await ErrorJson.FieldCodesAsync(response));
    }

    [Fact]
    public async Task A_document_version_over_64_characters_is_too_long()
    {
        await StartWithPrivacyAsync();

        var response = await RegisterWithAsync("a@example.invalid", new Dictionary<string, string> { ["privacy"] = new string('p', 65) });

        Assert.Equal(["Documents.privacy=tooLong"], await ErrorJson.FieldCodesAsync(response));
        Assert.Equal(["Documents.privacy=64"], await ErrorJson.LimitsAsync(response));
    }

    [Fact]
    public async Task A_stale_version_of_one_document_answers_409_termsChanged_for_that_document_only()
    {
        _version.PerDocument["privacy"] = "p2";
        await StartWithPrivacyAsync();

        var response = await RegisterWithAsync("a@example.invalid", new Dictionary<string, string> { ["privacy"] = "p1" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("termsChanged", await ErrorJson.CodeAsync(response));
        Assert.Equal("privacy", await DocumentOfAsync(response));
        Assert.Equal(0, Db(db => db.Users.Count()));
    }

    [Fact]
    public async Task A_stale_terms_version_next_to_a_current_document_answers_the_terms_error_as_before()
    {
        _version.PerDocument["privacy"] = "p1";
        await StartWithPrivacyAsync();

        var response = await RegisterWithAsync("a@example.invalid", new Dictionary<string, string> { ["privacy"] = "p1" }, termsVersion: "v1");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("termsChanged", await ErrorJson.CodeAsync(response));
        Assert.Null(await DocumentOfAsync(response));
    }

    [Fact]
    public async Task A_document_the_source_has_no_version_for_stores_the_marker()
    {
        _version.PerDocument["privacy"] = null;
        await StartWithPrivacyAsync();

        Assert.Equal(HttpStatusCode.OK, (await RegisterWithAsync("a@example.invalid", new Dictionary<string, string> { ["privacy"] = "anything" })).StatusCode);

        Assert.Equal(AccountConsent.Unversioned, Rows(UserIdOf("a@example.invalid")).Single(r => r.Document == "privacy").Version);
    }

    [Fact]
    public async Task Without_further_documents_the_documents_field_is_ignored_and_the_sign_up_stores_terms_and_age()
    {
        await StartAsync();

        var response = await RegisterWithAsync("a@example.invalid", new Dictionary<string, string> { ["privacy"] = "p1", ["terms"] = "v9" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([("terms", "v2"), ("age", "16")], Rows(UserIdOf("a@example.invalid")).Select(r => (r.Document, r.Version)).ToArray());
    }

    [Fact]
    public async Task A_new_version_of_privacy_makes_only_privacy_pending_and_accepting_it_clears_it()
    {
        _version.PerDocument["privacy"] = "p1";
        await StartWithPrivacyAsync();
        Assert.Equal(HttpStatusCode.OK, (await RegisterWithAsync("a@example.invalid", new Dictionary<string, string> { ["privacy"] = "p1" })).StatusCode);
        var id = UserIdOf("a@example.invalid");
        // Tokens are checked against the real clock, so the token is made before the fake clock moves.
        var token = (await LoginByEmailAsync("a@example.invalid")).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/terms", token)).StatusCode);

        _time.Advance(TimeSpan.FromHours(1));
        _version.PerDocument["privacy"] = "p2";
        await RequireAsync(_time.GetUtcNow().UtcDateTime, "privacy");
        _time.Advance(TimeSpan.FromSeconds(1));
        ExpireUserCheck(id);

        Assert.Equal(["privacy=p2"], Pending(await LoginByEmailAsync("a@example.invalid")));
        Assert.Equal("termsAcceptanceRequired", await ErrorJson.CodeAsync(await GetAsync("/terms", token)));

        var stale = await AcceptTermsAsync(new { document = "privacy", termsVersion = "p1" }, token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("privacy", await DocumentOfAsync(stale));

        Assert.Equal(HttpStatusCode.NoContent, (await AcceptTermsAsync(new { document = "privacy", termsVersion = "p2" }, token)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/terms", token)).StatusCode);
        Assert.Empty(Pending(await LoginByEmailAsync("a@example.invalid")));
        Assert.Equal(
            [("terms", "v2", "register"), ("age", "16", "register"), ("privacy", "p1", "register"), ("privacy", "p2", "reaccept")],
            Rows(id).Select(r => (r.Document, r.Version, r.Source)).ToArray());
    }

    [Fact]
    public async Task Two_pending_documents_are_listed_apart_and_each_is_accepted_on_its_own()
    {
        _version.PerDocument["privacy"] = "p2";
        await StartWithPrivacyAsync();
        await SeedAsync("u1");
        Accept("u1", "v1", _start, "terms");
        Accept("u1", "p1", _start, "privacy");
        var token = await TokenAsync("u1");
        _time.Advance(TimeSpan.FromHours(1));
        await RequireAsync(_time.GetUtcNow().UtcDateTime, "privacy");
        await RequireAsync(_time.GetUtcNow().UtcDateTime, "terms");
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(["privacy=p2", "terms=v2"], Pending(await LoginAsync("u1")));

        Assert.Equal(HttpStatusCode.NoContent, (await AcceptTermsAsync(new { termsVersion = "v2" }, token)).StatusCode);
        Assert.Equal(["privacy=p2"], Pending(await LoginAsync("u1")));
        Assert.Equal("termsAcceptanceRequired", await ErrorJson.CodeAsync(await GetAsync("/terms", token)));

        Assert.Equal(HttpStatusCode.NoContent, (await AcceptTermsAsync(new { document = "privacy", termsVersion = "p2" }, token)).StatusCode);
        Assert.Empty(Pending(await LoginAsync("u1")));
    }

    [Fact]
    public async Task Accepting_the_terms_by_name_works_like_the_old_body_and_names_the_document_in_a_conflict()
    {
        await StartWithPrivacyAsync();
        await SeedAsync("u1");
        var token = await TokenAsync("u1");

        var stale = await AcceptTermsAsync(new { document = "terms", termsVersion = "v1" }, token);
        var right = await AcceptTermsAsync(new { document = " terms ", termsVersion = "v2" }, token);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("terms", await DocumentOfAsync(stale));
        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        Assert.Equal(("terms", "v2", "reaccept"), Rows("u1").Select(r => (r.Document, r.Version, r.Source)).Single());
    }

    [Fact]
    public async Task The_old_accept_body_answers_a_conflict_without_a_document_as_before()
    {
        await StartWithPrivacyAsync();
        await SeedAsync("u1");

        var stale = await AcceptTermsAsync(new { termsVersion = "v1" }, await TokenAsync("u1"));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Null(await DocumentOfAsync(stale));
    }

    [Fact]
    public async Task A_document_name_over_32_characters_is_too_long()
    {
        await StartWithPrivacyAsync();
        await SeedAsync("u1");

        var response = await AcceptTermsAsync(new { document = new string('d', 33), termsVersion = "v2" }, await TokenAsync("u1"));

        Assert.Equal(["Document=tooLong"], await ErrorJson.FieldCodesAsync(response));
        Assert.Equal(["Document=32"], await ErrorJson.LimitsAsync(response));
    }

    [Fact]
    public async Task A_document_version_is_trimmed_like_the_terms_version()
    {
        _version.PerDocument["privacy"] = "p1";
        await StartWithPrivacyAsync();

        var response = await RegisterWithAsync("a@example.invalid", new Dictionary<string, string> { ["privacy"] = " p1 " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("cookies")]
    [InlineData("age")]
    public async Task Accepting_a_document_the_app_does_not_list_answers_409_and_records_nothing(string document)
    {
        await StartWithPrivacyAsync();
        await SeedAsync("u1");

        var response = await AcceptTermsAsync(new { document, termsVersion = "v2" }, await TokenAsync("u1"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("termsChanged", await ErrorJson.CodeAsync(response));
        Assert.Equal(document, await DocumentOfAsync(response));
        Assert.Empty(Rows("u1"));
    }
}

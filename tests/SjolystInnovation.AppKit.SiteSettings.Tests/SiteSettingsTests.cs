using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SjolystInnovation.AppKit.SiteSettings.Tests;

public class SiteSettingsTests
{
    private static Dictionary<string, object?> Body(Action<Dictionary<string, object?>>? change = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["contactRecipientEmail"] = "inbox@example.no",
            ["companyName"] = "Pyttogpanne",
            ["companyLegalName"] = "Pyttogpanne AS",
            ["orgNumber"] = "923 202 374",
            ["vatRegistered"] = true,
            ["streetAddress"] = "Mårvegen 21a",
            ["postalCode"] = "4347",
            ["addressLocality"] = "Lye",
            ["addressRegion"] = "Rogaland",
            ["publicEmail"] = "post@example.no",
            ["publicPhone"] = "+47 473 88 759",
        };
        change?.Invoke(body);
        return body;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string[]> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return [.. (await JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name)];
    }

    // Access

    [Fact]
    public async Task Company_and_branding_are_public_and_settings_need_the_policy()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Get, "/api/company", roles: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Get, "/api/branding", roles: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.SendAsync(HttpMethod.Get, "/api/settings", roles: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.SendAsync(HttpMethod.Get, "/api/settings", roles: "Default")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(Body()), roles: "Default")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.SendAsync(HttpMethod.Put, "/api/branding", new MultipartFormDataContent(), roles: "Default")).StatusCode);
        Assert.Null(await app.RowAsync());
    }

    [Fact]
    public async Task The_policy_can_be_changed()
    {
        await using var app = await TestApp.StartAsync(o => o.Policy = "Owner");

        Assert.Equal(HttpStatusCode.Forbidden, (await app.SendAsync(HttpMethod.Get, "/api/settings", roles: "Admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Get, "/api/settings", roles: "Owner")).StatusCode);
    }

    // Reading

    [Fact]
    public async Task Reads_answer_the_entity_defaults_before_the_row_exists()
    {
        await using var app = await TestApp.StartAsync();

        var settings = await JsonAsync(await app.SendAsync(HttpMethod.Get, "/api/settings"));
        var company = await JsonAsync(await app.SendAsync(HttpMethod.Get, "/api/company"));
        var branding = await JsonAsync(await app.SendAsync(HttpMethod.Get, "/api/branding"));

        Assert.Equal("Pyttogpanne", settings.GetProperty("companyName").GetString());
        Assert.Equal("Pyttogpanne", company.GetProperty("legalName").GetString());
        Assert.Equal(JsonValueKind.Null, branding.GetProperty("logoData").ValueKind);
        Assert.Null(await app.RowAsync());
    }

    [Fact]
    public async Task Company_info_shows_the_public_details_with_a_one_line_address()
    {
        await using var app = await TestApp.StartAsync();
        await app.SeedAsync(new TestSettings
        {
            ContactRecipientEmail = "secret@example.no", CompanyName = "Pyttogpanne", CompanyLegalName = "Pyttogpanne AS",
            OrgNumber = "923 202 374", VatRegistered = true, StreetAddress = "Mårvegen 21a", PostalCode = "4347",
            AddressLocality = "Lye", AddressRegion = "Rogaland", PublicEmail = "post@example.no", PublicPhone = "+47 1",
        });

        var response = await app.SendAsync(HttpMethod.Get, "/api/company", roles: null);
        var company = await JsonAsync(response);

        Assert.Equal("Pyttogpanne", company.GetProperty("name").GetString());
        Assert.Equal("Pyttogpanne AS", company.GetProperty("legalName").GetString());
        Assert.Equal("923 202 374", company.GetProperty("orgNumber").GetString());
        Assert.True(company.GetProperty("vatRegistered").GetBoolean());
        Assert.Equal("Mårvegen 21a, 4347 Lye", company.GetProperty("address").GetString());
        Assert.Equal("Mårvegen 21a", company.GetProperty("streetAddress").GetString());
        Assert.Equal("4347", company.GetProperty("postalCode").GetString());
        Assert.Equal("Lye", company.GetProperty("addressLocality").GetString());
        Assert.Equal("Rogaland", company.GetProperty("addressRegion").GetString());
        Assert.Equal("post@example.no", company.GetProperty("email").GetString());
        Assert.Equal("+47 1", company.GetProperty("phone").GetString());
        Assert.DoesNotContain("secret@example.no", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_blank_legal_name_falls_back_to_the_company_name()
    {
        await using var app = await TestApp.StartAsync();
        await app.SeedAsync(new TestSettings { CompanyName = "Garge", CompanyLegalName = " " });

        Assert.Equal("Garge", (await JsonAsync(await app.SendAsync(HttpMethod.Get, "/api/company"))).GetProperty("legalName").GetString());
    }

    // Writing

    [Fact]
    public async Task The_first_write_creates_the_row_with_trimmed_values_and_keeps_the_apps_own_settings()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(Body(b => { b["companyName"] = "  Pyttogpanne  "; b["contactRecipientEmail"] = " inbox@example.no "; })));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Pyttogpanne", (await JsonAsync(response)).GetProperty("companyName").GetString());
        var row = await app.RowAsync();
        Assert.NotNull(row);
        Assert.Equal(1, row.Id);
        Assert.Equal("inbox@example.no", row.ContactRecipientEmail);
        Assert.Equal("Pyttogpanne", row.CompanyName);
        Assert.Equal("Pyttogpanne AS", row.CompanyLegalName);
        Assert.Equal("923 202 374", row.OrgNumber);
        Assert.True(row.VatRegistered);
        Assert.Equal("Mårvegen 21a", row.StreetAddress);
        Assert.Equal("4347", row.PostalCode);
        Assert.Equal("Lye", row.AddressLocality);
        Assert.Equal("Rogaland", row.AddressRegion);
        Assert.Equal("post@example.no", row.PublicEmail);
        Assert.Equal("+47 473 88 759", row.PublicPhone);
        Assert.True(row.CookieBannerEnabled);
    }

    [Fact]
    public async Task A_later_write_changes_the_existing_row_and_leaves_branding_and_own_settings_alone()
    {
        await using var app = await TestApp.StartAsync();
        await app.SeedAsync(new TestSettings { CompanyName = "Old", LogoData = "AAAA", LogoContentType = "image/png", CookieBannerEnabled = false });

        await app.SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(Body(b =>
        {
            b["companyLegalName"] = null;
            b["orgNumber"] = null;
            b["vatRegistered"] = false;
            b["postalCode"] = null;
            b["addressLocality"] = null;
            b["addressRegion"] = null;
        })));

        var row = await app.RowAsync();
        Assert.Equal("Pyttogpanne", row!.CompanyName);
        Assert.Equal("", row.CompanyLegalName);
        Assert.Equal("", row.OrgNumber);
        Assert.False(row.VatRegistered);
        Assert.Equal("", row.PostalCode);
        Assert.Equal("", row.AddressLocality);
        Assert.Equal("", row.AddressRegion);
        Assert.Equal("AAAA", row.LogoData);
        Assert.False(row.CookieBannerEnabled);
    }

    [Theory]
    [InlineData("contactRecipientEmail", "", "ContactRecipientEmail")]
    [InlineData("contactRecipientEmail", "not-an-email", "ContactRecipientEmail")]
    [InlineData("contactRecipientEmail", "a@b.no x", "ContactRecipientEmail")]
    [InlineData("contactRecipientEmail", "Kari <kari@example.no>", "ContactRecipientEmail")]
    [InlineData("publicEmail", "\"Kari\" kari@example.no", "PublicEmail")]
    [InlineData("companyName", " ", "CompanyName")]
    [InlineData("streetAddress", "", "StreetAddress")]
    [InlineData("publicEmail", "", "PublicEmail")]
    [InlineData("publicEmail", "nope", "PublicEmail")]
    [InlineData("publicPhone", "", "PublicPhone")]
    [InlineData("orgNumber", "923 202 375", "OrgNumber")]
    [InlineData("orgNumber", "12345678", "OrgNumber")]
    [InlineData("orgNumber", "92320237A", "OrgNumber")]
    // '=' is 13 past '0', so in place of the 2 it keeps the weighted sum modulo 11.
    [InlineData("orgNumber", "9=3202374", "OrgNumber")]
    [InlineData("postalCode", "434", "PostalCode")]
    [InlineData("postalCode", "43470", "PostalCode")]
    [InlineData("postalCode", "43a7", "PostalCode")]
    public async Task A_bad_field_is_a_validation_problem_that_changes_nothing(string field, string value, string error)
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(Body(b => b[field] = value)));

        Assert.Equal([error], await ErrorsAsync(response));
        Assert.Null(await app.RowAsync());
    }

    [Theory]
    [InlineData("companyName", 100)]
    [InlineData("companyLegalName", 200)]
    [InlineData("streetAddress", 200)]
    [InlineData("addressLocality", 100)]
    [InlineData("addressRegion", 100)]
    [InlineData("publicPhone", 40)]
    public async Task Text_longer_than_its_limit_is_refused(string field, int max)
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(Body(b => b[field] = new string('x', max))))).StatusCode);
        var response = await app.SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(Body(b => b[field] = new string('x', max + 1))));

        Assert.Single(await ErrorsAsync(response));
    }

    [Fact]
    public async Task An_email_longer_than_200_is_refused()
    {
        await using var app = await TestApp.StartAsync();
        var fits = new string('a', 189) + "@example.no";
        var longEmail = new string('a', 190) + "@example.no";
        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(Body(b => { b["contactRecipientEmail"] = fits; b["publicEmail"] = fits; })))).StatusCode);

        var response = await app.SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(Body(b => { b["contactRecipientEmail"] = longEmail; b["publicEmail"] = longEmail; })));

        Assert.Equal(["ContactRecipientEmail", "PublicEmail"], (await ErrorsAsync(response)).Order());
    }

    [Fact]
    public async Task Vat_registration_needs_an_organisation_number()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(Body(b => { b["orgNumber"] = ""; b["vatRegistered"] = true; })));
        var message = (await JsonAsync(response)).GetProperty("errors").GetProperty("OrgNumber")[0].GetString();

        Assert.Contains("VAT", message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(Body(b => { b["orgNumber"] = ""; b["vatRegistered"] = false; })))).StatusCode);
    }

    [Fact]
    public async Task A_postal_code_needs_its_place_and_neither_is_required_alone()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(["AddressLocality"], await ErrorsAsync(await app.SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(Body(b => b["addressLocality"] = "")))));
        Assert.Equal(HttpStatusCode.OK, (await app.SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(Body(b => { b["postalCode"] = ""; b["addressLocality"] = ""; })))).StatusCode);
    }

    [Fact]
    public async Task Every_bad_field_is_named_once()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Put, "/api/settings", JsonContent.Create(Body(b =>
        {
            b["contactRecipientEmail"] = "";
            b["publicEmail"] = "";
        })));
        var errors = (await JsonAsync(response)).GetProperty("errors");

        Assert.Equal("Required.", Assert.Single(errors.GetProperty("ContactRecipientEmail").EnumerateArray()).GetString());
        Assert.Equal("Required.", Assert.Single(errors.GetProperty("PublicEmail").EnumerateArray()).GetString());
    }

    // Helpers

    [Theory]
    [InlineData("923202374", true)]
    [InlineData("923 202 374", true)]
    [InlineData("938517789", true)]
    [InlineData("974760673", true)]
    [InlineData("923202375", false)]
    [InlineData("000000000", true)]
    [InlineData("123456785", true)]
    [InlineData("123456789", false)]
    [InlineData("92320237", false)]
    [InlineData("9232023740", false)]
    [InlineData("", false)]
    [InlineData("923-202-374", false)]
    public void Organisation_numbers_are_checked_with_modulus_11(string value, bool valid) =>
        Assert.Equal(valid, OrgNumber.IsValid(value));

    [Fact]
    public void A_number_whose_check_digit_would_be_10_is_never_valid()
    {
        // 10000000x: the weighted sum is 3, so the check digit would be 11 - 3 = 8. 1000000x0 with a 2 in the
        // eighth place sums 3 + 4 = 7, giving 4. 30000000x sums 9, giving 2. 00000001x sums 2, giving 9.
        // 00000040x sums 12, whose remainder 1 asks for the impossible check digit 10.
        foreach (var last in "0123456789")
            Assert.False(OrgNumber.IsValid($"00000040{last}"));
    }

    [Theory]
    [InlineData("Mårvegen 21a", "4347", "Lye", "Mårvegen 21a, 4347 Lye")]
    [InlineData("Mårvegen 21a", "", "", "Mårvegen 21a")]
    [InlineData("", "4347", "Lye", "4347 Lye")]
    [InlineData(" Gata 1 ", " 4347 ", " Lye ", "Gata 1, 4347 Lye")]
    [InlineData("", "", "Lye", "Lye")]
    [InlineData("", "", "", "")]
    [InlineData(null, null, null, "")]
    public void The_address_joins_the_parts_it_has(string? street, string? postalCode, string? locality, string expected) =>
        Assert.Equal(expected, PostalAddress.Format(street, postalCode, locality));

    [Theory]
    [InlineData("4347", true)]
    [InlineData(" 0150 ", true)]
    [InlineData("434", false)]
    [InlineData("43470", false)]
    [InlineData("４３４７", false)]
    public void Postal_codes_are_four_digits(string value, bool valid) =>
        Assert.Equal(valid, PostalAddress.IsPostalCode(value));
}

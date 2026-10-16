using System.Net.Mail;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SjolystInnovation.AppKit.SiteSettings;

/// <summary>
/// The company details in the app's single settings row. The row has the key 1. The app's own entity
/// implements this interface next to any settings of its own.
/// </summary>
public interface ICompanySettings
{
    /// <summary>The row key. Always 1.</summary>
    int Id { get; set; }

    /// <summary>The address the contact form sends its messages to. Not shown publicly.</summary>
    string ContactRecipientEmail { get; set; }

    /// <summary>The company name shown on the site.</summary>
    string CompanyName { get; set; }

    /// <summary>The registered name. Blank until the company is registered. While blank, the public info uses the company name.</summary>
    string CompanyLegalName { get; set; }

    /// <summary>The Norwegian organization number. Blank until the company is registered.</summary>
    string OrgNumber { get; set; }

    /// <summary>Whether the business is VAT registered.</summary>
    bool VatRegistered { get; set; }

    /// <summary>The street address.</summary>
    string StreetAddress { get; set; }

    /// <summary>The Norwegian postal code. Optional.</summary>
    string PostalCode { get; set; }

    /// <summary>The place name that goes with the postal code.</summary>
    string AddressLocality { get; set; }

    /// <summary>The county. Optional.</summary>
    string AddressRegion { get; set; }

    /// <summary>The public contact email address.</summary>
    string PublicEmail { get; set; }

    /// <summary>The public phone number.</summary>
    string PublicPhone { get; set; }
}

/// <summary>The site logo and icon, stored as base64 with the type read from the image itself.</summary>
public interface IBrandingSettings
{
    /// <summary>The logo image as base64, or null if there is none.</summary>
    string? LogoData { get; set; }
    /// <summary>The logo's content type, for example <c>image/png</c>.</summary>
    string? LogoContentType { get; set; }
    /// <summary>The icon image as base64, or null if there is none.</summary>
    string? IconData { get; set; }
    /// <summary>The icon's content type, for example <c>image/x-icon</c>.</summary>
    string? IconContentType { get; set; }
}

/// <summary>The options for <see cref="SiteSettingsExtensions.MapAppKitSiteSettings{TContext, TSettings}"/>.</summary>
public sealed class AppKitSiteSettingsOptions
{
    /// <summary>The authorization policy for reading the settings and changing settings and branding. Defaults to <c>Admin</c>.</summary>
    public string Policy { get; set; } = "Admin";
}

/// <summary>The settings an admin reads and writes.</summary>
public sealed record SiteSettingsBody(
    string? ContactRecipientEmail,
    string? CompanyName,
    string? CompanyLegalName,
    string? OrgNumber,
    bool VatRegistered,
    string? StreetAddress,
    string? PostalCode,
    string? AddressLocality,
    string? AddressRegion,
    string? PublicEmail,
    string? PublicPhone);

/// <summary>
/// The public company info. <paramref name="Address"/> is the one-line form for display. The separate
/// parts are what structured data needs.
/// </summary>
public sealed record CompanyInfo(
    string Name,
    string LegalName,
    string OrgNumber,
    bool VatRegistered,
    string Address,
    string StreetAddress,
    string PostalCode,
    string AddressLocality,
    string AddressRegion,
    string Email,
    string Phone);

/// <summary>Maps the site settings and branding endpoints.</summary>
public static class SiteSettingsExtensions
{
    /// <summary>
    /// Maps the settings endpoints under <paramref name="prefix"/>. <c>GET company</c> and <c>GET branding</c>
    /// are open to everyone. <c>GET</c> and <c>PUT settings</c> and <c>PUT branding</c> are behind
    /// <see cref="AppKitSiteSettingsOptions.Policy"/>. The settings live in the row of
    /// <typeparamref name="TSettings"/> with the key 1. Before the row exists, reads return the entity's
    /// defaults. The first write creates the row.
    /// </summary>
    public static RouteGroupBuilder MapAppKitSiteSettings<TContext, TSettings>(this IEndpointRouteBuilder endpoints,
        string prefix = "/api", Action<AppKitSiteSettingsOptions>? configure = null)
        where TContext : DbContext
        where TSettings : class, ICompanySettings, IBrandingSettings, new()
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new AppKitSiteSettingsOptions();
        configure?.Invoke(options);

        var group = endpoints.MapGroup(prefix);
        group.MapGet("company", (Func<HttpContext, Task<IResult>>)SiteSettingsHandlers<TContext, TSettings>.CompanyAsync).AllowAnonymous();
        group.MapGet("settings", (Func<HttpContext, Task<IResult>>)SiteSettingsHandlers<TContext, TSettings>.GetAsync).RequireAuthorization(options.Policy);
        group.MapPut("settings", (SiteSettingsBody body, HttpContext http) => SiteSettingsHandlers<TContext, TSettings>.UpdateAsync(body, http)).RequireAuthorization(options.Policy);
        group.MapGet("branding", (Func<HttpContext, Task<IResult>>)BrandingHandlers<TContext, TSettings>.GetAsync).AllowAnonymous();
        group.MapPut("branding", (Func<HttpContext, Task<IResult>>)BrandingHandlers<TContext, TSettings>.UpdateAsync).RequireAuthorization(options.Policy).DisableAntiforgery();
        return group;
    }
}

// Services come from RequestServices, because the route handler analyzer cannot follow generic handler parameters.
internal static class SiteSettingsHandlers<TContext, TSettings>
    where TContext : DbContext
    where TSettings : class, ICompanySettings, IBrandingSettings, new()
{
    internal static async Task<TSettings?> FindAsync(HttpContext http) =>
        await http.RequestServices.GetRequiredService<TContext>().Set<TSettings>().FindAsync([1], http.RequestAborted);

    internal static async Task<(TContext Db, TSettings Settings)> FindOrAddAsync(HttpContext http)
    {
        var db = http.RequestServices.GetRequiredService<TContext>();
        var settings = await db.Set<TSettings>().FindAsync([1], http.RequestAborted);
        if (settings is null)
        {
            settings = new TSettings { Id = 1 };
            db.Set<TSettings>().Add(settings);
        }
        return (db, settings);
    }

    public static async Task<IResult> CompanyAsync(HttpContext http)
    {
        var s = await FindAsync(http) ?? new TSettings();
        return TypedResults.Ok(new CompanyInfo(
            s.CompanyName,
            string.IsNullOrWhiteSpace(s.CompanyLegalName) ? s.CompanyName : s.CompanyLegalName,
            s.OrgNumber,
            s.VatRegistered,
            PostalAddress.Format(s.StreetAddress, s.PostalCode, s.AddressLocality),
            s.StreetAddress,
            s.PostalCode,
            s.AddressLocality,
            s.AddressRegion,
            s.PublicEmail,
            s.PublicPhone));
    }

    public static async Task<IResult> GetAsync(HttpContext http) => TypedResults.Ok(ToBody(await FindAsync(http) ?? new TSettings()));

    public static async Task<IResult> UpdateAsync(SiteSettingsBody body, HttpContext http)
    {
        var errors = SiteSettingsValidation.Validate(body);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var (db, s) = await FindOrAddAsync(http);
        s.ContactRecipientEmail = body.ContactRecipientEmail!.Trim();
        s.CompanyName = body.CompanyName!.Trim();
        s.CompanyLegalName = (body.CompanyLegalName ?? "").Trim();
        s.OrgNumber = (body.OrgNumber ?? "").Trim();
        s.VatRegistered = body.VatRegistered;
        s.StreetAddress = body.StreetAddress!.Trim();
        s.PostalCode = (body.PostalCode ?? "").Trim();
        s.AddressLocality = (body.AddressLocality ?? "").Trim();
        s.AddressRegion = (body.AddressRegion ?? "").Trim();
        s.PublicEmail = body.PublicEmail!.Trim();
        s.PublicPhone = body.PublicPhone!.Trim();
        await db.SaveChangesAsync(http.RequestAborted);
        return TypedResults.Ok(ToBody(s));
    }

    private static SiteSettingsBody ToBody(TSettings s) => new(
        s.ContactRecipientEmail, s.CompanyName, s.CompanyLegalName, s.OrgNumber, s.VatRegistered,
        s.StreetAddress, s.PostalCode, s.AddressLocality, s.AddressRegion, s.PublicEmail, s.PublicPhone);
}

internal static class SiteSettingsValidation
{
    public static Dictionary<string, string[]> Validate(SiteSettingsBody body)
    {
        var errors = new Dictionary<string, string[]>();
        void Check(string field, bool ok, string message)
        {
            if (!ok && !errors.ContainsKey(field))
                errors[field] = [message];
        }
        static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
        static bool Fits(string? value, int max) => (value?.Trim().Length ?? 0) <= max;

        Check("ContactRecipientEmail", !Blank(body.ContactRecipientEmail), "Required.");
        Check("ContactRecipientEmail", IsEmail(body.ContactRecipientEmail) && Fits(body.ContactRecipientEmail, 200), "Not a valid email address.");
        Check("CompanyName", !Blank(body.CompanyName), "Required.");
        Check("CompanyName", Fits(body.CompanyName, 100), "At most 100 characters.");
        Check("CompanyLegalName", Fits(body.CompanyLegalName, 200), "At most 200 characters.");
        Check("OrgNumber", !body.VatRegistered || !Blank(body.OrgNumber), "An organisation number is required when the business is VAT registered.");
        Check("OrgNumber", Blank(body.OrgNumber) || OrgNumber.IsValid(body.OrgNumber!), "Not a valid organisation number. It has nine digits and ends in a check digit.");
        Check("StreetAddress", !Blank(body.StreetAddress), "Required.");
        Check("StreetAddress", Fits(body.StreetAddress, 200), "At most 200 characters.");
        Check("PostalCode", Blank(body.PostalCode) || PostalAddress.IsPostalCode(body.PostalCode!), "A Norwegian postal code is four digits.");
        Check("AddressLocality", Blank(body.PostalCode) || !Blank(body.AddressLocality), "A postal code needs the place name that goes with it.");
        Check("AddressLocality", Fits(body.AddressLocality, 100), "At most 100 characters.");
        Check("AddressRegion", Fits(body.AddressRegion, 100), "At most 100 characters.");
        Check("PublicEmail", !Blank(body.PublicEmail), "Required.");
        Check("PublicEmail", IsEmail(body.PublicEmail) && Fits(body.PublicEmail, 200), "Not a valid email address.");
        Check("PublicPhone", !Blank(body.PublicPhone), "Required.");
        Check("PublicPhone", Fits(body.PublicPhone, 40), "At most 40 characters.");
        return errors;
    }

    private static bool IsEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && MailAddress.TryCreate(value.Trim(), out var address) && address.Address == value.Trim();
}

/// <summary>Validates the Norwegian organization number: nine digits, the last of which is a modulus 11 check digit.</summary>
public static class OrgNumber
{
    private static readonly int[] Weights = [3, 2, 7, 6, 5, 4, 3, 2];

    /// <summary>Checks whether the number is nine digits with a correct check digit. Spaces are allowed, for example <c>923 202 374</c>.</summary>
    public static bool IsValid(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var digits = value.Replace(" ", "", StringComparison.Ordinal);
        if (digits.Length != 9 || !digits.All(char.IsAsciiDigit))
            return false;
        var sum = 0;
        for (var i = 0; i < 8; i++)
            sum += (digits[i] - '0') * Weights[i];
        // A remainder of 1 gives the check digit 10, which no valid number can have.
        var check = 11 - sum % 11;
        if (check == 11)
            check = 0;
        return check == digits[8] - '0';
    }
}

/// <summary>Helpers for Norwegian postal addresses, kept as parts. The one-line form is derived, so it cannot disagree with them.</summary>
public static class PostalAddress
{
    /// <summary>Joins the parts into one line, for example <c>Mårvegen 21a, 4347 Lye</c>, and skips blank parts.</summary>
    public static string Format(string? streetAddress, string? postalCode, string? addressLocality)
    {
        var place = string.Join(' ', new[] { postalCode, addressLocality }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return string.Join(", ", new[] { streetAddress, place }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
    }

    /// <summary>Checks whether the value is a Norwegian postal code of four digits.</summary>
    public static bool IsPostalCode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var code = value.Trim();
        return code.Length == 4 && code.All(char.IsAsciiDigit);
    }
}

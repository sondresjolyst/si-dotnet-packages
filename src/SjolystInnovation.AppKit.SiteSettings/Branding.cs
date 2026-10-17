using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace SjolystInnovation.AppKit.SiteSettings;

/// <summary>The stored site logo and icon, for the site's header and favicon.</summary>
/// <param name="LogoData">The logo image as base64, or null if there is none.</param>
/// <param name="LogoContentType">The logo's content type, or null if there is no logo.</param>
/// <param name="IconData">The icon image as base64, or null if there is none.</param>
/// <param name="IconContentType">The icon's content type, or null if there is no icon.</param>
public sealed record BrandingInfo(string? LogoData, string? LogoContentType, string? IconData, string? IconContentType);

internal static class BrandingHandlers<TContext, TSettings>
    where TContext : DbContext
    where TSettings : class, ICompanySettings, IBrandingSettings, new()
{
    public const long MaxImageBytes = 2 * 1024 * 1024;

    public static async Task<IResult> GetAsync(HttpContext http)
    {
        var s = await SiteSettingsHandlers<TContext, TSettings>.FindAsync(http);
        return TypedResults.Ok(new BrandingInfo(s?.LogoData, s?.LogoContentType, s?.IconData, s?.IconContentType));
    }

    /// <summary>
    /// Updates the branding from a form with an optional <c>logo</c> and <c>icon</c> file, or with
    /// <c>removeLogo</c> and <c>removeIcon</c> set to <c>true</c>. A part that is not sent stays as it is.
    /// </summary>
    public static async Task<IResult> UpdateAsync(HttpContext http, AppKitSiteSettingsOptions options)
    {
        if (!http.Request.HasFormContentType)
            return Refused("Logo", SiteSettingsErrorCodes.NotAForm, "Send the images as a form.");
        var form = await http.Request.ReadFormAsync(http.RequestAborted);

        (string Data, string ContentType)? logo = null, icon = null;
        if (form["removeLogo"] != "true" && form.Files["logo"] is { } logoFile)
        {
            var (image, refusal) = await ReadImageAsync("Logo", logoFile, http.RequestAborted);
            if (refusal is not null)
                return refusal;
            logo = image;
        }
        if (form["removeIcon"] != "true" && form.Files["icon"] is { } iconFile)
        {
            var (image, refusal) = await ReadImageAsync("Icon", iconFile, http.RequestAborted);
            if (refusal is not null)
                return refusal;
            icon = image;
        }

        var (db, s) = await SiteSettingsHandlers<TContext, TSettings>.FindOrAddAsync(http);
        if (form["removeLogo"] == "true")
            (s.LogoData, s.LogoContentType) = (null, null);
        else if (logo is { } l)
            (s.LogoData, s.LogoContentType) = l;
        if (form["removeIcon"] == "true")
            (s.IconData, s.IconContentType) = (null, null);
        else if (icon is { } i)
            (s.IconData, s.IconContentType) = i;
        await db.SaveChangesAsync(http.RequestAborted);
        if (options.OnSaved is { } onSaved)
            await onSaved(new SiteSettingsSaved(s, SiteSettingsPart.Branding, http));
        return TypedResults.Ok(new BrandingInfo(s.LogoData, s.LogoContentType, s.IconData, s.IconContentType));
    }

    private const string ImageRule = "The image must be a PNG, JPEG, WebP, GIF or ICO file of at most 2 MB.";

    // Return a validation problem keyed by the part, like the settings errors, so the app can show the reason.
    private static ValidationProblem Refused(string field, string code, string message)
    {
        var problem = new SiteSettingsProblem();
        problem.Set(field, code, message);
        return problem.ToResult();
    }

    /// <summary>Reads an accepted image, or gives the refusal for the part named <paramref name="field"/>.</summary>
    private static async Task<((string Data, string ContentType) Image, ValidationProblem? Refusal)> ReadImageAsync(
        string field, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length > MaxImageBytes)
            return (default, Refused(field, SiteSettingsErrorCodes.ImageTooLarge, ImageRule));
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, cancellationToken);
        var bytes = ms.ToArray();
        // An empty file has no signature, so this refuses it.
        return ImageSignature.ContentType(bytes) is { } contentType
            ? ((Convert.ToBase64String(bytes), contentType), null)
            : (default, Refused(field, SiteSettingsErrorCodes.InvalidImage, ImageRule));
    }
}

/// <summary>
/// Detects an image's type from its first bytes, never from what the browser claims. SVG is not accepted,
/// because it can carry script.
/// </summary>
internal static class ImageSignature
{
    public static string? ContentType(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? "image/png"
        : bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? "image/jpeg"
        : bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8) ? "image/gif"
        : bytes.Length >= 6 && bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00]) && (bytes[4] | bytes[5]) != 0 ? "image/x-icon"
        : null;
}

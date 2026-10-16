using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SjolystInnovation.AppKit.Storage.Images;

/// <summary>The answer to an upload.</summary>
/// <param name="Id">The image id.</param>
/// <param name="Url">The public URL of the image, <see cref="AppKitContentImagesEndpointOptions.PublicBase"/> and the id.</param>
public sealed record UploadedContentImage(string Id, string Url);

/// <summary>An image's dimensions as displayed.</summary>
/// <param name="Id">The image id.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
public sealed record ContentImageDimensions(string Id, int Width, int Height);

/// <summary>The options for <see cref="ContentImageEndpointExtensions.MapAppKitContentImages{TContext}"/>.</summary>
public sealed class AppKitContentImagesEndpointOptions
{
    /// <summary>The authorization policy that uploading and deleting require. Defaults to <c>Admin</c>.</summary>
    public string Policy { get; set; } = "Admin";

    /// <summary>
    /// The path that the upload answer puts in front of the image id, where your site serves the images.
    /// Defaults to <c>/content-images</c>.
    /// </summary>
    public string PublicBase { get; set; } = "/content-images";
}

/// <summary>Maps the content image endpoints.</summary>
public static class ContentImageEndpointExtensions
{
    /// <summary>
    /// Maps the content image endpoints under <paramref name="prefix"/>. <c>GET {id}</c> and
    /// <c>GET dimensions</c> are open to everyone. <c>POST</c> and <c>DELETE {id}</c> require
    /// <see cref="AppKitContentImagesEndpointOptions.Policy"/>.
    /// </summary>
    /// <typeparam name="TContext">Your app's <c>DbContext</c>, which calls <see cref="ContentImagesModelBuilderExtensions.AddAppKitContentImages"/>.</typeparam>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">The route prefix. Defaults to <c>/api/content-images</c>.</param>
    /// <param name="configure">A callback that sets the endpoint options.</param>
    /// <returns>The route group, for further conventions.</returns>
    public static RouteGroupBuilder MapAppKitContentImages<TContext>(this IEndpointRouteBuilder endpoints,
        string prefix = "/api/content-images", Action<AppKitContentImagesEndpointOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new AppKitContentImagesEndpointOptions();
        configure?.Invoke(options);
        var publicBase = options.PublicBase.TrimEnd('/');

        var group = endpoints.MapGroup(prefix);
        group.MapGet("{id}", (string id, HttpContext http, int? w) => ContentImageHandlers<TContext>.GetAsync(id, w, http)).AllowAnonymous();
        group.MapGet("dimensions", (HttpContext http, string? ids) => ContentImageHandlers<TContext>.DimensionsAsync(ids, http)).AllowAnonymous();
        group.MapPost("", (IFormFile file, HttpContext http) => ContentImageHandlers<TContext>.UploadAsync(file, http, publicBase))
            .RequireAuthorization(options.Policy)
            .DisableAntiforgery();
        group.MapDelete("{id}", (string id, HttpContext http) => ContentImageHandlers<TContext>.DeleteAsync(id, http))
            .RequireAuthorization(options.Policy);
        return group;
    }
}

internal static class ContentImageHandlers<TContext>
    where TContext : DbContext
{
    // A new upload gets a new id, so an answer never changes.
    internal const string ImmutableCache = "public, max-age=31536000, immutable";
    // The original stands in until the renditions exist, so a browser asks again within the hour.
    internal const string ShortCache = "public, max-age=3600";
    // A page shows a handful of images. The cap only bounds a hostile request.
    internal const int MaxDimensionIds = 100;

    private static IAppKitFileStore Store(HttpContext http) =>
        http.RequestServices.GetRequiredKeyedService<IAppKitFileStore>(AppKitContentImages.StoreName);

    /// <summary>
    /// Serves an image. A browser that accepts WebP gets the smallest rendition at least <paramref name="w"/>
    /// pixels wide, or the largest. Renditions that are missing are written first. Any other browser gets the
    /// original.
    /// </summary>
    public static async Task<IResult> GetAsync(string id, int? w, HttpContext http)
    {
        var db = http.RequestServices.GetRequiredService<TContext>();
        var store = Store(http);
        var image = await db.Set<ContentImage>().AsNoTracking().Include(i => i.Variants).FirstOrDefaultAsync(i => i.Id == id, http.RequestAborted);
        if (image is null || !await store.ExistsAsync(image.StoredPath, http.RequestAborted))
            return TypedResults.NotFound();

        http.Response.Headers.Vary = "Accept";
        http.Response.Headers.XContentTypeOptions = "nosniff";

        var acceptsWebp = http.Request.Headers.Accept.ToString().Contains("image/webp", StringComparison.OrdinalIgnoreCase);
        if (acceptsWebp)
        {
            var variants = image.Variants.Count > 0
                ? image.Variants.ToList()
                : await ContentImageService<TContext>.EnsureVariantsAsync(db,
                    http.RequestServices.GetRequiredService<ImageProcessor>(),
                    http.RequestServices.GetRequiredService<VariantBackfill>(),
                    store, image.Id, image.StoredPath, http.RequestAborted);

            var chosen = PickVariant(variants, w);
            if (chosen is not null && await store.ExistsAsync(chosen.StoredPath, http.RequestAborted))
            {
                http.Response.Headers.CacheControl = ImmutableCache;
                return TypedResults.File(await store.OpenReadAsync(chosen.StoredPath, http.RequestAborted), "image/webp");
            }

            http.Response.Headers.CacheControl = ShortCache;
            return TypedResults.File(await store.OpenReadAsync(image.StoredPath, http.RequestAborted), image.ContentType);
        }

        http.Response.Headers.CacheControl = ImmutableCache;
        return TypedResults.File(await store.OpenReadAsync(image.StoredPath, http.RequestAborted), image.ContentType);
    }

    /// <summary>
    /// Gets the dimensions of several images in one request. An image with none recorded yet is measured
    /// here and the result kept. Images that cannot be decoded are left out.
    /// </summary>
    public static async Task<IResult> DimensionsAsync(string? ids, HttpContext http)
    {
        var requested = (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .Take(MaxDimensionIds)
            .ToList();
        if (requested.Count == 0)
            return TypedResults.Ok(Array.Empty<ContentImageDimensions>());

        var db = http.RequestServices.GetRequiredService<TContext>();
        var images = await db.Set<ContentImage>().Where(i => requested.Contains(i.Id)).ToListAsync(http.RequestAborted);

        var backfill = http.RequestServices.GetRequiredService<VariantBackfill>();
        var unmeasured = images.Where(i => (i.Width == 0 || i.Height == 0) && !backfill.HasFailed(i.Id)).ToList();
        if (unmeasured.Count > 0)
        {
            var store = Store(http);
            var processor = http.RequestServices.GetRequiredService<ImageProcessor>();
            var measured = 0;
            foreach (var image in unmeasured)
            {
                if (!await store.ExistsAsync(image.StoredPath, http.RequestAborted))
                    continue;
                var probe = await processor.ProbeAsync(image.StoredPath, http.RequestAborted);
                if (probe.Dimensions is not { } measurement)
                {
                    // Like a failed decode, a file no codec can read is not read again.
                    if (probe.Undecodable)
                        backfill.RememberFailure(image.Id);
                    continue;
                }
                (image.Width, image.Height) = measurement;
                measured++;
            }
            if (measured > 0)
                await db.SaveChangesAsync(http.RequestAborted);
        }

        return TypedResults.Ok(images
            .Where(i => i.Width > 0 && i.Height > 0)
            .Select(i => new ContentImageDimensions(i.Id, i.Width, i.Height))
            .ToArray());
    }

    /// <summary>
    /// Saves an uploaded image. A refused file answers a validation problem keyed by <c>file</c>, with the
    /// reason as the title too.
    /// </summary>
    public static async Task<IResult> UploadAsync(IFormFile file, HttpContext http, string publicBase)
    {
        var images = http.RequestServices.GetRequiredService<IContentImageService>();
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
        try
        {
            await using var content = file.OpenReadStream();
            var image = await images.CreateAsync(content, file.FileName, userId, http.RequestAborted);
            return TypedResults.Ok(new UploadedContentImage(image.Id, $"{publicBase}/{image.Id}"));
        }
        catch (AppKitFileRefusedException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [ex.Message] }, title: ex.Message);
        }
    }

    /// <summary>Deletes an image, its renditions, and their files.</summary>
    public static async Task<IResult> DeleteAsync(string id, HttpContext http)
    {
        var db = http.RequestServices.GetRequiredService<TContext>();
        var image = await db.Set<ContentImage>().Include(i => i.Variants).FirstOrDefaultAsync(i => i.Id == id, http.RequestAborted);
        if (image is null)
            return TypedResults.NotFound();
        await http.RequestServices.GetRequiredService<IContentImageService>().DeleteAsync(image, http.RequestAborted);
        return TypedResults.NoContent();
    }

    internal static ContentImageVariant? PickVariant(IReadOnlyCollection<ContentImageVariant> variants, int? w)
    {
        var ordered = variants.OrderBy(v => v.Width).ToList();
        if (ordered.Count == 0)
            return null;
        if (w is null)
            return ordered[^1];
        return ordered.FirstOrDefault(v => v.Width >= w) ?? ordered[^1];
    }
}

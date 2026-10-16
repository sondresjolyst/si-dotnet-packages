using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Storage.Images;

/// <summary>
/// The options for content images. <see cref="ImagesPath"/> and <see cref="MaxImageBytes"/> bind from the
/// same <c>Storage</c> keys the apps already use.
/// </summary>
public sealed class AppKitContentImagesOptions
{
    /// <summary>The directory the images and their renditions are kept in. Defaults to <c>/data/images</c>.</summary>
    public string ImagesPath { get; set; } = "/data/images";

    /// <summary>The largest upload in bytes. Defaults to 5 MB.</summary>
    public long MaxImageBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// The widths in pixels that renditions are made at, below the image's own width. Defaults to 384, 640,
    /// 768, 1024, 1366, and 1600.
    /// </summary>
    public IReadOnlyList<int> Widths { get; set; } = [384, 640, 768, 1024, 1366, 1600];

    /// <summary>The widest rendition in pixels. A wider image gets a rendition at this width as its largest. Defaults to 2000.</summary>
    public int MaxWidth { get; set; } = 2000;

    /// <summary>The WebP quality from 1 to 100. Defaults to 80.</summary>
    public int Quality { get; set; } = 80;

    /// <summary>
    /// The longest side in pixels that renditions are made from. A larger image is served as uploaded, which
    /// bounds the memory a decode takes. Defaults to 8000.
    /// </summary>
    public int MaxSourceDimension { get; set; } = 8000;

    /// <summary>
    /// The most pixels, width times height, that renditions are made from. A larger image is served as
    /// uploaded. Defaults to 40,000,000.
    /// </summary>
    public long MaxSourcePixels { get; set; } = 40_000_000;

    /// <summary>The most images decoded and encoded at the same time. Defaults to 2.</summary>
    public int MaxConcurrentDecodes { get; set; } = 2;
}

/// <summary>Creates and deletes content images with their files and renditions.</summary>
public interface IContentImageService
{
    /// <summary>
    /// Saves an upload, records its dimensions, writes its WebP renditions, and adds the rows. If saving the
    /// rows fails, the files are deleted again.
    /// </summary>
    /// <param name="content">The upload.</param>
    /// <param name="fileName">The client's file name.</param>
    /// <param name="userId">The id of the user who uploads the image.</param>
    /// <param name="cancellationToken">A token that cancels the upload.</param>
    /// <returns>The saved image with its renditions.</returns>
    /// <exception cref="AppKitFileRefusedException">When the file is empty, too large, or not a PNG, JPEG, WebP, GIF, or AVIF image.</exception>
    Task<ContentImage> CreateAsync(Stream content, string fileName, string? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an image and its renditions. The rows go first, so a failure leaves files that nothing
    /// points at instead of rows that point at nothing.
    /// </summary>
    /// <param name="image">The image to delete.</param>
    /// <param name="cancellationToken">A token that cancels the delete.</param>
    Task DeleteAsync(ContentImage image, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the images among <paramref name="ids"/> that no row references any more. Every foreign key
    /// to <see cref="ContentImage"/> in your model counts, apart from the renditions' own. Call it after
    /// saving the change that dropped the references.
    /// </summary>
    /// <param name="ids">The ids of images that may have lost their last reference.</param>
    /// <param name="cancellationToken">A token that cancels the cleanup.</param>
    /// <returns>The number of images deleted.</returns>
    Task<int> DeleteOrphansAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);
}

/// <summary>Registers the content image services.</summary>
public static class AppKitContentImages
{
    /// <summary>The key of the <see cref="IAppKitFileStore"/> that holds the images and their renditions.</summary>
    public const string StoreName = "content-images";

    /// <summary>
    /// Registers <see cref="IContentImageService"/> on <typeparamref name="TContext"/> and the image store, with
    /// <see cref="AppKitContentImagesOptions"/> bound from <paramref name="section"/>, usually <c>Storage</c>.
    /// </summary>
    /// <typeparam name="TContext">Your app's <c>DbContext</c>, which calls <see cref="ContentImagesModelBuilderExtensions.AddAppKitContentImages"/>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="section">The configuration section with <c>ImagesPath</c> and <c>MaxImageBytes</c>.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddAppKitContentImages<TContext>(this IServiceCollection services, IConfiguration section)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(section);
        return services.AddAppKitContentImages<TContext>(o =>
        {
            section.Bind(o);
            // The binder adds configured widths to the default list. Configured widths replace it instead.
            var widths = section.GetSection(nameof(AppKitContentImagesOptions.Widths));
            if (widths.Exists())
                o.Widths = widths.Get<int[]>() ?? [];
        });
    }

    /// <summary>
    /// Registers <see cref="IContentImageService"/> on <typeparamref name="TContext"/> and the image store. The
    /// app fails to start with a blank path, a size limit below 1 byte, or a quality outside 1 to 100.
    /// </summary>
    /// <typeparam name="TContext">Your app's <c>DbContext</c>, which calls <see cref="ContentImagesModelBuilderExtensions.AddAppKitContentImages"/>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">A callback that sets the options. Without it, the defaults apply.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddAppKitContentImages<TContext>(this IServiceCollection services, Action<AppKitContentImagesOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<AppKitContentImagesOptions>()
            .Configure(o => configure?.Invoke(o))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ImagesPath), "Storage:ImagesPath is required.")
            .Validate(o => o.MaxImageBytes > 0, "Storage:MaxImageBytes must be positive.")
            .Validate(o => o.Quality is >= 1 and <= 100, "The WebP quality must be from 1 to 100.")
            .Validate(o => o.MaxWidth > 0 && o.MaxSourceDimension > 0 && o.MaxSourcePixels > 0, "MaxWidth, MaxSourceDimension, and MaxSourcePixels must be positive.")
            .Validate(o => o.MaxConcurrentDecodes > 0, "MaxConcurrentDecodes must be positive.")
            .ValidateOnStart();
        services.AddAppKitFileStore(StoreName, _ => { });
        services.AddOptions<AppKitFileStoreOptions>(StoreName)
            .Configure<IOptions<AppKitContentImagesOptions>>((store, images) =>
            {
                store.Path = images.Value.ImagesPath;
                store.MaxBytes = images.Value.MaxImageBytes;
                store.Allowed = AppKitFileKinds.Images;
            });
        services.TryAddSingleton<ImageProcessor>();
        services.TryAddSingleton<VariantBackfill>();
        services.TryAddScoped<IContentImageService, ContentImageService<TContext>>();
        return services;
    }
}

internal sealed class ContentImageService<TContext>(
    TContext db,
    [FromKeyedServices(AppKitContentImages.StoreName)] IAppKitFileStore store,
    ImageProcessor processor,
    VariantBackfill backfill) : IContentImageService
    where TContext : DbContext
{
    private static readonly MethodInfo ReferencedMethod =
        typeof(ContentImageService<TContext>).GetMethod(nameof(ReferencedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public async Task<ContentImage> CreateAsync(Stream content, string fileName, string? userId, CancellationToken cancellationToken = default)
    {
        var stored = await store.SaveAsync(content, fileName, cancellationToken);
        var variants = new List<GeneratedVariant>();
        try
        {
            var dimensions = processor.Probe(stored.StoredPath);
            var generated = await processor.GenerateWebpVariantsAsync(stored.StoredPath, cancellationToken);
            variants.AddRange(generated.Variants);
            var image = new ContentImage
            {
                FileName = stored.FileName,
                ContentType = stored.ContentType,
                SizeBytes = stored.SizeBytes,
                StoredPath = stored.StoredPath,
                Width = dimensions?.Width ?? 0,
                Height = dimensions?.Height ?? 0,
                UploadedByUserId = userId,
            };
            foreach (var variant in variants)
                image.Variants.Add(Row(image.Id, variant));
            db.Set<ContentImage>().Add(image);
            await db.SaveChangesAsync(cancellationToken);
            if (generated.Undecodable)
                backfill.RememberFailure(image.Id);
            return image;
        }
        catch
        {
            // Without its row, nothing would ever find these files again.
            store.Delete(stored.StoredPath);
            foreach (var variant in variants)
                store.Delete(variant.StoredPath);
            throw;
        }
    }

    public async Task DeleteAsync(ContentImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var paths = await db.Set<ContentImageVariant>()
            .Where(v => v.ContentImageId == image.Id)
            .Select(v => v.StoredPath)
            .ToListAsync(cancellationToken);
        paths.AddRange(image.Variants.Select(v => v.StoredPath));
        paths.Add(image.StoredPath);

        db.Set<ContentImage>().Remove(image);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var path in paths.Distinct())
            store.Delete(path);
    }

    public async Task<int> DeleteOrphansAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var candidates = ids.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        if (candidates.Count == 0)
            return 0;

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var foreignKey in ReferencingForeignKeys(db.Model))
        {
            var dependent = foreignKey.DeclaringEntityType;
            var property = foreignKey.Properties.Single().Name;
            var query = (Task<List<string>>)ReferencedMethod.MakeGenericMethod(dependent.ClrType)
                .Invoke(null, [db, dependent, property, candidates, cancellationToken])!;
            referenced.UnionWith(await query);
        }

        var orphanIds = candidates.Where(id => !referenced.Contains(id)).ToList();
        if (orphanIds.Count == 0)
            return 0;
        var orphans = await db.Set<ContentImage>()
            .Include(i => i.Variants)
            .Where(i => orphanIds.Contains(i.Id))
            .ToListAsync(cancellationToken);
        if (orphans.Count == 0)
            return 0;

        // Rows first: a failure here leaves the files in place, which the next cleanup picks up. Deleting
        // the files first would leave rows that point at nothing.
        var paths = orphans.SelectMany(i => i.Variants.Select(v => v.StoredPath).Append(i.StoredPath)).ToList();
        db.Set<ContentImage>().RemoveRange(orphans);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var path in paths)
            store.Delete(path);
        return orphans.Count;
    }

    /// <summary>Finds the foreign keys that reference <see cref="ContentImage"/>, apart from the renditions' own.</summary>
    internal static IEnumerable<IForeignKey> ReferencingForeignKeys(IModel model)
    {
        var imageType = model.FindEntityType(typeof(ContentImage))
            ?? throw new InvalidOperationException("The DbContext does not map ContentImage. Call modelBuilder.AddAppKitContentImages().");
        foreach (var foreignKey in imageType.GetReferencingForeignKeys())
        {
            if (foreignKey.DeclaringEntityType.ClrType == typeof(ContentImageVariant))
                continue;
            if (foreignKey.DeclaringEntityType.IsOwned())
                throw new NotSupportedException($"{foreignKey.DeclaringEntityType.DisplayName()} is an owned type that references ContentImage. The orphan cleanup cannot query it.");
            yield return foreignKey;
        }
    }

    private static Task<List<string>> ReferencedAsync<TEntity>(DbContext db, IEntityType type, string property, List<string> ids, CancellationToken cancellationToken)
        where TEntity : class
    {
        var set = type.HasSharedClrType ? db.Set<TEntity>(type.Name) : db.Set<TEntity>();
        return set
            .Where(e => ids.Contains(EF.Property<string>(e, property)))
            .Select(e => EF.Property<string>(e, property))
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets an image's renditions and writes them first when it has none, as for images uploaded before
    /// renditions existed. An image that failed to decode before is not tried again.
    /// </summary>
    internal static async Task<List<ContentImageVariant>> EnsureVariantsAsync(
        DbContext db, ImageProcessor processor, VariantBackfill backfill, IAppKitFileStore store, string imageId, string storedPath, CancellationToken cancellationToken)
    {
        if (backfill.HasFailed(imageId))
            return [];
        using var _ = await backfill.LockAsync(imageId, cancellationToken);
        var generated = new List<GeneratedVariant>();
        var rows = new List<ContentImageVariant>();
        try
        {
            var existing = await db.Set<ContentImageVariant>().Where(v => v.ContentImageId == imageId).ToListAsync(cancellationToken);
            if (existing.Count > 0 || backfill.HasFailed(imageId))
                return existing;

            var result = await processor.GenerateWebpVariantsAsync(storedPath, cancellationToken);
            generated.AddRange(result.Variants);
            if (generated.Count == 0)
            {
                // Only a file that cannot be decoded is remembered. A passing error gets another try.
                if (result.Undecodable)
                    backfill.RememberFailure(imageId);
                return [];
            }
            rows.AddRange(generated.Select(g => Row(imageId, g)));
            db.Set<ContentImageVariant>().AddRange(rows);
            await db.SaveChangesAsync(cancellationToken);
            return rows;
        }
#pragma warning disable CA1031 // The original still serves. A failed backfill must not fail the request.
        catch (Exception)
#pragma warning restore CA1031
        {
            // No row records these renditions, so their files go too.
            foreach (var row in rows)
                db.Entry(row).State = EntityState.Detached;
            foreach (var variant in generated)
                store.Delete(variant.StoredPath);
            if (cancellationToken.IsCancellationRequested)
                throw;
            return [];
        }
    }

    private static ContentImageVariant Row(string imageId, GeneratedVariant variant) =>
        new() { ContentImageId = imageId, Width = variant.Width, StoredPath = variant.StoredPath, SizeBytes = variant.SizeBytes };
}

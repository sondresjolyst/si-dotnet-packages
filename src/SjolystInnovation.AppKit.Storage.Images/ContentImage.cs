using Microsoft.EntityFrameworkCore;

namespace SjolystInnovation.AppKit.Storage.Images;

/// <summary>An uploaded image that site content shows, such as a photo in a recipe or a gallery.</summary>
public class ContentImage
{
    /// <summary>The id, a GUID in its 32-character <c>N</c> format. The image URL ends with it.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The client's file name, at most 260 characters.</summary>
    public required string FileName { get; set; }

    /// <summary>The content type that the file's first bytes show.</summary>
    public required string ContentType { get; set; }

    /// <summary>The size of the original in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>The width in pixels as displayed, or 0 while unknown. Lets pages reserve space before the image loads.</summary>
    public int Width { get; set; }

    /// <summary>The height in pixels as displayed, or 0 while unknown.</summary>
    public int Height { get; set; }

    /// <summary>The name the file store keeps the original under.</summary>
    public required string StoredPath { get; set; }

    /// <summary>The id of the user who uploaded the image.</summary>
    public string? UploadedByUserId { get; set; }

    /// <summary>When the image was uploaded, in UTC.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The resized WebP renditions.</summary>
    public ICollection<ContentImageVariant> Variants { get; set; } = new List<ContentImageVariant>();
}

/// <summary>A resized WebP rendition of a <see cref="ContentImage"/> at one width, used for serving.</summary>
public class ContentImageVariant
{
    /// <summary>The id, a GUID in its 32-character <c>N</c> format.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The id of the image this is a rendition of.</summary>
    public required string ContentImageId { get; set; }

    /// <summary>The image this is a rendition of.</summary>
    public ContentImage? ContentImage { get; set; }

    /// <summary>The width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>The size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>The name the file store keeps the rendition under.</summary>
    public required string StoredPath { get; set; }
}

/// <summary>Maps the content image tables.</summary>
public static class ContentImagesModelBuilderExtensions
{
    /// <summary>
    /// Maps <see cref="ContentImage"/> to the <c>ContentImages</c> table and <see cref="ContentImageVariant"/>
    /// to <c>ContentImageVariants</c>, with the same columns, lengths, index, and cascading foreign key
    /// the apps already have. Your app keeps its own migrations.
    /// </summary>
    /// <param name="modelBuilder">The model builder of your app's <c>DbContext</c>.</param>
    /// <returns>The model builder.</returns>
    public static ModelBuilder AddAppKitContentImages(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ContentImage>(e =>
        {
            e.ToTable("ContentImages");
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).HasMaxLength(32);
            e.Property(i => i.FileName).IsRequired().HasMaxLength(260);
            e.Property(i => i.ContentType).IsRequired().HasMaxLength(100);
            e.Property(i => i.StoredPath).IsRequired().HasMaxLength(400);
            e.Property(i => i.UploadedByUserId).HasMaxLength(450);
        });
        modelBuilder.Entity<ContentImageVariant>(e =>
        {
            e.ToTable("ContentImageVariants");
            e.HasKey(v => v.Id);
            e.Property(v => v.Id).HasMaxLength(32);
            e.Property(v => v.ContentImageId).IsRequired().HasMaxLength(32);
            e.Property(v => v.StoredPath).IsRequired().HasMaxLength(400);
            e.HasOne(v => v.ContentImage)
                .WithMany(i => i.Variants)
                .HasForeignKey(v => v.ContentImageId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        return modelBuilder;
    }
}

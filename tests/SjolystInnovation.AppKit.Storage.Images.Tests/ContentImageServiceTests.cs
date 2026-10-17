using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SjolystInnovation.AppKit.Storage.Images.Tests;

public class ContentImageServiceTests
{
    private static Task<ContentImage> CreateAsync(TestApp app, byte[] file) =>
        app.WithServiceAsync((images, _) => images.CreateAsync(new MemoryStream(file), "photo.png", "u1"));

    [Fact]
    public async Task Create_deletes_the_files_again_when_the_rows_cannot_be_saved()
    {
        await using var app = await TestApp.StartAsync();
        app.Saves.Fail = true;

        await Assert.ThrowsAsync<DbUpdateException>(() => CreateAsync(app, TestImages.Png(800, 400)));

        Assert.Empty(app.Files());
    }

    [Fact]
    public async Task Create_saves_the_image_with_its_renditions_in_one_go()
    {
        await using var app = await TestApp.StartAsync();

        var image = await CreateAsync(app, TestImages.Png(800, 400));

        Assert.Equal(4, image.Variants.Count);
        Assert.Equal(4, await app.WithDbAsync(db => db.Set<ContentImageVariant>().CountAsync(v => v.ContentImageId == image.Id)));
        Assert.Equal(5, app.Files().Length);
    }

    [Fact]
    public async Task The_storage_usage_of_the_image_store_counts_the_images_and_their_renditions()
    {
        await using var app = await TestApp.StartAsync();
        await CreateAsync(app, TestImages.Png(800, 400));

        var usage = await app.Services.GetRequiredService<IAppKitStorageUsage>().GetAsync([AppKitContentImages.StoreName]);

        Assert.Equal(Directory.GetFiles(app.Root).Sum(f => new FileInfo(f).Length), usage.StorageUsedBytes);
        Assert.True(usage.StorageUsedBytes > 0);
        Assert.True(usage.DiskTotalBytes > 0);
    }

    [Fact]
    public async Task Delete_removes_an_image_loaded_without_its_renditions()
    {
        await using var app = await TestApp.StartAsync();
        var created = await CreateAsync(app, TestImages.Png(800, 400));

        await app.WithServiceAsync(async (images, db) =>
        {
            await images.DeleteAsync(await db.Set<ContentImage>().SingleAsync());
            return 0;
        });

        Assert.Equal(0, await app.WithDbAsync(db => db.Set<ContentImageVariant>().CountAsync()));
        Assert.Empty(app.Files());
        Assert.NotNull(created);
    }

    [Fact]
    public async Task DeleteOrphans_removes_an_image_nothing_points_at_with_its_files()
    {
        await using var app = await TestApp.StartAsync();
        var image = await CreateAsync(app, TestImages.Png(800, 400));

        var deleted = await app.WithServiceAsync((images, _) => images.DeleteOrphansAsync([image.Id, image.Id]));

        Assert.Equal(1, deleted);
        Assert.Equal(0, await app.WithDbAsync(db => db.Set<ContentImage>().CountAsync()));
        Assert.Equal(0, await app.WithDbAsync(db => db.Set<ContentImageVariant>().CountAsync()));
        Assert.Empty(app.Files());
    }

    [Fact]
    public async Task DeleteOrphans_keeps_images_that_a_required_or_an_optional_reference_still_shows()
    {
        await using var app = await TestApp.StartAsync();
        var inGallery = await CreateAsync(app, TestImages.Png(10, 10));
        var inStep = await CreateAsync(app, TestImages.Png(10, 10));
        var orphan = await CreateAsync(app, TestImages.Png(10, 10));
        await app.WithDbAsync(db =>
        {
            db.GalleryItems.Add(new GalleryItem { ContentImageId = inGallery.Id });
            db.RecipeSteps.Add(new RecipeStep { ContentImageId = inStep.Id });
            db.RecipeSteps.Add(new RecipeStep());
            return db.SaveChangesAsync();
        });

        var deleted = await app.WithServiceAsync((images, _) => images.DeleteOrphansAsync([inGallery.Id, inStep.Id, orphan.Id, "unknown"]));

        Assert.Equal(1, deleted);
        var left = await app.WithDbAsync(db => db.Set<ContentImage>().Select(i => i.Id).ToListAsync());
        Assert.Equal(new[] { inGallery.Id, inStep.Id }.Order(), left.Order());
    }

    [Fact]
    public async Task DeleteOrphans_ignores_an_empty_candidate_list()
    {
        await using var app = await TestApp.StartAsync();
        await CreateAsync(app, TestImages.Png(10, 10));

        Assert.Equal(0, await app.WithServiceAsync((images, _) => images.DeleteOrphansAsync([])));
        Assert.Equal(1, await app.WithDbAsync(db => db.Set<ContentImage>().CountAsync()));
    }

    [Fact]
    public async Task The_cleanup_checks_every_foreign_key_to_content_images_except_the_renditions()
    {
        await using var app = await TestApp.StartAsync();

        var checkedTypes = await app.WithDbAsync(db => Task.FromResult(
            ContentImageService<TestDbContext>.ReferencingForeignKeys(db.Model).Select(fk => fk.DeclaringEntityType.ClrType.Name).Order().ToList()));

        Assert.Equal(["GalleryItem", "RecipeStep"], checkedTypes);
    }

    /// <summary>The apps keep their tables. This mapping must match their model snapshots column for column.</summary>
    [Fact]
    public async Task The_mapping_matches_the_existing_tables()
    {
        await using var app = await TestApp.StartAsync();
        var model = await app.WithDbAsync(db => Task.FromResult(db.Model));

        var image = model.FindEntityType(typeof(ContentImage))!;
        Assert.Equal("ContentImages", image.GetTableName());
        Assert.Equal(["Id"], image.FindPrimaryKey()!.Properties.Select(p => p.Name));
        AssertColumn(image, "Id", maxLength: 32, nullable: false);
        AssertColumn(image, "FileName", maxLength: 260, nullable: false);
        AssertColumn(image, "ContentType", maxLength: 100, nullable: false);
        AssertColumn(image, "StoredPath", maxLength: 400, nullable: false);
        AssertColumn(image, "UploadedByUserId", maxLength: 450, nullable: true);
        AssertColumn(image, "SizeBytes", maxLength: null, nullable: false);
        AssertColumn(image, "Width", maxLength: null, nullable: false);
        AssertColumn(image, "Height", maxLength: null, nullable: false);
        AssertColumn(image, "CreatedAt", maxLength: null, nullable: false);
        Assert.Equal(9, image.GetProperties().Count());
        Assert.Empty(image.GetIndexes());
        Assert.Equal(ValueGenerated.Never, image.FindProperty("Id")!.ValueGenerated);

        var variant = model.FindEntityType(typeof(ContentImageVariant))!;
        Assert.Equal("ContentImageVariants", variant.GetTableName());
        AssertColumn(variant, "Id", maxLength: 32, nullable: false);
        AssertColumn(variant, "ContentImageId", maxLength: 32, nullable: false);
        AssertColumn(variant, "StoredPath", maxLength: 400, nullable: false);
        AssertColumn(variant, "Width", maxLength: null, nullable: false);
        AssertColumn(variant, "SizeBytes", maxLength: null, nullable: false);
        Assert.Equal(5, variant.GetProperties().Count());
        var index = Assert.Single(variant.GetIndexes());
        Assert.Equal(["ContentImageId"], index.Properties.Select(p => p.Name));
        Assert.False(index.IsUnique);
        Assert.Equal("IX_ContentImageVariants_ContentImageId", index.GetDatabaseName());
        var foreignKey = Assert.Single(variant.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        Assert.True(foreignKey.IsRequired);
        Assert.Equal("FK_ContentImageVariants_ContentImages_ContentImageId", foreignKey.GetConstraintName());
        Assert.Equal("Variants", foreignKey.PrincipalToDependent!.Name);
    }

    private static void AssertColumn(IEntityType type, string name, int? maxLength, bool nullable)
    {
        var property = type.FindProperty(name)!;
        Assert.Equal(name, property.GetColumnName());
        Assert.Equal(maxLength, property.GetMaxLength());
        Assert.Equal(nullable, property.IsNullable);
    }
}

public class VariantBackfillTests
{
    [Fact]
    public void Failures_are_remembered_up_to_the_bound_and_the_oldest_goes_first()
    {
        var backfill = new VariantBackfill();

        for (var i = 0; i <= VariantBackfill.MaxRemembered; i++)
            backfill.RememberFailure($"id{i}");

        Assert.False(backfill.HasFailed("id0"));
        Assert.True(backfill.HasFailed("id1"));
        Assert.True(backfill.HasFailed($"id{VariantBackfill.MaxRemembered}"));
    }

    [Fact]
    public async Task A_lock_is_exclusive_per_image_and_goes_away_when_released()
    {
        var backfill = new VariantBackfill();

        var first = await backfill.LockAsync("a", CancellationToken.None);
        var other = await backfill.LockAsync("b", CancellationToken.None);
        var second = backfill.LockAsync("a", CancellationToken.None);
        Assert.False(second.IsCompleted);
        Assert.Equal(2, backfill.LockCount);

        first.Dispose();
        first.Dispose();
        (await second).Dispose();
        other.Dispose();

        Assert.Equal(0, backfill.LockCount);
    }

    [Fact]
    public async Task A_canceled_wait_releases_its_hold_on_the_lock()
    {
        var backfill = new VariantBackfill();
        var held = await backfill.LockAsync("a", CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var waiting = backfill.LockAsync("a", cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        held.Dispose();

        Assert.Equal(0, backfill.LockCount);
    }
}

# SjolystInnovation.AppKit.Storage.Images

This package handles the images that site content shows, such as photos in recipes and galleries. It
saves uploads, records their dimensions, writes resized WebP renditions, and serves the best fit for each
browser with long-lived cache headers. It works on the `ContentImages` and `ContentImageVariants` tables
your app already has, and it deletes images that nothing references any more.

```sh
dotnet add package SjolystInnovation.AppKit.Storage.Images
```

The package depends on SkiaSharp and on its Linux native assets without dependencies, so it runs in a
slim Linux container as well as on Windows and macOS.

## Setup

Your `DbContext` maps the tables, and your app's own entities reference `ContentImage`.

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.AddAppKitContentImages();
    modelBuilder.Entity<RecipeImage>()
        .HasOne(i => i.ContentImage).WithMany().HasForeignKey(i => i.ContentImageId)
        .OnDelete(DeleteBehavior.Cascade);
}

builder.Services.AddAppKitContentImages<ApplicationDbContext>(builder.Configuration.GetSection("Storage"));

app.MapAppKitContentImages<ApplicationDbContext>("/api/content-images", o => o.Policy = Policies.Admin);
```

The mapping keeps the columns, lengths, index, and cascading foreign key that the apps have today, so a
new migration for an app that already has `Width` and `Height` on `ContentImages` is empty. Your app keeps
its own migrations.

| Option | Default | |
| --- | --- | --- |
| `ImagesPath` | `/data/images` | The directory for the originals and their renditions. |
| `MaxImageBytes` | 5 MB | The largest upload. |
| `Widths` | 384, 640, 768, 1024, 1366, 1600 | The rendition widths below the image's own width. |
| `MaxWidth` | 2000 | The widest rendition. A wider image gets one at this width as its largest. |
| `Quality` | 80 | The WebP quality from 1 to 100. |
| `MaxSourceDimension` | 8000 | The longest side that renditions are made from. A larger image is served as uploaded. |
| `MaxSourcePixels` | 40,000,000 | The most pixels that renditions are made from. A larger image is served as uploaded. |
| `MaxConcurrentDecodes` | 2 | The most images decoded and encoded at the same time, which bounds the memory they take. |

`ImagesPath` and `MaxImageBytes` bind from the same `Storage` keys the apps use today. The image store is
also a keyed `IAppKitFileStore` named `AppKitContentImages.StoreName`.

## Endpoints

| Route | Who | Answer |
| --- | --- | --- |
| `GET {id}?w=` | Everyone | The image. See the next section. |
| `GET dimensions?ids=` | Everyone | `id`, `width`, and `height` for up to 100 comma-separated ids. An image without recorded dimensions is measured once and the result kept. Unknown ids and images that cannot be decoded are left out. |
| `POST` with a form `file` | `Policy` | Saves the image and answers `{ id, url }`. The URL is `PublicBase` and the id. A refused file answers a validation problem keyed by `file`, with the reason as the title too. |
| `DELETE {id}` | `Policy` | Deletes the image, its renditions, and their files, and answers `204`. |

| Option | Default | |
| --- | --- | --- |
| `Policy` | `Admin` | What uploading and deleting require. |
| `PublicBase` | `/content-images` | The path in front of the id in the upload answer, where your site serves the images. |

## Serving

Every image answer has `Vary: Accept` and `X-Content-Type-Options: nosniff`.

- If the browser accepts `image/webp`, it gets the smallest rendition at least `w` pixels wide, or the
  largest rendition when none is that wide or `w` is missing. The answer is cached for a year as
  `immutable`, because a new upload always gets a new id.
- If an image has no renditions yet, as for images from before renditions existed, the first WebP request
  writes them. Until then, and for an image that cannot be decoded, the original is cached for one hour, so
  the browser can pick up the renditions later.
- Any other browser gets the original, cached for a year as `immutable`.

An image that cannot be decoded, or is over `MaxSourceDimension` or `MaxSourcePixels`, is remembered, so
later requests do not decode or measure it again. The package remembers the last 1024 such images. A
passing error, such as a failed write or a failed database save, is not remembered, and its renditions
are deleted again, so the next request tries once more.

## Images

Uploads are PNG, JPEG, WebP, GIF, or AVIF, told apart by their first bytes. Dimensions and renditions
follow the EXIF orientation, all eight of them, so a portrait photo from a phone is stored as portrait.
Widths count as displayed, so a phone photo taken upright still gets a rendition `MaxWidth` pixels wide.
A JPEG decodes at a reduced size when it is much wider than `MaxWidth`. A PNG or GIF decodes at full size
and is then resized. If a width fails, the renditions already written are deleted again.

## The service

`IContentImageService` is for your own endpoints, such as a form that saves a cover image.

- `CreateAsync` saves an upload, records its dimensions, writes its renditions, and adds the rows in one
  save. If that save fails, it deletes the files again.
- `DeleteAsync` deletes an image's rows first and its files after, so a failure leaves stray files rather
  than rows that point at nothing.
- `DeleteOrphansAsync` takes the ids of images that may have lost their last reference and deletes the
  ones that no row references. It checks every foreign key to `ContentImage` in your model, apart from
  the renditions' own, so a new table that references images needs no change here. Call it after saving
  the change that dropped the references.

`DeleteOrphansAsync` does not support owned EF types that reference `ContentImage`. It throws
`NotSupportedException` for them. It also checks the references first and deletes after, in separate
queries. A reference that another request adds between the check and the delete can lose its image. The
apps edit content as a single admin, so that window is acceptable there.

## Storage usage

The images live in the file store `AppKitContentImages.StoreName`, so `IAppKitStorageUsage` from
`SjolystInnovation.AppKit.Storage` measures them, together with any other store you name:

```csharp
var usage = await storageUsage.GetAsync([AppKitContentImages.StoreName]);
```

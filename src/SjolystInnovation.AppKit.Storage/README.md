# SjolystInnovation.AppKit.Storage

This package keeps uploaded files in directories on local disk for ASP.NET Core apps, such as reports,
invoices, or icons on a persistent volume. Use it when your app accepts uploads and must not trust
what the browser says about them. Each store has a name, a directory, a size limit, and the kinds of
file it takes. A store tells a file's kind from its first bytes, never from the content type the
browser sends, and it serves files back with a correctly encoded `Content-Disposition` header.

```sh
dotnet add package SjolystInnovation.AppKit.Storage
```

## Get started

Register a store per kind of upload and resolve it by name as a keyed service. Keep the values of the
`StoredFile` that `SaveAsync` returns in your own row.

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SjolystInnovation.AppKit.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddAppKitFileStore("reports", o =>
{
    o.Path = builder.Configuration["Storage:ReportsPath"]!;
    o.MaxBytes = 50 * 1024 * 1024;
    o.Allowed = AppKitFileKinds.Pdf;
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/api/reports", async (IFormFile file, [FromKeyedServices("reports")] IAppKitFileStore reports,
    ApplicationDbContext db, CancellationToken cancellationToken) =>
{
    try
    {
        await using var content = file.OpenReadStream();
        var stored = await reports.SaveAsync(content, file.FileName, cancellationToken);
        var report = new Report
        {
            StoredPath = stored.StoredPath,
            FileName = stored.FileName,
            ContentType = stored.ContentType,
            SizeBytes = stored.SizeBytes,
        };
        db.Reports.Add(report);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { report.Id });
    }
    catch (AppKitFileRefusedException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [ex.Message] });
    }
}).RequireAuthorization().DisableAntiforgery();

app.MapGet("/api/reports/{id:int}", async (int id, [FromKeyedServices("reports")] IAppKitFileStore reports,
    ApplicationDbContext db, CancellationToken cancellationToken) =>
    await db.Reports.FindAsync([id], cancellationToken) is { } report
        ? await AppKitFiles.ServeAsync(reports, report.StoredPath, report.FileName, report.ContentType,
            inline: true, cancellationToken: cancellationToken)
        : Results.NotFound()).RequireAuthorization();

app.Run();

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<Report> Reports => Set<Report>();
}

public class Report
{
    public int Id { get; set; }
    public required string StoredPath { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
}
```

The app fails to start if a store has no `Path`, a `MaxBytes` below 1, or no allowed kinds.

| Option | Default | Meaning |
| --- | --- | --- |
| `Path` | None | The directory for the files. The store creates it. |
| `MaxBytes` | 5 MB | The largest file the store takes. |
| `Allowed` | None | The kinds the store takes, as `AppKitFileKinds` flags. `AppKitFileKinds.Images` is PNG, JPEG, WebP, GIF, and AVIF. |

## Save a file

`SaveAsync` reads the stream to the end and returns a `StoredFile` with these values:

- `StoredPath` is a new name, a GUID and the extension of the kind, such as `.pdf`.
- `ContentType` comes from the file's first bytes.
- `SizeBytes` is the number of bytes written.
- `FileName` is the client's file name without any directory, cut to 260 characters.

The store recognizes PNG, JPEG, WebP, GIF, AVIF, PDF, and ICO. It refuses an empty file, a file over
`MaxBytes`, and any other kind with an `AppKitFileRefusedException`. The message is safe to show to
the client, for example `Only PDF files are accepted.` A refused or canceled save leaves no file
behind.

## Read and delete a file

`OpenReadAsync`, `ExistsAsync`, and `DeleteAsync` take the stored path. They are async so that a store
on something other than local disk can implement them. Only the last segment of the path counts, and
the result must lie inside the store's directory, so a stored path such as `../../etc/passwd` cannot
reach another directory. `DeleteAsync` on a file that is gone does nothing.

## Serve a file

`AppKitFiles.ServeAsync` answers with the file, or with `404` when the store does not have it. Set
`inline` to `false` to make the browser download the file instead of showing it.

```csharp
return await AppKitFiles.ServeAsync(reports, report.StoredPath, report.FileName, report.ContentType,
    inline: true, cancellationToken: http.RequestAborted);
```

The answer has `X-Content-Type-Options: nosniff` and a `Content-Disposition` of `inline` or
`attachment`. Every type other than a raster image, PDF and SVG included, also gets
`Content-Security-Policy: sandbox`, so a document shown inline cannot run script on your site. The
file name goes in as plain ASCII `filename` and, when it has other letters such as `ø`, also as UTF-8
`filename*`. Quotes and line breaks in the name cannot break the header.

## Storage usage

`IAppKitStorageUsage` measures named stores for an admin page. `AddAppKitFileStore` registers it.

```csharp
var usage = await storageUsage.GetAsync(["reports", "invoices"]);
// { "storageUsedBytes": 73400320, "diskTotalBytes": 10737418240, "diskFreeBytes": 8589934592 }
```

| Field | Meaning |
| --- | --- |
| `StorageUsedBytes` | Every file in the stores, subdirectories included. A store whose directory does not exist yet counts as 0, and a store inside another listed one counts once. |
| `DiskTotalBytes` | The size of the disk that holds the first store. |
| `DiskFreeBytes` | The space free on that disk for the app. |

The disk is the mount point that holds the store's directory, so a volume mounted at `/data` reports
its own size, not the container's root file system. If the disk cannot be read, both disk fields are
0 and the service logs a warning. A name that no store was registered under throws
`InvalidOperationException`.

## Links

- [Source code](https://github.com/sondresjolyst/si-dotnet-packages/tree/main/src/SjolystInnovation.AppKit.Storage)
- [Changelog](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/src/SjolystInnovation.AppKit.Storage/CHANGELOG.md)
- [All AppKit packages](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/README.md)

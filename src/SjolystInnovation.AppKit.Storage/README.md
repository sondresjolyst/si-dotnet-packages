# SjolystInnovation.AppKit.Storage

This package keeps uploaded files in directories on local disk for ASP.NET Core apps. Each store has a
name, a directory, a size limit, and the kinds of file it takes. A store tells a file's kind from its
first bytes, never from the content type the browser sends, and it serves files back with a correctly
encoded `Content-Disposition` header.

```sh
dotnet add package SjolystInnovation.AppKit.Storage
```

## Setup

Register a store per kind of upload and resolve it by name.

```csharp
builder.Services.AddAppKitFileStore("reports", o =>
{
    o.Path = builder.Configuration["Storage:ReportsPath"]!;
    o.MaxBytes = 50 * 1024 * 1024;
    o.Allowed = AppKitFileKinds.Pdf;
});

app.MapPost("/api/reports", async (IFormFile file, [FromKeyedServices("reports")] IAppKitFileStore reports) =>
{
    try
    {
        await using var content = file.OpenReadStream();
        var stored = await reports.SaveAsync(content, file.FileName);
        // Keep stored.StoredPath, stored.FileName, stored.ContentType, and stored.SizeBytes in your row.
        return Results.Ok();
    }
    catch (AppKitFileRefusedException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [ex.Message] });
    }
});
```

The app fails to start if a store has no `Path`, a `MaxBytes` below 1, or no allowed kinds.

| Option | Default | |
| --- | --- | --- |
| `Path` | None | The directory for the files. The store creates it. |
| `MaxBytes` | 5 MB | The largest file the store takes. |
| `Allowed` | None | The kinds the store takes. `AppKitFileKinds.Images` is PNG, JPEG, WebP, GIF, and AVIF. |

## Saving

`SaveAsync` reads the stream to the end and returns a `StoredFile` with these values:

- `StoredPath` is a new name, a GUID and the extension of the kind, such as `.pdf`.
- `ContentType` comes from the file's first bytes.
- `SizeBytes` is the number of bytes written.
- `FileName` is the client's file name without any directory, cut to 260 characters.

The store recognizes PNG, JPEG, WebP, GIF, AVIF, PDF, and ICO. It refuses an empty file, a file over
`MaxBytes`, and any other kind with an `AppKitFileRefusedException`. The message is safe to show to the
client, for example `Only PDF files are accepted.` A refused or canceled save leaves no file behind.

## Reading and deleting

`OpenRead`, `Exists`, and `Delete` take the stored path. Only its last segment counts, and the result must
lie inside the store's directory, so a stored path such as `../../etc/passwd` cannot reach another
directory. `Delete` on a file that is gone does nothing.

## Serving

`AppKitFiles.Serve` answers with the file, or with `404` when the store does not have it.

```csharp
return AppKitFiles.Serve(reports, report.StoredPath, report.FileName, report.ContentType, inline: true);
```

The answer has `X-Content-Type-Options: nosniff` and a `Content-Disposition` of `inline` or
`attachment`. Every type other than a raster image, PDF and SVG included, also gets
`Content-Security-Policy: sandbox`, so a document shown inline cannot run script on your site. The file name goes in as plain ASCII `filename` and, when it has other letters such as `ø`,
also as UTF-8 `filename*`. Quotes and line breaks in the name cannot break the header.

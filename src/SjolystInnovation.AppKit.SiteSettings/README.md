# SjolystInnovation.AppKit.SiteSettings

This package handles admin-managed company details, site branding, and legal pages for ASP.NET Core
apps. Your app keeps one settings row of its own, and this package maps the endpoints that read and
change the shared part of it: the public company info, the admin settings, and the logo and icon. The
terms, privacy, and cookie pages are Markdown that an admin edits per language.

```sh
dotnet add package SjolystInnovation.AppKit.SiteSettings
```

## Setup

Your app's settings entity implements `ICompanySettings` and `IBrandingSettings` next to any settings
of its own. The row has the key 1. Before the row exists, reads answer with the entity's defaults.

```csharp
public class AppSettings : ICompanySettings, IBrandingSettings
{
    public int Id { get; set; } = 1;
    public string CompanyName { get; set; } = "Pyttogpanne";
    // The other company fields, the logo and icon, and the app's own settings.
}

app.MapAppKitSiteSettings<ApplicationDbContext, AppSettings>("/api", o => o.Policy = Policies.Admin);
```

## Endpoints

| Route | Who | Answer |
| --- | --- | --- |
| `GET company` | Everyone | The name, legal name, organization number, VAT status, the address as one line and as parts, and the public email and phone. While the legal name is blank, the company name stands in for it. The contact recipient stays private. |
| `GET settings` | `Policy` | The company fields as stored. |
| `PUT settings` | `Policy` | Saves the company fields, trimmed, and creates the row on the first write. A bad field gives a validation problem keyed by field. |
| `GET branding` | Everyone | `logoData`, `logoContentType`, `iconData`, and `iconContentType`, base64 and `null` while unset. |
| `PUT branding` | `Policy` | A form with an optional `logo` and `icon` file, or `removeLogo` and `removeIcon` set to `true`. A part that is not sent stays as it is. |

`Policy` is `Admin` by default.

### Hooks

Two options let your app act on a save:

- `OnSaving` runs after a settings change is applied to the row and before it is saved. It gets the row,
  the values the row held before, and the request. Use it to keep your own columns in step with the
  company fields, such as the date VAT registration started. If it throws, nothing is saved.
- `OnSaved` runs after the settings or the branding are saved. Use it to clear a cache of the row.

```csharp
app.MapAppKitSiteSettings<ApplicationDbContext, AppSettings>("/api", o =>
{
    o.OnSaving = saving =>
    {
        if (saving.Settings.VatRegistered && !saving.Previous.VatRegistered)
            ((AppSettings)saving.Settings).VatRegisteredFrom = DateOnly.FromDateTime(DateTime.UtcNow);
        return Task.CompletedTask;
    };
    o.OnSaved = http => { http.RequestServices.GetRequiredService<IAppSettingsCache>().Invalidate(); return Task.CompletedTask; };
});
```

## Rules

- The contact recipient and the public email must be email addresses of at most 200 characters.
- The company name, street address, and public phone are required.
- The organization number is nine digits with a modulus 11 check digit, spaces allowed. It may be
  blank until the business is registered, but not while it is VAT registered.
- A postal code is four digits and needs the place that goes with it. Neither is required.
- A logo or icon is a PNG, JPEG, WebP, GIF, or ICO file of at most 2 MB. Its type comes from its first
  bytes, never from what the browser claims. SVG is refused, because it can carry script. A refused
  image answers a validation problem keyed by `Logo` or `Icon` with the reason, and nothing is saved.

`OrgNumber.IsValid`, `PostalAddress.IsPostalCode`, and `PostalAddress.Format` are public for your
app's own use, such as invoices and structured data.

## Legal pages

Your app's `DbContext` maps the `LegalPages` table with `AddAppKitLegalPages`. At startup,
`SeedAppKitLegalPagesAsync` seeds each page's starting text. Seeding writes only the pages and
languages that have no text yet, so an admin's edits stay.

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddAppKitLegalPages();

app.MapAppKitLegalPages<ApplicationDbContext>("/api/content/legal", o => o.Locales = ["no", "en"]);
await app.Services.SeedAppKitLegalPagesAsync<ApplicationDbContext>(
[
    new LegalPageSeed("terms", "no", "Vilkår", termsMarkdown),
]);
```

| Route | Who | Answer |
| --- | --- | --- |
| `GET {key}?locale=` | Everyone | The page in the requested language, or in the first of `Locales` when it has none there. `404` for a page that is not in `Keys` or has no text. |
| `GET` | `Policy` | Every page in every language, by key and then language. |
| `PUT {key}/{locale}` with `title`, `bodyMarkdown` | `Policy` | Saves the page, creating it the first time. An unknown key or language answers `400` with the reason as `detail`. A missing title or body, or a title over 200 characters, is a validation problem. |

| Option | Default | |
| --- | --- | --- |
| `Keys` | `terms`, `privacy`, `cookies` | The pages there are. |
| `Locales` | `no` | The languages a page can be written in. The first is the fallback. |
| `Policy` | `Admin` | What listing and saving require. |

The requested language matches a supported one exactly, then by its primary language, so `en-GB`
reads `en`. `nb` and `nn` read as `no`. Keys and languages are stored in lowercase.

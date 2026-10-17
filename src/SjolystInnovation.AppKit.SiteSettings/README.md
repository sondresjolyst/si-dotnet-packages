# SjolystInnovation.AppKit.SiteSettings

This package handles admin-managed company details, site branding, legal pages, and page sections for
ASP.NET Core apps. Use it when an admin, not a developer, keeps the company name, address, logo,
terms, and home page text up to date.

- Your app keeps one settings row of its own. The package maps the endpoints that read and change the
  shared part of it: the public company info, the admin settings, and the logo and icon.
- The terms, privacy, and cookie pages are Markdown that an admin edits per language.
- The sections of pages such as the home page are a JSON array that an admin edits per language.

Every refused field answers a validation problem with a stable code, so your frontend can show its
own text in its own language.

```sh
dotnet add package SjolystInnovation.AppKit.SiteSettings
```

## Get started

Your app's settings entity implements `ICompanySettings` and `IBrandingSettings` next to any settings
of its own. The row has the key 1. Before the row exists, reads answer with the entity's defaults. The
endpoints that change data require the `Admin` authorization policy unless you set another one.

```csharp
using Microsoft.EntityFrameworkCore;
using SjolystInnovation.AppKit.SiteSettings;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddAuthentication();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole("Admin"));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapAppKitSiteSettings<ApplicationDbContext, AppSettings>("/api");
app.MapAppKitLegalPages<ApplicationDbContext>("/api/content/legal", o => o.Locales = ["no", "en"]);
app.MapAppKitPageSections<ApplicationDbContext>("/api/content", o => o.Locales = ["no", "en"]);

await app.Services.SeedAppKitLegalPagesAsync<ApplicationDbContext>(
[
    new LegalPageSeed("terms", "no", "Vilkår", "Vilkårene for bruk av nettstedet."),
    new LegalPageSeed("privacy", "no", "Personvern", "Slik behandler vi personopplysninger."),
]);

app.Run();

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<AppSettings> Settings => Set<AppSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddAppKitLegalPages();
        modelBuilder.AddAppKitPageSections();
    }
}

public class AppSettings : ICompanySettings, IBrandingSettings
{
    public int Id { get; set; } = 1;
    public string ContactRecipientEmail { get; set; } = "post@example.com";
    public string CompanyName { get; set; } = "Example";
    public string CompanyLegalName { get; set; } = "";
    public string OrgNumber { get; set; } = "";
    public bool VatRegistered { get; set; }
    public string StreetAddress { get; set; } = "Storgata 1";
    public string PostalCode { get; set; } = "";
    public string AddressLocality { get; set; } = "";
    public string AddressRegion { get; set; } = "";
    public string PublicEmail { get; set; } = "post@example.com";
    public string PublicPhone { get; set; } = "";
    public string? LogoData { get; set; }
    public string? LogoContentType { get; set; }
    public string? IconData { get; set; }
    public string? IconContentType { get; set; }
}
```

| Call | What it does |
| --- | --- |
| `MapAppKitSiteSettings<TContext, TSettings>` | Maps the company info, settings, and branding endpoints. |
| `AddAppKitLegalPages` | Maps the `LegalPages` table in `OnModelCreating`. |
| `MapAppKitLegalPages<TContext>` | Maps the legal page endpoints. |
| `SeedAppKitLegalPagesAsync<TContext>` | Writes the starting text of the legal pages that have none. |
| `AddAppKitPageSections` | Maps the `PageSections` table in `OnModelCreating`. |
| `MapAppKitPageSections<TContext>` | Maps the page sections endpoints. |

## Settings and branding

`MapAppKitSiteSettings` maps these routes under its prefix, which defaults to `/api`:

| Route | Who | What it does |
| --- | --- | --- |
| `GET company` | Everyone | Answers the name, legal name, organization number, VAT status, the address as one line and as parts, and the public email and phone. While the legal name is blank, the company name stands in for it. The contact recipient stays private. |
| `GET settings` | `Policy` | Answers the company fields as stored. |
| `PUT settings` | `Policy` | Saves the company fields, trimmed, and creates the row on the first write. A bad field answers a validation problem keyed by field, with a code for each error. |
| `GET branding` | Everyone | Answers `logoData`, `logoContentType`, `iconData`, and `iconContentType`, base64 and `null` while unset. |
| `PUT branding` | `Policy` | Takes a form with an optional `logo` and `icon` file, or `removeLogo` and `removeIcon` set to `true`. A part that is not sent stays as it is. A refused file answers a validation problem keyed by the part, with a code. |

| Option | Default | Meaning |
| --- | --- | --- |
| `Policy` | `Admin` | The authorization policy for reading the settings and changing the settings and branding. |
| `OnSaving` | None | Runs after a settings change is applied to the row and before it is saved. |
| `OnSaved` | None | Runs after the settings or the branding are saved. |

### Hooks

The two hooks let your app act on a save:

- `OnSaving` gets the row, the values the row held before, and the request. Use it to keep your own
  columns in step with the company fields, such as the date VAT registration started. If it throws,
  nothing is saved.
- `OnSaved` gets the saved row, the part that changed as `SiteSettingsPart.Settings` or
  `SiteSettingsPart.Branding`, and the request. Use it to clear a cache of the row.

```csharp
app.MapAppKitSiteSettings<ApplicationDbContext, AppSettings>("/api", o =>
{
    o.OnSaving = saving =>
    {
        if (saving.Settings.VatRegistered && !saving.Previous.VatRegistered)
            ((AppSettings)saving.Settings).VatRegisteredFrom = DateOnly.FromDateTime(DateTime.UtcNow);
        return Task.CompletedTask;
    };
    o.OnSaved = saved =>
    {
        saved.Http.RequestServices.GetRequiredService<IAppSettingsCache>().Invalidate();
        return Task.CompletedTask;
    };
});
```

### Contact recipient

`ContactRecipientEmail` is the private address that your app's contact form sends enquiries to. An
admin sets it with the other company fields, and it is required. It stays private: `GET company`
never shows it, and only `GET settings` behind `Policy` does. Show `PublicEmail` on the site instead.

The package sends no email itself. Your contact form endpoint reads the address from the row:

```csharp
var settings = await db.AppSettings.SingleAsync(ct);
await email.SendAsync(new EmailMessage(settings.ContactRecipientEmail, subject, html) { ReplyTo = enquiry.Email }, ct);
```

### Rules

`PUT settings` checks these rules and reports every field that breaks one.

| Field | Rule |
| --- | --- |
| `ContactRecipientEmail` | Required. An email address of at most 200 characters. Never shown publicly. |
| `CompanyName` | Required. At most 100 characters. |
| `CompanyLegalName` | Optional. At most 200 characters. |
| `OrgNumber` | Optional until the business is VAT registered. Nine digits with a modulus 11 check digit, spaces allowed. |
| `StreetAddress` | Required. At most 200 characters. |
| `PostalCode` | Optional. Four digits. |
| `AddressLocality` | Required when a postal code is given. At most 100 characters. |
| `AddressRegion` | Optional. At most 100 characters. |
| `PublicEmail` | Required. An email address of at most 200 characters. |
| `PublicPhone` | Optional. At most 40 characters. While it is blank, `GET company` answers an empty `phone`. |

A logo or icon is a PNG, JPEG, WebP, GIF, or ICO file of at most 2 MB. Its type comes from its first
bytes, never from what the browser claims. SVG is refused, because it can carry script. A refused
image answers a validation problem keyed by `Logo` or `Icon` with the reason and its code, and
nothing is saved.

`NorwegianOrgNumber.IsValid`, `PostalAddress.IsPostalCode`, and `PostalAddress.Format` are public for
your app's own use, such as invoices and structured data.

## Error codes

A bad field in `PUT settings`, a refused file in `PUT branding`, and a bad title or body when saving a
legal page answer a validation problem. Next to the English messages in `errors`, it has two
extensions keyed by the same fields:

- `codes` holds a stable code for each message, so a client can show its own text in its own
  language.
- `limits` holds the character limit for each field whose code is `tooLong`. It is empty otherwise.

```json
{
  "errors": {
    "OrgNumber": ["Not a valid organisation number. It has nine digits and ends in a check digit."],
    "CompanyName": ["At most 100 characters."]
  },
  "codes": { "OrgNumber": ["invalidOrgNumber"], "CompanyName": ["tooLong"] },
  "limits": { "CompanyName": 100 }
}
```

| Code | Meaning |
| --- | --- |
| `required` | The field is blank and must have a value. |
| `tooLong` | The text is longer than the limit in `limits`. |
| `invalidEmail` | Not a plain email address of at most 200 characters. |
| `orgNumberRequiredForVat` | The organization number is blank while the business is VAT registered. |
| `invalidOrgNumber` | The organization number is not nine digits with a correct check digit. |
| `invalidPostalCode` | The postal code is not four digits. |
| `placeRequired` | A postal code is given without its place name. |
| `invalidImage` | The logo or icon is not a PNG, JPEG, WebP, GIF, or ICO file, judged by its bytes. An empty file counts as not an image. |
| `imageTooLarge` | The logo or icon is larger than 2 MB. |
| `notAForm` | `PUT branding` got a body that is not a form. The error is keyed by `Logo`. |

Saving a legal page uses `required` for a blank `Title` or `BodyMarkdown`, and `tooLong` for a
`Title` over 200 characters.

The codes are also constants on `SiteSettingsErrorCodes`, such as `SiteSettingsErrorCodes.TooLong`.
New codes may come in a minor version, so a client shows the message from `errors` for a code it does
not know.

## Legal pages

Your app's `DbContext` maps the `LegalPages` table with `AddAppKitLegalPages`. At startup,
`SeedAppKitLegalPagesAsync` seeds each page's starting text. Seeding writes only the pages and
languages that have no text yet, so an admin's edits stay. It stores keys and locales in lowercase,
the form the endpoints look pages up by. It does not read the endpoint options, so seed only keys and
locales that are in `Keys` and `Locales`.

`MapAppKitLegalPages` maps these routes under its prefix, which defaults to `/api/content/legal`. A
page answers `key`, `locale`, `title`, `bodyMarkdown`, and `updatedAt`.

| Route | Who | What it does |
| --- | --- | --- |
| `GET {key}?locale=` | Everyone | Answers the page in the requested language, or in the first of `Locales` when it has none there. Answers `404` for a page that is not in `Keys` or has no text. |
| `GET` | `Policy` | Answers every page in every language, by key and then language. |
| `PUT {key}/{locale}` with `title` and `bodyMarkdown` | `Policy` | Saves the page, creating it the first time. An unknown key or language answers `400` with the reason as `detail`. A missing title or body, or a title over 200 characters, answers a validation problem with codes. |

| Option | Default | Meaning |
| --- | --- | --- |
| `Keys` | `terms`, `privacy`, `cookies` | The pages there are. |
| `Locales` | `no` | The languages a page can be written in. The first is the fallback. |
| `Policy` | `Admin` | What listing and saving require. |

The requested language matches a supported one exactly, then by its primary language, so `en-GB`
reads `en`. `nb` and `nn` read as `no`. Keys and languages are stored in lowercase.

## Page sections

A page such as the home page is a list of sections that an admin edits per language. The package
stores the sections as the JSON array the admin's editor sends and serves it back as it is. It never
reads the sections, so your frontend owns their shape.

Your app's `DbContext` maps the `PageSections` table with `AddAppKitPageSections`, and
`MapAppKitPageSections` maps the endpoints. Use `OnSaved` to revalidate the pages that show the
sections.

```csharp
app.MapAppKitPageSections<ApplicationDbContext>("/api/content", o =>
{
    o.Locales = ["no", "en"];
    o.OnSaved = saved => revalidator.RevalidateAsync(saved.Key, saved.Locale);
});
```

`MapAppKitPageSections` maps these routes under its prefix, which defaults to `/api/content`:

| Route | Who | What it does |
| --- | --- | --- |
| `GET {key}?locale=` | Everyone | Answers the sections as `application/json`, in the requested language or else in `DefaultLocale`. `Last-Modified` is when the served sections were saved. Answers an empty array, without `Last-Modified`, while the page has none. Answers `404` for a page that is not in `Keys`. |
| `PUT {key}?locale=` with a JSON array | `Policy` | Saves the array as it is sent and answers with it. Without `?locale=` it saves `DefaultLocale`. An unknown key answers `404`. A body that is not an array, or a language that is not in `Locales`, answers `400` with the reason as `detail`. |

| Option | Default | Meaning |
| --- | --- | --- |
| `Keys` | `home` | The pages there are. |
| `Locales` | `no` | The languages a page can be written in. |
| `DefaultLocale` | The first of `Locales` | The language a page falls back to, and the one a save without `?locale=` writes. |
| `Policy` | `Admin` | What saving requires. |
| `OnSaved` | None | Runs after a save with the key, the language, the sections, and the request. |

Reading matches the requested language the same way the legal pages do, so `en-GB` reads `en` and an
unknown language reads `DefaultLocale`. Saving takes only a language that is in `Locales`. Keys and
languages are stored in lowercase.

### An existing table

An app that already keeps one page of sections per language can map its table instead. Set
`TableName` to the table and `KeyColumn` to `false`. The table then has the columns `Id`, `Locale`
(at most 10 characters), `SectionsJson`, and `UpdatedAt`, with a unique index on `Locale`. A table
without a key column holds one page, so `Keys` must have exactly one key.

```csharp
modelBuilder.AddAppKitPageSections(o =>
{
    o.TableName = "HomePageContents";
    o.KeyColumn = false;
});
```

| Option | Default | Meaning |
| --- | --- | --- |
| `TableName` | `PageSections` | The table name. |
| `KeyColumn` | `true` | Whether the table has a `Key` column, so it can hold several pages. |

## Links

- [Source code](https://github.com/sondresjolyst/si-dotnet-packages/tree/main/src/SjolystInnovation.AppKit.SiteSettings)
- [Changelog](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/src/SjolystInnovation.AppKit.SiteSettings/CHANGELOG.md)
- [All AppKit packages](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/README.md)

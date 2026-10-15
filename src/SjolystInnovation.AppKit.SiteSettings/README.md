# SjolystInnovation.AppKit.SiteSettings

Admin-managed company details and site branding for ASP.NET Core apps. The app keeps one settings row
of its own, and this package maps the endpoints that read and change the shared part of it: the public
company info, the admin settings, and the logo and icon.

```sh
dotnet add package SjolystInnovation.AppKit.SiteSettings
```

## Setup

The app's settings entity implements `ICompanySettings` and `IBrandingSettings` next to any settings of
its own. The row has the key 1, and the entity's defaults are what reads answer before the row exists.

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
| `GET company` | Everyone | The name, legal name (the company name while blank), organisation number, VAT status, the address as one line and as parts, and the public email and phone. The contact recipient stays private. |
| `GET settings` | `Policy` | The company fields as stored. |
| `PUT settings` | `Policy` | Saves the company fields, trimmed, and creates the row on the first write. A bad field gives a validation problem keyed by field. |
| `GET branding` | Everyone | `logoData`, `logoContentType`, `iconData` and `iconContentType`, base64 and `null` while unset. |
| `PUT branding` | `Policy` | A form with an optional `logo` and `icon` file, or `removeLogo` and `removeIcon` set to `true`. A part not sent stays. |

`Policy` is `Admin` by default.

## Rules

- The contact recipient and the public email must be email addresses of at most 200 characters.
- The company name, street address and public phone are required.
- The organisation number is nine digits with a modulus 11 check digit, spaces allowed. It may be blank
  until the business is registered, but not while it is VAT registered.
- A postal code is four digits and needs the place that goes with it. Neither is required.
- A logo or icon is a PNG, JPEG, WebP, GIF or ICO file of at most 2 MB. Its type comes from its first
  bytes, never from what the browser claims. SVG is refused, since it can carry script. A refused image
  answers a validation problem keyed by `Logo` or `Icon` with the reason, and nothing is saved.

`OrgNumber.IsValid`, `PostalAddress.IsPostalCode` and `PostalAddress.Format` are public for the app's own
use, such as invoices and structured data.

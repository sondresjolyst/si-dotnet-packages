# SjolystInnovation.AppKit.Pdf

This package renders an HTML document to a PDF through headless Chromium, driven by PuppeteerSharp.
Use it for the documents your app attaches to emails or offers for download, such as invoices, order
confirmations, and forms. Your app keeps its own templates and passes the finished HTML.

```sh
dotnet add package SjolystInnovation.AppKit.Pdf
```

## Get started

`AddAppKitPdf` registers `IPdfRenderer` as a singleton.

```csharp
using SjolystInnovation.AppKit.Pdf;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAppKitPdf();

var app = builder.Build();

app.MapGet("/api/receipt", async (IPdfRenderer pdf, CancellationToken cancellationToken) =>
{
    var bytes = await pdf.RenderAsync("<!doctype html><html><body><h1>Receipt</h1></body></html>", cancellationToken);
    return Results.File(bytes, "application/pdf", "receipt.pdf");
});

app.Run();
```

| Option | Default | Meaning |
| --- | --- | --- |
| `ExecutablePath` | `PUPPETEER_EXECUTABLE_PATH` | The Chrome or Chromium binary to launch. When blank, PuppeteerSharp downloads a Chromium build on the first render. |
| `NoSandbox` | true only when `PUPPETEER_NO_SANDBOX` is `1` | Runs Chromium without its sandbox. |
| `Timeout` | 60 seconds | How long one render may take, launching the browser included. |
| `AllowJavaScript` | `false` | Runs the scripts in the HTML. |
| `AllowedOrigins` | None | The origins the page may load assets from, such as `https://cdn.example.com`. |

Set the options in the callback, which runs after the environment variables are read:

```csharp
builder.Services.AddAppKitPdf(o => o.Timeout = TimeSpan.FromSeconds(30));
```

If the timeout is not positive, or an allowed origin is not an http or https origin without a path,
the app fails to start.

## Pass trusted HTML

The renderer runs on your server, so treat the HTML like code. Build it yourself and HTML encode every
value that comes from a user, such as a name or an address. Two defaults limit what a page can do:

- Scripts do not run, unless `AllowJavaScript` is on.
- The page loads nothing over the network. Every request, each redirect included, is stopped unless it
  is a `data:` URL, `about:blank`, or on one of the `AllowedOrigins`, matched on scheme, host, and port.
  An image or a stylesheet that points at an internal address is never fetched.

Inline your CSS and embed images as `data:` URLs, or allow the one origin that serves them.

## Render a PDF

```csharp
byte[] bytes = await pdf.RenderAsync(html, cancellationToken);
```

The page is A4 with backgrounds printed. An `@page` rule in the HTML sets the size and the margins,
for example `@page { size: A4; margin: 20mm; }`. Without one, the page has no margins.

| Outcome | Result |
| --- | --- |
| The render succeeds | The bytes of the PDF. |
| It takes longer than `Timeout` | `TimeoutException`. The browser is closed and logged as an error. |
| Chromium fails | The exception passes through. The browser is closed and logged as an error. |
| The caller cancels | `OperationCanceledException`. |

The renderer launches one browser on the first render and reuses it. Each render gets its own page,
which is closed afterwards. When a render fails or times out, or the browser has gone away, the next
render launches a fresh browser. Renders that run while the browser is closed fail as well, so retry a
render that matters. The browser is closed when the app stops.

## Attach the withdrawal form

`WithdrawalForm` builds the standard withdrawal form as an HTML document. The Norwegian form (angreskjema)
follows forskrift 20. juni 2014 nr. 777 § 1 (blankett Q-0319B), and the English one follows
Directive 2011/83/EU, Annex I(B). Any Norwegian distance seller needs it. The form is for goods, so
"varer" is ticked in the Norwegian form. Pass the seller and the order as plain records and render the
result like any other HTML. The form is in Norwegian by default. Set `Language` to
`WithdrawalFormLanguage.English` for the English model form, which keeps the directive's (*) marks and its "Delete as appropriate" footnote for the buyer to complete.

```csharp
using SjolystInnovation.AppKit.Email;
using SjolystInnovation.AppKit.Pdf;

var form = new WithdrawalFormRequest(
    new WithdrawalFormSeller("Example Shop")
    {
        LegalName = "Example Shop AS",
        OrganizationNumber = "999 888 777",
        AddressLines = ["Storgata 1", "0155 Oslo"],
        Email = "post@example.no",
    },
    new WithdrawalFormOrder("ORD-1042", new DateOnly(2026, 10, 9), ["Gaming PC", "Mouse mat"]));

byte[] bytes = await pdf.RenderAsync(WithdrawalForm.Html(form), cancellationToken);

await emailSender.SendAsync(
    new EmailMessage(buyerEmail, "Your order ORD-1042", confirmationHtml)
    {
        Attachments = [new EmailAttachment(WithdrawalForm.FileName(form.Language), bytes)],
    },
    cancellationToken);
```

| Input | Meaning |
| --- | --- |
| `WithdrawalFormSeller` | The name, an optional legal name that is shown instead, an optional organization number, the address lines, and an optional email address. The send-to line shows the name, the organization number, the address and the email. |
| `WithdrawalFormOrder` | The order number, the date of the agreement, and the goods as plain strings, one per line. With no goods the form shows blank lines to write on. `ReceivedOn` is optional. |
| `Language` | Norwegian by default, or English. |

Dates print as `dd.MM.yyyy`. Convert a timestamp to the shop's local date before you pass it. The buyer
writes the name, the address, the date and the signature by hand, so those lines stay blank, and so does
the date of receipt unless you pass `ReceivedOn`, which also ticks it in the Norwegian form. Every value is HTML encoded. The document has inline CSS
and a Content-Security-Policy that blocks everything but inline styles and `data:` images, so it renders
with scripts off and no network. `WithdrawalForm.FileName` returns `angrerettskjema.pdf` or
`withdrawal-form.pdf`.

## Run Chromium in a container

Install Chrome in the image, point `PUPPETEER_EXECUTABLE_PATH` at it, and run the app as a non-root
user so the sandbox works. On the `mcr.microsoft.com/dotnet/aspnet:10.0` image, which is Ubuntu, the
`chromium` apt package is a snap stub that does not run in a container. Install Google Chrome from
Google's apt repository instead:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER root
RUN apt-get update && apt-get install -y --no-install-recommends wget gnupg ca-certificates fonts-liberation \
    && wget -qO- https://dl.google.com/linux/linux_signing_key.pub | gpg --dearmor -o /usr/share/keyrings/google-chrome.gpg \
    && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/google-chrome.gpg] http://dl.google.com/linux/chrome/deb/ stable main" > /etc/apt/sources.list.d/google-chrome.list \
    && apt-get update && apt-get install -y --no-install-recommends google-chrome-stable \
    && rm -rf /var/lib/apt/lists/*
ENV PUPPETEER_EXECUTABLE_PATH=/usr/bin/google-chrome-stable
USER app
```

Set `PUPPETEER_NO_SANDBOX=1` only where the environment already isolates the process.

## Links

- [Source code](https://github.com/sondresjolyst/si-dotnet-packages/tree/main/src/SjolystInnovation.AppKit.Pdf)
- [Changelog](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/src/SjolystInnovation.AppKit.Pdf/CHANGELOG.md)
- [All AppKit packages](https://github.com/sondresjolyst/si-dotnet-packages/blob/main/README.md)

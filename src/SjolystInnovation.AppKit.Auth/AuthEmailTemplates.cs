using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SjolystInnovation.AppKit.Email;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>The company an email comes from, as shown in its header and footer. Blank lines are left out.</summary>
/// <param name="Name">The brand name, used in the header, subjects and text.</param>
public sealed record EmailBrand(string Name)
{
    public string? LegalName { get; init; }

    /// <summary>The organisation number line exactly as shown, such as <c>Org.nr. 912 345 678 MVA</c>.</summary>
    public string? OrgNumber { get; init; }

    public string? Address { get; init; }

    public string? Email { get; init; }
}

/// <summary>Gives the auth emails the app's current company details, such as from its admin settings.</summary>
public interface IEmailBrand
{
    Task<EmailBrand> GetBrandAsync(CancellationToken cancellationToken);
}

/// <summary>The language the auth emails are written in.</summary>
public enum EmailLanguage
{
    English,
    Norwegian,
}

/// <summary>Settings for <see cref="AuthEmailServiceCollectionExtensions.AddAppKitAuthEmails{TUser}"/>.</summary>
public sealed partial class AppKitAuthEmailOptions
{
    public EmailLanguage Language { get; set; } = EmailLanguage.English;

    /// <summary>The header band's background and the code's colour, as <c>#rrggbb</c>.</summary>
    public string HeaderColor { get; set; } = "#182232";

    /// <summary>The button's and the code label's colour, as <c>#rrggbb</c>.</summary>
    public string AccentColor { get; set; } = "#0284c7";

    /// <summary>
    /// The absolute address of the app's page that takes a reset or invite code. The emails link to it
    /// with <c>email</c> and <c>code</c> added to the query string.
    /// </summary>
    public string SetPasswordUrl { get; set; } = string.Empty;

    /// <summary>
    /// The absolute address of the app's page that takes an email verification code. Required by
    /// <see cref="AuthEmailServiceCollectionExtensions.AddAppKitVerificationEmails{TUser}"/> only.
    /// </summary>
    public string VerifyEmailUrl { get; set; } = string.Empty;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    internal static partial Regex HexColor();

    internal static bool IsPageUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}

/// <summary>Sends the email verification code with the same layout as the other auth emails.</summary>
public interface IVerificationEmails<in TUser>
{
    Task SendEmailVerificationAsync(TUser user, string code, CancellationToken cancellationToken);
}

public static class AuthEmailServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IAuthEmails{TUser}"/> that sends the reset and invite codes through
    /// <see cref="IEmailSender"/> with a shared layout. The app registers an <see cref="IEmailBrand"/> and
    /// an <see cref="IEmailSender"/>. The options are checked at startup.
    /// </summary>
    public static IServiceCollection AddAppKitAuthEmails<TUser>(this IServiceCollection services, Action<AppKitAuthEmailOptions> configure)
        where TUser : IdentityUser, IAppKitAccount, IPasswordResetUser
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<AppKitAuthEmailOptions>()
            .Configure(configure)
            .Validate(o => AppKitAuthEmailOptions.HexColor().IsMatch(o.HeaderColor), "AppKitAuthEmailOptions.HeaderColor must be a #rrggbb colour.")
            .Validate(o => AppKitAuthEmailOptions.HexColor().IsMatch(o.AccentColor), "AppKitAuthEmailOptions.AccentColor must be a #rrggbb colour.")
            .Validate(o => AppKitAuthEmailOptions.IsPageUrl(o.SetPasswordUrl), "AppKitAuthEmailOptions.SetPasswordUrl must be an absolute http or https address.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAuthEmails<TUser>, AuthEmails<TUser>>();
        return services;
    }

    /// <summary>
    /// Registers an <see cref="IVerificationEmails{TUser}"/> for apps that verify email addresses. It uses
    /// the options from <see cref="AddAppKitAuthEmails{TUser}"/>, which must also be registered, and needs
    /// <see cref="AppKitAuthEmailOptions.VerifyEmailUrl"/>.
    /// </summary>
    public static IServiceCollection AddAppKitVerificationEmails<TUser>(this IServiceCollection services)
        where TUser : IdentityUser, IAppKitAccount, IEmailVerificationUser
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<AppKitAuthEmailOptions>()
            .Validate(o => AppKitAuthEmailOptions.IsPageUrl(o.VerifyEmailUrl), "AppKitAuthEmailOptions.VerifyEmailUrl must be an absolute http or https address.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IVerificationEmails<TUser>, VerificationEmails<TUser>>();
        return services;
    }
}

internal sealed class AuthEmails<TUser>(IEmailSender sender, IEmailBrand brands, IOptions<AppKitAuthEmailOptions> options, TimeProvider time)
    : IAuthEmails<TUser>
    where TUser : IdentityUser, IAppKitAccount, IPasswordResetUser
{
    public Task SendPasswordResetAsync(TUser user, string code, CancellationToken cancellationToken) =>
        SendAsync(user, code, AuthEmailKind.Reset, cancellationToken);

    public Task SendInviteAsync(TUser user, string code, CancellationToken cancellationToken) =>
        SendAsync(user, code, AuthEmailKind.Invite, cancellationToken);

    private async Task SendAsync(TUser user, string code, AuthEmailKind kind, CancellationToken cancellationToken)
    {
        var expires = user.PasswordResetCodeExpiration
            ?? throw new InvalidOperationException("The user has no reset code. Issue one with PasswordResetCodes.Issue first.");
        var brand = await brands.GetBrandAsync(cancellationToken);
        var email = AuthEmailRenderer.Render(kind, options.Value, brand, user.FirstName, user.Email!, code,
            expires - time.GetUtcNow().UtcDateTime, options.Value.SetPasswordUrl);
        await sender.SendAsync(email, cancellationToken);
    }
}

internal sealed class VerificationEmails<TUser>(IEmailSender sender, IEmailBrand brands, IOptions<AppKitAuthEmailOptions> options, TimeProvider time)
    : IVerificationEmails<TUser>
    where TUser : IdentityUser, IAppKitAccount, IEmailVerificationUser
{
    public async Task SendEmailVerificationAsync(TUser user, string code, CancellationToken cancellationToken)
    {
        var expires = user.EmailVerificationCodeExpiration
            ?? throw new InvalidOperationException("The user has no verification code. Issue one with EmailVerificationCodes.Issue first.");
        var brand = await brands.GetBrandAsync(cancellationToken);
        var email = AuthEmailRenderer.Render(AuthEmailKind.Verify, options.Value, brand, user.FirstName, user.Email!, code,
            expires - time.GetUtcNow().UtcDateTime, options.Value.VerifyEmailUrl);
        await sender.SendAsync(email, cancellationToken);
    }
}

internal enum AuthEmailKind
{
    Reset,
    Invite,
    Verify,
}

/// <summary>The words of one email kind in one language.</summary>
internal sealed record AuthEmailText(string Badge, string Subject, string Heading, string Intro, string Button, string Ignore);

/// <summary>The words shared by every email kind in one language.</summary>
internal sealed record AuthEmailWords(
    string Hello, string HelloName, string CodeLabel, string Expiry, string Fallback, string Footer,
    (string One, string Many) Minutes, (string One, string Many) Hours, (string One, string Many) Days,
    IReadOnlyDictionary<AuthEmailKind, AuthEmailText> Kinds);

internal static class AuthEmailRenderer
{
    // {0} is the brand name in subjects, intros and the footer, and the first name in HelloName. {0} is the
    // lifetime in Expiry.
    private static readonly AuthEmailWords English = new(
        "Hi,", "Hi {0},", "Your code", "The code expires in {0}.", "Button not working? Copy this link:",
        "You got this email because of your account at {0}.",
        ("minute", "minutes"), ("hour", "hours"), ("day", "days"),
        new Dictionary<AuthEmailKind, AuthEmailText>
        {
            [AuthEmailKind.Reset] = new("Password reset", "Password reset code", "Reset your password",
                "We got a request to reset your password at {0}. Use the code below to choose a new one.",
                "Choose a new password", "If you did not ask for this, ignore this email. Your password stays the same."),
            [AuthEmailKind.Invite] = new("Invitation", "Set your password at {0}", "Set your password",
                "An account has been created for you at {0}. Use the code below to choose a password.",
                "Set your password", "If you did not expect this, ignore this email."),
            [AuthEmailKind.Verify] = new("Verify email", "Verify your email", "Verify your email",
                "Enter the code below in {0} to confirm this email address.",
                "Verify email", "If you did not sign up, ignore this email."),
        });

    private static readonly AuthEmailWords Norwegian = new(
        "Hei,", "Hei {0},", "Koden din", "Koden utløper om {0}.", "Virker ikke knappen? Kopier denne lenken:",
        "Du får denne e-posten fordi du har en konto hos {0}.",
        ("minutt", "minutter"), ("time", "timer"), ("dag", "dager"),
        new Dictionary<AuthEmailKind, AuthEmailText>
        {
            [AuthEmailKind.Reset] = new("Nytt passord", "Tilbakestill passordet", "Tilbakestill passordet",
                "Vi har fått en forespørsel om å tilbakestille passordet ditt hos {0}. Bruk koden under for å velge et nytt.",
                "Velg nytt passord", "Har du ikke bedt om dette, kan du se bort fra e-posten. Passordet endres ikke."),
            [AuthEmailKind.Invite] = new("Invitasjon", "Sett passordet ditt hos {0}", "Sett passordet ditt",
                "Det er opprettet en konto for deg hos {0}. Bruk koden under for å velge et passord.",
                "Sett passord", "Venter du ikke denne e-posten, kan du se bort fra den."),
            [AuthEmailKind.Verify] = new("Bekreft e-post", "Bekreft e-posten din", "Bekreft e-posten din",
                "Skriv inn koden under i {0} for å bekrefte denne e-postadressen.",
                "Bekreft e-post", "Har du ikke registrert deg, kan du se bort fra e-posten."),
        });

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string F(string format, string value) => string.Format(CultureInfo.InvariantCulture, format, value);

    /// <summary>The page address with the email and code added to its query string.</summary>
    internal static string Link(string page, string email, string code) =>
        $"{page}{(page.Contains('?', StringComparison.Ordinal) ? '&' : '?')}email={Uri.EscapeDataString(email)}&code={Uri.EscapeDataString(code)}";

    /// <summary>The lifetime in whole days, hours or minutes, whichever unit it is closest to being a count of.</summary>
    internal static string Duration(TimeSpan lifetime, AuthEmailWords words)
    {
        var minutes = Math.Max(1, (int)Math.Round(lifetime.TotalMinutes, MidpointRounding.AwayFromZero));
        var (count, unit) = minutes >= 1410 ? ((int)Math.Round(minutes / 1440.0, MidpointRounding.AwayFromZero), words.Days)
            : minutes >= 60 ? ((int)Math.Round(minutes / 60.0, MidpointRounding.AwayFromZero), words.Hours)
            : (minutes, words.Minutes);
        return $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? unit.One : unit.Many)}";
    }

    internal static EmailMessage Render(AuthEmailKind kind, AppKitAuthEmailOptions options, EmailBrand brand,
        string? firstName, string to, string code, TimeSpan lifetime, string page)
    {
        var words = options.Language == EmailLanguage.Norwegian ? Norwegian : English;
        var text = words.Kinds[kind];
        var link = Link(page, to, code);
        var hello = string.IsNullOrWhiteSpace(firstName) ? words.Hello : F(words.HelloName, H(firstName));
        var brandLines = string.Join("<br>", new[] { brand.LegalName, brand.OrgNumber, brand.Address, brand.Email }
            .Where(l => !string.IsNullOrWhiteSpace(l)).Select(H));
        var header = options.HeaderColor;
        var accent = options.AccentColor;
        const string Paragraph = "line-height:1.6;color:#333;font-size:13px";

        var html = $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"></head>
            <body style="margin:0;background:#fff;font-family:Arial,Helvetica,sans-serif;color:#1a1a1a">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="border-collapse:collapse;max-width:600px;margin:0 auto">
              <tr><td style="background:{{header}};color:#fff;padding:28px 32px 20px">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr>
                  <td style="vertical-align:top">
                    <div style="font-size:22px;font-weight:700;letter-spacing:.04em;margin-bottom:4px">{{H(brand.Name)}}</div>
                    <div style="font-size:11px;opacity:.85;line-height:1.5">{{brandLines}}</div>
                  </td>
                  <td style="vertical-align:top;text-align:right">
                    <span style="display:inline-block;background:rgba(255,255,255,.25);border:1px solid rgba(255,255,255,.5);padding:2px 10px;border-radius:20px;font-size:10px;font-weight:700;letter-spacing:.08em;text-transform:uppercase">{{H(text.Badge)}}</span>
                  </td>
                </tr></table>
              </td></tr>
              <tr><td style="padding:24px 32px">
                <h1 style="font-size:18px;margin:0 0 12px">{{H(text.Heading)}}</h1>
                <p style="margin:0 0 10px;{{Paragraph}}">{{hello}}</p>
                <p style="margin:0;{{Paragraph}}">{{H(F(text.Intro, brand.Name))}}</p>
                <div style="text-align:center;font-size:9px;font-weight:700;text-transform:uppercase;letter-spacing:.1em;color:{{accent}};margin:22px 0 6px">{{H(words.CodeLabel)}}</div>
                <div style="text-align:center;font-family:'Courier New',Courier,monospace;font-size:32px;font-weight:700;letter-spacing:.3em;color:{{header}};background:#f1f5f9;border:1px solid #cbd5e1;border-radius:8px;padding:18px 12px;margin:0 0 20px">{{H(code)}}</div>
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:4px 0 20px">
                  <a href="{{H(link)}}" style="display:inline-block;padding:12px 22px;border-radius:8px;background:{{accent}};color:#fff;font-weight:700;font-size:14px;text-decoration:none">{{H(text.Button)}}</a>
                </td></tr></table>
                <p style="margin:0 0 8px;{{Paragraph}}">{{H(F(words.Expiry, Duration(lifetime, words)))}}</p>
                <p style="margin:0;{{Paragraph}}">{{H(text.Ignore)}}</p>
                <p style="margin:16px 0 0;color:#6b7280;font-size:11px;line-height:1.5;word-break:break-all">{{H(words.Fallback)}} {{H(link)}}</p>
              </td></tr>
              <tr><td style="padding:14px 32px 24px;border-top:1px solid #e5e7eb;font-size:10px;color:#888;line-height:1.5">{{H(F(words.Footer, brand.LegalName is { Length: > 0 } legal ? legal : brand.Name))}}</td></tr>
            </table>
            </body></html>
            """;
        return new EmailMessage(to, F(text.Subject, brand.Name), html);
    }
}

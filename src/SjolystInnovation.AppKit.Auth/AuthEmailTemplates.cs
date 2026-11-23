using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SjolystInnovation.AppKit.Email;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>The company details shown in the header and footer of an email. Blank details are left out.</summary>
/// <param name="Name">The brand name, used in the header, subjects and text.</param>
public sealed record EmailBrand(string Name)
{
    /// <summary>The legal company name, shown in the header. The footer uses it instead of the brand name when set.</summary>
    public string? LegalName { get; init; }

    /// <summary>
    /// The organization number, for example <c>923202374</c> or <c>923 202 374</c>. The header shows it in
    /// the email's language, as <c>Org.nr. 923 202 374</c> in Norwegian and <c>Org. no. 923 202 374</c> in
    /// English. Nine digits are grouped in threes, and anything else is shown trimmed as given. A blank
    /// number leaves the line out.
    /// </summary>
    public string? OrgNumber { get; init; }

    /// <summary>
    /// Whether the company is registered for VAT. Adds <c>MVA</c> after the organization number in every
    /// language, as Norwegian bookkeeping rules require for a registered business. Without a number it
    /// shows nothing.
    /// </summary>
    public bool VatRegistered { get; init; }

    /// <summary>The postal address, shown in the header.</summary>
    public string? Address { get; init; }

    /// <summary>The contact email address, shown in the header.</summary>
    public string? Email { get; init; }
}

/// <summary>Provides the app's current company details to the auth emails, for example from its admin settings.</summary>
public interface IEmailBrand
{
    /// <summary>Gets the current company details.</summary>
    Task<EmailBrand> GetBrandAsync(CancellationToken cancellationToken);
}

/// <summary>The language the auth emails are written in.</summary>
public enum EmailLanguage
{
    /// <summary>English.</summary>
    English,

    /// <summary>Norwegian Bokmål.</summary>
    Norwegian,
}

/// <summary>The options for <see cref="AuthEmailServiceCollectionExtensions.AddAppKitAuthEmails{TUser}"/>.</summary>
public sealed class AppKitAuthEmailOptions
{
    /// <summary>
    /// The language of the emails. Defaults to English. The sign-up emails use the language of the person's locale when the
    /// package has it, and this language otherwise.
    /// </summary>
    public EmailLanguage Language { get; set; } = EmailLanguage.English;

    /// <summary>
    /// The background color of the header band and the color of the code, as <c>#rrggbb</c>. Text on the
    /// band is white, or near black when the color is too light for white text. The code is then near
    /// black too. Defaults to <c>#182232</c>.
    /// </summary>
    public string HeaderColor { get; set; } = "#182232";

    /// <summary>
    /// The color of the button and the code label, as <c>#rrggbb</c>. The button text and a label that is
    /// too light follow the same rule as <see cref="HeaderColor"/>. Defaults to <c>#0284c7</c>.
    /// </summary>
    public string AccentColor { get; set; } = "#0284c7";

    /// <summary>
    /// The absolute http or https address of the app's page that takes a reset or invite code. The emails link to it
    /// with <c>email</c> and <c>code</c> added to the query string. Leave it empty only with <see cref="SetPasswordInApp"/>.
    /// </summary>
    public string SetPasswordUrl { get; set; } = string.Empty;

    /// <summary>
    /// Whether the person types the reset or invite code into the app instead of opening a page, for an app with no web
    /// page such as a mobile app. The reset and invite emails then show the code and tell the person to enter it in the
    /// app, with no button and no link. <see cref="SetPasswordUrl"/> must then be empty. Defaults to false, so an empty
    /// <see cref="SetPasswordUrl"/> still fails the start.
    /// </summary>
    public bool SetPasswordInApp { get; set; }

    /// <summary>
    /// The absolute http or https address of the app's page that takes an email verification code. The verification
    /// email and the removal reminders link to it with <c>email</c> and <c>code</c> added to the query string. An https
    /// app link that opens the app works here too. Leave it empty only with <see cref="VerifyInApp"/>.
    /// </summary>
    public string VerifyEmailUrl { get; set; } = string.Empty;

    /// <summary>
    /// Whether the person types the verification code into the app instead of opening a page, for an app with no web
    /// page such as a mobile app. The verification email and the removal reminders then show the code and tell the
    /// person to enter it in the app, with no button and no link. <see cref="VerifyEmailUrl"/> must then be empty.
    /// Defaults to false, so an empty <see cref="VerifyEmailUrl"/> still fails the start.
    /// </summary>
    public bool VerifyInApp { get; set; }

    internal static bool IsPageUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    // Code-only is an explicit choice, so a web app whose page setting went missing still fails. A page set next to it
    // fails too, because the emails would leave it out.
    internal bool HasValidVerifyPage => VerifyInApp ? string.IsNullOrEmpty(VerifyEmailUrl) : IsPageUrl(VerifyEmailUrl);

    internal const string VerifyRule =
        "AppKitAuthEmailOptions.VerifyEmailUrl must be an absolute http or https address. With AppKitAuthEmailOptions.VerifyInApp set to true it must be empty instead.";

    // The verify page, or null when the code is entered in the app. Options that break the rule throw, so nothing is sent.
    internal string? VerifyPage() => !HasValidVerifyPage ? throw new InvalidOperationException(VerifyRule)
        : VerifyInApp ? null : VerifyEmailUrl;

    // The same rule for the reset and invite page.
    internal bool HasValidSetPasswordPage => SetPasswordInApp ? string.IsNullOrEmpty(SetPasswordUrl) : IsPageUrl(SetPasswordUrl);

    internal const string SetPasswordRule =
        "AppKitAuthEmailOptions.SetPasswordUrl must be an absolute http or https address. With AppKitAuthEmailOptions.SetPasswordInApp set to true it must be empty instead.";

    // The set password page, or null when the code is entered in the app. Options that break the rule throw, so nothing is sent.
    internal string? SetPasswordPage() => !HasValidSetPasswordPage ? throw new InvalidOperationException(SetPasswordRule)
        : SetPasswordInApp ? null : SetPasswordUrl;
}

/// <summary>Sends the email verification code with the same layout as the other auth emails.</summary>
public interface IVerificationEmails<in TUser>
{
    /// <summary>Sends the verification code to the user.</summary>
    Task SendEmailVerificationAsync(TUser user, string code, CancellationToken cancellationToken);
}

/// <summary>Registers the auth email services.</summary>
public static class AuthEmailServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IAuthEmails{TUser}"/> that sends reset and invite codes through
    /// <see cref="IEmailSender"/> with a shared layout. You register an <see cref="IEmailBrand"/> and an
    /// <see cref="IEmailSender"/> yourself. The options are validated at startup. It requires
    /// <see cref="AppKitAuthEmailOptions.SetPasswordUrl"/>, or <see cref="AppKitAuthEmailOptions.SetPasswordInApp"/> with no
    /// page, in which case the emails show the code with no link.
    /// </summary>
    public static IServiceCollection AddAppKitAuthEmails<TUser>(this IServiceCollection services, Action<AppKitAuthEmailOptions> configure)
        where TUser : IdentityUser, IAppKitAccount, IPasswordResetUser
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<AppKitAuthEmailOptions>()
            .Configure(configure)
            .Validate(o => EmailLayout.IsColor(o.HeaderColor), "AppKitAuthEmailOptions.HeaderColor must be a #rrggbb colour.")
            .Validate(o => EmailLayout.IsColor(o.AccentColor), "AppKitAuthEmailOptions.AccentColor must be a #rrggbb colour.")
            .Validate(o => o.HasValidSetPasswordPage, AppKitAuthEmailOptions.SetPasswordRule)
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAuthEmails<TUser>, AuthEmails<TUser>>();
        return services;
    }

    /// <summary>
    /// Registers an <see cref="IVerificationEmails{TUser}"/> for apps that verify email addresses. It uses
    /// the options from <see cref="AddAppKitAuthEmails{TUser}"/>, which you must also register. It
    /// requires <see cref="AppKitAuthEmailOptions.VerifyEmailUrl"/>, or <see cref="AppKitAuthEmailOptions.VerifyInApp"/>
    /// with no page, in which case the email shows the code with no link.
    /// </summary>
    public static IServiceCollection AddAppKitVerificationEmails<TUser>(this IServiceCollection services)
        where TUser : IdentityUser, IAppKitAccount, IEmailVerificationUser
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<AppKitAuthEmailOptions>()
            .Validate(o => o.HasValidVerifyPage, AppKitAuthEmailOptions.VerifyRule)
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
            ?? throw new InvalidOperationException("The user has no reset code. Issue one through the reset or invite endpoints first.");
        var brand = await brands.GetBrandAsync(cancellationToken);
        var email = AuthEmailRenderer.Render(kind, options.Value, brand, user.FirstName, user.Email!, code,
            expires - time.GetUtcNow().UtcDateTime, options.Value.SetPasswordPage());
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
            expires - time.GetUtcNow().UtcDateTime, options.Value.VerifyPage());
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
/// <param name="Badge">The label in the header.</param>
/// <param name="Subject">The subject. <c>{0}</c> is the brand name.</param>
/// <param name="Heading">The heading above the body.</param>
/// <param name="Intro">The first paragraph. <c>{0}</c> is the brand name.</param>
/// <param name="Button">The button text.</param>
/// <param name="Ignore">The paragraph for a person who did not expect the email.</param>
/// <param name="AppIntro">The first paragraph when there is no page to link to, so the person enters the code in the app. Null to keep <paramref name="Intro"/>.</param>
/// <param name="AppSubject">The subject when there is no page to link to. <c>{0}</c> is the brand name. Null to keep <paramref name="Subject"/>.</param>
internal sealed record AuthEmailText(string Badge, string Subject, string Heading, string Intro, string Button, string Ignore,
    string? AppIntro = null, string? AppSubject = null);

/// <summary>The words shared by every email kind in one language.</summary>
internal sealed record AuthEmailWords(
    string Hello, string HelloName, string CodeLabel, string Expiry, string Fallback, string Footer,
    (string One, string Many) Minutes, (string One, string Many) Hours, (string One, string Many) Days,
    IReadOnlyDictionary<AuthEmailKind, AuthEmailText> Kinds);

internal static class AuthEmailRenderer
{
    // In subjects and intros, {0} is the brand name. In the footer it is the legal name, or the brand name
    // when no legal name is set. In HelloName it is the first name, and in Expiry it is the lifetime.
    private static readonly AuthEmailWords English = new(
        "Hi,", "Hi {0},", "Your code", "The code expires in {0}.", "Button not working? Copy this link:",
        "You got this email because of your account at {0}.",
        ("minute", "minutes"), ("hour", "hours"), ("day", "days"),
        new Dictionary<AuthEmailKind, AuthEmailText>
        {
            [AuthEmailKind.Reset] = new("Password reset", "Password reset code", "Reset your password",
                "We got a request to reset your password at {0}. Use the code below to choose a new one.",
                "Choose a new password", "If you did not ask for this, ignore this email. Your password stays the same.",
                "We got a request to reset your password in the {0} app. Open the app and enter the code below to choose a new one."),
            [AuthEmailKind.Invite] = new("Invitation", "Set your password at {0}", "Set your password",
                "An account has been created for you at {0}. Use the code below to choose a password.",
                "Set your password", "If you did not expect this, ignore this email.",
                "An account has been created for you in the {0} app. Open the app and enter the code below to choose a password.",
                "Set your password in the {0} app"),
            [AuthEmailKind.Verify] = new("Verify email", "Verify your email", "Verify your email",
                "Enter the code below in {0} to confirm this email address.",
                "Verify email", "If you did not sign up, ignore this email.",
                "Open the {0} app and enter the code below to confirm this email address."),
        });

    private static readonly AuthEmailWords Norwegian = new(
        "Hei,", "Hei {0},", "Koden din", "Koden utløper om {0}.", "Virker ikke knappen? Kopier denne lenken:",
        "Du får denne e-posten fordi du har en konto hos {0}.",
        ("minutt", "minutter"), ("time", "timer"), ("dag", "dager"),
        new Dictionary<AuthEmailKind, AuthEmailText>
        {
            [AuthEmailKind.Reset] = new("Nytt passord", "Tilbakestill passordet", "Tilbakestill passordet",
                "Vi har fått en forespørsel om å tilbakestille passordet ditt hos {0}. Bruk koden under for å velge et nytt.",
                "Velg nytt passord", "Har du ikke bedt om dette, kan du se bort fra e-posten. Passordet endres ikke.",
                "Vi har fått en forespørsel om å tilbakestille passordet ditt i appen {0}. Åpne appen og skriv inn koden under for å velge et nytt."),
            [AuthEmailKind.Invite] = new("Invitasjon", "Sett passordet ditt hos {0}", "Sett passordet ditt",
                "Det er opprettet en konto for deg hos {0}. Bruk koden under for å velge et passord.",
                "Sett passord", "Venter du ikke denne e-posten, kan du se bort fra den.",
                "Det er opprettet en konto for deg i appen {0}. Åpne appen og skriv inn koden under for å velge et passord.",
                "Sett passordet ditt i appen {0}"),
            [AuthEmailKind.Verify] = new("Bekreft e-post", "Bekreft e-posten din", "Bekreft e-posten din",
                "Skriv inn koden under i {0} for å bekrefte denne e-postadressen.",
                "Bekreft e-post", "Har du ikke registrert deg, kan du se bort fra e-posten.",
                "Åpne appen {0} og skriv inn koden under for å bekrefte denne e-postadressen."),
        });

    internal static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    internal static string F(string format, string value) => string.Format(CultureInfo.InvariantCulture, format, value);

    /// <summary>Builds the page address with the email and code added to its query string.</summary>
    internal static string Link(string page, string email, string code) =>
        $"{page}{(page.Contains('?', StringComparison.Ordinal) ? '&' : '?')}email={Uri.EscapeDataString(email)}&code={Uri.EscapeDataString(code)}";

    /// <summary>
    /// Formats the lifetime as a rounded count of days, hours, or minutes. It uses days from 23.5 hours
    /// and hours from 60 minutes.
    /// </summary>
    internal static string Duration(TimeSpan lifetime, AuthEmailWords words)
    {
        var minutes = Math.Max(1, (int)Math.Round(lifetime.TotalMinutes, MidpointRounding.AwayFromZero));
        var (count, unit) = minutes >= 1410 ? ((int)Math.Round(minutes / 1440.0, MidpointRounding.AwayFromZero), words.Days)
            : minutes >= 60 ? ((int)Math.Round(minutes / 60.0, MidpointRounding.AwayFromZero), words.Hours)
            : (minutes, words.Minutes);
        return $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? unit.One : unit.Many)}";
    }

    internal static AuthEmailWords Words(EmailLanguage language) => language == EmailLanguage.Norwegian ? Norwegian : English;

    /// <summary>Maps the brand to the company details of the layout.</summary>
    internal static EmailCompany Company(EmailBrand brand) => new(brand.Name)
    {
        LegalName = brand.LegalName,
        OrgNumber = brand.OrgNumber,
        VatRegistered = brand.VatRegistered,
        Address = brand.Address,
        Email = brand.Email,
    };

    internal static EmailLayoutLanguage Language(EmailLanguage language) =>
        language == EmailLanguage.Norwegian ? EmailLayoutLanguage.Norwegian : EmailLayoutLanguage.English;

    /// <summary>
    /// The language of an email for a locale such as <c>nb</c>, <c>no-NO</c> or <c>en_GB</c>. Only the part before the first
    /// <c>-</c> or <c>_</c> counts. Norwegian covers <c>nb</c>, <c>no</c> and <c>nn</c>, and English covers <c>en</c>. A blank or
    /// unknown locale gives <paramref name="fallback"/>.
    /// </summary>
    internal static EmailLanguage LanguageFor(string? locale, EmailLanguage fallback)
    {
        var end = locale?.AsSpan().IndexOfAny('-', '_') ?? -1;
        var primary = locale is null ? default : (end < 0 ? locale.AsSpan() : locale.AsSpan(0, end)).Trim();
        if (primary.Equals("nb", StringComparison.OrdinalIgnoreCase) || primary.Equals("no", StringComparison.OrdinalIgnoreCase) || primary.Equals("nn", StringComparison.OrdinalIgnoreCase))
            return EmailLanguage.Norwegian;
        return primary.Equals("en", StringComparison.OrdinalIgnoreCase) ? EmailLanguage.English : fallback;
    }

    // Without a page there is no button and no link, and the intro and subject tell the person to enter the code in the app.
    internal static EmailMessage Render(AuthEmailKind kind, AppKitAuthEmailOptions options, EmailBrand brand,
        string? firstName, string to, string code, TimeSpan lifetime, string? page, EmailLanguage? language = null)
    {
        var emailLanguage = language ?? options.Language;
        var words = Words(emailLanguage);
        var text = words.Kinds[kind];
        var link = page is null ? null : Link(page, to, code);
        var intro = link is null && text.AppIntro is { } appIntro ? appIntro : text.Intro;
        var subject = link is null && text.AppSubject is { } appSubject ? appSubject : text.Subject;
        var hello = string.IsNullOrWhiteSpace(firstName) ? words.Hello : F(words.HelloName, H(firstName));
        var codeColor = EmailLayout.ReadsOnWhite(options.HeaderColor) ? options.HeaderColor : EmailLayout.Ink;
        var labelColor = EmailLayout.ReadsOnWhite(options.AccentColor) ? options.AccentColor : EmailLayout.Ink;
        const string Paragraph = EmailLayout.TextStyle;

        var body = $$"""
                <p style="margin:0 0 10px;{{Paragraph}}">{{hello}}</p>
                <p style="margin:0;{{Paragraph}}">{{H(F(intro, brand.Name))}}</p>
                <div style="text-align:center;font-size:9px;font-weight:700;text-transform:uppercase;letter-spacing:.1em;color:{{labelColor}};margin:22px 0 6px">{{H(words.CodeLabel)}}</div>
                <div style="text-align:center;font-family:'Courier New',Courier,monospace;font-size:32px;font-weight:700;letter-spacing:.3em;color:{{codeColor}};background:#f1f5f9;border:1px solid #cbd5e1;border-radius:8px;padding:18px 12px;margin:0 0 20px">{{H(code)}}</div>
            """;
        var afterButton = $$"""
                <p style="margin:0 0 8px;{{Paragraph}}">{{H(F(words.Expiry, Duration(lifetime, words)))}}</p>
                <p style="margin:0;{{Paragraph}}">{{H(text.Ignore)}}</p>
            """;
        if (link is not null)
            afterButton += $"""

                    <p style="margin:16px 0 0;color:#6b7280;font-size:11px;line-height:1.5;word-break:break-all">{H(words.Fallback)} {H(link)}</p>
                """;
        var html = EmailLayout.Render(new EmailLayoutRequest(Company(brand), Language(emailLanguage), text.Badge, text.Heading, body)
        {
            HeaderColor = options.HeaderColor,
            AccentColor = options.AccentColor,
            Button = link is null ? null : new EmailButton(text.Button, link),
            AfterButtonHtml = afterButton,
            Footer = F(words.Footer, brand.LegalName is { Length: > 0 } legal ? legal : brand.Name),
        });
        return new EmailMessage(to, F(subject, brand.Name), html);
    }
}

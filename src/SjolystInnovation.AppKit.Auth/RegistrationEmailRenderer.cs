using System.Globalization;
using SjolystInnovation.AppKit.Email;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>The words of one sign-up email in one language.</summary>
/// <param name="Badge">The label in the header.</param>
/// <param name="Subject">The subject. <c>{0}</c> is the brand name.</param>
/// <param name="Heading">The heading above the body.</param>
/// <param name="Intro">The first paragraph. <c>{0}</c> is the brand name and <c>{1}</c> is the removal date.</param>
/// <param name="Step">A paragraph that names the step in words, used when there is no page to link to. Null for none.</param>
/// <param name="Button">The button text.</param>
/// <param name="Closing">The last paragraph.</param>
/// <param name="AppIntro">The first paragraph when there is no page to link to, so the person enters the code in the app. Null to keep <paramref name="Intro"/>.</param>
internal sealed record RegistrationEmailText(string Badge, string Subject, string Heading, string Intro, string? Step, string Button, string Closing, string? AppIntro = null);

// Renders the two sign-up emails that carry no reset or invite code: the notice that someone tried to sign up with an
// address that has an account, and the reminder that an unverified account is about to be removed. They use the shared layout.
internal static class RegistrationEmailRenderer
{
    private static readonly string[] EnglishMonths =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    private static readonly string[] NorwegianMonths =
        ["januar", "februar", "mars", "april", "mai", "juni", "juli", "august", "september", "oktober", "november", "desember"];

    private static readonly RegistrationEmailText EnglishAlreadyRegistered = new(
        "Sign-up", "Someone tried to sign up at {0}", "Someone tried to sign up with your email",
        "Someone tried to sign up at {0} with this email address, but it already has an account. If it was you and you cannot sign in, reset your password.",
        "On the sign-in page, choose that you forgot your password.", "Reset password",
        "If it was not you, nothing has changed and you do not need to do anything.");

    private static readonly RegistrationEmailText NorwegianAlreadyRegistered = new(
        "Registrering", "Noen prøvde å registrere seg hos {0}", "Noen prøvde å registrere seg med e-posten din",
        "Noen prøvde å registrere seg hos {0} med denne e-postadressen, men den har allerede en konto. Var det deg og du ikke kommer inn, kan du tilbakestille passordet.",
        "Velg at du har glemt passordet på innloggingssiden.", "Tilbakestill passordet",
        "Har du ikke gjort det selv, er ingenting endret, og du trenger ikke gjøre noe.");

    private static readonly RegistrationEmailText EnglishReminder = new(
        "Reminder", "Your account at {0} will be removed", "Verify your email to keep your account",
        "The email address of your account at {0} is not verified yet. If it stays unverified, the account is removed on {1}. To verify it, sign in and enter the code below.",
        null, "Verify email",
        "If you do not want the account, ignore this email. It is then removed on the date above.",
        "The email address of your account at {0} is not verified yet. If it stays unverified, the account is removed on {1}. To verify it, sign in to the app and enter the code below.");

    private static readonly RegistrationEmailText NorwegianReminder = new(
        "Påminnelse", "Kontoen din hos {0} blir fjernet", "Bekreft e-posten din for å beholde kontoen",
        "E-postadressen til kontoen din hos {0} er ikke bekreftet ennå. Blir den ikke bekreftet, fjernes kontoen {1}. Logg inn og skriv inn koden under for å bekrefte den.",
        null, "Bekreft e-post",
        "Vil du ikke ha kontoen, kan du se bort fra e-posten. Den fjernes da på datoen over.",
        "E-postadressen til kontoen din hos {0} er ikke bekreftet ennå. Blir den ikke bekreftet, fjernes kontoen {1}. Logg inn i appen og skriv inn koden under for å bekrefte den.");

    // The date as a person reads it in the email's language, without depending on the culture data of the host.
    internal static string Date(DateTime date, EmailLanguage language) => language == EmailLanguage.Norwegian
        ? $"{date.Day.ToString(CultureInfo.InvariantCulture)}. {NorwegianMonths[date.Month - 1]} {date.Year.ToString(CultureInfo.InvariantCulture)}"
        : $"{date.Day.ToString(CultureInfo.InvariantCulture)} {EnglishMonths[date.Month - 1]} {date.Year.ToString(CultureInfo.InvariantCulture)}";

    // The notice has no code. It links to the reset page when the options hold one, and otherwise names the step in words.
    internal static EmailMessage AlreadyRegistered(AppKitAuthEmailOptions options, EmailLanguage language, EmailBrand brand, string? firstName, string to)
    {
        var text = language == EmailLanguage.Norwegian ? NorwegianAlreadyRegistered : EnglishAlreadyRegistered;
        var page = AppKitAuthEmailOptions.IsPageUrl(options.SetPasswordUrl) ? options.SetPasswordUrl : null;
        var words = AuthEmailRenderer.Words(language);
        var step = page is null && text.Step is { } s
            ? $"""<p style="margin:10px 0 0;{EmailLayout.TextStyle}">{AuthEmailRenderer.H(s)}</p>"""
            : string.Empty;
        var body = $"""
                {Hello(words, firstName)}
                <p style="margin:0;{EmailLayout.TextStyle}">{AuthEmailRenderer.H(AuthEmailRenderer.F(text.Intro, brand.Name))}</p>{step}
            """;
        var after = $"""<p style="margin:0;{EmailLayout.TextStyle}">{AuthEmailRenderer.H(text.Closing)}</p>""";
        return Build(options, language, brand, to, text, body, after, page, words);
    }

    // The reminder carries a fresh code and links to the verify page when the options hold one. Without one it tells the
    // person to enter the code in the app.
    internal static EmailMessage Reminder(AppKitAuthEmailOptions options, EmailLanguage language, EmailBrand brand, string? firstName, string to, string code, DateTime removalAt)
    {
        var text = language == EmailLanguage.Norwegian ? NorwegianReminder : EnglishReminder;
        var page = options.VerifyPage() is { } verify ? AuthEmailRenderer.Link(verify, to, code) : null;
        var words = AuthEmailRenderer.Words(language);
        var codeColor = EmailLayout.ReadsOnWhite(options.HeaderColor) ? options.HeaderColor : EmailLayout.Ink;
        var labelColor = EmailLayout.ReadsOnWhite(options.AccentColor) ? options.AccentColor : EmailLayout.Ink;
        var intro = string.Format(CultureInfo.InvariantCulture, page is null && text.AppIntro is { } appIntro ? appIntro : text.Intro, brand.Name, Date(removalAt, language));
        var body = $$"""
                {{Hello(words, firstName)}}
                <p style="margin:0;{{EmailLayout.TextStyle}}">{{AuthEmailRenderer.H(intro)}}</p>
                <div style="text-align:center;font-size:9px;font-weight:700;text-transform:uppercase;letter-spacing:.1em;color:{{labelColor}};margin:22px 0 6px">{{AuthEmailRenderer.H(words.CodeLabel)}}</div>
                <div style="text-align:center;font-family:'Courier New',Courier,monospace;font-size:32px;font-weight:700;letter-spacing:.3em;color:{{codeColor}};background:#f1f5f9;border:1px solid #cbd5e1;border-radius:8px;padding:18px 12px;margin:0 0 20px">{{AuthEmailRenderer.H(code)}}</div>
            """;
        var after = $"""
                <p style="margin:0 0 8px;{EmailLayout.TextStyle}">{AuthEmailRenderer.H(AuthEmailRenderer.F(words.Expiry, AuthEmailRenderer.Duration(EmailVerificationCodes.Lifetime, words)))}</p>
                <p style="margin:0;{EmailLayout.TextStyle}">{AuthEmailRenderer.H(text.Closing)}</p>
            """;
        return Build(options, language, brand, to, text, body, after, page, words);
    }

    private static string Hello(AuthEmailWords words, string? firstName) =>
        $"""<p style="margin:0 0 10px;{EmailLayout.TextStyle}">{(string.IsNullOrWhiteSpace(firstName) ? words.Hello : AuthEmailRenderer.F(words.HelloName, AuthEmailRenderer.H(firstName)))}</p>""";

    // The button and the fallback link appear only when there is a page to link to.
    private static EmailMessage Build(AppKitAuthEmailOptions options, EmailLanguage language, EmailBrand brand, string to, RegistrationEmailText text,
        string body, string afterButton, string? link, AuthEmailWords words)
    {
        if (link is not null)
            afterButton += $"""<p style="margin:16px 0 0;color:#6b7280;font-size:11px;line-height:1.5;word-break:break-all">{AuthEmailRenderer.H(words.Fallback)} {AuthEmailRenderer.H(link)}</p>""";
        var html = EmailLayout.Render(new EmailLayoutRequest(AuthEmailRenderer.Company(brand), AuthEmailRenderer.Language(language), text.Badge, text.Heading, body)
        {
            HeaderColor = options.HeaderColor,
            AccentColor = options.AccentColor,
            Button = link is null ? null : new EmailButton(text.Button, link),
            AfterButtonHtml = afterButton,
            Footer = AuthEmailRenderer.F(words.Footer, brand.LegalName is { Length: > 0 } legal ? legal : brand.Name),
        });
        return new EmailMessage(to, AuthEmailRenderer.F(text.Subject, brand.Name), html);
    }
}

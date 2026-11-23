using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SjolystInnovation.AppKit.Email;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public class IpShorteningTests
{
    [Theory]
    [InlineData("203.0.113.77", "203.0.113.0")]
    [InlineData("10.0.0.1", "10.0.0.0")]
    [InlineData("2001:db8:abcd:1234:5678:9abc:def0:1234", "2001:db8:abcd::")]
    [InlineData("::1", "::")]
    [InlineData("::ffff:203.0.113.77", "203.0.113.0")]
    public void Keeps_three_ipv4_parts_and_the_first_48_bits_of_ipv6(string ip, string expected) =>
        Assert.Equal(expected, IpShortening.Shorten(IPAddress.Parse(ip)));

    [Fact]
    public void No_address_gives_null() => Assert.Null(IpShortening.Shorten(null));

    [Fact]
    public void The_result_fits_the_consent_column()
    {
        var longest = IpShortening.Shorten(IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"))!;

        Assert.True(longest.Length <= 45);
    }
}

public class RegistrationEmailsTests
{
    private sealed class FakeSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.FromResult(new EmailResult(true));
        }
    }

    private sealed class FakeBrand : IEmailBrand
    {
        public Task<EmailBrand> GetBrandAsync(CancellationToken cancellationToken) => Task.FromResult(new EmailBrand("Pyttogpanne") { LegalName = "Pyttogpanne AS" });
    }

    private static readonly DateTime RemovalAt = new(2026, 12, 5, 9, 30, 0, DateTimeKind.Utc);

    private static (RegistrationEmails<IdentityTestUser> Emails, FakeSender Sender) Build(Action<AppKitAuthEmailOptions>? configure = null)
    {
        var sender = new FakeSender();
        var options = new AppKitAuthEmailOptions { SetPasswordUrl = "https://app.example/reset-password", VerifyEmailUrl = "https://app.example/profile" };
        configure?.Invoke(options);
        return (new RegistrationEmails<IdentityTestUser>(sender, new FakeBrand(), Options.Create(options), TimeProvider.System), sender);
    }

    private static IdentityTestUser User() => new() { Id = "u1", Email = "kari+test@example.no", FirstName = "Kari" };

    [Theory]
    [InlineData("Kari")]
    [InlineData("<b>Kari</b>")]
    public async Task The_verification_email_uses_the_neutral_greeting_whatever_name_the_user_holds(string firstName)
    {
        var (emails, sender) = Build();
        var user = User();
        user.FirstName = firstName;
        var expires = EmailVerificationCodes.Lifetime;
        user.EmailVerificationCodeExpiration = DateTime.UtcNow + expires;

        await emails.SendVerificationAsync(user, "ABC123", "en", CancellationToken.None);

        var email = Assert.Single(sender.Sent);
        Assert.Equal("kari+test@example.no", email.To);
        Assert.Equal("Verify your email", email.Subject);
        Assert.Contains(">Hi,</p>", email.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Kari", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(">ABC123</div>", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://app.example/profile?email=kari%2Btest%40example.no&amp;code=ABC123\"", email.HtmlBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EmailLanguage.English, "nb", "Bekreft e-posten din")]
    [InlineData(EmailLanguage.English, "no", "Bekreft e-posten din")]
    [InlineData(EmailLanguage.English, "nn", "Bekreft e-posten din")]
    [InlineData(EmailLanguage.English, "nb-NO", "Bekreft e-posten din")]
    [InlineData(EmailLanguage.English, "NB_no", "Bekreft e-posten din")]
    [InlineData(EmailLanguage.Norwegian, "en", "Verify your email")]
    [InlineData(EmailLanguage.Norwegian, "en-GB", "Verify your email")]
    [InlineData(EmailLanguage.Norwegian, "fr", "Bekreft e-posten din")]
    [InlineData(EmailLanguage.Norwegian, "", "Bekreft e-posten din")]
    [InlineData(EmailLanguage.Norwegian, null, "Bekreft e-posten din")]
    [InlineData(EmailLanguage.English, "fr", "Verify your email")]
    [InlineData(EmailLanguage.English, "xx-yy-zz", "Verify your email")]
    [InlineData(EmailLanguage.English, null, "Verify your email")]
    public async Task The_verification_email_is_written_in_the_language_of_the_locale_or_else_the_configured_one(EmailLanguage configured, string? locale, string subject)
    {
        var (emails, sender) = Build(o => o.Language = configured);
        var user = User();
        user.EmailVerificationCodeExpiration = DateTime.UtcNow + EmailVerificationCodes.Lifetime;

        await emails.SendVerificationAsync(user, "ABC123", locale, CancellationToken.None);

        Assert.Equal(subject, Assert.Single(sender.Sent).Subject);
    }

    [Theory]
    [InlineData(EmailLanguage.English, "nb", ">Hei Kari,</p>")]
    [InlineData(EmailLanguage.Norwegian, "en", ">Hi Kari,</p>")]
    [InlineData(EmailLanguage.Norwegian, "fr", ">Hei Kari,</p>")]
    [InlineData(EmailLanguage.English, null, ">Hi Kari,</p>")]
    public async Task The_already_registered_email_is_written_in_the_language_of_the_locale_or_else_the_configured_one(EmailLanguage configured, string? locale, string greeting)
    {
        var (emails, sender) = Build(o => o.Language = configured);

        await emails.SendAlreadyRegisteredAsync(User(), locale, CancellationToken.None);

        Assert.Contains(greeting, Assert.Single(sender.Sent).HtmlBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EmailLanguage.English, "nb", ">Hei,</p>", "5. desember 2026")]
    [InlineData(EmailLanguage.Norwegian, "en", ">Hi,</p>", "5 December 2026")]
    [InlineData(EmailLanguage.Norwegian, "fr", ">Hei,</p>", "5. desember 2026")]
    [InlineData(EmailLanguage.English, null, ">Hi,</p>", "5 December 2026")]
    public async Task The_removal_reminder_is_written_in_the_language_of_the_locale_or_else_the_configured_one(EmailLanguage configured, string? locale, string greeting, string date)
    {
        var (emails, sender) = Build(o => o.Language = configured);

        await emails.SendRemovalReminderAsync(User(), "AB12CD", RemovalAt, locale, CancellationToken.None);

        var email = Assert.Single(sender.Sent);
        Assert.Contains(greeting, email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(date, email.HtmlBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", "Open the Pyttogpanne app and enter the code below to confirm this email address.", "Button not working?")]
    [InlineData("nb", "Åpne appen Pyttogpanne og skriv inn koden under for å bekrefte denne e-postadressen.", "Virker ikke knappen?")]
    public async Task The_verification_email_without_a_verify_page_shows_the_code_and_says_to_enter_it_in_the_app(string locale, string intro, string fallback)
    {
        var (emails, sender) = Build(o => { o.VerifyEmailUrl = string.Empty; o.VerifyInApp = true; });
        var user = User();
        user.EmailVerificationCodeExpiration = DateTime.UtcNow + EmailVerificationCodes.Lifetime;

        await emails.SendVerificationAsync(user, "ABC123", locale, CancellationToken.None);

        var html = WebUtility.HtmlDecode(Assert.Single(sender.Sent).HtmlBody);
        Assert.Contains(intro, html, StringComparison.Ordinal);
        Assert.Contains(">ABC123</div>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=", html, StringComparison.Ordinal);
        Assert.DoesNotContain(fallback, html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/profile")]
    [InlineData("treeclicker://verify")]
    public async Task The_verification_email_with_a_verify_page_that_is_not_an_http_address_fails_with_a_message_that_names_the_option(string page)
    {
        var (emails, sender) = Build(o => o.VerifyEmailUrl = page);
        var user = User();
        user.EmailVerificationCodeExpiration = DateTime.UtcNow + EmailVerificationCodes.Lifetime;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => emails.SendVerificationAsync(user, "ABC123", null, CancellationToken.None));

        Assert.Contains("VerifyEmailUrl", failure.Message, StringComparison.Ordinal);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Without_a_verify_page_or_VerifyInApp_both_emails_fail_with_a_message_that_names_the_options()
    {
        var (emails, sender) = Build(o => o.VerifyEmailUrl = string.Empty);
        var user = User();
        user.EmailVerificationCodeExpiration = DateTime.UtcNow + EmailVerificationCodes.Lifetime;

        var verification = await Assert.ThrowsAsync<InvalidOperationException>(() => emails.SendVerificationAsync(user, "ABC123", null, CancellationToken.None));
        var reminder = await Assert.ThrowsAsync<InvalidOperationException>(() => emails.SendRemovalReminderAsync(User(), "AB12CD", RemovalAt, null, CancellationToken.None));

        Assert.Contains("VerifyInApp", verification.Message, StringComparison.Ordinal);
        Assert.Contains("VerifyEmailUrl", reminder.Message, StringComparison.Ordinal);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task VerifyInApp_with_a_verify_page_fails_with_a_message_that_names_the_options()
    {
        var (emails, sender) = Build(o => o.VerifyInApp = true);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => emails.SendRemovalReminderAsync(User(), "AB12CD", RemovalAt, null, CancellationToken.None));

        Assert.Contains("VerifyInApp", failure.Message, StringComparison.Ordinal);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task The_removal_reminder_with_a_verify_page_that_is_not_an_http_address_fails_with_a_message_that_names_the_option()
    {
        var (emails, sender) = Build(o => o.VerifyEmailUrl = "javascript:alert(1)");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => emails.SendRemovalReminderAsync(User(), "AB12CD", RemovalAt, null, CancellationToken.None));

        Assert.Contains("VerifyEmailUrl", failure.Message, StringComparison.Ordinal);
        Assert.Empty(sender.Sent);
    }

    [Theory]
    [InlineData("en", "If it stays unverified, the account is removed on 5 December 2026. To verify it, sign in to the app and enter the code below.", "Verify email")]
    [InlineData("nb", "Blir den ikke bekreftet, fjernes kontoen 5. desember 2026. Logg inn i appen og skriv inn koden under for å bekrefte den.", "Bekreft e-post")]
    public async Task The_removal_reminder_without_a_verify_page_says_to_enter_the_code_in_the_app(string locale, string steps, string button)
    {
        var (emails, sender) = Build(o => { o.VerifyEmailUrl = string.Empty; o.VerifyInApp = true; });

        await emails.SendRemovalReminderAsync(User(), "AB12CD", RemovalAt, locale, CancellationToken.None);

        var html = WebUtility.HtmlDecode(Assert.Single(sender.Sent).HtmlBody);
        Assert.Contains(steps, html, StringComparison.Ordinal);
        Assert.Contains(">AB12CD</div>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=", html, StringComparison.Ordinal);
        Assert.DoesNotContain($">{button}</a>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", "If it stays unverified, the account is removed on 5 December 2026. To verify it, sign in and enter the code below.", ">Verify email</a>")]
    [InlineData("nb", "Blir den ikke bekreftet, fjernes kontoen 5. desember 2026. Logg inn og skriv inn koden under for å bekrefte den.", ">Bekreft e-post</a>")]
    public async Task The_removal_reminder_with_a_verify_page_keeps_its_text_and_its_button(string locale, string steps, string button)
    {
        var (emails, sender) = Build();

        await emails.SendRemovalReminderAsync(User(), "AB12CD", RemovalAt, locale, CancellationToken.None);

        var html = WebUtility.HtmlDecode(Assert.Single(sender.Sent).HtmlBody);
        Assert.Contains(steps, html, StringComparison.Ordinal);
        Assert.Contains(button, html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://app.example/profile?email=kari%2Btest%40example.no&code=AB12CD\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_removal_reminder_uses_the_neutral_greeting_whatever_name_the_user_holds()
    {
        var (emails, sender) = Build();

        await emails.SendRemovalReminderAsync(User(), "AB12CD", RemovalAt, null, CancellationToken.None);

        var email = Assert.Single(sender.Sent);
        Assert.Contains(">Hi,</p>", email.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Kari", email.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_already_registered_email_has_no_code_and_links_to_the_reset_page()
    {
        var (emails, sender) = Build();
        var user = User();
        EmailVerificationCodes.Issue(user);

        await emails.SendAlreadyRegisteredAsync(user, "en", CancellationToken.None);

        var email = Assert.Single(sender.Sent);
        Assert.Equal("kari+test@example.no", email.To);
        Assert.Equal("Someone tried to sign up at Pyttogpanne", email.Subject);
        Assert.Contains(">Hi Kari,</p>", email.HtmlBody);
        Assert.Contains("If it was you and you cannot sign in, reset your password.", email.HtmlBody);
        Assert.Contains("If it was not you, nothing has changed and you do not need to do anything.", email.HtmlBody);
        Assert.Contains("<a href=\"https://app.example/reset-password\"", email.HtmlBody);
        Assert.Contains(">Reset password</a>", email.HtmlBody);
        Assert.DoesNotContain("Your code", email.HtmlBody);
        Assert.DoesNotContain("code=", email.HtmlBody);
        Assert.DoesNotContain(user.EmailVerificationCodeHash!, email.HtmlBody);
    }

    [Fact]
    public async Task The_already_registered_email_names_the_step_in_words_when_there_is_no_reset_page()
    {
        var (emails, sender) = Build(o => o.SetPasswordUrl = string.Empty);

        await emails.SendAlreadyRegisteredAsync(User(), null, CancellationToken.None);

        var email = Assert.Single(sender.Sent);
        Assert.DoesNotContain("href=", email.HtmlBody);
        Assert.Contains("On the sign-in page, choose that you forgot your password.", email.HtmlBody);
        Assert.Contains("If it was not you, nothing has changed", email.HtmlBody);
    }

    [Fact]
    public async Task The_removal_reminder_gives_the_date_the_steps_and_the_fresh_code()
    {
        var (emails, sender) = Build();

        await emails.SendRemovalReminderAsync(User(), "AB12CD", RemovalAt, "en", CancellationToken.None);

        var email = Assert.Single(sender.Sent);
        Assert.Equal("kari+test@example.no", email.To);
        Assert.Equal("Your account at Pyttogpanne will be removed", email.Subject);
        Assert.Contains("is not verified yet", email.HtmlBody);
        Assert.Contains("is removed on 5 December 2026", email.HtmlBody);
        Assert.Contains("sign in and enter the code below", email.HtmlBody);
        Assert.Contains(">AB12CD</div>", email.HtmlBody);
        Assert.Contains("The code expires in 1 hour.", email.HtmlBody);
        Assert.Contains("<a href=\"https://app.example/profile?email=kari%2Btest%40example.no&amp;code=AB12CD\"", email.HtmlBody);
    }

    [Fact]
    public async Task The_removal_reminder_has_no_button_without_a_verify_page()
    {
        var (emails, sender) = Build(o => { o.VerifyEmailUrl = string.Empty; o.VerifyInApp = true; });

        await emails.SendRemovalReminderAsync(User(), "AB12CD", RemovalAt, null, CancellationToken.None);

        var email = Assert.Single(sender.Sent);
        Assert.DoesNotContain("href=", email.HtmlBody);
        Assert.Contains(">AB12CD</div>", email.HtmlBody);
    }

    [Fact]
    public async Task Both_emails_are_written_in_Norwegian_when_the_options_say_so()
    {
        var (emails, sender) = Build(o => o.Language = EmailLanguage.Norwegian);

        await emails.SendAlreadyRegisteredAsync(User(), null, CancellationToken.None);
        await emails.SendRemovalReminderAsync(User(), "AB12CD", RemovalAt, null, CancellationToken.None);

        Assert.Equal("Noen prøvde å registrere seg hos Pyttogpanne", sender.Sent[0].Subject);
        Assert.Contains("Har du ikke gjort det selv, er ingenting endret", sender.Sent[0].HtmlBody);
        Assert.Equal("Kontoen din hos Pyttogpanne blir fjernet", sender.Sent[1].Subject);
        Assert.Contains("5. desember 2026", sender.Sent[1].HtmlBody);
        Assert.Contains("Koden utl", sender.Sent[1].HtmlBody);
    }

    [Fact]
    public async Task A_name_and_brand_with_markup_are_encoded()
    {
        var (emails, sender) = Build();
        var user = User();
        user.FirstName = "<b>Kari</b>";

        await emails.SendAlreadyRegisteredAsync(user, null, CancellationToken.None);

        Assert.DoesNotContain("<b>Kari", sender.Sent[0].HtmlBody);
        Assert.Contains("&lt;b&gt;Kari&lt;/b&gt;", sender.Sent[0].HtmlBody);
    }

    [Fact]
    public void AddAppKitSelfRegistration_registers_the_default_with_TryAdd()
    {
        var services = new ServiceCollection();
        services.AddAppKitSelfRegistration<IdentityTestUser, RegistrationTestDbContext>(o => o.NewUser = _ => new IdentityTestUser());

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IRegistrationEmails<IdentityTestUser>));
        Assert.Equal(typeof(RegistrationEmails<IdentityTestUser>), descriptor.ImplementationType);
    }

    [Fact]
    public void An_app_that_registered_its_own_emails_first_keeps_them()
    {
        var own = new RegistrationEndpointsOwnEmails();
        var services = new ServiceCollection();
        services.AddSingleton<IRegistrationEmails<IdentityTestUser>>(own);
        services.AddAppKitSelfRegistration<IdentityTestUser, RegistrationTestDbContext>(o => o.NewUser = _ => new IdentityTestUser());

        Assert.Single(services, d => d.ServiceType == typeof(IRegistrationEmails<IdentityTestUser>));
    }
}

internal sealed class RegistrationEndpointsOwnEmails : IRegistrationEmails<IdentityTestUser>
{
    public Task SendVerificationAsync(IdentityTestUser user, string code, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SendAlreadyRegisteredAsync(IdentityTestUser user, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SendRemovalReminderAsync(IdentityTestUser user, string code, DateTime removalAt, string? locale, CancellationToken cancellationToken) => Task.CompletedTask;
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SjolystInnovation.AppKit.Email;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public class AuthEmailTests
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

    private sealed class FakeBrand(EmailBrand brand) : IEmailBrand
    {
        public Task<EmailBrand> GetBrandAsync(CancellationToken cancellationToken) => Task.FromResult(brand);
    }

    private static readonly EmailBrand Brand = new("Pyttogpanne")
    {
        LegalName = "Pyttogpanne AS",
        OrgNumber = "945678901",
        Address = "Torget 2, 5014 Bergen",
        Email = "post@pyttogpanne.example",
    };

    private readonly FakeSender _sender = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));

    private ServiceProvider Services(Action<AppKitAuthEmailOptions>? configure = null, EmailBrand? brand = null, bool verification = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmailSender>(_sender);
        services.AddSingleton<IEmailBrand>(new FakeBrand(brand ?? Brand));
        services.AddSingleton<TimeProvider>(_time);
        services.AddAppKitAuthEmails<IdentityTestUser>(o =>
        {
            o.SetPasswordUrl = "https://pyttogpanne.example/nb/reset-password";
            o.VerifyEmailUrl = "https://pyttogpanne.example/profile";
            configure?.Invoke(o);
        });
        if (verification)
            services.AddAppKitVerificationEmails<IdentityTestUser>();
        return services.BuildServiceProvider();
    }

    private IdentityTestUser User(TimeSpan? resetLifetime = null)
    {
        var user = new IdentityTestUser { Email = "kari+test@example.no", FirstName = "Kari" };
        PasswordResetCodes.Issue(user, _time, resetLifetime);
        return user;
    }

    private async Task<EmailMessage> Reset(Action<AppKitAuthEmailOptions>? configure = null, IdentityTestUser? user = null, EmailBrand? brand = null)
    {
        using var services = Services(configure, brand);
        await services.GetRequiredService<IAuthEmails<IdentityTestUser>>().SendPasswordResetAsync(user ?? User(), "AB12CD", default);
        return Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task A_reset_email_goes_to_the_user_with_the_code_brand_and_English_text()
    {
        var email = await Reset();
        Assert.Equal("kari+test@example.no", email.To);
        Assert.Equal("Password reset code", email.Subject);
        Assert.Contains(">AB12CD</div>", email.HtmlBody);
        Assert.Contains(">Pyttogpanne</div>", email.HtmlBody);
        Assert.Contains("Pyttogpanne AS<br>Org. no. 945 678 901<br>Torget 2, 5014 Bergen<br>post@pyttogpanne.example", email.HtmlBody);
        Assert.Contains(">Reset your password</h1>", email.HtmlBody);
        Assert.Contains(">Hi Kari,</p>", email.HtmlBody);
        Assert.Contains("reset your password at Pyttogpanne.", email.HtmlBody);
        Assert.Contains(">Password reset</span>", email.HtmlBody);
        Assert.Contains(">Choose a new password</a>", email.HtmlBody);
        Assert.Contains("The code expires in 30 minutes.", email.HtmlBody);
        Assert.Contains("Your password stays the same.", email.HtmlBody);
        Assert.Contains("because of your account at Pyttogpanne AS.", email.HtmlBody);
    }

    [Fact]
    public async Task The_button_and_the_fallback_link_carry_the_email_and_code()
    {
        var email = await Reset();
        const string Link = "https://pyttogpanne.example/nb/reset-password?email=kari%2Btest%40example.no&amp;code=AB12CD";
        Assert.Contains($"<a href=\"{Link}\"", email.HtmlBody);
        Assert.Contains($"Button not working? Copy this link: {Link}</p>", email.HtmlBody);
    }

    [Fact]
    public async Task A_page_address_with_a_query_string_gets_the_email_and_code_appended()
    {
        var email = await Reset(o => o.SetPasswordUrl = "https://garge.example/reset-password?from=email");
        Assert.Contains("href=\"https://garge.example/reset-password?from=email&amp;email=kari%2Btest%40example.no&amp;code=AB12CD\"", email.HtmlBody);
    }

    [Fact]
    public async Task Norwegian_emails_use_Norwegian_text()
    {
        var email = await Reset(o => o.Language = EmailLanguage.Norwegian);
        Assert.Equal("Tilbakestill passordet", email.Subject);
        Assert.Contains(">Hei Kari,</p>", email.HtmlBody);
        Assert.Contains(">Velg nytt passord</a>", email.HtmlBody);
        Assert.Contains("Koden utløper om 30 minutter.", email.HtmlBody.Replace("&#248;", "ø", StringComparison.Ordinal));
        Assert.Contains("Virker ikke knappen? Kopier denne lenken:", email.HtmlBody);
        Assert.Contains(">Koden din</div>", email.HtmlBody);
    }

    [Fact]
    public async Task The_colours_come_from_the_options()
    {
        var email = await Reset(o => { o.HeaderColor = "#0f3d3a"; o.AccentColor = "#00887a"; });
        Assert.Contains("background:#0f3d3a;color:#fff;padding:28px", email.HtmlBody);
        Assert.Contains("color:#0f3d3a;background:#f1f5f9", email.HtmlBody);
        Assert.Contains("background:#00887a;color:#fff;font-weight:700", email.HtmlBody);
        Assert.Contains("color:#00887a;margin:22px", email.HtmlBody);
        Assert.DoesNotContain("#182232", email.HtmlBody);
        Assert.DoesNotContain("#0284c7", email.HtmlBody);
    }

    [Fact]
    public async Task Dark_colours_get_white_text_and_keep_their_own_colour_for_the_label_and_code()
    {
        var email = await Reset(o => { o.HeaderColor = "#182232"; o.AccentColor = "#0284c7"; });
        Assert.Contains("background:#182232;color:#fff;padding", email.HtmlBody);
        Assert.Contains("background:rgba(255,255,255,.25);border:1px solid rgba(255,255,255,.5)", email.HtmlBody);
        Assert.Contains("background:#0284c7;color:#fff;font-weight:700", email.HtmlBody);
        Assert.DoesNotContain("#1a1a1a;padding", email.HtmlBody);
    }

    [Fact]
    public async Task A_light_brand_colour_gets_near_black_text_label_and_code()
    {
        var email = await Reset(o => { o.HeaderColor = "#ffd400"; o.AccentColor = "#ffd400"; });
        Assert.Contains("background:#ffd400;color:#1a1a1a;padding", email.HtmlBody);
        Assert.Contains("background:rgba(0,0,0,.08);border:1px solid rgba(0,0,0,.4)", email.HtmlBody);
        Assert.Contains("background:#ffd400;color:#1a1a1a;font-weight:700", email.HtmlBody);
        Assert.Contains("color:#1a1a1a;margin:22px", email.HtmlBody);
        Assert.Contains("color:#1a1a1a;background:#f1f5f9", email.HtmlBody);
        Assert.DoesNotContain("color:#ffd400", email.HtmlBody);
    }

    [Fact]
    public async Task The_header_and_the_accent_are_judged_separately()
    {
        var email = await Reset(o => { o.HeaderColor = "#182232"; o.AccentColor = "#ffd400"; });
        Assert.Contains("background:#182232;color:#fff;padding", email.HtmlBody);
        Assert.Contains("color:#182232;background:#f1f5f9", email.HtmlBody);
        Assert.Contains("background:#ffd400;color:#1a1a1a;font-weight:700", email.HtmlBody);
        Assert.Contains("color:#1a1a1a;margin:22px", email.HtmlBody);
    }

    [Fact]
    public async Task A_user_without_a_first_name_gets_a_plain_greeting()
    {
        var user = User();
        user.FirstName = " ";
        var email = await Reset(user: user);
        Assert.Contains(">Hi,</p>", email.HtmlBody);
    }

    [Fact]
    public async Task Text_from_the_user_and_the_brand_is_encoded()
    {
        var user = User();
        user.FirstName = "<b>Kari</b>";
        var email = await Reset(user: user, brand: new EmailBrand("A&B <Co>") { Address = "<script>" });
        Assert.Contains("Hi &lt;b&gt;Kari&lt;/b&gt;,", email.HtmlBody);
        Assert.Contains(">A&amp;B &lt;Co&gt;</div>", email.HtmlBody);
        Assert.Contains("&lt;script&gt;", email.HtmlBody);
        Assert.DoesNotContain("<b>Kari", email.HtmlBody);
        Assert.DoesNotContain("<script>", email.HtmlBody);
    }

    [Fact]
    public async Task Blank_brand_lines_are_left_out_and_the_footer_falls_back_to_the_name()
    {
        var email = await Reset(brand: new EmailBrand("Garge") { OrgNumber = "912 345 678", Email = "" });
        Assert.Contains("line-height:1.5\">Org. no. 912 345 678</div>", email.HtmlBody);
        Assert.Contains("because of your account at Garge.", email.HtmlBody);
    }

    [Theory]
    [InlineData(EmailLanguage.Norwegian, false, "Org.nr. 923 202 374")]
    [InlineData(EmailLanguage.Norwegian, true, "Org.nr. 923 202 374 MVA")]
    [InlineData(EmailLanguage.English, false, "Org. no. 923 202 374")]
    [InlineData(EmailLanguage.English, true, "Org. no. 923 202 374 MVA")]
    public async Task The_org_number_line_follows_the_email_language_and_the_VAT_flag(EmailLanguage language, bool vat, string line)
    {
        var brand = Brand with { OrgNumber = "923202374", VatRegistered = vat };
        var email = await Reset(o => o.Language = language, brand: brand);
        Assert.Contains($"Pyttogpanne AS<br>{line}<br>Torget 2, 5014 Bergen<br>post@pyttogpanne.example", email.HtmlBody);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_org_number_leaves_the_line_out_even_when_VAT_registered(string? number)
    {
        var email = await Reset(o => o.Language = EmailLanguage.Norwegian, brand: Brand with { OrgNumber = number, VatRegistered = true });
        Assert.Contains("Pyttogpanne AS<br>Torget 2, 5014 Bergen<br>post@pyttogpanne.example", email.HtmlBody);
        Assert.DoesNotContain("Org.nr.", email.HtmlBody);
        Assert.DoesNotContain("MVA", email.HtmlBody);
    }

    [Fact]
    public async Task An_invite_uses_the_invite_text_and_its_own_lifetime()
    {
        using var services = Services();
        await services.GetRequiredService<IAuthEmails<IdentityTestUser>>().SendInviteAsync(User(TimeSpan.FromDays(7)), "XY34ZW", default);
        var email = Assert.Single(_sender.Sent);
        Assert.Equal("Set your password at Pyttogpanne", email.Subject);
        Assert.Contains(">Invitation</span>", email.HtmlBody);
        Assert.Contains("An account has been created for you at Pyttogpanne.", email.HtmlBody);
        Assert.Contains(">Set your password</a>", email.HtmlBody);
        Assert.Contains("The code expires in 7 days.", email.HtmlBody);
        Assert.Contains("reset-password?email=kari%2Btest%40example.no&amp;code=XY34ZW", email.HtmlBody);
    }

    [Fact]
    public async Task A_verification_email_links_to_the_verify_page()
    {
        using var services = Services(verification: true);
        var user = new IdentityTestUser { Email = "kari@example.no", FirstName = "Kari" };
        var code = EmailVerificationCodes.Issue(user, _time);
        await services.GetRequiredService<IVerificationEmails<IdentityTestUser>>().SendEmailVerificationAsync(user, code, default);
        var email = Assert.Single(_sender.Sent);
        Assert.Equal("Verify your email", email.Subject);
        Assert.Contains(">Verify email</span>", email.HtmlBody);
        Assert.Contains("Enter the code below in Pyttogpanne to confirm this email address.", email.HtmlBody);
        Assert.Contains($"href=\"https://pyttogpanne.example/profile?email=kari%40example.no&amp;code={code}\"", email.HtmlBody);
        Assert.Contains("The code expires in 1 hour.", email.HtmlBody);
    }

    [Fact]
    public async Task The_lifetime_counts_from_now()
    {
        var user = User();
        _time.Advance(TimeSpan.FromMinutes(10));
        var email = await Reset(user: user);
        Assert.Contains("The code expires in 20 minutes.", email.HtmlBody);
    }

    [Fact]
    public async Task The_verification_lifetime_counts_from_now()
    {
        using var services = Services(verification: true);
        var user = new IdentityTestUser { Email = "kari@example.no" };
        var code = EmailVerificationCodes.Issue(user, _time);
        _time.Advance(TimeSpan.FromMinutes(40));
        await services.GetRequiredService<IVerificationEmails<IdentityTestUser>>().SendEmailVerificationAsync(user, code, default);
        Assert.Contains("The code expires in 20 minutes.", Assert.Single(_sender.Sent).HtmlBody);
    }

    [Fact]
    public async Task A_user_without_a_code_is_refused()
    {
        using var services = Services(verification: true);
        var user = new IdentityTestUser { Email = "kari@example.no" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.GetRequiredService<IAuthEmails<IdentityTestUser>>().SendPasswordResetAsync(user, "AB12CD", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.GetRequiredService<IVerificationEmails<IdentityTestUser>>().SendEmailVerificationAsync(user, "AB12CD", default));
        Assert.Empty(_sender.Sent);
    }

    [Theory]
    [InlineData(1, "1 minute")]
    [InlineData(0.2, "1 minute")]
    [InlineData(59, "59 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(89, "1 hour")]
    [InlineData(90, "2 hours")]
    [InlineData(1409, "23 hours")]
    [InlineData(1410, "1 day")]
    [InlineData(2880, "2 days")]
    [InlineData(10080, "7 days")]
    public async Task Lifetimes_read_in_the_nearest_unit(double minutes, string expected)
    {
        using var services = Services();
        var user = User(TimeSpan.FromMinutes(minutes));
        await services.GetRequiredService<IAuthEmails<IdentityTestUser>>().SendPasswordResetAsync(user, "AB12CD", default);
        Assert.Contains($"The code expires in {expected}.", Assert.Single(_sender.Sent).HtmlBody);
    }

    [Theory]
    [InlineData(1, "1 minutt")]
    [InlineData(30, "30 minutter")]
    [InlineData(60, "1 time")]
    [InlineData(120, "2 timer")]
    [InlineData(1440, "1 dag")]
    [InlineData(10080, "7 dager")]
    public async Task Norwegian_lifetimes_use_Norwegian_units(double minutes, string expected)
    {
        using var services = Services(o => o.Language = EmailLanguage.Norwegian);
        var user = User(TimeSpan.FromMinutes(minutes));
        await services.GetRequiredService<IAuthEmails<IdentityTestUser>>().SendPasswordResetAsync(user, "AB12CD", default);
        Assert.Contains($"Koden utl&#248;per om {expected}.", Assert.Single(_sender.Sent).HtmlBody);
    }

    [Theory]
    [InlineData("HeaderColor", "red")]
    [InlineData("HeaderColor", "#18223")]
    [InlineData("HeaderColor", "x#182232")]
    [InlineData("AccentColor", "#0284c7;background:url(x)")]
    [InlineData("SetPasswordUrl", "")]
    [InlineData("SetPasswordUrl", "/reset-password")]
    [InlineData("SetPasswordUrl", "javascript:alert(1)")]
    public void Bad_options_fail_at_startup(string option, string value)
    {
        using var services = Services(o =>
        {
            if (option == "HeaderColor") o.HeaderColor = value;
            if (option == "AccentColor") o.AccentColor = value;
            if (option == "SetPasswordUrl") o.SetPasswordUrl = value;
        });
        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<AppKitAuthEmailOptions>>().Value);
        Assert.Contains(option, error.Message);
    }

    [Fact]
    public void The_verify_page_is_required_only_with_verification_emails()
    {
        using var without = Services(o => o.VerifyEmailUrl = "");
        Assert.Equal("", without.GetRequiredService<IOptions<AppKitAuthEmailOptions>>().Value.VerifyEmailUrl);

        using var with = Services(o => o.VerifyEmailUrl = "", verification: true);
        var error = Assert.Throws<OptionsValidationException>(() => with.GetRequiredService<IOptions<AppKitAuthEmailOptions>>().Value);
        Assert.Contains("VerifyEmailUrl", error.Message);
    }

    [Fact]
    public void The_defaults_are_English_and_the_garge_colours()
    {
        var options = new AppKitAuthEmailOptions();
        Assert.Equal(EmailLanguage.English, options.Language);
        Assert.Equal("#182232", options.HeaderColor);
        Assert.Equal("#0284c7", options.AccentColor);
    }
}

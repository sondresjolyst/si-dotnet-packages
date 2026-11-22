using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public class SelfRegistrationTests
{
    private static TestUser NewUser(AppKitRegistration r) => new() { Email = r.Email, UserName = r.Email };

    private static ServiceProvider Build(Action<AppKitRegistrationOptions<TestUser>>? configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppKitSelfRegistration<TestUser, ConsentTestDbContext>(o =>
        {
            o.NewUser = NewUser;
            configure?.Invoke(o);
        });
        return services.BuildServiceProvider();
    }

    private static AppKitRegistrationOptions<TestUser> Options(ServiceProvider provider) =>
        provider.GetRequiredService<IOptions<AppKitRegistrationOptions<TestUser>>>().Value;

    private static string Failure(Action<AppKitRegistrationOptions<TestUser>> configure)
    {
        using var provider = Build(configure);
        return Assert.Throws<OptionsValidationException>(() => Options(provider)).Message;
    }

    private static async Task<string> StartFailureAsync(Action<ServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppKitSelfRegistration<TestUser, ConsentTestDbContext>(o => o.NewUser = NewUser);
        configure(services);
        using var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<IAsyncStartupValidator>();
        return (await Assert.ThrowsAsync<OptionsValidationException>(() => validator.ValidateAsync(TestContext.Current.CancellationToken))).Message;
    }

    [Fact]
    public async Task Sign_up_fails_at_start_when_AddAppKitAuth_was_not_called()
    {
        var message = await StartFailureAsync(_ => { });

        Assert.Contains("AddAppKitAuth", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_up_fails_at_start_when_the_signing_key_is_shorter_than_AddAppKitAuth_allows()
    {
        var message = await StartFailureAsync(s => s.Configure<AppKitAuthOptions>(o => o.Key = new string('k', AppKitAuthOptions.MinimumKeyBytes - 1)));

        Assert.Contains("signing key", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_up_starts_with_a_signing_key_of_the_length_AddAppKitAuth_requires()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppKitSelfRegistration<TestUser, ConsentTestDbContext>(o => o.NewUser = NewUser);
        services.Configure<AppKitAuthOptions>(o => o.Key = new string('k', AppKitAuthOptions.MinimumKeyBytes));
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IAsyncStartupValidator>().ValidateAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void The_defaults_match_the_design()
    {
        using var provider = Build(null);
        var o = Options(provider);

        Assert.Null(o.Role);
        Assert.Equal(16, o.MinimumAge);
        Assert.Equal(20, o.MaxSignUpsPerNetworkPerDay);
        Assert.Equal("terms", o.TermsDocument);
        Assert.False(o.RequireNames);
        Assert.False(o.AllowUserName);
        Assert.False(o.RecordTruncatedIp);
        Assert.Equal(TimeSpan.FromMinutes(1), o.ResendCooldown);
        Assert.Equal(TimeSpan.FromDays(30), o.UnverifiedLifetime);
        Assert.Equal([TimeSpan.FromDays(14), TimeSpan.FromDays(3)], o.RemindBefore);
    }

    [Fact]
    public void NewUser_builds_the_user_from_the_registration()
    {
        using var provider = Build(null);

        var user = Options(provider).NewUser!(new AppKitRegistration("a@example.com", null, "Ada", "Lovelace", "nb"));

        Assert.Equal("a@example.com", user.Email);
    }

    [Fact]
    public void The_registration_record_carries_the_fields_the_sign_up_asks_for()
    {
        var r = new AppKitRegistration("a@example.com", "ada", "Ada", "Lovelace", "nb");

        Assert.Equal(("a@example.com", "ada", "Ada", "Lovelace", "nb"), (r.Email, r.UserName, r.FirstName, r.LastName, r.Locale));
    }

    [Fact]
    public void A_missing_NewUser_fails_at_start_with_a_message_that_names_it()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppKitSelfRegistration<TestUser, ConsentTestDbContext>(_ => { });
        using var provider = services.BuildServiceProvider();

        var message = Assert.Throws<OptionsValidationException>(() => Options(provider)).Message;

        Assert.Contains("NewUser", message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_unverified_lifetime_that_is_not_positive_fails(int days) =>
        Assert.Contains("lifetime", Failure(o => o.UnverifiedLifetime = TimeSpan.FromDays(days)));

    [Fact]
    public void A_reminder_that_is_not_positive_fails() =>
        Assert.Contains("reminder", Failure(o => o.RemindBefore = [TimeSpan.Zero]));

    [Fact]
    public void A_reminder_at_or_after_the_lifetime_fails() =>
        Assert.Contains("reminder", Failure(o => { o.UnverifiedLifetime = TimeSpan.FromDays(10); o.RemindBefore = [TimeSpan.FromDays(10)]; }));

    [Fact]
    public void A_negative_resend_cooldown_fails() =>
        Assert.Contains("cooldown", Failure(o => o.ResendCooldown = TimeSpan.FromSeconds(-1)));

    [Fact]
    public void A_network_cap_that_is_not_positive_fails() =>
        Assert.Contains("network", Failure(o => o.MaxSignUpsPerNetworkPerDay = 0));

    [Fact]
    public void A_minimum_age_that_is_not_positive_fails() =>
        Assert.Contains("age", Failure(o => o.MinimumAge = 0));

    [Theory]
    [InlineData("terms.v2")]
    [InlineData("terms of use")]
    [InlineData("a-name-that-is-longer-than-32-chars")]
    public void A_terms_document_with_other_characters_or_over_32_characters_fails(string name) =>
        Assert.Contains("document", Failure(o => o.TermsDocument = name));

    [Theory]
    [InlineData("tos")]
    [InlineData("terms_of-use2")]
    [InlineData("a-name-that-is-exactly-32-chars-")]
    public void A_terms_document_of_letters_digits_hyphens_and_underscores_is_allowed(string name)
    {
        using var provider = Build(o => o.TermsDocument = name);

        Assert.Equal(name, Options(provider).TermsDocument);
    }

    [Fact]
    public void A_blank_terms_document_fails() =>
        Assert.Contains("document", Failure(o => o.TermsDocument = " "));

    [Fact]
    public void There_are_no_further_documents_by_default()
    {
        using var provider = Build(null);

        Assert.Empty(Options(provider).AdditionalDocuments);
    }

    [Fact]
    public void Further_documents_with_distinct_names_are_allowed()
    {
        using var provider = Build(o => o.AdditionalDocuments = ["privacy", "cookies"]);

        Assert.Equal(["privacy", "cookies"], Options(provider).AdditionalDocuments);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    [InlineData("terms")]
    [InlineData("age")]
    [InlineData("privacy,privacy")]
    [InlineData("privacy ")]
    [InlineData("a-name-that-is-longer-than-32-chars")]
    [InlineData("privacy.v2")]
    [InlineData("pri vacy")]
    [InlineData("personvern-ø")]
    public void A_further_document_that_is_blank_taken_repeated_or_too_long_fails(string names) =>
        Assert.Contains("further document", Failure(o => o.AdditionalDocuments = names.Split(',')));

    [Fact]
    public void A_null_list_of_further_documents_fails() =>
        Assert.Contains("further document", Failure(o => o.AdditionalDocuments = null!));

    [Fact]
    public void No_reminders_is_allowed()
    {
        using var provider = Build(o => o.RemindBefore = []);

        Assert.Empty(Options(provider).RemindBefore);
    }

    [Fact]
    public void The_marker_service_shows_that_the_app_opted_in()
    {
        using var provider = Build(null);

        Assert.NotNull(provider.GetService<SelfRegistrationRegistered>());
    }

    [Fact]
    public void Registering_twice_keeps_one_marker()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppKitSelfRegistration<TestUser, ConsentTestDbContext>(o => o.NewUser = NewUser);
        services.AddAppKitSelfRegistration<TestUser, ConsentTestDbContext>(o => o.NewUser = NewUser);

        Assert.Single(services, d => d.ServiceType == typeof(SelfRegistrationRegistered));
    }

    [Fact]
    public void The_consent_service_resolves_in_a_scope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ConsentTestDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        services.AddAppKitSelfRegistration<TestUser, ConsentTestDbContext>(o => o.NewUser = NewUser);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<AccountConsentService<ConsentTestDbContext>>());
    }
}

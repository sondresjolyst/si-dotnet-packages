using System.Net;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private sealed class FakeVerificationEmails : IVerificationEmails<IdentityTestUser>
    {
        public List<(string UserId, string Code)> Sent { get; } = [];

        public Task SendEmailVerificationAsync(IdentityTestUser user, string code, CancellationToken cancellationToken)
        {
            Sent.Add((user.Id, code));
            return Task.CompletedTask;
        }
    }

    private static (RegistrationEmails<IdentityTestUser> Emails, FakeVerificationEmails Inner, FakeLogCollector Logs) Build(RegistrationEmailsWarning? warning = null)
    {
        var logs = new FakeLogCollector();
        var inner = new FakeVerificationEmails();
        var logger = new FakeLogger<RegistrationEmails<IdentityTestUser>>(logs);
        return (new RegistrationEmails<IdentityTestUser>(inner, warning ?? new RegistrationEmailsWarning(), logger), inner, logs);
    }

    [Fact]
    public async Task The_verification_email_goes_through_IVerificationEmails()
    {
        var (emails, inner, logs) = Build();

        await emails.SendVerificationAsync(new IdentityTestUser { Id = "u1" }, "ABC123", "nb", CancellationToken.None);

        Assert.Equal([("u1", "ABC123")], inner.Sent);
        Assert.Empty(logs.GetSnapshot());
    }

    [Fact]
    public async Task The_other_two_emails_send_nothing_and_log_one_warning_in_all()
    {
        var warning = new RegistrationEmailsWarning();
        var (first, inner, logs) = Build(warning);
        var (second, _, _) = Build(warning);
        var user = new IdentityTestUser { Id = "u1" };

        await first.SendAlreadyRegisteredAsync(user, "nb", CancellationToken.None);
        await first.SendRemovalReminderAsync(user, "ABC123", DateTime.UtcNow, "nb", CancellationToken.None);
        await second.SendAlreadyRegisteredAsync(user, "nb", CancellationToken.None);

        Assert.Empty(inner.Sent);
        var entry = Assert.Single(logs.GetSnapshot());
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain("ABC123", entry.Message, StringComparison.Ordinal);
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

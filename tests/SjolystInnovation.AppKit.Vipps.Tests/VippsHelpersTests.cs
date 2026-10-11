using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace SjolystInnovation.AppKit.Vipps.Tests;

public class VippsHelpersTests
{
    [Theory]
    [InlineData("abc", 45, "abc")]
    [InlineData("abcdef", 3, "abc")]
    [InlineData("ab\U0001F600cd", 3, "ab")]
    [InlineData("ab\U0001F600cd", 4, "ab\U0001F600")]
    public void Truncate_NeverSplitsASurrogatePair(string value, int max, string expected) =>
        Assert.Equal(expected, VippsLimits.Truncate(value, max));

    [Theory]
    [InlineData("1.4.2+0123456789abcdef0123456789abcdef01234567", "1.4.2")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("garge-api-with-a-name-longer-than-thirty", "garge-api-with-a-name-longer-t")]
    public void SystemHeader_DropsBuildMetadataAndKeepsAtMost30Characters(string value, string expected) =>
        Assert.Equal(expected, VippsLimits.SystemHeader(value));

    [Fact]
    public void Format_NullOrEmptyAddress_IsNull()
    {
        Assert.Null(VippsAddressFormatter.Format(null));
        Assert.Null(VippsAddressFormatter.Format(new VippsAddress { StreetAddress = "", Country = null }));
    }

    [Fact]
    public void Format_PrefersTheFormattedAddress() =>
        Assert.Equal("Gate 1, 0001 Oslo", VippsAddressFormatter.Format(new VippsAddress { Formatted = "Gate 1, 0001 Oslo", StreetAddress = "x" }));

    [Fact]
    public void Format_JoinsThePartsItHas() =>
        Assert.Equal("Gate 1, 0001, NO", VippsAddressFormatter.Format(new VippsAddress { StreetAddress = "Gate 1", PostalCode = "0001", Region = "", Country = "NO" }));

    [Fact]
    public void Format_CutsToTheMaxLength()
    {
        Assert.Equal("Gate", VippsAddressFormatter.Format(new VippsAddress { Formatted = "Gate 1" }, 4));
        Assert.Equal("Gate 1", VippsAddressFormatter.Format(new VippsAddress { Formatted = "Gate 1" }, 6));
    }

    [Fact]
    public void Credentials_AreConfiguredOnlyWhenEveryPartIsSet()
    {
        var full = new VippsCredentials { BaseUrl = "https://api.vipps.no", ClientId = "a", ClientSecret = "b", SubscriptionKey = "c", MerchantSerialNumber = "d" };
        Assert.True(full.IsConfigured);
        Assert.False(WithClientSecret(full, "").IsConfigured);
        foreach (var missing in new Func<VippsCredentials>[]
        {
            () => new() { ClientId = "a", ClientSecret = "b", SubscriptionKey = "c", MerchantSerialNumber = "d" },
            () => new() { BaseUrl = "u", ClientSecret = "b", SubscriptionKey = "c", MerchantSerialNumber = "d" },
            () => new() { BaseUrl = "u", ClientId = "a", SubscriptionKey = "c", MerchantSerialNumber = "d" },
            () => new() { BaseUrl = "u", ClientId = "a", ClientSecret = "b", MerchantSerialNumber = "d" },
            () => new() { BaseUrl = "u", ClientId = "a", ClientSecret = "b", SubscriptionKey = "c" },
        })
        {
            Assert.False(missing().IsConfigured);
        }
        Assert.False(new VippsOptions().Live.IsConfigured);
        Assert.Equal("https://apitest.vipps.no", new VippsOptions().For(true).BaseUrl);
        Assert.Equal("https://api.vipps.no", new VippsOptions().For(false).BaseUrl);
    }

    private static VippsCredentials WithClientSecret(VippsCredentials c, string secret) => new()
    {
        BaseUrl = c.BaseUrl, ClientId = c.ClientId, ClientSecret = secret, SubscriptionKey = c.SubscriptionKey, MerchantSerialNumber = c.MerchantSerialNumber,
    };

    [Fact]
    public void Events_ListWhatEachFlowNeeds()
    {
        Assert.Equal(7, VippsEvents.Payments.Count);
        Assert.All(VippsEvents.Payments, e => Assert.StartsWith("epayments.payment.", e, StringComparison.Ordinal));
        Assert.Equal(7, VippsEvents.Recurring.Count);
        Assert.All(VippsEvents.Recurring, e => Assert.StartsWith("recurring.", e, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddVipps_RegistersTheClient_LiveByDefault()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddVipps(o => o.SystemName = "garge-api");
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<VippsClient>(provider.GetRequiredService<IVippsClient>());
        Assert.False(await provider.GetRequiredService<IVippsEnvironment>().IsTestAsync());
        Assert.Equal("garge-api", provider.GetRequiredService<IOptions<VippsOptions>>().Value.SystemName);
    }

    [Fact]
    public async Task AddVipps_KeepsTheAppsOwnEnvironment()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IVippsEnvironment>(new FixedVippsEnvironment(true));
        services.AddVipps(_ => { });
        await using var provider = services.BuildServiceProvider();
        Assert.True(await provider.GetRequiredService<IVippsClient>().IsTestAsync());
    }
}

using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Vipps.Tests;

public class VippsWebhookSecretProtectorTests
{
    private static readonly EphemeralDataProtectionProvider Keys = new();

    private static VippsWebhookSecretProtector Create(Action<VippsWebhookOptions>? configure = null, IDataProtectionProvider? provider = null)
    {
        var options = new VippsWebhookOptions();
        configure?.Invoke(options);
        return new VippsWebhookSecretProtector(provider ?? Keys, Options.Create(options));
    }

    [Fact]
    public void RoundTrip_RestoresTheSecret()
    {
        var p = Create();
        var protectedSecret = p.Protect("vipps-webhook-secret-abc-123");

        Assert.StartsWith("enc:v1:", protectedSecret);
        Assert.DoesNotContain("vipps-webhook-secret", protectedSecret, StringComparison.Ordinal);
        Assert.Equal("vipps-webhook-secret-abc-123", p.Unprotect(protectedSecret));
    }

    [Fact]
    public void APlaintextLegacyValue_ComesBackAsItIs() => Assert.Equal("old-plaintext-secret", Create().Unprotect("old-plaintext-secret"));

    [Fact]
    public void AnEmptyValue_StaysEmpty()
    {
        Assert.Equal("", Create().Protect(""));
        Assert.Equal("", Create().Unprotect(""));
        Assert.False(Create().TryUnprotect("", out _));
        Assert.False(Create().TryUnprotect(null, out _));
    }

    [Fact]
    public void ThePurposeAndPrefixCanBeAnAppsOwn_SoItsStoredSecretsStayReadable()
    {
        // What garge's own protector wrote before the move.
        var stored = "enc:v1:" + Keys.CreateProtector("garge.vipps.webhook-secret.v1").Protect("garge-secret");
        var p = Create(o => o.SecretPurpose = "garge.vipps.webhook-secret.v1");

        Assert.Equal("garge-secret", p.Unprotect(stored));
        Assert.StartsWith("v2:", Create(o => o.SecretPrefix = "v2:").Protect("x"));
    }

    [Fact]
    public void ASecretFromOtherKeysOrAnotherPurpose_CannotBeRead()
    {
        var stored = Create(provider: new EphemeralDataProtectionProvider()).Protect("secret");
        var p = Create();

        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(stored));
        Assert.False(p.TryUnprotect(stored, out var secret));
        Assert.Equal("", secret);
        Assert.False(p.TryUnprotect("enc:v1:not-a-protected-value", out _));
        Assert.False(Create(o => o.SecretPurpose = "other").TryUnprotect(Create().Protect("secret"), out _));
    }
}

public class VippsWebhookCleanupTests
{
    [Fact]
    public async Task EventsOlderThanTheRetention_AreDeleted()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<WebhookDbContext>(o => o.UseSqlite(connection));
        var provider = services.BuildServiceProvider();
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WebhookDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.ProcessedWebhookEvents.AddRange(
                new VippsProcessedWebhookEvent { Id = "old", Source = "shop", ProcessedAt = now.AddDays(-31) },
                new VippsProcessedWebhookEvent { Id = "kept", Source = "shop", ProcessedAt = now.AddDays(-29) });
            await db.SaveChangesAsync();
        }
        var service = new VippsWebhookCleanupService<WebhookDbContext>(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new VippsWebhookOptions()), new FakeTimeProvider(now), NullLogger<VippsWebhookCleanupService<WebhookDbContext>>.Instance);

        Assert.Equal(1, await service.CleanupAsync(CancellationToken.None));

        using var check = provider.CreateScope();
        Assert.Equal("kept", Assert.Single(await check.ServiceProvider.GetRequiredService<WebhookDbContext>().ProcessedWebhookEvents.ToListAsync()).Id);
    }

    [Fact]
    public void ProcessedEvents_AreKeptLongerThanASignatureStaysValid() =>
        Assert.True(new VippsWebhookOptions().Retention > VippsWebhookSignature.MaxAge);

    [Fact]
    public void TheMarkerTable_HasTheColumnsAnExistingTableUses()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        using var db = new WebhookDbContext(new DbContextOptionsBuilder<WebhookDbContext>().UseSqlite(connection).Options);
        var entity = db.Model.FindEntityType(typeof(VippsProcessedWebhookEvent))!;

        Assert.Equal("ProcessedWebhookEvents", entity.GetTableName());
        Assert.Equal(200, entity.FindProperty("Id")!.GetMaxLength());
        Assert.Equal(50, entity.FindProperty("Source")!.GetMaxLength());
        Assert.False(entity.FindProperty("Source")!.IsNullable);
        Assert.Equal(["Id"], entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(["ProcessedAt", "Source,Id"], entity.GetIndexes().Select(i => string.Join(",", i.Properties.Select(p => p.Name))).Order());
    }
}

public class VippsPhoneNumberTests
{
    [Theory]
    [InlineData("91234567", "4791234567")]
    [InlineData("4791234567", "4791234567")]
    [InlineData("+47 91 23 45 67", "4791234567")]
    [InlineData("(91) 23-45-67", "4791234567")]
    [InlineData("12345678", "4712345678")]
    public void AcceptsNorwegianFormats(string input, string expected)
    {
        Assert.True(VippsPhoneNumber.TryNormalize(input, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123")]
    [InlineData("4691234567")]
    [InlineData("479123456789")]
    [InlineData("٩١٢٣٤٥٦٧")]
    public void RejectsOtherNumbers(string? input)
    {
        Assert.False(VippsPhoneNumber.TryNormalize(input, out var result));
        Assert.Equal("", result);
    }
}

public class VippsFlatConfigurationTests
{
    [Fact]
    public void TheFlatLayout_FillsTheLiveAndTestCredentials()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vipps:ClientId"] = "id", ["Vipps:ClientSecret"] = "secret", ["Vipps:SubscriptionKey"] = "key",
            ["Vipps:MerchantSerialNumber"] = "123456", ["Vipps:BaseUrl"] = "https://api.vipps.no",
            ["Vipps:TestClientId"] = "tid", ["Vipps:TestClientSecret"] = "tsecret", ["Vipps:TestSubscriptionKey"] = "tkey",
            ["Vipps:TestMerchantSerialNumber"] = "654321",
        }).Build();
        var services = new ServiceCollection();
        services.AddAppKitVipps(config.GetSection("Vipps"), o => o.SystemName = "garge");

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<VippsOptions>>().Value;

        Assert.Equal(("https://api.vipps.no", "id", "secret", "key", "123456"),
            (options.Live.BaseUrl, options.Live.ClientId, options.Live.ClientSecret, options.Live.SubscriptionKey, options.Live.MerchantSerialNumber));
        Assert.Equal(("https://apitest.vipps.no", "tid", "tsecret", "tkey", "654321"),
            (options.Test.BaseUrl, options.Test.ClientId, options.Test.ClientSecret, options.Test.SubscriptionKey, options.Test.MerchantSerialNumber));
        Assert.True(options.Live.IsConfigured && options.Test.IsConfigured);
        Assert.Equal("garge", options.SystemName);
    }

    [Fact]
    public void AnEmptyTestSection_LeavesTheTestEnvironmentUnconfigured()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Vipps:ClientId"] = "id" }).Build();
        var options = new VippsOptions();

        VippsServiceCollectionExtensions.ReadFlat(config.GetSection("Vipps"), options);

        Assert.False(options.Test.IsConfigured);
        Assert.Equal("https://apitest.vipps.no", options.Test.BaseUrl);
    }
}

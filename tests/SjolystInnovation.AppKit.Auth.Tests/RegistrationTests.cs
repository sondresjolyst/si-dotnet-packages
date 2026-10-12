using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public sealed class RegistrationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ServiceProvider Provider(Dictionary<string, string?> jwt)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(jwt.ToDictionary(kv => $"Jwt:{kv.Key}", kv => kv.Value))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _db.NewContext());
        services.AddAppKitAuth<TestDbContext>(config.GetSection("Jwt"));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void BindsTheJwtSectionAndRegistersBothServices()
    {
        using var provider = Provider(new() { ["Key"] = new string('k', 32), ["Issuer"] = "garge", ["MaxLiveRefreshTokens"] = "3" });
        using var scope = provider.CreateScope();

        var options = provider.GetRequiredService<IOptions<AuthOptions>>().Value;
        Assert.Equal("garge", options.Issuer);
        Assert.Equal(3, options.MaxLiveRefreshTokens);
        Assert.Equal(TimeSpan.FromHours(1), options.AccessTokenLifetime);
        Assert.Equal(6, options.RefreshTokenLifetimeMonths);
        Assert.IsType<JwtTokenService>(provider.GetRequiredService<IJwtTokenService>());
        Assert.IsType<RefreshTokenService<TestDbContext>>(scope.ServiceProvider.GetRequiredService<IRefreshTokenService>());
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Theory]
    [InlineData("Key", "", "Jwt:Key must be at least 32 bytes.")]
    [InlineData("Key", "kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk", "Jwt:Key must be at least 32 bytes.")]
    [InlineData("AccessTokenLifetime", "00:00:00", "The access token lifetime must be positive.")]
    [InlineData("RefreshTokenLifetimeMonths", "0", "The refresh token lifetime must be positive.")]
    [InlineData("MaxLiveRefreshTokens", "0", "The refresh token cap must be positive.")]
    public void RefusesSettingsThatCannotWork(string name, string value, string message)
    {
        var jwt = new Dictionary<string, string?> { ["Key"] = new string('k', 32), [name] = value };
        using var provider = Provider(jwt);

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AuthOptions>>().Value);
        Assert.Contains(message, error.Failures);
    }

    [Fact]
    public async Task AWiredUpAppSignsInAndRotates()
    {
        using var provider = Provider(new() { ["Key"] = new string('k', 32), ["Issuer"] = "garge" });
        await using (var setup = _db.NewContext())
        {
            setup.Users.Add(new TestUser { Id = "ada", UserName = "ada" });
            await setup.SaveChangesAsync();
        }

        string raw;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            raw = await scope.ServiceProvider.GetRequiredService<IRefreshTokenService>().IssueAsync("ada");
            await db.SaveChangesAsync();
        }

        var jwt = provider.GetRequiredService<IJwtTokenService>();
        var token = jwt.Build(new IdentityUser { Id = "ada" }, []);
        Assert.Equal("ada", jwt.ReadUserIdFromExpired(token));
        using (var scope = provider.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IRefreshTokenService>().RotateAsync("ada", raw);
            Assert.Equal(RotationStatus.Rotated, result.Status);
        }
    }

    [Fact]
    public void DeletingAUserDeletesTheirTokens()
    {
        using (var db = _db.NewContext())
        {
            db.Users.Add(new TestUser { Id = "ada", UserName = "ada" });
            db.RefreshTokens.Add(new RefreshToken { UserId = "ada", Token = "h", Created = DateTime.UtcNow, Expires = DateTime.UtcNow });
            db.SaveChanges();
        }
        using (var db = _db.NewContext())
        {
            db.Users.Remove(db.Users.Single());
            db.SaveChanges();
        }

        using var check = _db.NewContext();
        Assert.Empty(check.RefreshTokens);
    }

    [Fact]
    public void MapsTheTableTheAppsHaveToday()
    {
        using var db = _db.NewContext();
        var entity = db.Model.FindEntityType(typeof(RefreshToken))!;

        Assert.Equal("RefreshTokens", entity.GetTableName());
        Assert.False(entity.FindProperty(nameof(RefreshToken.Token))!.IsNullable);
        Assert.False(entity.FindProperty(nameof(RefreshToken.UserId))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(RefreshToken.Revoked))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(RefreshToken.Revoked))!.IsConcurrencyToken);
        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(typeof(TestUser), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        Assert.True(foreignKey.IsRequired);
        Assert.Contains(entity.GetIndexes(), i => i.Properties.Single().Name == nameof(RefreshToken.UserId));
    }
}

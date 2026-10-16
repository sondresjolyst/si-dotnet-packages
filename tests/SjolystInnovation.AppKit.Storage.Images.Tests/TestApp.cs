using System.Data.Common;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Storage.Images.Tests;

/// <summary>An app's gallery entry that must keep its image.</summary>
public class GalleryItem
{
    public int Id { get; set; }
    public required string ContentImageId { get; set; }
    public ContentImage? ContentImage { get; set; }
}

/// <summary>An app's step whose image is optional.</summary>
public class RecipeStep
{
    public int Id { get; set; }
    public string? ContentImageId { get; set; }
    public ContentImage? ContentImage { get; set; }
}

public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<GalleryItem> GalleryItems => Set<GalleryItem>();
    public DbSet<RecipeStep> RecipeSteps => Set<RecipeStep>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddAppKitContentImages();
        modelBuilder.Entity<GalleryItem>().HasOne(g => g.ContentImage).WithMany().HasForeignKey(g => g.ContentImageId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RecipeStep>().HasOne(s => s.ContentImage).WithMany().HasForeignKey(s => s.ContentImageId).OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>Fails every save while <see cref="Fail"/> is set, to stand in for a database that is down.</summary>
public sealed class FailingSaves : SaveChangesInterceptor
{
    public bool Fail { get; set; }

    private int _count;

    /// <summary>The number of saves tried.</summary>
    public int Count => Volatile.Read(ref _count);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return Fail ? throw new DbUpdateException("The database is down.") : ValueTask.FromResult(result);
    }
}

/// <summary>Signs a request in with the roles in <c>X-Test-Roles</c>.</summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Roles", out var roles))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "u1") };
        claims.AddRange(roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
    }
}

/// <summary>A test server with the content image endpoints on SQLite in memory and a temporary image directory.</summary>
public sealed class TestApp : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private IHost _host = default!;

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"appkit-images-{Guid.NewGuid():N}");
    public FailingSaves Saves { get; } = new();
    public HttpClient Client { get; private set; } = default!;
    public IServiceProvider Services => _host.Services;

    public static async Task<TestApp> StartAsync(Action<AppKitContentImagesEndpointOptions>? endpoints = null, Dictionary<string, string?>? storage = null)
    {
        var app = new TestApp();
        await app._connection.OpenAsync();
        var settings = new Dictionary<string, string?>
        {
            ["Storage:ImagesPath"] = app.Root,
            ["Storage:MaxImageBytes"] = "5242880",
        };
        foreach (var (key, value) in storage ?? [])
            settings[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        app._host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddDbContext<TestDbContext>(o => o.UseSqlite((DbConnection)app._connection).AddInterceptors(app.Saves));
                    s.AddAppKitContentImages<TestDbContext>(configuration.GetSection("Storage"));
                    s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    s.AddAuthorization(o =>
                    {
                        o.AddPolicy("Admin", p => p.RequireRole("Admin"));
                        o.AddPolicy("Owner", p => p.RequireRole("Owner"));
                    });
                    s.AddRouting();
                })
                .Configure(web =>
                {
                    web.UseRouting();
                    web.UseAuthentication();
                    web.UseAuthorization();
                    web.UseEndpoints(e => e.MapAppKitContentImages<TestDbContext>(configure: endpoints));
                }))
            .Build();
        using (var scope = app._host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync();
        await app._host.StartAsync();
        app.Client = app._host.GetTestClient();
        return app;
    }

    public IAppKitFileStore Store => Services.GetRequiredKeyedService<IAppKitFileStore>(AppKitContentImages.StoreName);

    internal ImageProcessor Processor => Services.GetRequiredService<ImageProcessor>();

    internal VariantBackfill Backfill => Services.GetRequiredService<VariantBackfill>();

    public string[] Files() => Directory.Exists(Root) ? Directory.GetFiles(Root).Select(f => Path.GetFileName(f)).ToArray() : [];

    public async Task<T> WithDbAsync<T>(Func<TestDbContext, Task<T>> action)
    {
        using var scope = _host.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TestDbContext>());
    }

    public async Task<T> WithServiceAsync<T>(Func<IContentImageService, TestDbContext, Task<T>> action)
    {
        using var scope = _host.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IContentImageService>(), scope.ServiceProvider.GetRequiredService<TestDbContext>());
    }

    /// <summary>Adds an image row for a file written straight to the directory, as for an image uploaded before renditions existed.</summary>
    public async Task<string> SeedAsync(byte[] file, string extension, string contentType, int width = 0, int height = 0)
    {
        var storedPath = $"{Guid.NewGuid():N}{extension}";
        Directory.CreateDirectory(Root);
        await File.WriteAllBytesAsync(Path.Combine(Root, storedPath), file);
        var image = new ContentImage { FileName = $"old{extension}", ContentType = contentType, StoredPath = storedPath, SizeBytes = file.Length, Width = width, Height = height };
        await WithDbAsync(async db =>
        {
            db.Add(image);
            return await db.SaveChangesAsync();
        });
        return image.Id;
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, string? roles = "Admin", string? accept = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (roles is not null)
            request.Headers.Add("X-Test-Roles", roles);
        if (accept is not null)
            request.Headers.TryAddWithoutValidation("Accept", accept);
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _connection.DisposeAsync();
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

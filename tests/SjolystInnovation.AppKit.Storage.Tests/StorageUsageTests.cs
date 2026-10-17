using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SjolystInnovation.AppKit.Storage.Tests;

public sealed class StorageUsageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"appkit-usage-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Dir(string name) => Path.Combine(_root, name);

    private void Write(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private static ServiceProvider Services(params (string Name, string Path)[] stores)
    {
        var services = new ServiceCollection().AddLogging();
        foreach (var (name, path) in stores)
            services.AddAppKitFileStore(name, o => { o.Path = path; o.Allowed = AppKitFileKinds.Images; });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Adds_up_every_file_of_the_store_with_its_subdirectories()
    {
        Write("images/a.png", 100);
        Write("images/b.webp", 250);
        Write("images/variants/c.webp", 50);
        Write("elsewhere/d.png", 1000);
        using var services = Services(("images", Dir("images")));

        var usage = await services.GetRequiredService<IAppKitStorageUsage>().GetAsync(["images"]);

        Assert.Equal(400, usage.StorageUsedBytes);
    }

    [Fact]
    public async Task Adds_up_several_stores()
    {
        Write("images/a.png", 100);
        Write("reports/r.pdf", 30);
        using var services = Services(("images", Dir("images")), ("reports", Dir("reports")));

        var usage = await services.GetRequiredService<IAppKitStorageUsage>().GetAsync(["images", "reports"]);

        Assert.Equal(130, usage.StorageUsedBytes);
    }

    [Fact]
    public async Task Counts_a_store_inside_another_and_a_repeated_store_once()
    {
        Write("data/a.png", 100);
        Write("data/reports/r.pdf", 30);
        using var services = Services(("data", Dir("data")), ("reports", Dir("data/reports")));

        var usage = await services.GetRequiredService<IAppKitStorageUsage>().GetAsync(["reports", "data", "data"]);

        Assert.Equal(130, usage.StorageUsedBytes);
    }

    [Fact]
    public async Task A_store_without_a_directory_yet_counts_as_zero()
    {
        using var services = Services(("images", Dir("missing")));

        var usage = await services.GetRequiredService<IAppKitStorageUsage>().GetAsync(["images"]);

        Assert.Equal(0, usage.StorageUsedBytes);
    }

    [Fact]
    public async Task Reads_the_disk_that_holds_the_store()
    {
        Write("images/a.png", 1);
        using var services = Services(("images", Dir("images")));

        var usage = await services.GetRequiredService<IAppKitStorageUsage>().GetAsync(["images"]);

        var drive = new DriveInfo(Path.GetPathRoot(_root)!);
        Assert.True(usage.DiskTotalBytes > 0);
        Assert.InRange(usage.DiskFreeBytes, 1, usage.DiskTotalBytes);
        if (OperatingSystem.IsWindows())
            Assert.Equal(drive.TotalSize, usage.DiskTotalBytes);
    }

    [Fact]
    public async Task An_unknown_store_fails_and_names_it()
    {
        using var services = Services(("images", Dir("images")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<IAppKitStorageUsage>().GetAsync(["reports"]));

        Assert.Contains("'reports'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_store_at_all_fails()
    {
        using var services = Services(("images", Dir("images")));

        await Assert.ThrowsAsync<ArgumentException>(() => services.GetRequiredService<IAppKitStorageUsage>().GetAsync([]));
    }

    [Fact]
    public async Task A_canceled_measure_throws()
    {
        Write("images/a.png", 1);
        using var services = Services(("images", Dir("images")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => services.GetRequiredService<IAppKitStorageUsage>().GetAsync(["images"], new CancellationToken(true)));
    }

    [Fact]
    public void The_json_has_the_names_the_admin_stats_used() =>
        Assert.Equal(
            """{"storageUsedBytes":1,"diskTotalBytes":2,"diskFreeBytes":3}""",
            JsonSerializer.Serialize(new AppKitStorageUsage(1, 2, 3), JsonSerializerOptions.Web));

    [Theory]
    [InlineData("/data/images", "/data", true)]
    [InlineData("/data", "/data", true)]
    [InlineData("/data/", "/data", true)]
    [InlineData("/data", "/data/", true)]
    [InlineData("/data/images", "/", true)]
    [InlineData("/database/x", "/data", false)]
    [InlineData("/dat", "/data", false)]
    [InlineData("/other", "/data", false)]
    public void A_path_is_under_its_own_directory_and_the_ones_above_it(string path, string directory, bool under)
    {
        if (OperatingSystem.IsWindows())
            (path, directory) = (path.Replace('/', '\\'), directory.Replace('/', '\\'));
        Assert.Equal(under, StorageUsage.IsUnder(path, directory));
    }

    [Fact]
    public void The_longest_mount_point_that_holds_the_path_wins()
    {
        var s = Path.DirectorySeparatorChar;
        string P(string p) => p.Replace('/', s);

        Assert.Equal(P("/data/"), StorageUsage.MountPointFor(P("/data/images"), [P("/"), P("/data/"), P("/proc"), P("/data/images-old")]));
        Assert.Equal(P("/"), StorageUsage.MountPointFor(P("/srv/images"), [P("/data"), P("/")]));
        Assert.Null(StorageUsage.MountPointFor(P("/srv/images"), [P("/data")]));
    }

    [Fact]
    public void Outermost_keeps_the_directories_no_other_listed_one_holds()
    {
        var s = Path.DirectorySeparatorChar;
        string P(string p) => p.Replace('/', s);

        Assert.Equal([P("/data"), P("/srv")], StorageUsage.Outermost([P("/data/reports"), P("/data"), P("/srv"), P("/data/")]).Order(StringComparer.Ordinal));
    }
}

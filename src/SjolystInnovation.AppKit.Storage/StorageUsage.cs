using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Storage;

/// <summary>The space that file stores take and the disk they are on, for an admin page.</summary>
/// <param name="StorageUsedBytes">The total size in bytes of every file in the stores, subdirectories included.</param>
/// <param name="DiskTotalBytes">The size in bytes of the disk that holds the first store, or 0 when it cannot be read.</param>
/// <param name="DiskFreeBytes">The bytes free on that disk for the app, or 0 when it cannot be read.</param>
public sealed record AppKitStorageUsage(long StorageUsedBytes, long DiskTotalBytes, long DiskFreeBytes);

/// <summary>Measures the space that named file stores take on disk.</summary>
public interface IAppKitStorageUsage
{
    /// <summary>
    /// Adds up the files of the stores named in <paramref name="storeNames"/> and reads the disk that holds the
    /// first of them. A store directory that does not exist yet counts as 0, and a directory inside another
    /// listed one counts once. The disk is the mount point that holds the directory, so a volume mounted at
    /// <c>/data</c> reports its own size, not the root file system's. When the disk cannot be read, its sizes
    /// are 0 and a warning is logged.
    /// </summary>
    /// <param name="storeNames">The names the stores were registered under, such as <c>content-images</c>.</param>
    /// <param name="cancellationToken">A token that cancels the measuring.</param>
    /// <returns>The usage, whose JSON is <c>{ storageUsedBytes, diskTotalBytes, diskFreeBytes }</c>.</returns>
    /// <exception cref="ArgumentException">When no store is named.</exception>
    /// <exception cref="InvalidOperationException">When a named store has no path, because it was never registered.</exception>
    Task<AppKitStorageUsage> GetAsync(IEnumerable<string> storeNames, CancellationToken cancellationToken = default);
}

internal sealed partial class StorageUsage(IOptionsMonitor<AppKitFileStoreOptions> stores, ILogger<StorageUsage> logger) : IAppKitStorageUsage
{
    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public Task<AppKitStorageUsage> GetAsync(IEnumerable<string> storeNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storeNames);
        var paths = storeNames.Select(name =>
        {
            var path = stores.Get(name).Path;
            return string.IsNullOrWhiteSpace(path)
                ? throw new InvalidOperationException($"The file store '{name}' has no Path. Register it with AddAppKitFileStore first.")
                : Path.GetFullPath(path);
        }).ToList();
        if (paths.Count == 0)
            throw new ArgumentException("Name at least one file store.", nameof(storeNames));

        return Task.Run(() =>
        {
            var used = Outermost(paths).Sum(path => DirectorySize(path, cancellationToken));
            var (total, free) = Disk(paths[0]);
            return new AppKitStorageUsage(used, total, free);
        });
    }

    /// <summary>The distinct paths that are not inside another listed path, so no file counts twice.</summary>
    internal static List<string> Outermost(IEnumerable<string> paths)
    {
        var distinct = paths.Select(p => Path.TrimEndingDirectorySeparator(p)).Distinct(StringComparer.FromComparison(PathComparison)).ToList();
        return distinct.Where(p => !distinct.Any(other => !string.Equals(other, p, PathComparison) && IsUnder(p, other))).ToList();
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="directory"/> or lies inside it.</summary>
    internal static bool IsUnder(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(directory);
        if (string.Equals(Path.TrimEndingDirectorySeparator(path), root, PathComparison))
            return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    /// <summary>The longest mount point that holds <paramref name="path"/>, or null when none does.</summary>
    internal static string? MountPointFor(string path, IEnumerable<string> mountPoints) =>
        mountPoints.Where(m => IsUnder(path, m)).OrderByDescending(m => Path.TrimEndingDirectorySeparator(m).Length).FirstOrDefault();

    private static long DirectorySize(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
            return 0;
        long total = 0;
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", AllFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += file.Length;
        }
        return total;
    }

    private (long Total, long Free) Disk(string path)
    {
        try
        {
            var mount = MountPointFor(path, DriveInfo.GetDrives().Select(d => d.Name)) ?? Path.GetPathRoot(path) ?? path;
            var drive = new DriveInfo(mount);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DiskUnreadable(logger, path, e);
            return (0, 0);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the disk that holds {Path}.")]
    private static partial void DiskUnreadable(ILogger logger, string path, Exception exception);
}

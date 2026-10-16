using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Storage;

/// <summary>The options for one file store, set in <see cref="FileStoreServiceCollectionExtensions.AddAppKitFileStore"/>.</summary>
public sealed class AppKitFileStoreOptions
{
    /// <summary>The directory the store keeps its files in. The store creates it on first use.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The largest file the store takes, in bytes. Defaults to 5 MB.</summary>
    public long MaxBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>The kinds of file the store takes. The store refuses every other kind.</summary>
    public AppKitFileKinds Allowed { get; set; }
}

/// <summary>A file the store saved.</summary>
/// <param name="StoredPath">The name the store keeps the file under, such as <c>3f2a...e1.pdf</c>. Keep it in your row.</param>
/// <param name="ContentType">The content type that the file's first bytes show.</param>
/// <param name="SizeBytes">The file size in bytes.</param>
/// <param name="FileName">The client's file name without any directory, cut to 260 characters.</param>
public sealed record StoredFile(string StoredPath, string ContentType, long SizeBytes, string FileName);

/// <summary>
/// The problem with an upload that the client can correct, such as a file that is empty, too large, or
/// of a kind the store does not take. The message is safe to return to the client.
/// </summary>
public sealed class AppKitFileRefusedException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public AppKitFileRefusedException() : base("The file was refused.") { }

    /// <summary>Creates the exception with a message for the client.</summary>
    /// <param name="message">The reason, safe to show to the client.</param>
    public AppKitFileRefusedException(string message) : base(message) { }

    /// <summary>Creates the exception with a message for the client and the exception that caused it.</summary>
    /// <param name="message">The reason, safe to show to the client.</param>
    /// <param name="innerException">The exception that caused the refusal.</param>
    public AppKitFileRefusedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// A directory of uploaded files. Resolve a named store with <c>[FromKeyedServices("name")]</c>.
/// </summary>
public interface IAppKitFileStore
{
    /// <summary>
    /// Saves an upload under a new name. The file's first bytes decide its kind and content type. The
    /// content type the client sent plays no part.
    /// </summary>
    /// <param name="content">The file content, read from its current position to the end.</param>
    /// <param name="fileName">The client's file name. Only its last segment is kept.</param>
    /// <param name="cancellationToken">A token that cancels the save. A canceled save leaves no file.</param>
    /// <returns>The saved file.</returns>
    /// <exception cref="AppKitFileRefusedException">When the file is empty, too large, or of a kind the store does not take.</exception>
    Task<StoredFile> SaveAsync(Stream content, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Opens a stored file for reading.</summary>
    /// <param name="storedPath">The <see cref="StoredFile.StoredPath"/> of the file.</param>
    /// <returns>The file content. Dispose it when you are done.</returns>
    /// <exception cref="FileNotFoundException">When the store has no such file.</exception>
    Stream OpenRead(string storedPath);

    /// <summary>Checks whether the store has a file.</summary>
    /// <param name="storedPath">The <see cref="StoredFile.StoredPath"/> of the file.</param>
    /// <returns>True when the file exists.</returns>
    bool Exists(string storedPath);

    /// <summary>Deletes a stored file. A file that does not exist is not an error.</summary>
    /// <param name="storedPath">The <see cref="StoredFile.StoredPath"/> of the file.</param>
    void Delete(string storedPath);
}

/// <summary>Registers named file stores.</summary>
public static class FileStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers a file store as a keyed <see cref="IAppKitFileStore"/> under <paramref name="name"/>. The
    /// app fails to start without a <see cref="AppKitFileStoreOptions.Path"/>, a positive
    /// <see cref="AppKitFileStoreOptions.MaxBytes"/>, and at least one allowed kind.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The key to resolve the store by.</param>
    /// <param name="configure">A callback that sets the store's options.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddAppKitFileStore(this IServiceCollection services, string name, Action<AppKitFileStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<AppKitFileStoreOptions>(name)
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.Path), $"The file store '{name}' needs a Path.")
            .Validate(o => o.MaxBytes > 0, $"The file store '{name}' needs a positive MaxBytes.")
            .Validate(o => o.Allowed != AppKitFileKinds.None, $"The file store '{name}' must allow at least one kind of file.")
            .ValidateOnStart();
        services.TryAddKeyedSingleton<IAppKitFileStore>(name, (sp, _) => new LocalFileStore(
            sp.GetRequiredService<IOptionsMonitor<AppKitFileStoreOptions>>().Get(name),
            sp.GetRequiredService<ILogger<LocalFileStore>>()));
        return services;
    }
}

/// <summary>A file store in a directory on local disk.</summary>
internal sealed partial class LocalFileStore : IAppKitFileStore
{
    /// <summary>The longest file name kept, the same as the <c>FileName</c> columns.</summary>
    public const int MaxFileNameLength = 260;

    private readonly string _root;
    private readonly string _rootWithSeparator;
    private readonly long _maxBytes;
    private readonly AppKitFileKinds _allowed;
    private readonly ILogger<LocalFileStore> _logger;

    public LocalFileStore(AppKitFileStoreOptions options, ILogger<LocalFileStore> logger)
    {
        _root = System.IO.Path.GetFullPath(options.Path);
        _rootWithSeparator = System.IO.Path.EndsInDirectorySeparator(_root) ? _root : _root + System.IO.Path.DirectorySeparatorChar;
        _maxBytes = options.MaxBytes;
        _allowed = options.Allowed;
        _logger = logger;
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredFile> SaveAsync(Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.CanSeek && content.Length - content.Position > _maxBytes)
            throw TooLarge();

        var header = new byte[FileSignature.HeaderLength];
        var headerLength = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        if (headerLength == 0)
            throw new AppKitFileRefusedException("Uploaded file is empty.");
        var kind = FileSignature.Detect(header.AsSpan(0, headerLength));
        if (kind == AppKitFileKinds.None || !_allowed.HasFlag(kind))
            throw new AppKitFileRefusedException($"Only {FileSignature.Describe(_allowed)} files are accepted.");

        var storedName = $"{Guid.NewGuid():N}{FileSignature.Extension(kind)}";
        var fullPath = ResolvePath(storedName);
        long size = headerLength;
        try
        {
            await using var output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (size > _maxBytes)
                throw TooLarge();
            await output.WriteAsync(header.AsMemory(0, headerLength), cancellationToken);
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                size += read;
                // A stream that cannot seek shows its length only here, so the limit is checked as it is read.
                if (size > _maxBytes)
                    throw TooLarge();
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch
        {
            File.Delete(fullPath);
            throw;
        }

        LogStored(storedName, size);
        return new StoredFile(storedName, FileSignature.ContentType(kind), size, CleanFileName(fileName, storedName));
    }

    public Stream OpenRead(string storedPath) =>
        new FileStream(ResolvePath(storedPath), FileMode.Open, FileAccess.Read, FileShare.Read);

    public bool Exists(string storedPath) => File.Exists(ResolvePath(storedPath));

    public void Delete(string storedPath)
    {
        var fullPath = ResolvePath(storedPath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }

    /// <summary>
    /// Finds a stored file's full path. Only the last segment of <paramref name="storedPath"/> counts, and
    /// the result must lie inside the root, so a path cannot reach another directory.
    /// </summary>
    internal string ResolvePath(string storedPath)
    {
        var name = System.IO.Path.GetFileName(storedPath);
        var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, name));
        if (!fullPath.StartsWith(_rootWithSeparator, StringComparison.Ordinal) || fullPath.Length == _rootWithSeparator.Length)
            throw new InvalidOperationException("Invalid stored path.");
        return fullPath;
    }

    /// <summary>
    /// Keeps the last segment of the client's file name, for both <c>/</c> and <c>\</c>, cut to
    /// <see cref="MaxFileNameLength"/> characters. A blank name falls back to the stored name.
    /// </summary>
    internal static string CleanFileName(string? fileName, string storedName)
    {
        var name = fileName ?? string.Empty;
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..].Trim();
        if (name.Length == 0)
            return storedName;
        if (name.Length > MaxFileNameLength)
        {
            var cut = char.IsHighSurrogate(name[MaxFileNameLength - 1]) ? MaxFileNameLength - 1 : MaxFileNameLength;
            name = name[..cut];
        }
        return name;
    }

    private AppKitFileRefusedException TooLarge() => new($"The file exceeds the {DescribeLimit(_maxBytes)} limit.");

    private static string DescribeLimit(long bytes) =>
        bytes % (1024 * 1024) == 0 ? $"{bytes / (1024 * 1024)} MB"
        : bytes % 1024 == 0 ? $"{bytes / 1024} KB"
        : $"{bytes} byte";

    [LoggerMessage(Level = LogLevel.Information, Message = "Stored file {StoredName} ({Bytes} bytes)")]
    private partial void LogStored(string storedName, long bytes);
}

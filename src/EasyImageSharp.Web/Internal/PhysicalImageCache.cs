using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EasyImageSharp.Web.Internal;

/// <summary>
/// An <see cref="IImageCache"/> on the local disk. Each entry is one file,
/// <c>{root}/{key[0..2]}/{key[2..4]}/{key}.eisc</c>, in the <see cref="CacheEntryCodec"/> layout.
/// </summary>
/// <remarks>
/// <para>
/// Writes go to a uniquely named temporary file in the same directory and are then renamed over the final name,
/// so a reader sees either the previous complete entry or the new complete one, never a partial file. Readers open
/// entries with <see cref="FileShare.Delete"/> so a concurrent replace does not fail on Windows. Keys are checked to
/// be 64 lowercase hexadecimal characters, so a key can never name a path outside the root.
/// </para>
/// <para>
/// Entries older than <see cref="EasyImageSharpWebOptions.CacheMaxAge"/> are treated as misses and replaced on the
/// next write. Nothing deletes files in the background: bound the directory's size with an external job if needed.
/// </para>
/// </remarks>
internal sealed class PhysicalImageCache : IImageCache
{
    private const string Extension = ".eisc";

    private readonly IOptionsMonitor<EasyImageSharpWebOptions> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;

    public PhysicalImageCache(
        string rootPath,
        IOptionsMonitor<EasyImageSharpWebOptions> options,
        TimeProvider timeProvider,
        ILogger<PhysicalImageCache> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.RootPath = Path.GetFullPath(rootPath);
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>The absolute cache directory.</summary>
    public string RootPath { get; }

    public async ValueTask<IImageCacheEntry?> GetAsync(string key, CancellationToken cancellationToken)
    {
        string path = this.GetPath(key);
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 0,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            byte[] header = new byte[(int)Math.Min(CacheEntryCodec.MaxHeaderLength, stream.Length)];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (!CacheEntryCodec.TryReadHeader(header, out string contentType, out long length, out DateTimeOffset created, out int headerLength)
                || headerLength + length != stream.Length)
            {
                Log.CorruptCacheEntry(this.logger, path);
                await stream.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            if (created + this.options.CurrentValue.CacheMaxAge <= this.timeProvider.GetUtcNow())
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            stream.Position = headerLength;
            return new FileEntry(stream, contentType, length);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask SetAsync(string key, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        string path = this.GetPath(key);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            byte[] header = new byte[CacheEntryCodec.GetHeaderLength(contentType)];
            CacheEntryCodec.WriteHeader(header, contentType, content.Length, this.timeProvider.GetUtcNow());
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) when (File.Exists(path))
        {
            // A concurrent writer replaced the entry first. Its content is identical (same key, deterministic encoders),
            // so losing the race is harmless.
            TryDelete(temporary);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Checks that the cache directory exists (creating it) and accepts a write; used by the health check.</summary>
    public void Probe()
    {
        Directory.CreateDirectory(this.RootPath);
        string probe = Path.Combine(this.RootPath, "probe." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(probe, "EISC"u8.ToArray());
        File.Delete(probe);
    }

    internal string GetPath(string key)
    {
        if (!IsValidKey(key))
        {
            throw new ArgumentException("Image cache keys are 64 lowercase hexadecimal characters.", nameof(key));
        }

        return Path.Combine(this.RootPath, key[..2], key[2..4], key + Extension);
    }

    private static bool IsValidKey(string? key)
        => key is { Length: 64 } && key.All(static c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: an orphaned temporary file is harmless and never read.
        }
    }

    /// <summary>An open cache file positioned at the content.</summary>
    private sealed class FileEntry : IImageCacheEntry
    {
        private readonly FileStream stream;

        public FileEntry(FileStream stream, string contentType, long length)
        {
            this.stream = stream;
            this.ContentType = contentType;
            this.Length = length;
        }

        public string ContentType { get; }

        public long Length { get; }

        public Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
            => this.stream.CopyToAsync(destination, cancellationToken);

        public ValueTask DisposeAsync() => this.stream.DisposeAsync();
    }
}

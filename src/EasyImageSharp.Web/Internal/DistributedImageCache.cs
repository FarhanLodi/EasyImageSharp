using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace EasyImageSharp.Web.Internal;

/// <summary>
/// An <see cref="IImageCache"/> over the application's <see cref="IDistributedCache"/> (Redis, SQL Server, the
/// in-memory implementation, ...). Each entry is one value in the <see cref="CacheEntryCodec"/> layout under the key
/// <c>EasyImageSharp.Web:{key}</c>, written in a single set, so a reader never sees half an entry. Entries expire after
/// <see cref="EasyImageSharpWebOptions.CacheMaxAge"/>.
/// </summary>
internal sealed class DistributedImageCache : IImageCache
{
    private const string KeyPrefix = "EasyImageSharp.Web:";

    private readonly IDistributedCache cache;
    private readonly IOptionsMonitor<EasyImageSharpWebOptions> options;
    private readonly TimeProvider timeProvider;

    public DistributedImageCache(IDistributedCache cache, IOptionsMonitor<EasyImageSharpWebOptions> options, TimeProvider timeProvider)
    {
        this.cache = cache;
        this.options = options;
        this.timeProvider = timeProvider;
    }

    public async ValueTask<IImageCacheEntry?> GetAsync(string key, CancellationToken cancellationToken)
    {
        byte[]? blob = await this.cache.GetAsync(KeyPrefix + key, cancellationToken).ConfigureAwait(false);
        if (blob is null
            || !CacheEntryCodec.TryReadHeader(blob, out string contentType, out long length, out DateTimeOffset created, out int headerLength)
            || headerLength + length != blob.Length
            || created + this.options.CurrentValue.CacheMaxAge <= this.timeProvider.GetUtcNow())
        {
            return null;
        }

        return new MemoryEntry(contentType, blob.AsMemory(headerLength));
    }

    public async ValueTask SetAsync(string key, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        byte[] blob = CacheEntryCodec.Encode(contentType, content.Span, this.timeProvider.GetUtcNow());
        var entryOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = this.options.CurrentValue.CacheMaxAge };
        await this.cache.SetAsync(KeyPrefix + key, blob, entryOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A cached image held in memory.</summary>
    private sealed class MemoryEntry : IImageCacheEntry
    {
        private readonly ReadOnlyMemory<byte> content;

        public MemoryEntry(string contentType, ReadOnlyMemory<byte> content)
        {
            this.ContentType = contentType;
            this.content = content;
        }

        public string ContentType { get; }

        public long Length => this.content.Length;

        public Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
            => destination.WriteAsync(this.content, cancellationToken).AsTask();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

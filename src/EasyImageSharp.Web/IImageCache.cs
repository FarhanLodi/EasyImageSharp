namespace EasyImageSharp.Web;

/// <summary>
/// A server-side store of processed images. Choose one with <c>UsePhysicalCache</c>, <c>UseDistributedCache</c> or
/// <c>UseCache&lt;T&gt;</c>; without one every cache miss at the HTTP layer is processed again.
/// </summary>
/// <remarks>
/// <para>
/// Keys are produced by the middleware: 64 lowercase hexadecimal characters, the SHA-256 of the source provider,
/// path and version, the normalised commands, the negotiated output selection, the output-affecting options and the
/// library version. Every input that can change the output bytes is part of the key and nothing a client sends
/// outside the recognised commands is, so two different outputs can never share an entry (no cache poisoning) and
/// unknown query parameters cannot multiply entries.
/// </para>
/// <para>
/// Implementations are singletons and must be thread-safe. A failure inside a cache is logged and the request is
/// served without it, so an implementation may throw.
/// </para>
/// </remarks>
public interface IImageCache
{
    /// <summary>Looks up a processed image.</summary>
    /// <param name="key">The cache key (64 lowercase hexadecimal characters).</param>
    /// <param name="cancellationToken">Signalled when the client disconnects.</param>
    /// <returns>The entry, or <see langword="null"/> on a miss. The caller disposes it.</returns>
    ValueTask<IImageCacheEntry?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Stores a processed image. Must be atomic: a concurrent reader sees either no entry or the whole entry.</summary>
    /// <param name="key">The cache key (64 lowercase hexadecimal characters).</param>
    /// <param name="contentType">The media type of <paramref name="content"/>, for example <c>image/webp</c>.</param>
    /// <param name="content">The encoded image.</param>
    /// <param name="cancellationToken">Signalled when the client disconnects.</param>
    /// <returns>A task that completes when the entry is stored.</returns>
    ValueTask SetAsync(string key, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

/// <summary>A processed image read from an <see cref="IImageCache"/>.</summary>
public interface IImageCacheEntry : IAsyncDisposable
{
    /// <summary>The media type of the cached image.</summary>
    string ContentType { get; }

    /// <summary>The size of the cached image in bytes.</summary>
    long Length { get; }

    /// <summary>Copies the cached image to <paramref name="destination"/>.</summary>
    /// <param name="destination">The response body.</param>
    /// <param name="cancellationToken">Signalled when the client disconnects.</param>
    /// <returns>A task that completes when the whole image has been written.</returns>
    Task CopyToAsync(Stream destination, CancellationToken cancellationToken);
}

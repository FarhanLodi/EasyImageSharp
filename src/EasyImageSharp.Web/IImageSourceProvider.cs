using Microsoft.AspNetCore.Http;

namespace EasyImageSharp.Web;

/// <summary>
/// Resolves the original image behind a request path. Register implementations with
/// <c>AddSourceProvider</c>; they are consulted in registration order and the first non-<see langword="null"/>
/// result wins. When none is registered, a <see cref="FileProviderImageSourceProvider"/> over
/// <see cref="EasyImageSharpWebOptions.SourceRootPath"/> (or the web root) is used.
/// </summary>
/// <remarks>
/// Implementations are singletons and must be thread-safe. The middleware has already rejected paths containing
/// dot segments, backslashes, control characters, drive or stream separators and other traversal tricks before a
/// provider is called, but a provider that maps paths onto a store of its own must still confine them to that store.
/// </remarks>
public interface IImageSourceProvider
{
    /// <summary>Looks up the source image for <paramref name="path"/>.</summary>
    /// <param name="context">The current request, for providers that depend on it (tenancy, authorisation).</param>
    /// <param name="path">
    /// The request path with <see cref="EasyImageSharpWebOptions.RequestPathPrefix"/> removed; it always starts with
    /// <c>/</c> and has a file extension of a decodable format.
    /// </param>
    /// <param name="cancellationToken">Signalled when the client disconnects.</param>
    /// <returns>The source, or <see langword="null"/> when this provider has no image at that path.</returns>
    ValueTask<IImageSource?> GetSourceAsync(HttpContext context, string path, CancellationToken cancellationToken);
}

/// <summary>An original image found by an <see cref="IImageSourceProvider"/>.</summary>
public interface IImageSource
{
    /// <summary>
    /// A string that changes whenever the content changes, for example the last-write time and length of a file or
    /// a blob's ETag. It is part of the server-side cache key and of the response ETag, so a changed source is never
    /// served from a stale cache entry. When several stores or applications share one cache, include something that
    /// identifies the store (a root directory, a container name) as well. Must not be empty.
    /// </summary>
    string Version { get; }

    /// <summary>When the source last changed, for the <c>Last-Modified</c> header; <see langword="null"/> when unknown.</summary>
    DateTimeOffset? LastModified { get; }

    /// <summary>
    /// The size of the encoded source in bytes, or <see langword="null"/> when unknown. A known size larger than
    /// <see cref="EasyImageSharpWebOptions.MaxSourceBytes"/> is rejected before the source is opened; an unknown size
    /// is enforced while reading.
    /// </summary>
    long? Length { get; }

    /// <summary>Opens the encoded source for reading. The middleware disposes the stream.</summary>
    /// <param name="cancellationToken">Signalled when the client disconnects.</param>
    /// <returns>A readable stream positioned at the start of the encoded image.</returns>
    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken);
}

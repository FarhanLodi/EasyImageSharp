using Microsoft.Extensions.Logging;

namespace EasyImageSharp.Web.Internal;

/// <summary>
/// The middleware's log events, generated at compile time by the LoggerMessage source generator: no boxing, no
/// format parsing per call, and nothing for the trimmer to lose. Request paths are logged; query strings are not,
/// because a signed URL's query carries its signature.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Debug, "Processed {Path} to {ContentType} ({Length} bytes) in {ElapsedMilliseconds} ms.")]
    public static partial void Processed(ILogger logger, string path, string contentType, int length, long elapsedMilliseconds);

    [LoggerMessage(2, LogLevel.Debug, "Served {Path} from the image cache.")]
    public static partial void ServedFromCache(ILogger logger, string path);

    [LoggerMessage(3, LogLevel.Debug, "Answered {Path} with 304 Not Modified.")]
    public static partial void NotModified(ILogger logger, string path);

    [LoggerMessage(4, LogLevel.Debug, "No source image for {Path}.")]
    public static partial void SourceNotFound(ILogger logger, string path);

    [LoggerMessage(5, LogLevel.Information, "Rejected an unsafe image path.")]
    public static partial void UnsafePath(ILogger logger);

    [LoggerMessage(6, LogLevel.Information, "Rejected {Path}: {Reason}")]
    public static partial void BadCommand(ILogger logger, string path, string reason);

    [LoggerMessage(7, LogLevel.Warning, "Rejected {Path}: the URL signature is missing or invalid.")]
    public static partial void InvalidSignature(ILogger logger, string path);

    [LoggerMessage(8, LogLevel.Information, "Rejected {Path}: the signed URL has expired.")]
    public static partial void ExpiredSignature(ILogger logger, string path);

    [LoggerMessage(9, LogLevel.Information, "Rejected {Path} with {StatusCode}: {Reason}")]
    public static partial void Rejected(ILogger logger, string path, int statusCode, string reason);

    [LoggerMessage(10, LogLevel.Warning, "Image processing is saturated; answered {Path} with 503.")]
    public static partial void Saturated(ILogger logger, string path);

    [LoggerMessage(11, LogLevel.Debug, "The client disconnected while {Path} was being served.")]
    public static partial void ClientDisconnected(ILogger logger, string path);

    [LoggerMessage(12, LogLevel.Warning, "Reading {Path} from the image cache failed; processing it instead.")]
    public static partial void CacheReadFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(13, LogLevel.Warning, "Writing {Path} to the image cache failed.")]
    public static partial void CacheWriteFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(14, LogLevel.Warning, "The source image {Path} could not be processed ({StatusCode}).")]
    public static partial void SourceRejected(ILogger logger, string path, int statusCode, Exception exception);

    [LoggerMessage(15, LogLevel.Warning, "Ignored the corrupt image cache entry {File}.")]
    public static partial void CorruptCacheEntry(ILogger logger, string file);
}

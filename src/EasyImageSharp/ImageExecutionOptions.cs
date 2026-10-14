namespace EasyImageSharp;

/// <summary>Request-local execution settings. Values are captured when an operation starts.</summary>
public sealed class ImageExecutionOptions
{
    /// <summary>Parallelism settings; null captures the current operation or process defaults.</summary>
    public Configuration? Configuration { get; init; }

    /// <summary>Cooperative cancellation, checked at processing and decoding checkpoints.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Maximum cumulative bytes requested for library-owned pixel buffers in this operation.
    /// Includes replacement and copy-on-write buffers, not encoded input, metadata, or codec scratch.
    /// This is an allocation budget, not a bound on total process memory or live memory.
    /// </summary>
    public long MaxPixelBufferBytes { get; init; } = long.MaxValue;
}

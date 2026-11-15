using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Streaming;

/// <summary>Produces a single image frame top to bottom, one row at a time, without materialising the frame.</summary>
internal interface IScanlineSource<TPixel> : IDisposable
    where TPixel : unmanaged, IPixel<TPixel>
{
    /// <summary>Width of the rows this source produces (after any decode-time reduction).</summary>
    int Width { get; }

    /// <summary>Number of rows this source produces.</summary>
    int Height { get; }

    /// <summary>Root-level metadata (EXIF, ICC, XMP, resolution) known once the header has been read.</summary>
    ImageMetadata Metadata { get; }

    /// <summary>Index of the next row ReadRow will produce; equals Height when exhausted.</summary>
    int CurrentRow { get; }

    /// <summary>Writes the next row into destination (length at least Width).</summary>
    /// <returns>false once every row has been produced.</returns>
    /// <exception cref="InvalidImageContentException">The encoded data is malformed or truncated.</exception>
    bool ReadRow(Span<TPixel> destination);
}

/// <summary>Consumes a single image frame top to bottom, one row at a time, encoding incrementally.</summary>
internal interface IScanlineSink<TPixel> : IDisposable
    where TPixel : unmanaged, IPixel<TPixel>
{
    /// <summary>Starts encoding. Called exactly once before the first row.</summary>
    void Begin(int width, int height, ImageMetadata metadata);

    /// <summary>Encodes the next row (exactly Width pixels), top to bottom.</summary>
    void WriteRow(ReadOnlySpan<TPixel> row);

    /// <summary>Flushes the trailer. Called exactly once after the last row.</summary>
    void Complete();
}

namespace EasyImageSharp.Formats.Tiff;

/// <summary>Limits for seekable TIFF region reads. These do not constrain the complete page's dimensions or pixel count.</summary>
public sealed class TiffRegionOptions
{
    public int MaxDirectoryEntries { get; init; } = 4096;
    public int MaxPages { get; init; } = 4096;
    /// <summary>Maximum bytes read from the stream in one open or region operation, including repeated reads.</summary>
    public long MaxReadBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxEncodedSegmentBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxDecodedSegmentBytes { get; init; } = 64 * 1024 * 1024;
    public long MaxRegionPixels { get; init; } = 64L * 1024 * 1024;
    /// <summary>Maximum yielded tiles per enumeration. A caller can stop early without reading remaining tiles.</summary>
    public long MaxTiles { get; init; } = 1_000_000;
    /// <summary>Request-local cancellation, parallelism and pixel-buffer allocation budget. Each yielded tile has its own operation scope.</summary>
    public ImageExecutionOptions? ExecutionOptions { get; init; }

    internal void Validate()
    {
        if (this.MaxDirectoryEntries < 1 || this.MaxPages < 1 || this.MaxReadBytes < 1
            || this.MaxEncodedSegmentBytes < 1 || this.MaxDecodedSegmentBytes < 1
            || this.MaxRegionPixels < 1 || this.MaxTiles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(TiffRegionOptions), "All TIFF region resource limits must be positive.");
        }
    }
}

/// <summary>A region in stored TIFF pixel coordinates. Long origins address huge pages; each materialized image has int dimensions.</summary>
public readonly record struct TiffRegion(long X, long Y, int Width, int Height);

/// <summary>One independently owned output tile. Dispose it before advancing to bound retained pixel memory.</summary>
public sealed class TiffRegionTile<TPixel> : IDisposable
    where TPixel : unmanaged, PixelFormats.IPixel<TPixel>
{
    internal TiffRegionTile(TiffRegion region, Image<TPixel> image)
    {
        this.Region = region;
        this.Image = image;
    }

    public TiffRegion Region { get; }
    public Image<TPixel> Image { get; }
    public void Dispose() => this.Image.Dispose();
}

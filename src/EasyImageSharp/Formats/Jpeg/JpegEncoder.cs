using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Encodes images as JPEG (ITU-T T.81): baseline or progressive Huffman coding, YCbCr with any of the common
/// chroma subsampling layouts, grayscale, RGB, CMYK and YCCK colour, optional restart intervals and optional
/// per-image optimised Huffman tables. Colour images default to 4:4:4 YCbCr and <see cref="L8"/> images to a
/// single luminance component.
/// </summary>
/// <remarks>
/// <para>
/// Quality maps to the Annex K quantisation tables with the usual IJG scaling (5000/q below 50, 200 - 2q above),
/// clamped to 1..255. Chroma is subsampled by box averaging; RGB is converted with the JFIF (BT.601 full-range)
/// matrix. YCbCr and grayscale files carry a JFIF APP0 segment, RGB/CMYK/YCCK files an Adobe APP14 segment.
/// </para>
/// <para>
/// Baseline interleaved output with the standard tables is streamed strip by strip, so memory use stays constant
/// in the image size; progressive, optimised-table and non-interleaved output buffer the quantised coefficients
/// of the whole image (two bytes per coefficient per component) before the first scan is written.
/// </para>
/// </remarks>
public sealed class JpegEncoder : IImageEncoder
{
    private int quality = 90;
    private JpegEncodingColor colorType = JpegEncodingColor.YCbCrRatio444;
    private bool colorTypeSet;
    private int? restartInterval;
    private int progressiveScans;
    private bool? optimizeHuffmanTables;

    /// <summary>Encoding quality from 1 (smallest) to 100 (best); values outside the range are clamped. Defaults to 90.</summary>
    public int Quality
    {
        get => this.quality;
        init => this.quality = Math.Clamp(value, 1, 100);
    }

    /// <summary>
    /// The colour model and chroma subsampling to write. Defaults to <see cref="JpegEncodingColor.YCbCrRatio444"/>;
    /// <see cref="L8"/> images are written as <see cref="JpegEncodingColor.Luminance"/> unless a colour type was set
    /// explicitly.
    /// </summary>
    public JpegEncodingColor ColorType
    {
        get => this.colorType;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown JPEG encoding colour type.");
            }

            this.colorType = value;
            this.colorTypeSet = true;
        }
    }

    /// <summary>
    /// Whether all components of a sequential frame share one scan (true, the default) or each component gets its
    /// own scan. Progressive frames always code AC coefficients per component; the setting only decides whether
    /// their DC scans are interleaved.
    /// </summary>
    public bool Interleaved { get; init; } = true;

    /// <summary>
    /// Number of MCUs between restart markers (1..65535), or null (the default) for no restart markers. A DRI
    /// segment is written and RSTn markers are inserted after every interval, allowing decoders to resynchronise
    /// after corruption or to decode intervals in parallel.
    /// </summary>
    public int? RestartInterval
    {
        get => this.restartInterval;
        init
        {
            if (value is < 1 or > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The restart interval must be between 1 and 65535 MCUs.");
            }

            this.restartInterval = value;
        }
    }

    /// <summary>Writes a progressive (SOF2) frame instead of a baseline (SOF0) one. Defaults to false.</summary>
    public bool Progressive { get; init; }

    /// <summary>
    /// Number of scans in the progressive script (2..64), or 0 (the default) for the standard script: 10 scans for
    /// YCbCr, 6 for grayscale, 14 for RGB and 18 for CMYK/YCCK, following libjpeg's simple progression (DC first
    /// with one bit of successive approximation, spectral selection of the AC band, then refinement scans).
    /// Values below the minimum for the colour type (one DC scan plus one AC scan per component) are raised to it.
    /// </summary>
    public int ProgressiveScans
    {
        get => this.progressiveScans;
        init
        {
            if (value != 0 && value is < 2 or > 64)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "ProgressiveScans must be 0 (default script) or between 2 and 64.");
            }

            this.progressiveScans = value;
        }
    }

    /// <summary>
    /// Whether to derive Huffman tables from the image's own symbol statistics (smaller files, one extra pass over
    /// the coefficients) instead of writing the standard Annex K tables. Defaults to true for progressive output
    /// and false otherwise. With the standard tables, progressive scans cannot use end-of-band runs longer than one
    /// block, so progressive files are noticeably larger without optimisation.
    /// </summary>
    public bool OptimizeHuffmanTables
    {
        get => this.optimizeHuffmanTables ?? this.Progressive;
        init => this.optimizeHuffmanTables = value;
    }

    /// <summary>Encodes <paramref name="image"/> as JPEG to <paramref name="stream"/>.</summary>
    /// <typeparam name="TPixel">The pixel type of the image.</typeparam>
    /// <param name="image">The image to encode; only its root frame is written, as JPEG has no notion of frames.</param>
    /// <param name="stream">The stream to write to.</param>
    /// <exception cref="NotSupportedException">
    /// Either dimension exceeds 65 535 pixels, which a JPEG frame header cannot express.
    /// </exception>
    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        JpegEncodingColor effectiveColor = this.colorType;
        if (!this.colorTypeSet && typeof(TPixel) == typeof(L8))
        {
            effectiveColor = JpegEncodingColor.Luminance;
        }

        var core = new JpegEncoderCore(
            this.quality,
            effectiveColor,
            this.Interleaved,
            this.restartInterval ?? 0,
            this.Progressive,
            this.progressiveScans,
            this.OptimizeHuffmanTables);
        core.Encode(image, stream);
    }
}

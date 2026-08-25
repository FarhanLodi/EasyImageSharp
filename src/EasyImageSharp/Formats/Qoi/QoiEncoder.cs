using System.Buffers.Binary;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Qoi;

/// <summary>The channel count written into a QOI header.</summary>
public enum QoiChannels
{
    /// <summary>Three channels; alpha is treated as fully opaque.</summary>
    Rgb = 3,

    /// <summary>Four channels including alpha.</summary>
    Rgba = 4,
}

/// <summary>The colourspace flag written into a QOI header (informational; pixel values are unaffected).</summary>
public enum QoiColorSpace
{
    /// <summary>sRGB with linear alpha (0).</summary>
    Srgb = 0,

    /// <summary>All channels linear (1).</summary>
    Linear = 1,
}

/// <summary>
/// Encodes images as QOI. The chunk selection mirrors the reference encoder exactly (run, index, diff, luma,
/// then RGB/RGBA), so the output is byte-for-byte identical to that of the reference implementation for the
/// same pixels. Unless <see cref="Channels"/> is set, opaque pixel formats are written with 3 channels and
/// alpha formats with 4. Only the first frame is written.
/// </summary>
public sealed class QoiEncoder : IImageEncoder
{
    /// <summary>The channel count to declare, or <see langword="null"/> to choose from the image's pixel format.</summary>
    public QoiChannels? Channels { get; init; }

    /// <summary>The colourspace flag to declare. Defaults to <see cref="QoiColorSpace.Srgb"/>.</summary>
    public QoiColorSpace ColorSpace { get; init; } = QoiColorSpace.Srgb;

    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        int width = image.Width;
        int height = image.Height;
        QoiChannels channels = this.Channels ?? DefaultChannels<TPixel>();
        if (channels is not (QoiChannels.Rgb or QoiChannels.Rgba))
        {
            throw new ArgumentOutOfRangeException(nameof(this.Channels), channels, "Channels must be Rgb or Rgba.");
        }

        Span<byte> header = stackalloc byte[QoiDecoder.HeaderSize];
        "qoif"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)height);
        header[12] = (byte)channels;
        header[13] = (byte)this.ColorSpace;
        stream.Write(header);

        // Worst case is one QOI_OP_RGBA (5 bytes) per pixel; buffer a row at a time.
        var chunkBuffer = new byte[(width * 5) + 1];
        var rgbaRow = new Rgba32[width];
        Span<Rgba32> index = stackalloc Rgba32[64];
        index.Clear();
        bool opaqueOnly = channels == QoiChannels.Rgb;
        var prev = new Rgba32(0, 0, 0, 255);
        int run = 0;
        long lastPixel = ((long)width * height) - 1;
        long pixelIndex = 0;
        ImageFrame<TPixel> frame = image.Frames.RootFrame;

        for (int y = 0; y < height; y++)
        {
            PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y), rgbaRow);
            int p = 0;
            for (int x = 0; x < width; x++, pixelIndex++)
            {
                Rgba32 px = rgbaRow[x];
                if (opaqueOnly)
                {
                    px.A = 255;
                }

                if (px == prev)
                {
                    run++;
                    if (run == 62 || pixelIndex == lastPixel)
                    {
                        chunkBuffer[p++] = (byte)(0xC0 | (run - 1));
                        run = 0;
                    }
                }
                else
                {
                    if (run > 0)
                    {
                        chunkBuffer[p++] = (byte)(0xC0 | (run - 1));
                        run = 0;
                    }

                    int hash = QoiDecoder.Hash(px);
                    if (index[hash] == px)
                    {
                        chunkBuffer[p++] = (byte)hash;
                    }
                    else
                    {
                        index[hash] = px;
                        if (px.A == prev.A)
                        {
                            sbyte vr = (sbyte)(px.R - prev.R);
                            sbyte vg = (sbyte)(px.G - prev.G);
                            sbyte vb = (sbyte)(px.B - prev.B);
                            sbyte vgr = (sbyte)(vr - vg);
                            sbyte vgb = (sbyte)(vb - vg);
                            if (vr is > -3 and < 2 && vg is > -3 and < 2 && vb is > -3 and < 2)
                            {
                                chunkBuffer[p++] = (byte)(0x40 | ((vr + 2) << 4) | ((vg + 2) << 2) | (vb + 2));
                            }
                            else if (vgr is > -9 and < 8 && vg is > -33 and < 32 && vgb is > -9 and < 8)
                            {
                                chunkBuffer[p++] = (byte)(0x80 | (vg + 32));
                                chunkBuffer[p++] = (byte)(((vgr + 8) << 4) | (vgb + 8));
                            }
                            else
                            {
                                chunkBuffer[p++] = 0xFE;
                                chunkBuffer[p++] = px.R;
                                chunkBuffer[p++] = px.G;
                                chunkBuffer[p++] = px.B;
                            }
                        }
                        else
                        {
                            chunkBuffer[p++] = 0xFF;
                            chunkBuffer[p++] = px.R;
                            chunkBuffer[p++] = px.G;
                            chunkBuffer[p++] = px.B;
                            chunkBuffer[p++] = px.A;
                        }
                    }
                }

                prev = px;
            }

            stream.Write(chunkBuffer, 0, p);
        }

        stream.Write(QoiDecoder.EndMarker);
    }

    private static QoiChannels DefaultChannels<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => typeof(TPixel) == typeof(Rgba32) || typeof(TPixel) == typeof(Bgra32) ? QoiChannels.Rgba : QoiChannels.Rgb;
}

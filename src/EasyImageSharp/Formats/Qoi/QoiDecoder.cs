using System.Buffers.Binary;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Qoi;

/// <summary>
/// Decodes "Quite OK Image" (QOI) files as specified at qoiformat.org: the 14-byte header, every chunk type
/// (QOI_OP_RGB, QOI_OP_RGBA, QOI_OP_INDEX, QOI_OP_DIFF, QOI_OP_LUMA, QOI_OP_RUN), the 64-entry colour index
/// and the 8-byte end marker. Both 3- and 4-channel streams decode to RGBA; the colourspace byte is validated
/// but does not change the pixel values.
/// </summary>
/// <remarks>
/// The decoder is strict: the chunk stream must produce exactly width × height pixels and be followed by the
/// end marker, otherwise <see cref="InvalidImageContentException"/> is thrown.
/// </remarks>
public sealed class QoiDecoder : IImageDecoder
{
    internal const int HeaderSize = 14;
    internal const int EndMarkerSize = 8;

    private const byte OpRgb = 0xFE;
    private const byte OpRgba = 0xFF;
    private const byte OpIndex = 0x00;
    private const byte OpDiff = 0x40;
    private const byte OpLuma = 0x80;
    private const byte OpRun = 0xC0;
    private const byte Mask2 = 0xC0;

    public Image<TPixel> Decode<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return DecodeCore<TPixel>(data, options);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("QOI", ex);
        }
    }

    public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            Header header = ParseHeader(data);
            return new ImageInfo(header.Width, header.Height, header.Channels * 8, 1, ImageFormat.Qoi);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("QOI", ex);
        }
    }

    private static Image<TPixel> DecodeCore<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Header header = ParseHeader(data);
        int width = header.Width;
        int height = header.Height;
        options.EnsureFrameWithinLimits(width, height, "QOI");

        var image = new Image<TPixel>(width, height);
        ImageFrame<TPixel> frame = image.Frames.RootFrame;
        var rgbaRow = new Rgba32[width];
        Span<Rgba32> index = stackalloc Rgba32[64];
        index.Clear();
        var px = new Rgba32(0, 0, 0, 255);
        int run = 0;
        int pos = HeaderSize;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (run > 0)
                {
                    run--;
                }
                else
                {
                    if (pos >= data.Length)
                    {
                        throw new InvalidImageContentException("QOI chunk stream ends before every pixel is decoded.");
                    }

                    byte b1 = data[pos++];
                    if (b1 == OpRgb)
                    {
                        if (pos + 3 > data.Length)
                        {
                            throw new InvalidImageContentException("QOI_OP_RGB chunk is truncated.");
                        }

                        px = new Rgba32(data[pos], data[pos + 1], data[pos + 2], px.A);
                        pos += 3;
                    }
                    else if (b1 == OpRgba)
                    {
                        if (pos + 4 > data.Length)
                        {
                            throw new InvalidImageContentException("QOI_OP_RGBA chunk is truncated.");
                        }

                        px = new Rgba32(data[pos], data[pos + 1], data[pos + 2], data[pos + 3]);
                        pos += 4;
                    }
                    else
                    {
                        switch (b1 & Mask2)
                        {
                            case OpIndex:
                                px = index[b1];
                                break;
                            case OpDiff:
                                px = new Rgba32(
                                    (byte)(px.R + ((b1 >> 4) & 0x03) - 2),
                                    (byte)(px.G + ((b1 >> 2) & 0x03) - 2),
                                    (byte)(px.B + (b1 & 0x03) - 2),
                                    px.A);
                                break;
                            case OpLuma:
                            {
                                if (pos >= data.Length)
                                {
                                    throw new InvalidImageContentException("QOI_OP_LUMA chunk is truncated.");
                                }

                                byte b2 = data[pos++];
                                int vg = (b1 & 0x3F) - 32;
                                px = new Rgba32(
                                    (byte)(px.R + vg - 8 + ((b2 >> 4) & 0x0F)),
                                    (byte)(px.G + vg),
                                    (byte)(px.B + vg - 8 + (b2 & 0x0F)),
                                    px.A);
                                break;
                            }

                            default:
                                run = b1 & 0x3F;
                                break;
                        }
                    }

                    index[Hash(px)] = px;
                }

                rgbaRow[x] = px;
            }

            PixelOps.FromRgba32(rgbaRow, frame.GetRowSpan(y));
        }

        if (run > 0)
        {
            throw new InvalidImageContentException("QOI run extends past the last pixel.");
        }

        if (pos + EndMarkerSize > data.Length || !data.Slice(pos, EndMarkerSize).SequenceEqual(EndMarker))
        {
            throw new InvalidImageContentException("QOI end marker is missing.");
        }

        return image;
    }

    internal static ReadOnlySpan<byte> EndMarker => new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 };

    internal static int Hash(Rgba32 p) => ((p.R * 3) + (p.G * 5) + (p.B * 7) + (p.A * 11)) & 63;

    private static Header ParseHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || !data[..4].SequenceEqual("qoif"u8))
        {
            throw new InvalidImageContentException("Missing QOI signature.");
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        int channels = data[12];
        int colorSpace = data[13];
        if (width == 0 || height == 0)
        {
            throw new InvalidImageContentException("QOI image has a zero dimension.");
        }

        if (width > int.MaxValue || height > int.MaxValue)
        {
            throw new InvalidImageContentException($"QOI dimensions {width}x{height} are out of range.");
        }

        if (channels is not (3 or 4))
        {
            throw new InvalidImageContentException($"Invalid QOI channel count {channels}; expected 3 or 4.");
        }

        if (colorSpace is not (0 or 1))
        {
            throw new InvalidImageContentException($"Invalid QOI colourspace {colorSpace}; expected 0 (sRGB) or 1 (linear).");
        }

        return new Header((int)width, (int)height, channels, colorSpace);
    }

    private readonly record struct Header(int Width, int Height, int Channels, int ColorSpace);
}

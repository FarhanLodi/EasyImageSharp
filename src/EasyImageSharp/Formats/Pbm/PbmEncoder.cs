using System.Buffers.Binary;
using System.Text;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Pbm;

/// <summary>The Netpbm colour models <see cref="PbmEncoder"/> can write.</summary>
public enum PbmColorType
{
    /// <summary>Bilevel bitmap (PBM, P1/P4): pixels with luminance below 128 become black.</summary>
    BlackAndWhite,

    /// <summary>Grayscale graymap (PGM, P2/P5).</summary>
    Grayscale,

    /// <summary>RGB pixmap (PPM, P3/P6). Alpha is discarded.</summary>
    Rgb,
}

/// <summary>Whether <see cref="PbmEncoder"/> writes the plain (ASCII) or the binary (raw) variant.</summary>
public enum PbmEncoding
{
    /// <summary>ASCII decimal samples (P1/P2/P3).</summary>
    Plain,

    /// <summary>Binary samples (P4/P5/P6).</summary>
    Binary,
}

/// <summary>The sample width <see cref="PbmEncoder"/> writes for grayscale and RGB output.</summary>
public enum PbmComponentType
{
    /// <summary>8-bit samples, maxval 255.</summary>
    Byte,

    /// <summary>16-bit samples, maxval 65535 (values are widened by <c>v * 257</c>).</summary>
    Short,
}

/// <summary>
/// Encodes images in the Netpbm formats. Unless <see cref="ColorType"/> is set, <see cref="L8"/> images
/// are written as graymaps and every other pixel format as an RGB pixmap; alpha is not representable and is
/// discarded. Only the first frame is written.
/// </summary>
public sealed class PbmEncoder : IImageEncoder
{
    private const int MaxPlainLineLength = 70;

    /// <summary>The colour model to write, or <see langword="null"/> to choose from the image's pixel format.</summary>
    public PbmColorType? ColorType { get; init; }

    /// <summary>Plain (ASCII) or binary output. Defaults to <see cref="PbmEncoding.Binary"/>.</summary>
    public PbmEncoding Encoding { get; init; } = PbmEncoding.Binary;

    /// <summary>8- or 16-bit samples for graymaps and pixmaps. Defaults to <see cref="PbmComponentType.Byte"/>.</summary>
    public PbmComponentType ComponentType { get; init; } = PbmComponentType.Byte;

    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        PbmColorType colorType = this.ColorType ?? (typeof(TPixel) == typeof(L8) ? PbmColorType.Grayscale : PbmColorType.Rgb);
        bool binary = this.Encoding == PbmEncoding.Binary;
        bool wide = colorType != PbmColorType.BlackAndWhite && this.ComponentType == PbmComponentType.Short;
        int width = image.Width;
        int height = image.Height;

        int magic = colorType switch
        {
            PbmColorType.BlackAndWhite => binary ? 4 : 1,
            PbmColorType.Grayscale => binary ? 5 : 2,
            _ => binary ? 6 : 3,
        };
        var header = new StringBuilder();
        header.Append('P').Append(magic).Append('\n').Append(width).Append(' ').Append(height).Append('\n');
        if (colorType != PbmColorType.BlackAndWhite)
        {
            header.Append(wide ? 65535 : 255).Append('\n');
        }

        stream.Write(System.Text.Encoding.ASCII.GetBytes(header.ToString()));

        ImageFrame<TPixel> frame = image.Frames.RootFrame;
        var rgbaRow = new Rgba32[width];
        int channels = colorType == PbmColorType.Rgb ? 3 : 1;
        var samples = new int[width * channels];
        byte[] rowBytes = binary
            ? new byte[colorType == PbmColorType.BlackAndWhite ? (width + 7) / 8 : width * channels * (wide ? 2 : 1)]
            : Array.Empty<byte>();
        var text = binary ? null : new StringBuilder();

        for (int y = 0; y < height; y++)
        {
            PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y), rgbaRow);
            FillSamples(rgbaRow, samples, colorType, wide);
            if (binary)
            {
                if (colorType == PbmColorType.BlackAndWhite)
                {
                    Array.Clear(rowBytes);
                    for (int x = 0; x < width; x++)
                    {
                        if (samples[x] != 0)
                        {
                            rowBytes[x >> 3] |= (byte)(0x80 >> (x & 7));
                        }
                    }
                }
                else if (wide)
                {
                    for (int i = 0; i < samples.Length; i++)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(rowBytes.AsSpan(i * 2), (ushort)samples[i]);
                    }
                }
                else
                {
                    for (int i = 0; i < samples.Length; i++)
                    {
                        rowBytes[i] = (byte)samples[i];
                    }
                }

                stream.Write(rowBytes);
            }
            else
            {
                text!.Clear();
                AppendPlainRow(text, samples, colorType == PbmColorType.BlackAndWhite);
                stream.Write(System.Text.Encoding.ASCII.GetBytes(text.ToString()));
            }
        }
    }

    /// <summary>Converts a row to file samples: 0/1 for bitmaps (1 = black), 8- or 16-bit gray or RGB otherwise.</summary>
    private static void FillSamples(ReadOnlySpan<Rgba32> row, Span<int> samples, PbmColorType colorType, bool wide)
    {
        int scale = wide ? 257 : 1;
        switch (colorType)
        {
            case PbmColorType.BlackAndWhite:
                for (int x = 0; x < row.Length; x++)
                {
                    samples[x] = PixelOps.Luminance8(row[x]) < 128 ? 1 : 0;
                }

                break;
            case PbmColorType.Grayscale:
                for (int x = 0; x < row.Length; x++)
                {
                    samples[x] = PixelOps.Luminance8(row[x]) * scale;
                }

                break;
            default:
                for (int x = 0; x < row.Length; x++)
                {
                    Rgba32 p = row[x];
                    int i = x * 3;
                    samples[i] = p.R * scale;
                    samples[i + 1] = p.G * scale;
                    samples[i + 2] = p.B * scale;
                }

                break;
        }
    }

    /// <summary>Appends one row of plain samples, wrapping so that no line exceeds 70 characters as the format requires.</summary>
    private static void AppendPlainRow(StringBuilder text, ReadOnlySpan<int> samples, bool bitmap)
    {
        int lineLength = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            string token = samples[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            int needed = token.Length + (lineLength > 0 && !bitmap ? 1 : 0);
            if (lineLength > 0 && lineLength + needed > MaxPlainLineLength)
            {
                text.Append('\n');
                lineLength = 0;
                needed = token.Length;
            }

            if (lineLength > 0 && !bitmap)
            {
                text.Append(' ');
            }

            text.Append(token);
            lineLength += needed;
        }

        text.Append('\n');
    }
}

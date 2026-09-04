using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Pbm;

/// <summary>
/// Decodes the Netpbm family: plain (ASCII) P1/P2/P3 and binary P4/P5/P6 bitmaps, graymaps and pixmaps with
/// any maxval up to 65535 (samples above 255 are stored as big-endian 16-bit values), comments anywhere in the
/// header, and P7 PAM files with the GRAYSCALE, GRAYSCALE_ALPHA, RGB, RGB_ALPHA and BLACKANDWHITE(_ALPHA)
/// tuple types. Samples are scaled to 8 bits with rounding (<c>round(v * 255 / maxval)</c>). A stream holding
/// several concatenated images decodes to one frame per image, subject to <see cref="DecoderOptions.MaxFrames"/>.
/// </summary>
/// <remarks>
/// PBM (P1/P4) follows the Netpbm convention 1 = black; a PAM BLACKANDWHITE tuple follows the PAM convention
/// 1 = white. PAM files with a depth other than 1-4 are reported as unsupported.
/// </remarks>
public sealed class PbmDecoder : IImageDecoder
{
    private const int MaxMaxVal = 65535;

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
            throw DecoderGuard.Wrap("PBM", ex);
        }
    }

    public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            int pos = 0;
            PbmHeader first = ReadHeader(data, ref pos);
            int frames = 1;
            int cursor = pos;
            PbmHeader current = first;
            while (TrySkipRaster(data, current, ref cursor) && TryReadNextHeader(data, ref cursor, out PbmHeader next))
            {
                frames++;
                current = next;
            }

            return new ImageInfo(first.Width, first.Height, first.BitsPerPixel, frames, ImageFormat.Pbm);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("PBM", ex);
        }
    }

    private static Image<TPixel> DecodeCore<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int pos = 0;
        PbmHeader header = ReadHeader(data, ref pos);
        var frames = new List<ImageFrame<TPixel>>();

        // A concatenated stream costs about fifteen bytes per extra declared image, so the per-frame limit
        // alone leaves total allocation unbounded; charge every raster to the cumulative budget as well.
        DecoderOptions.FrameBudget budget = options.CreateBudget();
        while (true)
        {
            budget.Add(header.Width, header.Height, "PBM");
            frames.Add(DecodeRaster<TPixel>(data, header, ref pos));
            if (frames.Count >= options.MaxFrames || !TryReadNextHeader(data, ref pos, out header))
            {
                break;
            }
        }

        return new Image<TPixel>(frames);
    }

    // ----- Header parsing -----

    private static PbmHeader ReadHeader(ReadOnlySpan<byte> data, ref int pos)
    {
        if (data.Length < pos + 3 || data[pos] != 'P' || data[pos + 1] is < (byte)'1' or > (byte)'7')
        {
            throw new InvalidImageContentException("Missing Netpbm magic number.");
        }

        int kind = data[pos + 1] - '0';
        pos += 2;
        if (kind == 7)
        {
            return ReadPamHeader(data, ref pos);
        }

        if (!IsWhitespace(data[pos]) && data[pos] != '#')
        {
            throw new InvalidImageContentException("Netpbm magic number is not followed by whitespace.");
        }

        int width = ReadInt(data, ref pos, "width");
        int height = ReadInt(data, ref pos, "height");
        int maxVal = 1;
        if (kind is not (1 or 4))
        {
            maxVal = ReadInt(data, ref pos, "maxval");
        }

        // Exactly one whitespace character separates the header from the raster (for plain formats the raster
        // tokenizer skips any further whitespace or comments anyway).
        if (pos >= data.Length || !IsWhitespace(data[pos]))
        {
            throw new InvalidImageContentException("Netpbm header is not terminated by whitespace.");
        }

        pos++;

        Validate(width, height, maxVal);
        TupleType tuple = kind switch
        {
            1 or 4 => TupleType.Bitmap,
            2 or 5 => TupleType.Gray,
            _ => TupleType.Rgb,
        };
        return new PbmHeader(kind, width, height, maxVal, tuple);
    }

    private static PbmHeader ReadPamHeader(ReadOnlySpan<byte> data, ref int pos)
    {
        int width = -1, height = -1, depth = -1, maxVal = -1;
        string tupleType = string.Empty;
        while (true)
        {
            if (pos >= data.Length)
            {
                throw new InvalidImageContentException("PAM header has no ENDHDR line.");
            }

            int end = data[pos..].IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = end < 0 ? data[pos..] : data.Slice(pos, end);
            pos += end < 0 ? line.Length : end + 1;
            int hash = line.IndexOf((byte)'#');
            if (hash >= 0)
            {
                line = line[..hash];
            }

            line = TrimWhitespace(line);
            if (line.IsEmpty)
            {
                continue;
            }

            int space = IndexOfWhitespace(line);
            ReadOnlySpan<byte> keyword = space < 0 ? line : line[..space];
            ReadOnlySpan<byte> value = space < 0 ? default : TrimWhitespace(line[space..]);
            if (keyword.SequenceEqual("ENDHDR"u8))
            {
                break;
            }

            if (keyword.SequenceEqual("WIDTH"u8))
            {
                width = ParseInt(value, "WIDTH");
            }
            else if (keyword.SequenceEqual("HEIGHT"u8))
            {
                height = ParseInt(value, "HEIGHT");
            }
            else if (keyword.SequenceEqual("DEPTH"u8))
            {
                depth = ParseInt(value, "DEPTH");
            }
            else if (keyword.SequenceEqual("MAXVAL"u8))
            {
                maxVal = ParseInt(value, "MAXVAL");
            }
            else if (keyword.SequenceEqual("TUPLTYPE"u8))
            {
                // Repeated TUPLTYPE lines concatenate (separated by a space) per the PAM specification.
                string part = System.Text.Encoding.ASCII.GetString(value);
                tupleType = tupleType.Length == 0 ? part : tupleType + " " + part;
            }
            else
            {
                throw new InvalidImageContentException($"Unknown PAM header keyword '{System.Text.Encoding.ASCII.GetString(keyword)}'.");
            }
        }

        if (width < 0 || height < 0 || depth < 0 || maxVal < 0)
        {
            throw new InvalidImageContentException("PAM header is missing WIDTH, HEIGHT, DEPTH or MAXVAL.");
        }

        Validate(width, height, maxVal);
        TupleType tuple = depth switch
        {
            1 => TupleType.Gray,
            2 => TupleType.GrayAlpha,
            3 => TupleType.Rgb,
            4 => TupleType.RgbAlpha,
            _ => throw new NotSupportedException($"PAM images with DEPTH {depth} are not supported (only 1-4 channel tuples)."),
        };

        // Known tuple types must agree with the depth; unknown ones are interpreted by depth alone.
        int? expectedDepth = tupleType switch
        {
            "BLACKANDWHITE" or "GRAYSCALE" => 1,
            "BLACKANDWHITE_ALPHA" or "GRAYSCALE_ALPHA" => 2,
            "RGB" => 3,
            "RGB_ALPHA" => 4,
            _ => null,
        };
        if (expectedDepth is int e && e != depth)
        {
            throw new InvalidImageContentException($"PAM TUPLTYPE {tupleType} requires DEPTH {e} but the header declares {depth}.");
        }

        return new PbmHeader(7, width, height, maxVal, tuple);
    }

    private static void Validate(int width, int height, int maxVal)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidImageContentException($"Invalid Netpbm dimensions {width}x{height}.");
        }

        if (maxVal <= 0 || maxVal > MaxMaxVal)
        {
            throw new InvalidImageContentException($"Invalid Netpbm maxval {maxVal}; it must be between 1 and {MaxMaxVal}.");
        }
    }

    /// <summary>Attempts to read a further image header after a raster; anything else (trailing bytes, EOF) ends the stream.</summary>
    private static bool TryReadNextHeader(ReadOnlySpan<byte> data, ref int pos, out PbmHeader header)
    {
        int cursor = pos;
        SkipWhitespaceAndComments(data, ref cursor);
        if (cursor + 3 > data.Length || data[cursor] != 'P' || data[cursor + 1] is < (byte)'1' or > (byte)'7'
            || !(IsWhitespace(data[cursor + 2]) || data[cursor + 2] == '#'))
        {
            header = default;
            return false;
        }

        try
        {
            header = ReadHeader(data, ref cursor);
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            // Trailing bytes that merely resemble a header end the stream instead of failing the decode.
            header = default;
            return false;
        }

        pos = cursor;
        return true;
    }

    // ----- Raster decoding -----

    private static ImageFrame<TPixel> DecodeRaster<TPixel>(ReadOnlySpan<byte> data, in PbmHeader header, ref int pos)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = header.Width;
        int height = header.Height;
        var frame = new ImageFrame<TPixel>(width, height);
        var rgbaRow = new Rgba32[width];
        byte[]? lut = header.MaxVal == 255 ? null : BuildScaleTable(header.MaxVal);
        int channels = header.Channels;
        var samples = new int[width * channels];

        switch (header.Kind)
        {
            case 1:
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        SkipWhitespaceAndComments(data, ref pos);
                        if (pos >= data.Length)
                        {
                            throw new InvalidImageContentException("PBM raster ends before all pixels are read.");
                        }

                        byte c = data[pos++];
                        if (c is not ((byte)'0' or (byte)'1'))
                        {
                            throw new InvalidImageContentException($"Invalid character '{(char)c}' in a plain PBM raster.");
                        }

                        rgbaRow[x] = c == '1' ? Rgba32.Black : Rgba32.White;
                    }

                    PixelOps.FromRgba32(rgbaRow, frame.GetRowSpan(y));
                }

                break;

            case 2:
            case 3:
                for (int y = 0; y < height; y++)
                {
                    for (int i = 0; i < samples.Length; i++)
                    {
                        int v = ReadInt(data, ref pos, "sample");
                        if (v > header.MaxVal)
                        {
                            throw new InvalidImageContentException($"Netpbm sample {v} exceeds maxval {header.MaxVal}.");
                        }

                        samples[i] = v;
                    }

                    SamplesToRgba(samples, rgbaRow, header.Tuple, lut);
                    PixelOps.FromRgba32(rgbaRow, frame.GetRowSpan(y));
                }

                break;

            case 4:
            {
                int stride = (width + 7) / 8;
                if ((long)stride * height > data.Length - pos)
                {
                    throw new InvalidImageContentException("PBM raster is truncated.");
                }

                for (int y = 0; y < height; y++)
                {
                    ReadOnlySpan<byte> row = data.Slice(pos, stride);
                    for (int x = 0; x < width; x++)
                    {
                        rgbaRow[x] = (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? Rgba32.Black : Rgba32.White;
                    }

                    PixelOps.FromRgba32(rgbaRow, frame.GetRowSpan(y));
                    pos += stride;
                }

                break;
            }

            default:
            {
                int bytesPerSample = header.BytesPerSample;
                int stride = width * channels * bytesPerSample;
                if ((long)stride * height > data.Length - pos)
                {
                    throw new InvalidImageContentException("Netpbm raster is truncated.");
                }

                for (int y = 0; y < height; y++)
                {
                    ReadOnlySpan<byte> row = data.Slice(pos, stride);
                    if (bytesPerSample == 1)
                    {
                        for (int i = 0; i < samples.Length; i++)
                        {
                            samples[i] = row[i];
                        }
                    }
                    else
                    {
                        for (int i = 0; i < samples.Length; i++)
                        {
                            samples[i] = (row[i * 2] << 8) | row[(i * 2) + 1];
                        }
                    }

                    for (int i = 0; i < samples.Length; i++)
                    {
                        if (samples[i] > header.MaxVal)
                        {
                            throw new InvalidImageContentException($"Netpbm sample {samples[i]} exceeds maxval {header.MaxVal}.");
                        }
                    }

                    SamplesToRgba(samples, rgbaRow, header.Tuple, lut);
                    PixelOps.FromRgba32(rgbaRow, frame.GetRowSpan(y));
                    pos += stride;
                }

                break;
            }
        }

        return frame;
    }

    private static void SamplesToRgba(ReadOnlySpan<int> samples, Span<Rgba32> dest, TupleType tuple, byte[]? lut)
    {
        switch (tuple)
        {
            case TupleType.Gray:
                for (int x = 0; x < dest.Length; x++)
                {
                    byte v = Scale(samples[x], lut);
                    dest[x] = new Rgba32(v, v, v);
                }

                break;
            case TupleType.GrayAlpha:
                for (int x = 0; x < dest.Length; x++)
                {
                    byte v = Scale(samples[x * 2], lut);
                    dest[x] = new Rgba32(v, v, v, Scale(samples[(x * 2) + 1], lut));
                }

                break;
            case TupleType.Rgb:
                for (int x = 0; x < dest.Length; x++)
                {
                    int i = x * 3;
                    dest[x] = new Rgba32(Scale(samples[i], lut), Scale(samples[i + 1], lut), Scale(samples[i + 2], lut));
                }

                break;
            default:
                for (int x = 0; x < dest.Length; x++)
                {
                    int i = x * 4;
                    dest[x] = new Rgba32(Scale(samples[i], lut), Scale(samples[i + 1], lut), Scale(samples[i + 2], lut), Scale(samples[i + 3], lut));
                }

                break;
        }
    }

    private static byte Scale(int sample, byte[]? lut) => lut is null ? (byte)sample : lut[sample];

    private static byte[] BuildScaleTable(int maxVal)
    {
        var table = new byte[maxVal + 1];
        for (int v = 0; v <= maxVal; v++)
        {
            table[v] = (byte)(((v * 255) + (maxVal / 2)) / maxVal);
        }

        return table;
    }

    /// <summary>Advances past one raster without decoding it (used by <see cref="Identify"/> to count images).</summary>
    private static bool TrySkipRaster(ReadOnlySpan<byte> data, in PbmHeader header, ref int pos)
    {
        long width = header.Width;
        long height = header.Height;
        switch (header.Kind)
        {
            case 1:
            {
                long remaining = width * height;
                while (remaining > 0)
                {
                    SkipWhitespaceAndComments(data, ref pos);
                    if (pos >= data.Length)
                    {
                        return false;
                    }

                    pos++;
                    remaining--;
                }

                return true;
            }

            case 2:
            case 3:
            {
                long remaining = width * height * header.Channels;
                while (remaining > 0)
                {
                    SkipWhitespaceAndComments(data, ref pos);
                    int start = pos;
                    while (pos < data.Length && data[pos] is >= (byte)'0' and <= (byte)'9')
                    {
                        pos++;
                    }

                    if (pos == start)
                    {
                        return false;
                    }

                    remaining--;
                }

                return true;
            }

            case 4:
            {
                long bytes = ((width + 7) / 8) * height;
                if (bytes > data.Length - pos)
                {
                    return false;
                }

                pos += (int)bytes;
                return true;
            }

            default:
            {
                long bytes = width * height * header.Channels * header.BytesPerSample;
                if (bytes > data.Length - pos)
                {
                    return false;
                }

                pos += (int)bytes;
                return true;
            }
        }
    }

    // ----- Tokenizer -----

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0B or 0x0C;

    private static void SkipWhitespaceAndComments(ReadOnlySpan<byte> data, ref int pos)
    {
        while (pos < data.Length)
        {
            byte b = data[pos];
            if (IsWhitespace(b))
            {
                pos++;
            }
            else if (b == '#')
            {
                while (pos < data.Length && data[pos] != '\n' && data[pos] != '\r')
                {
                    pos++;
                }
            }
            else
            {
                return;
            }
        }
    }

    private static int ReadInt(ReadOnlySpan<byte> data, ref int pos, string what)
    {
        SkipWhitespaceAndComments(data, ref pos);
        int start = pos;
        long value = 0;
        while (pos < data.Length && data[pos] is >= (byte)'0' and <= (byte)'9')
        {
            value = (value * 10) + (data[pos] - '0');
            if (value > int.MaxValue)
            {
                throw new InvalidImageContentException($"Netpbm {what} value is out of range.");
            }

            pos++;
        }

        if (pos == start)
        {
            throw new InvalidImageContentException(pos >= data.Length
                ? $"Netpbm data ends where a {what} value was expected."
                : $"Expected a decimal {what} value in Netpbm data but found '{(char)data[pos]}'.");
        }

        return (int)value;
    }

    private static int ParseInt(ReadOnlySpan<byte> text, string what)
    {
        int pos = 0;
        int value = ReadInt(text, ref pos, what);
        if (pos != text.Length)
        {
            throw new InvalidImageContentException($"PAM {what} value contains non-numeric characters.");
        }

        return value;
    }

    private static ReadOnlySpan<byte> TrimWhitespace(ReadOnlySpan<byte> line)
    {
        int start = 0;
        int end = line.Length;
        while (start < end && IsWhitespace(line[start]))
        {
            start++;
        }

        while (end > start && IsWhitespace(line[end - 1]))
        {
            end--;
        }

        return line[start..end];
    }

    private static int IndexOfWhitespace(ReadOnlySpan<byte> line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (IsWhitespace(line[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private enum TupleType
    {
        Bitmap,
        Gray,
        GrayAlpha,
        Rgb,
        RgbAlpha,
    }

    private readonly record struct PbmHeader(int Kind, int Width, int Height, int MaxVal, TupleType Tuple)
    {
        public int Channels => this.Tuple switch
        {
            TupleType.Bitmap or TupleType.Gray => 1,
            TupleType.GrayAlpha => 2,
            TupleType.Rgb => 3,
            _ => 4,
        };

        public int BytesPerSample => this.MaxVal > 255 ? 2 : 1;

        /// <summary>Bits per pixel as stored in the file (1 for bitmaps, 8 or 16 per channel otherwise).</summary>
        public int BitsPerPixel => this.Tuple == TupleType.Bitmap ? 1 : this.Channels * this.BytesPerSample * 8;
    }
}

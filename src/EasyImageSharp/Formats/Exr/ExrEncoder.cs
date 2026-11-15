using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using EasyImageSharp.Formats.Hdr;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Exr;

/// <summary>
/// Encodes images as single-part scanline OpenEXR files with HALF or FLOAT channels and NONE, RLE, ZIPS, ZIP or
/// PIZ compression. Only the first frame is written; the data and display windows both cover the whole image.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RgbaVector"/> pixels are written as the linear values they hold; every other pixel format is taken
/// to be sRGB-encoded and its colour components are linearised with the inverse sRGB transfer function (alpha is
/// written as is), so an 8-bit image written as
/// HALF and read back into the same pixel format is unchanged. HALF rounds to nearest even and saturates to
/// infinity beyond 65504; FLOAT is exact.
/// </para>
/// <para>
/// A chunk whose compressed form would not be smaller than its raw form is stored raw, as the reference library
/// does. When the image carries <see cref="ExrMetadata"/>, its chromaticities, pixel aspect ratio and string
/// attributes are written as well.
/// </para>
/// </remarks>
public sealed class ExrEncoder : IImageEncoder
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "channels", "chromaticities", "compression", "dataWindow", "displayWindow", "lineOrder", "pixelAspectRatio",
        "screenWindowCenter", "screenWindowWidth", "tiles", "type", "name", "version", "chunkCount",
    };

    /// <summary>The compression method. Defaults to <see cref="ExrCompression.Zip"/>.</summary>
    public ExrCompression Compression { get; init; } = ExrCompression.Zip;

    /// <summary>The sample type of every channel: <see cref="ExrPixelType.Half"/> (the default) or <see cref="ExrPixelType.Float"/>.</summary>
    public ExrPixelType PixelType { get; init; } = ExrPixelType.Half;

    /// <summary>
    /// The channels to write, or <see langword="null"/> to choose from the pixel format: luminance for the gray
    /// formats, luminance and alpha for the gray-alpha formats, RGBA for formats with alpha and RGB otherwise.
    /// </summary>
    public ExrChannelLayout? Channels { get; init; }

    /// <inheritdoc/>
    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);
        if (this.Compression is not (ExrCompression.None or ExrCompression.Rle or ExrCompression.Zips or ExrCompression.Zip or ExrCompression.Piz))
        {
            throw new NotSupportedException($"Writing OpenEXR with {this.Compression} compression is not supported; use None, Rle, Zips, Zip or Piz.");
        }

        if (this.PixelType is not (ExrPixelType.Half or ExrPixelType.Float))
        {
            throw new ArgumentOutOfRangeException(nameof(this.PixelType), this.PixelType, "OpenEXR images can be written with Half or Float channels.");
        }

        ExrChannelLayout layout = this.Channels ?? DefaultLayout<TPixel>();
        string[] names = layout switch
        {
            ExrChannelLayout.Rgb => new[] { "B", "G", "R" },
            ExrChannelLayout.Rgba => new[] { "A", "B", "G", "R" },
            ExrChannelLayout.Luminance => new[] { "Y" },
            ExrChannelLayout.LuminanceAlpha => new[] { "A", "Y" },
            _ => throw new ArgumentOutOfRangeException(nameof(this.Channels), layout, "Unknown OpenEXR channel layout."),
        };

        ImageFrame<TPixel> frame = image.Frames.RootFrame;
        int width = frame.Width;
        int height = frame.Height;
        int sampleBytes = this.PixelType == ExrPixelType.Half ? 2 : 4;
        int pixelType = this.PixelType == ExrPixelType.Half ? ExrChannel.Half : ExrChannel.Float;
        var channels = names.Select(n => new ExrChannel(n, pixelType, false, 1, 1)).ToArray();
        int linesPerChunk = this.Compression switch
        {
            ExrCompression.Zip => 16,
            ExrCompression.Piz => 32,
            _ => 1,
        };

        long lineBytes = (long)width * names.Length * sampleBytes;
        if (lineBytes * linesPerChunk > Array.MaxLength / 2)
        {
            throw new ArgumentException($"A {width}-pixel-wide image is too wide to write as OpenEXR.", nameof(image));
        }

        image.Metadata.TryGetFormatMetadata(out ExrMetadata? metadata);
        int chunkCount = (int)(((long)height + linesPerChunk - 1) / linesPerChunk);
        byte[] header = this.BuildHeader(width, height, channels, metadata);

        using var body = new MemoryStream();
        var offsets = new long[chunkCount];
        long tableEnd = header.Length + (8L * chunkCount);
        int chunkCapacity = (int)(lineBytes * linesPerChunk);
        byte[] raw = ArrayPool<byte>.Shared.Rent(Math.Max(chunkCapacity, 1));
        byte[] scratch = ArrayPool<byte>.Shared.Rent(Math.Max(chunkCapacity + (chunkCapacity / 127) + 16, 1));
        Vector4[] linear = ArrayPool<Vector4>.Shared.Rent(Math.Max(width, 1));
        ExrPiz? piz = this.Compression == ExrCompression.Piz ? new ExrPiz() : null;
        try
        {
            Span<byte> chunkHeader = stackalloc byte[8];
            for (int chunk = 0; chunk < chunkCount; chunk++)
            {
                OperationExecution.Checkpoint();
                int y0 = chunk * linesPerChunk;
                int y1 = Math.Min(y0 + linesPerChunk, height) - 1;
                int size = (int)(lineBytes * (y1 - y0 + 1));
                Span<byte> chunkRaw = raw.AsSpan(0, size);
                int write = 0;
                for (int y = y0; y <= y1; y++)
                {
                    SceneLinearPixels.ToLinear<TPixel>(frame.GetRowSpan(y), linear.AsSpan(0, width));
                    foreach (string name in names)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            float value = Component(linear[x], name);
                            if (sampleBytes == 2)
                            {
                                BinaryPrimitives.WriteUInt16LittleEndian(chunkRaw[write..], BitConverter.HalfToUInt16Bits((Half)value));
                            }
                            else
                            {
                                BinaryPrimitives.WriteSingleLittleEndian(chunkRaw[write..], value);
                            }

                            write += sampleBytes;
                        }
                    }
                }

                ReadOnlySpan<byte> payload = this.CompressChunk(chunkRaw, scratch, channels, width, y0, y1, piz);
                offsets[chunk] = tableEnd + body.Position;
                BinaryPrimitives.WriteInt32LittleEndian(chunkHeader, y0);
                BinaryPrimitives.WriteInt32LittleEndian(chunkHeader[4..], payload.Length);
                body.Write(chunkHeader);
                body.Write(payload);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
            ArrayPool<byte>.Shared.Return(scratch);
            ArrayPool<Vector4>.Shared.Return(linear);
        }

        stream.Write(header);
        var table = new byte[8 * chunkCount];
        for (int i = 0; i < chunkCount; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(i * 8), (ulong)offsets[i]);
        }

        stream.Write(table);
        body.Position = 0;
        body.CopyTo(stream);
    }

    private static ExrChannelLayout DefaultLayout<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
    {
        bool hasAlpha = TPixel.FromRgba32(new Rgba32(0, 0, 0, 0)).ToRgba32().A != byte.MaxValue;
        bool gray = typeof(TPixel) == typeof(L8) || typeof(TPixel) == typeof(L16)
            || typeof(TPixel) == typeof(La16) || typeof(TPixel) == typeof(La32);
        return gray
            ? hasAlpha ? ExrChannelLayout.LuminanceAlpha : ExrChannelLayout.Luminance
            : hasAlpha ? ExrChannelLayout.Rgba : ExrChannelLayout.Rgb;
    }

    private static float Component(Vector4 v, string name) => name switch
    {
        "R" => v.X,
        "G" => v.Y,
        "B" => v.Z,
        "A" => v.W,

        // Gray pixels come back with three equal components; keep them exact rather than re-weighting.
        _ => v.X == v.Y && v.Y == v.Z ? v.X : (0.2126f * v.X) + (0.7152f * v.Y) + (0.0722f * v.Z),
    };

    private ReadOnlySpan<byte> CompressChunk(ReadOnlySpan<byte> raw, byte[] scratch, ExrChannel[] channels, int width, int y0, int y1, ExrPiz? piz)
    {
        byte[] packed;
        switch (this.Compression)
        {
            case ExrCompression.None:
                return raw;
            case ExrCompression.Rle:
            {
                byte[] predicted = ArrayPool<byte>.Shared.Rent(raw.Length);
                try
                {
                    ExrCodecs.ApplyInterleaveAndPredictor(raw, predicted);
                    int length = ExrCodecs.RleEncode(predicted.AsSpan(0, raw.Length), scratch);
                    return length < raw.Length ? scratch.AsSpan(0, length) : raw;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(predicted);
                }
            }

            case ExrCompression.Piz:
                packed = piz!.Compress(raw, channels, 0, width - 1, y0, y1);
                break;
            default:
            {
                Span<byte> predicted = scratch.AsSpan(0, raw.Length);
                ExrCodecs.ApplyInterleaveAndPredictor(raw, predicted);
                packed = ExrCodecs.Deflate(predicted, CompressionLevel.Optimal);
                break;
            }
        }

        return packed.Length < raw.Length ? packed : raw;
    }

    private byte[] BuildHeader(int width, int height, ExrChannel[] channels, ExrMetadata? metadata)
    {
        var attributes = new List<(string Name, string Type, byte[] Value)>();

        using (var list = new MemoryStream())
        {
            Span<byte> entry = stackalloc byte[16];
            foreach (ExrChannel channel in channels)
            {
                list.Write(Encoding.ASCII.GetBytes(channel.Name));
                list.WriteByte(0);
                entry.Clear();
                BinaryPrimitives.WriteInt32LittleEndian(entry, channel.PixelType);
                BinaryPrimitives.WriteInt32LittleEndian(entry[8..], 1);
                BinaryPrimitives.WriteInt32LittleEndian(entry[12..], 1);
                list.Write(entry);
            }

            list.WriteByte(0);
            attributes.Add(("channels", "chlist", list.ToArray()));
        }

        attributes.Add(("compression", "compression", new[] { (byte)this.Compression }));
        byte[] window = Box(0, 0, width - 1, height - 1);
        attributes.Add(("dataWindow", "box2i", window));
        attributes.Add(("displayWindow", "box2i", window));
        attributes.Add(("lineOrder", "lineOrder", new byte[] { 0 }));
        float aspect = metadata is { PixelAspectRatio: > 0f } && float.IsFinite(metadata.PixelAspectRatio) ? metadata.PixelAspectRatio : 1f;
        attributes.Add(("pixelAspectRatio", "float", Floats(aspect)));
        attributes.Add(("screenWindowCenter", "v2f", Floats(0f, 0f)));
        attributes.Add(("screenWindowWidth", "float", Floats(1f)));
        if (metadata?.Chromaticities is { } chroma)
        {
            attributes.Add(("chromaticities", "chromaticities", Floats(
                chroma.Red.X, chroma.Red.Y, chroma.Green.X, chroma.Green.Y, chroma.Blue.X, chroma.Blue.Y, chroma.White.X, chroma.White.Y)));
        }

        if (metadata is not null)
        {
            foreach (KeyValuePair<string, string> pair in metadata.StringAttributes)
            {
                if (pair.Key.Length > 0 && !ReservedNames.Contains(pair.Key) && !pair.Key.Contains('\0'))
                {
                    attributes.Add((pair.Key, "string", Encoding.UTF8.GetBytes(pair.Value)));
                }
            }
        }

        attributes.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        bool longNames = attributes.Any(a => Encoding.UTF8.GetByteCount(a.Name) > 31);
        using var header = new MemoryStream();
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(word, 20000630);
        header.Write(word);
        BinaryPrimitives.WriteInt32LittleEndian(word, 2 | (longNames ? ExrHeader.FlagLongNames : 0));
        header.Write(word);
        foreach ((string name, string type, byte[] value) in attributes)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            if (nameBytes.Length > 255)
            {
                continue;
            }

            header.Write(nameBytes);
            header.WriteByte(0);
            header.Write(Encoding.ASCII.GetBytes(type));
            header.WriteByte(0);
            BinaryPrimitives.WriteInt32LittleEndian(word, value.Length);
            header.Write(word);
            header.Write(value);
        }

        header.WriteByte(0);
        return header.ToArray();
    }

    private static byte[] Box(int minX, int minY, int maxX, int maxY)
    {
        var value = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(value, minX);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4), minY);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(8), maxX);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(12), maxY);
        return value;
    }

    private static byte[] Floats(params float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }
}

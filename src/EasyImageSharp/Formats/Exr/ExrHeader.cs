using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EasyImageSharp.Metadata;

namespace EasyImageSharp.Formats.Exr;

/// <summary>One entry of an OpenEXR channel list.</summary>
/// <param name="Name">The channel name (bytes decoded as Latin-1, so ordinal order is byte order).</param>
/// <param name="PixelType">0 = UINT, 1 = HALF, 2 = FLOAT.</param>
/// <param name="Linear">The <c>pLinear</c> flag.</param>
/// <param name="XSampling">Horizontal subsampling.</param>
/// <param name="YSampling">Vertical subsampling.</param>
internal readonly record struct ExrChannel(string Name, int PixelType, bool Linear, int XSampling, int YSampling)
{
    public const int UInt = 0;
    public const int Half = 1;
    public const int Float = 2;

    /// <summary>Bytes per stored sample.</summary>
    public int BytesPerSample => this.PixelType == Half ? 2 : 4;
}

/// <summary>
/// The parsed header of a single-part OpenEXR file: magic number, version flags and the attributes the decoder
/// needs, plus the offset of the chunk offset table that follows it.
/// </summary>
internal sealed class ExrHeader
{
    public const int FlagTiled = 0x200;
    public const int FlagLongNames = 0x400;
    public const int FlagNonImage = 0x800;
    public const int FlagMultipart = 0x1000;

    private ExrHeader()
    {
    }

    /// <summary>The channels sorted by name, which is the order their samples are stored in.</summary>
    public ExrChannel[] Channels { get; private set; } = Array.Empty<ExrChannel>();

    /// <summary>The raw compression code (0-255).</summary>
    public int Compression { get; private set; }

    public int DataMinX { get; private set; }

    public int DataMinY { get; private set; }

    public int DataMaxX { get; private set; }

    public int DataMaxY { get; private set; }

    public int DisplayMinX { get; private set; }

    public int DisplayMinY { get; private set; }

    public int DisplayMaxX { get; private set; }

    public int DisplayMaxY { get; private set; }

    public int LineOrder { get; private set; }

    public float PixelAspectRatio { get; private set; } = 1f;

    public bool Tiled { get; private set; }

    public int TileWidth { get; private set; }

    public int TileHeight { get; private set; }

    public int LevelMode { get; private set; }

    public int RoundingMode { get; private set; }

    public ExrChromaticities? Chromaticities { get; private set; }

    public Dictionary<string, string> Strings { get; } = new(StringComparer.Ordinal);

    /// <summary>Offset of the first byte after the header, where the chunk offset table starts.</summary>
    public int HeaderEnd { get; private set; }

    public int DataWidth => this.DataMaxX - this.DataMinX + 1;

    public int DataHeight => this.DataMaxY - this.DataMinY + 1;

    public int DisplayWidth => this.DisplayMaxX - this.DisplayMinX + 1;

    public int DisplayHeight => this.DisplayMaxY - this.DisplayMinY + 1;

    /// <summary>Scanlines per chunk for a scanline file, by compression method.</summary>
    public int LinesPerChunk => this.Compression switch
    {
        3 or 5 => 16,
        4 or 6 or 7 or 8 => 32,
        9 => 256,
        _ => 1,
    };

    /// <summary>The display name of a compression code, for messages.</summary>
    public static string CompressionName(int code) => code switch
    {
        0 => "NONE",
        1 => "RLE",
        2 => "ZIPS",
        3 => "ZIP",
        4 => "PIZ",
        5 => "PXR24",
        6 => "B44",
        7 => "B44A",
        8 => "DWAA",
        9 => "DWAB",
        10 => "HTJ2K256",
        11 => "HTJ2K32",
        12 => "LJ2K",
        13 => "ZSTD",
        _ => $"unknown ({code})",
    };

    public static ExrHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 20000630)
        {
            throw new InvalidImageContentException("Missing the OpenEXR magic number.");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if ((version & 0xFF) != 2)
        {
            throw new NotSupportedException($"OpenEXR file format version {version & 0xFF} is not supported; only version 2 is.");
        }

        int flags = version & ~0xFF;
        if ((flags & ~(FlagTiled | FlagLongNames | FlagNonImage | FlagMultipart)) != 0)
        {
            throw new NotSupportedException($"OpenEXR version flags 0x{flags:X} include features this decoder does not know.");
        }

        if ((flags & FlagMultipart) != 0)
        {
            throw new NotSupportedException("Multi-part OpenEXR files are not supported; only single-part files are.");
        }

        if ((flags & FlagNonImage) != 0)
        {
            throw new NotSupportedException("OpenEXR deep data is not supported.");
        }

        var header = new ExrHeader { Tiled = (flags & FlagTiled) != 0 };
        int maxName = (flags & FlagLongNames) != 0 ? 255 : 31;
        int pos = 8;
        bool haveChannels = false, haveCompression = false, haveDataWindow = false, haveDisplayWindow = false, haveTiles = false;
        while (true)
        {
            if (pos >= data.Length)
            {
                throw new InvalidImageContentException("OpenEXR header is truncated.");
            }

            if (data[pos] == 0)
            {
                pos++;
                break;
            }

            string name = ReadName(data, ref pos, maxName, "attribute name");
            string type = ReadName(data, ref pos, maxName, "attribute type");
            if (pos + 4 > data.Length)
            {
                throw new InvalidImageContentException("OpenEXR header is truncated.");
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
            pos += 4;
            if (size < 0 || size > data.Length - pos)
            {
                throw new InvalidImageContentException($"OpenEXR attribute '{name}' declares {size} bytes, more than the file holds.");
            }

            ReadOnlySpan<byte> value = data.Slice(pos, size);
            pos += size;
            switch (name)
            {
                case "channels":
                    Expect(type, "chlist", name);
                    header.Channels = ParseChannels(value);
                    haveChannels = true;
                    break;
                case "compression":
                    Expect(type, "compression", name, value, 1);
                    header.Compression = value[0];
                    haveCompression = true;
                    break;
                case "dataWindow":
                    Expect(type, "box2i", name, value, 16);
                    (header.DataMinX, header.DataMinY, header.DataMaxX, header.DataMaxY) = ParseBox(value, name);
                    haveDataWindow = true;
                    break;
                case "displayWindow":
                    Expect(type, "box2i", name, value, 16);
                    (header.DisplayMinX, header.DisplayMinY, header.DisplayMaxX, header.DisplayMaxY) = ParseBox(value, name);
                    haveDisplayWindow = true;
                    break;
                case "lineOrder":
                    Expect(type, "lineOrder", name, value, 1);
                    header.LineOrder = value[0] <= 2 ? value[0] : throw new InvalidImageContentException($"OpenEXR line order {value[0]} is invalid.");
                    break;
                case "pixelAspectRatio":
                    Expect(type, "float", name, value, 4);
                    header.PixelAspectRatio = BinaryPrimitives.ReadSingleLittleEndian(value);
                    break;
                case "tiles":
                    Expect(type, "tiledesc", name, value, 9);
                    header.ParseTiles(value);
                    haveTiles = true;
                    break;
                case "chromaticities" when type == "chromaticities" && size == 32:
                    header.Chromaticities = new ExrChromaticities(
                        ReadVector2(value), ReadVector2(value[8..]), ReadVector2(value[16..]), ReadVector2(value[24..]));
                    break;
                case "type" when type == "string":
                    string partType = Encoding.UTF8.GetString(value);
                    if (partType is "deepscanline" or "deeptile")
                    {
                        throw new NotSupportedException("OpenEXR deep data is not supported.");
                    }

                    break;
            }

            if (type == "string" && name != "type")
            {
                header.Strings[name] = Encoding.UTF8.GetString(value);
            }
        }

        if (!haveChannels || !haveCompression || !haveDataWindow)
        {
            throw new InvalidImageContentException("OpenEXR header lacks a required attribute (channels, compression or dataWindow).");
        }

        if (header.Tiled && !haveTiles)
        {
            throw new InvalidImageContentException("Tiled OpenEXR header lacks the 'tiles' attribute.");
        }

        if (!haveDisplayWindow)
        {
            (header.DisplayMinX, header.DisplayMinY, header.DisplayMaxX, header.DisplayMaxY) =
                (header.DataMinX, header.DataMinY, header.DataMaxX, header.DataMaxY);
        }

        if (header.Compression > 13)
        {
            throw new InvalidImageContentException($"OpenEXR compression code {header.Compression} is not a known method.");
        }

        header.ValidateSampling();
        header.HeaderEnd = pos;
        return header;
    }

    /// <summary>Builds the public metadata view of this header.</summary>
    public ExrMetadata CreateMetadata()
    {
        var metadata = new ExrMetadata
        {
            Compression = (ExrCompression)this.Compression,
            LineOrder = (ExrLineOrder)this.LineOrder,
            DataWindow = new Rectangle(this.DataMinX, this.DataMinY, this.DataWidth, this.DataHeight),
            DisplayWindow = new Rectangle(this.DisplayMinX, this.DisplayMinY, this.DisplayWidth, this.DisplayHeight),
            PixelAspectRatio = this.PixelAspectRatio,
            IsTiled = this.Tiled,
            TileSize = this.Tiled ? new Size(this.TileWidth, this.TileHeight) : default,
            LevelMode = (ExrLevelMode)this.LevelMode,
            Chromaticities = this.Chromaticities,
        };

        foreach (ExrChannel channel in this.Channels)
        {
            metadata.Channels.Add(new ExrChannelInfo(channel.Name, (ExrPixelType)channel.PixelType, channel.XSampling, channel.YSampling, channel.Linear));
        }

        foreach (KeyValuePair<string, string> pair in this.Strings)
        {
            metadata.StringAttributes[pair.Key] = pair.Value;
        }

        return metadata;
    }

    /// <summary>Floor division for a positive divisor, as OpenEXR's <c>divp</c>.</summary>
    public static int FloorDiv(int x, int y) => x >= 0 ? x / y : -((y - 1 - x) / y);

    /// <summary>Non-negative remainder for a positive divisor, as OpenEXR's <c>modp</c>.</summary>
    public static int FloorMod(int x, int y) => x - (y * FloorDiv(x, y));

    /// <summary>The number of multiples of <paramref name="sampling"/> in [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public static int SampleCount(int sampling, int min, int max)
        => FloorDiv(max, sampling) - FloorDiv(min - 1, sampling);

    private static void Expect(string type, string expected, string name)
    {
        if (type != expected)
        {
            throw new InvalidImageContentException($"OpenEXR attribute '{name}' has type '{type}', expected '{expected}'.");
        }
    }

    private static void Expect(string type, string expected, string name, ReadOnlySpan<byte> value, int size)
    {
        Expect(type, expected, name);
        if (value.Length != size)
        {
            throw new InvalidImageContentException($"OpenEXR attribute '{name}' has {value.Length} bytes, expected {size}.");
        }
    }

    private static string ReadName(ReadOnlySpan<byte> data, ref int pos, int maxLength, string what)
    {
        ReadOnlySpan<byte> rest = data[pos..];
        int end = rest[..Math.Min(rest.Length, maxLength + 1)].IndexOf((byte)0);
        if (end <= 0)
        {
            throw new InvalidImageContentException(end == 0
                ? $"OpenEXR {what} is empty."
                : $"OpenEXR {what} is unterminated or longer than {maxLength} bytes.");
        }

        pos += end + 1;
        return Encoding.Latin1.GetString(rest[..end]);
    }

    private static ExrChannel[] ParseChannels(ReadOnlySpan<byte> value)
    {
        var channels = new List<ExrChannel>();
        int pos = 0;
        while (true)
        {
            if (pos >= value.Length)
            {
                throw new InvalidImageContentException("OpenEXR channel list is not terminated.");
            }

            if (value[pos] == 0)
            {
                break;
            }

            string name = ReadName(value, ref pos, 255, "channel name");
            if (pos + 16 > value.Length)
            {
                throw new InvalidImageContentException("OpenEXR channel list is truncated.");
            }

            int pixelType = BinaryPrimitives.ReadInt32LittleEndian(value[pos..]);
            bool linear = value[pos + 4] != 0;
            int xSampling = BinaryPrimitives.ReadInt32LittleEndian(value[(pos + 8)..]);
            int ySampling = BinaryPrimitives.ReadInt32LittleEndian(value[(pos + 12)..]);
            pos += 16;
            if (pixelType is < 0 or > 2)
            {
                throw new InvalidImageContentException($"OpenEXR channel '{name}' has the invalid pixel type {pixelType}.");
            }

            if (xSampling < 1 || ySampling < 1)
            {
                throw new InvalidImageContentException($"OpenEXR channel '{name}' has an invalid sampling of {xSampling}x{ySampling}.");
            }

            channels.Add(new ExrChannel(name, pixelType, linear, xSampling, ySampling));
        }

        if (channels.Count == 0)
        {
            throw new InvalidImageContentException("OpenEXR channel list is empty.");
        }

        // Samples are stored in name order whatever order the list was written in, exactly as the reference
        // library's map-based channel list orders them.
        ExrChannel[] sorted = channels.ToArray();
        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (int i = 1; i < sorted.Length; i++)
        {
            if (sorted[i].Name == sorted[i - 1].Name)
            {
                throw new InvalidImageContentException($"OpenEXR channel '{sorted[i].Name}' is declared twice.");
            }
        }

        return sorted;
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) ParseBox(ReadOnlySpan<byte> value, string name)
    {
        int minX = BinaryPrimitives.ReadInt32LittleEndian(value);
        int minY = BinaryPrimitives.ReadInt32LittleEndian(value[4..]);
        int maxX = BinaryPrimitives.ReadInt32LittleEndian(value[8..]);
        int maxY = BinaryPrimitives.ReadInt32LittleEndian(value[12..]);

        // Coordinates are bounded the way the reference library bounds them, so widths, tile origins and the
        // sample arithmetic below cannot overflow 32 bits.
        const int Limit = int.MaxValue / 2;
        if (minX > maxX || minY > maxY || minX < -Limit || minY < -Limit || maxX > Limit || maxY > Limit
            || (long)maxX - minX + 1 > Limit || (long)maxY - minY + 1 > Limit)
        {
            throw new InvalidImageContentException($"OpenEXR {name} ({minX}, {minY}) - ({maxX}, {maxY}) is invalid.");
        }

        return (minX, minY, maxX, maxY);
    }

    private static Vector2 ReadVector2(ReadOnlySpan<byte> value)
        => new(BinaryPrimitives.ReadSingleLittleEndian(value), BinaryPrimitives.ReadSingleLittleEndian(value[4..]));

    private void ParseTiles(ReadOnlySpan<byte> value)
    {
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(value);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(value[4..]);
        int mode = value[8];
        if (width == 0 || height == 0 || width > int.MaxValue / 2 || height > int.MaxValue / 2)
        {
            throw new InvalidImageContentException($"OpenEXR tile size {width}x{height} is invalid.");
        }

        if ((mode & 0x0F) > 2 || (mode >> 4) > 1)
        {
            throw new InvalidImageContentException($"OpenEXR tile mode 0x{mode:X2} is invalid.");
        }

        this.TileWidth = (int)width;
        this.TileHeight = (int)height;
        this.LevelMode = mode & 0x0F;
        this.RoundingMode = mode >> 4;
    }

    /// <summary>
    /// Subsampled channels must tile the data window exactly, as the reference library requires; tiled files
    /// cannot subsample at all.
    /// </summary>
    private void ValidateSampling()
    {
        foreach (ExrChannel channel in this.Channels)
        {
            if (channel.XSampling == 1 && channel.YSampling == 1)
            {
                continue;
            }

            if (this.Tiled)
            {
                throw new InvalidImageContentException($"OpenEXR tiled file has the subsampled channel '{channel.Name}'.");
            }

            if (FloorMod(this.DataMinX, channel.XSampling) != 0 || FloorMod(this.DataWidth, channel.XSampling) != 0
                || FloorMod(this.DataMinY, channel.YSampling) != 0 || FloorMod(this.DataHeight, channel.YSampling) != 0)
            {
                throw new InvalidImageContentException($"OpenEXR channel '{channel.Name}' sampling does not divide the data window.");
            }
        }
    }
}

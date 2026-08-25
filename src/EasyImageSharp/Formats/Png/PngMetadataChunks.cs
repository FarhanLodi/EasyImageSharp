using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;

namespace EasyImageSharp.Formats.Png;

/// <summary>
/// Reads and writes the PNG ancillary chunks that carry metadata: pHYs, gAMA, iCCP, eXIf, tEXt, zTXt and iTXt
/// (including the "XML:com.adobe.xmp" iTXt packet). Reading is lenient: a malformed chunk is ignored; only
/// the size caps raise <see cref="InvalidImageContentException"/>.
/// </summary>
internal static class PngMetadataChunks
{
    private const uint PhysType = 0x70485973; // pHYs
    private const uint GammaType = 0x67414D41; // gAMA
    private const uint IccpType = 0x69434350; // iCCP
    private const uint ExifType = 0x65584966; // eXIf
    private const uint TextType = 0x74455874; // tEXt
    private const uint ZTextType = 0x7A545874; // zTXt
    private const uint ITextType = 0x69545874; // iTXt

    private const string XmpKeyword = "XML:com.adobe.xmp";
    private const long MaxTextBytes = 64L * 1024 * 1024;

    // ----- Reading -----

    /// <summary>Interprets a chunk if it is one of the metadata chunks; returns false for anything else.</summary>
    public static bool TryReadChunk(uint type, ReadOnlySpan<byte> chunk, ImageMetadata metadata, PngMetadata png)
    {
        switch (type)
        {
            case PhysType:
                ReadPhys(chunk, metadata);
                return true;
            case GammaType:
                if (chunk.Length >= 4)
                {
                    uint gamma = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                    if (gamma > 0)
                    {
                        png.Gamma = gamma / 100000f;
                    }
                }

                return true;
            case IccpType:
                ReadIccp(chunk, metadata);
                return true;
            case ExifType:
                if (chunk.Length > ExifReader.MaxExifBytes)
                {
                    throw new InvalidImageContentException($"PNG eXIf chunk of {chunk.Length:N0} bytes exceeds the {ExifReader.MaxExifBytes:N0} byte limit.");
                }

                metadata.ExifProfile ??= ExifProfile.TryParse(chunk);
                return true;
            case TextType:
                ReadText(chunk, png);
                return true;
            case ZTextType:
                ReadCompressedText(chunk, png);
                return true;
            case ITextType:
                ReadInternationalText(chunk, metadata, png);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Applies the EXIF resolution (if any) after all chunks have been read; EXIF wins over pHYs.</summary>
    public static void Finish(ImageMetadata metadata)
    {
        if (metadata.ExifProfile is not null)
        {
            metadata.ApplyExifResolution(metadata.ExifProfile);
        }
    }

    private static void ReadPhys(ReadOnlySpan<byte> chunk, ImageMetadata metadata)
    {
        if (chunk.Length < 9)
        {
            return;
        }

        uint x = BinaryPrimitives.ReadUInt32BigEndian(chunk);
        uint y = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
        byte unit = chunk[8];
        if (x == 0 || y == 0)
        {
            return;
        }

        metadata.ResolutionUnits = unit == 1 ? PixelResolutionUnit.PixelsPerMeter : PixelResolutionUnit.AspectRatio;
        metadata.HorizontalResolution = x;
        metadata.VerticalResolution = y;
    }

    private static void ReadIccp(ReadOnlySpan<byte> chunk, ImageMetadata metadata)
    {
        // name\0 method(1) zlib(profile)
        int nul = chunk.IndexOf((byte)0);
        if (nul < 1 || nul > 79 || nul + 2 > chunk.Length || chunk[nul + 1] != 0)
        {
            return;
        }

        byte[]? profile = InflateCapped(chunk[(nul + 2)..], ExifReader.MaxIccBytes, "PNG iCCP profile");
        if (profile is not null && profile.Length > 0)
        {
            metadata.IccProfile ??= new IccProfile(profile);
        }
    }

    private static void ReadText(ReadOnlySpan<byte> chunk, PngMetadata png)
    {
        int nul = chunk.IndexOf((byte)0);
        if (nul < 1 || nul > 79)
        {
            return;
        }

        string keyword = Encoding.Latin1.GetString(chunk[..nul]);
        string value = Encoding.Latin1.GetString(chunk[(nul + 1)..]);
        png.TextData.Add(new PngTextData(keyword, value));
    }

    private static void ReadCompressedText(ReadOnlySpan<byte> chunk, PngMetadata png)
    {
        int nul = chunk.IndexOf((byte)0);
        if (nul < 1 || nul > 79 || nul + 2 > chunk.Length || chunk[nul + 1] != 0)
        {
            return;
        }

        byte[]? text = InflateCapped(chunk[(nul + 2)..], MaxTextBytes, "PNG zTXt text");
        if (text is null)
        {
            return;
        }

        png.TextData.Add(new PngTextData(Encoding.Latin1.GetString(chunk[..nul]), Encoding.Latin1.GetString(text)));
    }

    private static void ReadInternationalText(ReadOnlySpan<byte> chunk, ImageMetadata metadata, PngMetadata png)
    {
        // keyword\0 compressionFlag(1) compressionMethod(1) languageTag\0 translatedKeyword\0 text
        int nul = chunk.IndexOf((byte)0);
        if (nul < 1 || nul > 79 || nul + 3 > chunk.Length)
        {
            return;
        }

        string keyword = Encoding.Latin1.GetString(chunk[..nul]);
        byte compressed = chunk[nul + 1];
        byte method = chunk[nul + 2];
        ReadOnlySpan<byte> rest = chunk[(nul + 3)..];
        int langEnd = rest.IndexOf((byte)0);
        if (langEnd < 0)
        {
            return;
        }

        string language = Encoding.ASCII.GetString(rest[..langEnd]);
        rest = rest[(langEnd + 1)..];
        int translatedEnd = rest.IndexOf((byte)0);
        if (translatedEnd < 0)
        {
            return;
        }

        string translated = Encoding.UTF8.GetString(rest[..translatedEnd]);
        ReadOnlySpan<byte> textBytes = rest[(translatedEnd + 1)..];
        byte[]? inflated = null;
        if (compressed == 1)
        {
            if (method != 0)
            {
                return;
            }

            inflated = InflateCapped(textBytes, MaxTextBytes, "PNG iTXt text");
            if (inflated is null)
            {
                return;
            }

            textBytes = inflated;
        }
        else if (compressed != 0)
        {
            return;
        }

        if (keyword == XmpKeyword)
        {
            if (metadata.XmpProfile is null)
            {
                metadata.XmpProfile = new XmpProfile(inflated ?? textBytes.ToArray());
            }

            return;
        }

        png.TextData.Add(new PngTextData(keyword, Encoding.UTF8.GetString(textBytes), language, translated));
    }

    /// <summary>Inflates a zlib stream, returning null on corrupt data and throwing when the output exceeds <paramref name="maxBytes"/>.</summary>
    private static byte[]? InflateCapped(ReadOnlySpan<byte> compressed, long maxBytes, string what)
    {
        try
        {
            using var input = new MemoryStream(compressed.ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            long total = 0;
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new InvalidImageContentException($"{what} exceeds the {maxBytes:N0} byte limit.");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            return null;
        }
    }

    // ----- Writing -----

    /// <summary>Writes pHYs, gAMA, iCCP, eXIf, iTXt (XMP) and text chunks. Call right after IHDR (before PLTE/IDAT).</summary>
    public static void Write(Stream stream, ImageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(metadata);

        WritePhys(stream, metadata);
        metadata.TryGetFormatMetadata(out PngMetadata? png);

        if (png?.Gamma is float gamma && gamma > 0)
        {
            Span<byte> data = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(data, (uint)Math.Round(gamma * 100000.0));
            WriteChunk(stream, "gAMA"u8, data);
        }

        if (metadata.IccProfile is not null)
        {
            byte[] name = Encoding.Latin1.GetBytes(SanitizeKeyword(metadata.IccProfile.Header.Description) ?? "ICC Profile");
            byte[] compressed = Deflate(metadata.IccProfile.RawArray);
            var payload = new byte[name.Length + 2 + compressed.Length];
            name.CopyTo(payload, 0);
            payload[name.Length] = 0;
            payload[name.Length + 1] = 0; // Compression method: deflate.
            compressed.CopyTo(payload, name.Length + 2);
            WriteChunk(stream, "iCCP"u8, payload);
        }

        ExifProfile? exif = metadata.PrepareExifForWrite();
        if (exif is not null)
        {
            WriteChunk(stream, "eXIf"u8, exif.ToByteArray());
        }

        if (metadata.XmpProfile is not null)
        {
            WriteInternationalText(stream, XmpKeyword, string.Empty, string.Empty, metadata.XmpProfile.RawArray, compress: false);
        }

        if (png is not null)
        {
            foreach (PngTextData text in png.TextData)
            {
                if (string.IsNullOrEmpty(text.Keyword) || text.Keyword.Length > 79)
                {
                    continue;
                }

                bool needsInternational = !string.IsNullOrEmpty(text.LanguageTag)
                    || !string.IsNullOrEmpty(text.TranslatedKeyword)
                    || !IsLatin1(text.Value)
                    || !IsLatin1(text.Keyword);
                if (needsInternational)
                {
                    WriteInternationalText(
                        stream, text.Keyword, text.LanguageTag, text.TranslatedKeyword, Encoding.UTF8.GetBytes(text.Value), compress: text.Value.Length > 1024);
                }
                else
                {
                    byte[] keyword = Encoding.Latin1.GetBytes(text.Keyword);
                    byte[] value = Encoding.Latin1.GetBytes(text.Value);
                    if (value.Length > 1024)
                    {
                        byte[] compressed = Deflate(value);
                        var payload = new byte[keyword.Length + 2 + compressed.Length];
                        keyword.CopyTo(payload, 0);
                        payload[keyword.Length] = 0;
                        payload[keyword.Length + 1] = 0;
                        compressed.CopyTo(payload, keyword.Length + 2);
                        WriteChunk(stream, "zTXt"u8, payload);
                    }
                    else
                    {
                        var payload = new byte[keyword.Length + 1 + value.Length];
                        keyword.CopyTo(payload, 0);
                        payload[keyword.Length] = 0;
                        value.CopyTo(payload, keyword.Length + 1);
                        WriteChunk(stream, "tEXt"u8, payload);
                    }
                }
            }
        }
    }

    private static void WritePhys(Stream stream, ImageMetadata metadata)
    {
        Span<byte> data = stackalloc byte[9];
        if (metadata.ResolutionUnits == PixelResolutionUnit.AspectRatio)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data, ClampToUInt32(metadata.HorizontalResolution));
            BinaryPrimitives.WriteUInt32BigEndian(data[4..], ClampToUInt32(metadata.VerticalResolution));
            data[8] = 0;
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(data, ClampToUInt32(metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerMeter)));
            BinaryPrimitives.WriteUInt32BigEndian(data[4..], ClampToUInt32(metadata.GetVerticalResolution(PixelResolutionUnit.PixelsPerMeter)));
            data[8] = 1;
        }

        WriteChunk(stream, "pHYs"u8, data);
    }

    private static void WriteInternationalText(Stream stream, string keyword, string language, string translated, byte[] text, bool compress)
    {
        byte[] keywordBytes = Encoding.Latin1.GetBytes(keyword);
        byte[] languageBytes = Encoding.ASCII.GetBytes(language ?? string.Empty);
        byte[] translatedBytes = Encoding.UTF8.GetBytes(translated ?? string.Empty);
        byte[] body = compress ? Deflate(text) : text;

        using var payload = new MemoryStream(keywordBytes.Length + languageBytes.Length + translatedBytes.Length + body.Length + 5);
        payload.Write(keywordBytes);
        payload.WriteByte(0);
        payload.WriteByte((byte)(compress ? 1 : 0));
        payload.WriteByte(0);
        payload.Write(languageBytes);
        payload.WriteByte(0);
        payload.Write(translatedBytes);
        payload.WriteByte(0);
        payload.Write(body);
        WriteChunk(stream, "iTXt"u8, payload.ToArray());
    }

    private static uint ClampToUInt32(double value)
        => (uint)Math.Clamp(Math.Round(value), 1, uint.MaxValue);

    private static bool IsLatin1(string text)
    {
        foreach (char c in text)
        {
            if (c > 0xFF)
            {
                return false;
            }
        }

        return true;
    }

    private static string? SanitizeKeyword(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (char c in text)
        {
            if (c is >= (char)32 and <= (char)126 or >= (char)161 and <= (char)255)
            {
                builder.Append(c);
            }
        }

        string result = builder.ToString().Trim();
        if (result.Length == 0)
        {
            return null;
        }

        return result.Length > 79 ? result[..79] : result;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    internal static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, data.Length);
        stream.Write(lengthBytes);
        stream.Write(type);
        stream.Write(data);

        uint crc = Crc32.Append(Crc32.Append(0, type), data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }
}

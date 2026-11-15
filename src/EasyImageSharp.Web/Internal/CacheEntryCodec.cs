using System.Buffers.Binary;
using System.Text;

namespace EasyImageSharp.Web.Internal;

/// <summary>
/// The self-describing layout shared by the disk and distributed caches: <c>"EISC"</c>, a version byte, the creation
/// time (UTC ticks, little-endian <see cref="long"/>), the content type (a length byte and printable ASCII), the
/// content length (little-endian <see cref="long"/>), then the content. Keeping the metadata inside the one blob makes a
/// write a single atomic replace and lets a reader detect a truncated or foreign entry.
/// </summary>
internal static class CacheEntryCodec
{
    public const byte Version = 1;

    /// <summary>The largest possible header: magic, version, ticks, content-type length byte and 255 characters, content length.</summary>
    public const int MaxHeaderLength = 4 + 1 + 8 + 1 + 255 + 8;

    private static ReadOnlySpan<byte> Magic => "EISC"u8;

    public static int GetHeaderLength(string contentType) => 4 + 1 + 8 + 1 + contentType.Length + 8;

    public static byte[] Encode(string contentType, ReadOnlySpan<byte> content, DateTimeOffset created)
    {
        int headerLength = GetHeaderLength(contentType);
        byte[] blob = new byte[headerLength + content.Length];
        WriteHeader(blob, contentType, content.Length, created);
        content.CopyTo(blob.AsSpan(headerLength));
        return blob;
    }

    /// <summary>Writes the header into <paramref name="destination"/> and returns its length.</summary>
    public static int WriteHeader(Span<byte> destination, string contentType, long contentLength, DateTimeOffset created)
    {
        if (contentType.Length is 0 or > 255 || !contentType.All(static c => c is >= ' ' and <= '~'))
        {
            throw new ArgumentException("The content type must be 1 to 255 printable ASCII characters.", nameof(contentType));
        }

        Magic.CopyTo(destination);
        destination[4] = Version;
        BinaryPrimitives.WriteInt64LittleEndian(destination[5..], created.UtcTicks);
        destination[13] = (byte)contentType.Length;
        Encoding.ASCII.GetBytes(contentType, destination[14..]);
        int offset = 14 + contentType.Length;
        BinaryPrimitives.WriteInt64LittleEndian(destination[offset..], contentLength);
        return offset + 8;
    }

    /// <summary>Reads a header; false when the data is too short, foreign, from another version or malformed.</summary>
    public static bool TryReadHeader(
        ReadOnlySpan<byte> data,
        out string contentType,
        out long contentLength,
        out DateTimeOffset created,
        out int headerLength)
    {
        contentType = string.Empty;
        contentLength = 0;
        created = default;
        headerLength = 0;
        if (data.Length < 14 || !data[..4].SequenceEqual(Magic) || data[4] != Version)
        {
            return false;
        }

        long ticks = BinaryPrimitives.ReadInt64LittleEndian(data[5..]);
        int typeLength = data[13];
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks
            || typeLength == 0 || data.Length < 14 + typeLength + 8)
        {
            return false;
        }

        ReadOnlySpan<byte> type = data.Slice(14, typeLength);
        foreach (byte b in type)
        {
            if (b is < (byte)' ' or > (byte)'~')
            {
                return false;
            }
        }

        contentLength = BinaryPrimitives.ReadInt64LittleEndian(data[(14 + typeLength)..]);
        if (contentLength < 0)
        {
            return false;
        }

        contentType = Encoding.ASCII.GetString(type);
        created = new DateTimeOffset(ticks, TimeSpan.Zero);
        headerLength = 14 + typeLength + 8;
        return true;
    }
}

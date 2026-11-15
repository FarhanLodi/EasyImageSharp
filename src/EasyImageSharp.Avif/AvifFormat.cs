using System.Buffers.Binary;
using EasyImageSharp.Formats;

namespace EasyImageSharp.Avif;

/// <summary>
/// The opt-in AVIF format provider. Register it per request with
/// <c>new DecoderOptions { AdditionalFormats = [AvifFormat.Instance] }</c>; the core library's format registry
/// is never changed. Detection is managed code and works without the native library; decoding and encoding
/// need libavif 1.x at run time and throw <see cref="NotSupportedException"/> when it cannot be loaded.
/// </summary>
public static class AvifFormat
{
    private static readonly Lazy<ImageFormat> Format = new(() => ImageFormat.Create(
        "AVIF", "image/avif", ["avif"], IsAvif, static () => new AvifDecoder(), static () => new AvifEncoder()));

    /// <summary>The AVIF <see cref="ImageFormat"/>, with <see cref="AvifDecoder"/> and <see cref="AvifEncoder"/> defaults.</summary>
    public static ImageFormat Instance => Format.Value;

    /// <summary>True when libavif 1.x could be loaded in this process.</summary>
    public static bool IsNativeLibraryAvailable => NativeAvif.LoadFailure is null;

    /// <summary>The loaded libavif version, for example <c>1.4.2</c>.</summary>
    /// <exception cref="NotSupportedException">libavif cannot be loaded.</exception>
    public static string NativeVersion
    {
        get
        {
            NativeAvif.EnsureAvailable();
            return NativeAvif.VersionString();
        }
    }

    /// <summary>The AV1 codecs compiled into the loaded libavif, as libavif reports them (for example <c>aom [enc/dec]:v3.12.1</c>).</summary>
    /// <exception cref="NotSupportedException">libavif cannot be loaded.</exception>
    public static unsafe string NativeCodecVersions
    {
        get
        {
            NativeAvif.EnsureAvailable();
            byte* buffer = stackalloc byte[256];
            new Span<byte>(buffer, 256).Clear();
            NativeAvif.CodecVersions(buffer);
            return NativeAvif.Diagnostic(buffer);
        }
    }

    /// <summary>True when the loaded libavif includes an AV1 decoder (dav1d, libaom or libgav1).</summary>
    public static unsafe bool CanDecode => IsNativeLibraryAvailable && NativeAvif.CodecName(0, NativeAvif.CodecFlagCanDecode) != null;

    /// <summary>True when the loaded libavif includes an AV1 encoder (libaom, rav1e or SVT-AV1).</summary>
    public static unsafe bool CanEncode => IsNativeLibraryAvailable && NativeAvif.CodecName(0, NativeAvif.CodecFlagCanEncode) != null;

    /// <summary>
    /// True when <paramref name="data"/> starts with an ISO-BMFF 'ftyp' box whose major or compatible brands
    /// include 'avif' (still image) or 'avis' (image sequence). HEIF files with other brands are not claimed.
    /// </summary>
    public static bool IsAvif(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || !data.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return false;
        }

        ulong size = BinaryPrimitives.ReadUInt32BigEndian(data);
        int header = 8;
        if (size == 1)
        {
            if (data.Length < 24)
            {
                return false;
            }

            size = BinaryPrimitives.ReadUInt64BigEndian(data[8..]);
            header = 16;
        }
        else if (size == 0)
        {
            // "Extends to the end of the file": scan whatever brands the supplied prefix holds.
            size = (ulong)data.Length - ((ulong)data.Length % 4);
        }

        // A FileTypeBox holds the major brand, the minor version and whole four-byte compatible brands.
        if (size < (ulong)header + 8 || (size - (ulong)header) % 4 != 0)
        {
            return false;
        }

        int end = (int)Math.Min(size, (ulong)data.Length);
        ReadOnlySpan<byte> major = data.Slice(header, 4);
        if (IsAvifBrand(major))
        {
            return true;
        }

        for (int offset = header + 8; offset + 4 <= end; offset += 4)
        {
            if (IsAvifBrand(data.Slice(offset, 4)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAvifBrand(ReadOnlySpan<byte> brand) => brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8);

    /// <summary>Exposes the struct sizes the bindings were compiled against (used by tests).</summary>
    internal static unsafe (int Image, int Decoder, int Encoder, int Rgb) NativeStructSizes
        => (sizeof(AvifImage), sizeof(AvifDecoderNative), sizeof(AvifEncoderNative), sizeof(AvifRgbImage));
}

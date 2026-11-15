using EasyImageSharp.Formats;

namespace EasyImageSharp.JpegXl;

/// <summary>
/// The opt-in JPEG XL format provider. Register it per request with
/// <c>new DecoderOptions { AdditionalFormats = [JpegXlFormat.Instance] }</c>; the core library's format registry
/// is never changed. Detection is managed code and works without the native library; decoding and encoding
/// need libjxl 0.9 or newer at run time and throw <see cref="NotSupportedException"/> when it cannot be loaded.
/// </summary>
public static class JpegXlFormat
{
    /// <summary>The ISO-BMFF signature box that starts a JPEG XL container file.</summary>
    private static ReadOnlySpan<byte> ContainerSignature => [0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    private static readonly Lazy<ImageFormat> Format = new(() => ImageFormat.Create(
        "JPEGXL", "image/jxl", ["jxl"], IsJpegXl, static () => new JpegXlDecoder(), static () => new JpegXlEncoder()));

    /// <summary>The JPEG XL <see cref="ImageFormat"/>, with <see cref="JpegXlDecoder"/> and <see cref="JpegXlEncoder"/> defaults.</summary>
    public static ImageFormat Instance => Format.Value;

    /// <summary>True when libjxl 0.9 or newer could be loaded in this process.</summary>
    public static bool IsNativeLibraryAvailable => NativeJxl.LoadFailure is null;

    /// <summary>True when the optional libjxl_threads library was found, so libjxl can run multi-threaded.</summary>
    public static bool IsThreadRunnerAvailable => IsNativeLibraryAvailable && NativeJxl.Threads is not null;

    /// <summary>The loaded libjxl decoder version, for example <c>0.12.0</c>.</summary>
    /// <exception cref="NotSupportedException">libjxl cannot be loaded.</exception>
    public static string NativeVersion
    {
        get
        {
            NativeJxl.EnsureAvailable();
            return NativeJxl.FormatVersion(NativeJxl.DecoderVersion());
        }
    }

    /// <summary>
    /// True when <paramref name="data"/> starts with a bare JPEG XL codestream (<c>FF 0A</c>) or the JPEG XL
    /// container signature box.
    /// </summary>
    public static bool IsJpegXl(ReadOnlySpan<byte> data)
        => (data.Length >= 2 && data[0] == 0xFF && data[1] == 0x0A)
            || (data.Length >= ContainerSignature.Length && data[..ContainerSignature.Length].SequenceEqual(ContainerSignature));

    /// <summary>Exposes the struct sizes the bindings were compiled against (used by tests).</summary>
    internal static unsafe (int BasicInfo, int PixelFormat, int FrameHeader, int ExtraChannel, int ColorEncoding) NativeStructSizes
        => (sizeof(JxlBasicInfo), sizeof(JxlPixelFormat), sizeof(JxlFrameHeader), sizeof(JxlExtraChannelInfo), sizeof(JxlColorEncoding));
}

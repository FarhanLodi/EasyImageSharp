using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyImageSharp.Avif;

/// <summary>
/// Source-generated P/Invoke bindings for the libavif 1.x C API.
/// </summary>
/// <remarks>
/// <para>
/// Pinned against <c>include/avif/avif.h</c> of libavif v1.4.2 (tag <c>v1.4.2</c>, ABI SOVERSION 16). Since 1.0.0
/// libavif only appends members to <c>avifImage</c>, <c>avifDecoder</c> and <c>avifEncoder</c> after the
/// "Version 1.0.0 ends here" marker, and the library allocates those three structs itself; the mirrors below
/// therefore declare only the 1.0.0 prefix and are only ever accessed through library-owned pointers.
/// <c>avifRGBImage</c> is caller-allocated: its layout has been unchanged since 1.0.0 (which added
/// <c>maxThreads</c>), and <see cref="AvifRgbImage"/> carries trailing reserved space as a guard.
/// </para>
/// <para>
/// All libavif entry points use the C default calling convention, which is spelled out explicitly so the
/// bindings stay correct on 32-bit Windows where the platform default would be stdcall.
/// </para>
/// </remarks>
internal static unsafe partial class NativeAvif
{
    internal const string LibraryName = "avif";

    /// <summary>Environment variable holding an explicit path to the libavif shared library.</summary>
    internal const string PathVariable = "EASYIMAGESHARP_LIBAVIF_PATH";

    internal const int ResultOk = 0;
    internal const int ResultInvalidFtyp = 2;
    internal const int ResultNoContent = 3;
    internal const int ResultUnsupportedDepth = 6;
    internal const int ResultBmffParseFailed = 9;
    internal const int ResultNoCodecAvailable = 15;
    internal const int ResultNoImagesRemaining = 16;
    internal const int ResultInvalidExifPayload = 17;
    internal const int ResultTruncatedData = 20;
    internal const int ResultWaitingOnIo = 23;
    internal const int ResultInvalidArgument = 24;
    internal const int ResultNotImplemented = 25;
    internal const int ResultOutOfMemory = 26;

    internal const int PixelFormatNone = 0;
    internal const int PixelFormatYuv444 = 1;
    internal const int PixelFormatYuv422 = 2;
    internal const int PixelFormatYuv420 = 3;
    internal const int PixelFormatYuv400 = 4;

    internal const int RangeFull = 1;
    internal const int RgbFormatRgba = 1;
    internal const uint CodecFlagCanDecode = 1;
    internal const uint CodecFlagCanEncode = 2;
    internal const uint AddImageFlagSingle = 2;
    internal const uint TransformIrot = 1 << 2;
    internal const uint TransformImir = 1 << 3;
    internal const int RepetitionCountInfinite = -1;
    internal const int RepetitionCountUnknown = -2;

    /// <summary><c>AVIF_DEFAULT_IMAGE_SIZE_LIMIT</c>: libavif rejects larger values of <c>imageSizeLimit</c>.</summary>
    internal const uint DefaultImageSizeLimit = 16384u * 16384u;

    private static readonly Lazy<string?> LoadError = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    static NativeAvif()
    {
        EasyImageSharp.Codecs.NativeLibraryResolver.Register(Resolve);
    }

    /// <summary>Loads libavif and checks its ABI once; returns the failure reason, or <see langword="null"/>.</summary>
    internal static string? LoadFailure => LoadError.Value;

    /// <summary>Throws <see cref="NotSupportedException"/> when libavif cannot be loaded or is older than 1.0.</summary>
    internal static void EnsureAvailable()
    {
        if (LoadError.Value is { } reason)
        {
            throw new NotSupportedException(reason);
        }
    }

    /// <summary>Returns libavif's NUL-terminated diagnostic text from an <c>avifDiagnostics</c> buffer.</summary>
    internal static string Diagnostic(byte* buffer)
    {
        int length = 0;
        while (length < 256 && buffer[length] != 0)
        {
            length++;
        }

        return length == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(buffer, length);
    }

    /// <summary>Returns libavif's name for a result code.</summary>
    internal static string ResultName(int result)
        => Marshal.PtrToStringUTF8((IntPtr)ResultToString(result)) ?? result.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Returns the loaded libavif version string, for example <c>1.4.2</c>.</summary>
    internal static string VersionString() => Marshal.PtrToStringUTF8((IntPtr)Version()) ?? string.Empty;

    private static string? Probe()
    {
        // An explicit override is authoritative: if it cannot be loaded, report that rather than letting the
        // runtime's default probing silently pick up a different copy of the library.
        string? explicitPath = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath) && !NativeLibrary.TryLoad(explicitPath, out _))
        {
            return $"{PathVariable} is set to '{explicitPath}', but that file could not be loaded as libavif.";
        }

        string version;
        try
        {
            version = VersionString();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return "The libavif native library could not be loaded. Install libavif 1.x (libavif.so.16 / libavif.16.dylib), "
                + $"ship avif.dll next to the application, or set {PathVariable} to the library file. Details: {ex.Message}";
        }

        int major = 0;
        foreach (char c in version)
        {
            if (!char.IsAsciiDigit(c))
            {
                break;
            }

            major = (major * 10) + (c - '0');
        }

        return major >= 1 ? null : $"libavif {version} is too old; EasyImageSharp.Avif requires the stable 1.x ABI.";
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != LibraryName)
        {
            return IntPtr.Zero;
        }

        string? explicitPath = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return NativeLibrary.TryLoad(explicitPath, out IntPtr overridden) ? overridden : IntPtr.Zero;
        }

        string[] candidates = OperatingSystem.IsWindows() ? ["avif.dll", "libavif.dll", "libavif-16.dll"]
            : OperatingSystem.IsMacOS() ? ["libavif.16.dylib", "libavif.dylib", "/opt/homebrew/lib/libavif.16.dylib", "/usr/local/lib/libavif.16.dylib",
                "/opt/homebrew/opt/libavif/lib/libavif.16.dylib", "/usr/local/opt/libavif/lib/libavif.16.dylib"]
            : ["libavif.so.16", "libavif.so"];
        foreach (string candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out IntPtr handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    // ----- Version and codecs -----

    [LibraryImport(LibraryName, EntryPoint = "avifVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte* Version();

    [LibraryImport(LibraryName, EntryPoint = "avifCodecVersions")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CodecVersions(byte* outBuffer256);

    [LibraryImport(LibraryName, EntryPoint = "avifCodecName")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte* CodecName(int choice, uint requiredFlags);

    [LibraryImport(LibraryName, EntryPoint = "avifResultToString")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte* ResultToString(int result);

    // ----- Raw data -----

    [LibraryImport(LibraryName, EntryPoint = "avifRWDataFree")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RWDataFree(AvifRWData* raw);

    [LibraryImport(LibraryName, EntryPoint = "avifGetExifTiffHeaderOffset")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetExifTiffHeaderOffset(byte* exif, nuint exifSize, nuint* offset);

    // ----- avifImage -----

    [LibraryImport(LibraryName, EntryPoint = "avifImageCreate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial AvifImage* ImageCreate(uint width, uint height, uint depth, int yuvFormat);

    [LibraryImport(LibraryName, EntryPoint = "avifImageDestroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ImageDestroy(AvifImage* image);

    [LibraryImport(LibraryName, EntryPoint = "avifImageSetProfileICC")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ImageSetProfileIcc(AvifImage* image, byte* icc, nuint iccSize);

    [LibraryImport(LibraryName, EntryPoint = "avifImageSetMetadataExif")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ImageSetMetadataExif(AvifImage* image, byte* exif, nuint exifSize);

    [LibraryImport(LibraryName, EntryPoint = "avifImageSetMetadataXMP")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ImageSetMetadataXmp(AvifImage* image, byte* xmp, nuint xmpSize);

    [LibraryImport(LibraryName, EntryPoint = "avifRGBImageSetDefaults")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RgbImageSetDefaults(AvifRgbImage* rgb, AvifImage* image);

    [LibraryImport(LibraryName, EntryPoint = "avifImageYUVToRGB")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ImageYuvToRgb(AvifImage* image, AvifRgbImage* rgb);

    [LibraryImport(LibraryName, EntryPoint = "avifImageRGBToYUV")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ImageRgbToYuv(AvifImage* image, AvifRgbImage* rgb);

    // ----- avifDecoder -----

    [LibraryImport(LibraryName, EntryPoint = "avifDecoderCreate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial AvifDecoderNative* DecoderCreate();

    [LibraryImport(LibraryName, EntryPoint = "avifDecoderDestroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DecoderDestroy(AvifDecoderNative* decoder);

    [LibraryImport(LibraryName, EntryPoint = "avifDecoderSetIOMemory")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderSetIoMemory(AvifDecoderNative* decoder, byte* data, nuint size);

    [LibraryImport(LibraryName, EntryPoint = "avifDecoderParse")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderParse(AvifDecoderNative* decoder);

    [LibraryImport(LibraryName, EntryPoint = "avifDecoderNextImage")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderNextImage(AvifDecoderNative* decoder);

    // ----- avifEncoder -----

    [LibraryImport(LibraryName, EntryPoint = "avifEncoderCreate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial AvifEncoderNative* EncoderCreate();

    [LibraryImport(LibraryName, EntryPoint = "avifEncoderDestroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void EncoderDestroy(AvifEncoderNative* encoder);

    [LibraryImport(LibraryName, EntryPoint = "avifEncoderAddImage")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderAddImage(AvifEncoderNative* encoder, AvifImage* image, ulong durationInTimescales, uint addImageFlags);

    [LibraryImport(LibraryName, EntryPoint = "avifEncoderFinish")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderFinish(AvifEncoderNative* encoder, AvifRWData* output);
}

/// <summary><c>avifRWData</c>: a libavif-owned byte buffer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AvifRWData
{
    public byte* Data;
    public nuint Size;
}

/// <summary><c>avifImageTiming</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AvifImageTiming
{
    public ulong Timescale;
    public double Pts;
    public ulong PtsInTimescales;
    public double Duration;
    public ulong DurationInTimescales;
}

/// <summary>The libavif 1.0.0 prefix of <c>avifImage</c> (avif.h v1.4.2, lines 777-836). Library-allocated only.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AvifImage
{
    public uint Width;
    public uint Height;
    public uint Depth;
    public int YuvFormat;
    public int YuvRange;
    public int YuvChromaSamplePosition;
    public byte* YuvPlaneY;
    public byte* YuvPlaneU;
    public byte* YuvPlaneV;
    public fixed uint YuvRowBytes[3];
    public int ImageOwnsYuvPlanes;
    public byte* AlphaPlane;
    public uint AlphaRowBytes;
    public int ImageOwnsAlphaPlane;
    public int AlphaPremultiplied;
    public AvifRWData Icc;
    public ushort ColorPrimaries;
    public ushort TransferCharacteristics;
    public ushort MatrixCoefficients;
    public ushort MaxCll;
    public ushort MaxPall;
    public uint TransformFlags;
    public uint PaspHSpacing;
    public uint PaspVSpacing;
    public fixed uint Clap[8];
    public byte IrotAngle;
    public byte ImirAxis;
    public AvifRWData Exif;
    public AvifRWData Xmp;
}

/// <summary>The libavif 1.0.0 prefix of <c>avifDecoder</c> (avif.h v1.4.2, lines 1253-1369). Library-allocated only.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AvifDecoderNative
{
    public int CodecChoice;
    public int MaxThreads;
    public int RequestedSource;
    public int AllowProgressive;
    public int AllowIncremental;
    public int IgnoreExif;
    public int IgnoreXmp;
    public uint ImageSizeLimit;
    public uint ImageDimensionLimit;
    public uint ImageCountLimit;
    public uint StrictFlags;
    public AvifImage* Image;
    public int ImageIndex;
    public int ImageCount;
    public int ProgressiveState;
    public AvifImageTiming ImageTiming;
    public ulong Timescale;
    public double Duration;
    public ulong DurationInTimescales;
    public int RepetitionCount;
    public int AlphaPresent;
    public nuint ColorObuSize;
    public nuint AlphaObuSize;
    public fixed byte Diagnostics[256];
    public void* Io;
    public void* Data;
}

/// <summary>The libavif 1.0.0 prefix of <c>avifEncoder</c> (avif.h v1.4.2, lines 1511-1596). Library-allocated only.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AvifEncoderNative
{
    public int CodecChoice;
    public int MaxThreads;
    public int Speed;
    public int KeyframeInterval;
    public ulong Timescale;
    public int RepetitionCount;
    public uint ExtraLayerCount;
    public int Quality;
    public int QualityAlpha;
    public int MinQuantizer;
    public int MaxQuantizer;
    public int MinQuantizerAlpha;
    public int MaxQuantizerAlpha;
    public int TileRowsLog2;
    public int TileColsLog2;
    public int AutoTiling;
    public int ScalingHorizontalN;
    public int ScalingHorizontalD;
    public int ScalingVerticalN;
    public int ScalingVerticalD;
    public nuint ColorObuSize;
    public nuint AlphaObuSize;
    public fixed byte Diagnostics[256];
    public void* Data;
    public void* CodecSpecificOptions;
}

/// <summary>
/// <c>avifRGBImage</c> (avif.h v1.4.2, lines 996-1018). Caller-allocated: always initialise it with
/// <c>avifRGBImageSetDefaults</c>. The trailing reserved block keeps a future, larger layout from writing past
/// the end of this stack allocation.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AvifRgbImage
{
    public uint Width;
    public uint Height;
    public uint Depth;
    public int Format;
    public int ChromaUpsampling;
    public int ChromaDownsampling;
    public int AvoidLibYuv;
    public int IgnoreAlpha;
    public int AlphaPremultiplied;
    public int IsFloat;
    public int MaxThreads;
    public byte* Pixels;
    public uint RowBytes;
    private fixed byte reserved[64];
}

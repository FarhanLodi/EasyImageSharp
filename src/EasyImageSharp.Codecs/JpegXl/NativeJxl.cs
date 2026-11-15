using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyImageSharp.JpegXl;

/// <summary>
/// Source-generated P/Invoke bindings for the libjxl C API (decoder, encoder and the optional thread runner).
/// </summary>
/// <remarks>
/// <para>
/// Pinned against <c>lib/include/jxl/*.h</c> of libjxl v0.12.0 (tag <c>v0.12.0</c>). The structs mirrored here
/// (<c>JxlBasicInfo</c>, <c>JxlPixelFormat</c>, <c>JxlFrameHeader</c>, <c>JxlLayerInfo</c>, <c>JxlBlendInfo</c>,
/// <c>JxlExtraChannelInfo</c>, <c>JxlColorEncoding</c>) have kept their layout since libjxl 0.7; the binding
/// requires 0.9 or newer at run time because 0.9 changed the ICC query signatures it uses.
/// </para>
/// <para>
/// libjxl entry points use the C default calling convention, which is spelled out explicitly so the bindings
/// stay correct on 32-bit Windows where the platform default would be stdcall.
/// </para>
/// </remarks>
internal static unsafe partial class NativeJxl
{
    internal const string LibraryName = "jxl";

    /// <summary>Environment variable holding an explicit path to the libjxl shared library.</summary>
    internal const string PathVariable = "EASYIMAGESHARP_LIBJXL_PATH";

    /// <summary>Environment variable holding an explicit path to the optional libjxl_threads library.</summary>
    internal const string ThreadsPathVariable = "EASYIMAGESHARP_LIBJXL_THREADS_PATH";

    /// <summary>The oldest libjxl whose ABI matches these bindings: 0.9.0 encoded as major*1000000 + minor*1000 + patch.</summary>
    internal const uint MinimumVersion = 9000;

    internal const int DecSuccess = 0;
    internal const int DecError = 1;
    internal const int DecNeedMoreInput = 2;
    internal const int DecNeedPreviewOutBuffer = 3;
    internal const int DecNeedImageOutBuffer = 5;
    internal const int DecJpegNeedMoreOutput = 6;
    internal const int DecBoxNeedMoreOutput = 7;
    internal const int DecBasicInfo = 0x40;
    internal const int DecColorEncoding = 0x100;
    internal const int DecFrame = 0x400;
    internal const int DecFullImage = 0x1000;
    internal const int DecBox = 0x4000;

    internal const int SigNotEnoughBytes = 0;
    internal const int SigInvalid = 1;

    internal const int TypeFloat = 0;
    internal const int TypeUInt8 = 2;
    internal const int TypeUInt16 = 3;

    internal const int ColorProfileTargetData = 1;
    internal const int ColorSpaceRgb = 0;
    internal const int ColorSpaceGray = 1;
    internal const int WhitePointD65 = 1;
    internal const int PrimariesSrgb = 1;
    internal const int TransferFunctionSrgb = 13;

    internal const int ChannelBlack = 4;

    internal const int EncSuccess = 0;
    internal const int EncError = 1;
    internal const int EncNeedMoreOutput = 2;
    internal const int EncErrorOutOfMemory = 2;
    internal const int EncErrorNotSupported = 0x80;
    internal const int EncErrorApiUsage = 0x81;
    internal const int FrameSettingEffort = 0;

    private static readonly Lazy<string?> LoadError = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ThreadRunner?> Runner = new(LoadRunner, LazyThreadSafetyMode.ExecutionAndPublication);

    static NativeJxl()
    {
        EasyImageSharp.Codecs.NativeLibraryResolver.Register(Resolve);
    }

    /// <summary>Loads libjxl and checks its version once; returns the failure reason, or <see langword="null"/>.</summary>
    internal static string? LoadFailure => LoadError.Value;

    /// <summary>The optional libjxl_threads runner, or <see langword="null"/> when that library is not present.</summary>
    internal static ThreadRunner? Threads => Runner.Value;

    /// <summary>Throws <see cref="NotSupportedException"/> when libjxl cannot be loaded or is older than 0.9.</summary>
    internal static void EnsureAvailable()
    {
        if (LoadError.Value is { } reason)
        {
            throw new NotSupportedException(reason);
        }
    }

    /// <summary>Formats a libjxl version number (major*1000000 + minor*1000 + patch) as text.</summary>
    internal static string FormatVersion(uint version)
        => FormattableString.Invariant($"{version / 1000000}.{version / 1000 % 1000}.{version % 1000}");

    private static string? Probe()
    {
        string? explicitPath = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath) && !NativeLibrary.TryLoad(explicitPath, out _))
        {
            return $"{PathVariable} is set to '{explicitPath}', but that file could not be loaded as libjxl.";
        }

        uint decoder, encoder;
        try
        {
            decoder = DecoderVersion();
            encoder = EncoderVersion();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return "The libjxl native library could not be loaded. Install libjxl 0.9 or newer (libjxl.so.0.x / libjxl.dylib), "
                + $"ship jxl.dll next to the application, or set {PathVariable} to the library file. Details: {ex.Message}";
        }

        return decoder >= MinimumVersion && encoder >= MinimumVersion ? null
            : $"libjxl {FormatVersion(decoder)} is too old; EasyImageSharp.JpegXl requires libjxl 0.9 or newer.";
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

        return TryLoadAny(Candidates("jxl", "jpeg-xl"), assembly, searchPath);
    }

    /// <summary>Platform file names for a libjxl component, newest ABI first.</summary>
    private static string[] Candidates(string library, string formula)
    {
        if (OperatingSystem.IsWindows())
        {
            return [$"{library}.dll", $"lib{library}.dll"];
        }

        string[] minors = ["12", "11", "10", "9"];
        if (OperatingSystem.IsMacOS())
        {
            var mac = new List<string>();
            foreach (string minor in minors)
            {
                mac.Add($"lib{library}.0.{minor}.dylib");
            }

            mac.Add($"lib{library}.dylib");
            foreach (string prefix in new[] { "/opt/homebrew/lib", "/usr/local/lib", $"/opt/homebrew/opt/{formula}/lib", $"/usr/local/opt/{formula}/lib" })
            {
                mac.Add($"{prefix}/lib{library}.dylib");
            }

            return [.. mac];
        }

        var linux = new List<string>();
        foreach (string minor in minors)
        {
            linux.Add($"lib{library}.so.0.{minor}");
        }

        linux.Add($"lib{library}.so");
        return [.. linux];
    }

    private static IntPtr TryLoadAny(string[] candidates, Assembly assembly, DllImportSearchPath? searchPath)
    {
        foreach (string candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out IntPtr handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static ThreadRunner? LoadRunner()
    {
        IntPtr handle = IntPtr.Zero;
        string? explicitPath = Environment.GetEnvironmentVariable(ThreadsPathVariable);
        string? jxlPath = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            _ = NativeLibrary.TryLoad(explicitPath, out handle);
        }
        else if (!string.IsNullOrWhiteSpace(jxlPath) && Path.GetDirectoryName(jxlPath) is { Length: > 0 } directory)
        {
            // An explicit libjxl path implies its companion thread library lives beside it.
            foreach (string candidate in Candidates("jxl_threads", "jpeg-xl"))
            {
                if (!Path.IsPathRooted(candidate) && NativeLibrary.TryLoad(Path.Combine(directory, candidate), out handle))
                {
                    break;
                }
            }
        }
        else
        {
            handle = TryLoadAny(Candidates("jxl_threads", "jpeg-xl"), typeof(NativeJxl).Assembly, null);
        }

        if (handle == IntPtr.Zero
            || !NativeLibrary.TryGetExport(handle, "JxlThreadParallelRunner", out IntPtr run)
            || !NativeLibrary.TryGetExport(handle, "JxlThreadParallelRunnerCreate", out IntPtr create)
            || !NativeLibrary.TryGetExport(handle, "JxlThreadParallelRunnerDestroy", out IntPtr destroy))
        {
            return null;
        }

        return new ThreadRunner(run, create, destroy);
    }

    // ----- Decoder -----

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint DecoderVersion();

    [LibraryImport(LibraryName, EntryPoint = "JxlSignatureCheck")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int SignatureCheck(byte* buffer, nuint length);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderCreate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr DecoderCreate(void* memoryManager);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderDestroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DecoderDestroy(IntPtr decoder);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderSetParallelRunner")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderSetParallelRunner(IntPtr decoder, IntPtr runner, IntPtr runnerOpaque);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderSubscribeEvents")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderSubscribeEvents(IntPtr decoder, int eventsWanted);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderSetUnpremultiplyAlpha")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderSetUnpremultiplyAlpha(IntPtr decoder, int unpremultiply);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderSetDecompressBoxes")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderSetDecompressBoxes(IntPtr decoder, int decompress);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderSetInput")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderSetInput(IntPtr decoder, byte* data, nuint size);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderCloseInput")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DecoderCloseInput(IntPtr decoder);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderProcessInput")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderProcessInput(IntPtr decoder);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderGetBasicInfo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderGetBasicInfo(IntPtr decoder, JxlBasicInfo* info);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderGetExtraChannelInfo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderGetExtraChannelInfo(IntPtr decoder, nuint index, JxlExtraChannelInfo* info);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderGetColorAsEncodedProfile")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderGetColorAsEncodedProfile(IntPtr decoder, int target, JxlColorEncoding* encoding);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderGetICCProfileSize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderGetIccProfileSize(IntPtr decoder, int target, nuint* size);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderGetColorAsICCProfile")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderGetColorAsIccProfile(IntPtr decoder, int target, byte* icc, nuint size);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderGetFrameHeader")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderGetFrameHeader(IntPtr decoder, JxlFrameHeader* header);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderSkipCurrentFrame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderSkipCurrentFrame(IntPtr decoder);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderImageOutBufferSize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderImageOutBufferSize(IntPtr decoder, JxlPixelFormat* format, nuint* size);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderSetImageOutBuffer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderSetImageOutBuffer(IntPtr decoder, JxlPixelFormat* format, void* buffer, nuint size);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderSetBoxBuffer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderSetBoxBuffer(IntPtr decoder, byte* data, nuint size);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderReleaseBoxBuffer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint DecoderReleaseBoxBuffer(IntPtr decoder);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderGetBoxType")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderGetBoxType(IntPtr decoder, byte* type4, int decompressed);

    [LibraryImport(LibraryName, EntryPoint = "JxlDecoderGetBoxSizeRaw")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DecoderGetBoxSizeRaw(IntPtr decoder, ulong* size);

    // ----- Encoder -----

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint EncoderVersion();

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderCreate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr EncoderCreate(void* memoryManager);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderDestroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void EncoderDestroy(IntPtr encoder);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderSetParallelRunner")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderSetParallelRunner(IntPtr encoder, IntPtr runner, IntPtr runnerOpaque);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderGetError")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderGetError(IntPtr encoder);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderProcessOutput")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderProcessOutput(IntPtr encoder, byte** nextOut, nuint* availOut);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderSetFrameHeader")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderSetFrameHeader(IntPtr frameSettings, JxlFrameHeader* header);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderAddImageFrame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderAddImageFrame(IntPtr frameSettings, JxlPixelFormat* format, void* buffer, nuint size);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderAddBox")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderAddBox(IntPtr encoder, byte* type4, byte* contents, nuint size, int compress);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderUseBoxes")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderUseBoxes(IntPtr encoder);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderCloseBoxes")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void EncoderCloseBoxes(IntPtr encoder);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderCloseInput")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void EncoderCloseInput(IntPtr encoder);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderSetColorEncoding")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderSetColorEncoding(IntPtr encoder, JxlColorEncoding* color);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderSetICCProfile")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderSetIccProfile(IntPtr encoder, byte* icc, nuint size);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderInitBasicInfo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void EncoderInitBasicInfo(JxlBasicInfo* info);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderInitFrameHeader")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void EncoderInitFrameHeader(JxlFrameHeader* header);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderSetBasicInfo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderSetBasicInfo(IntPtr encoder, JxlBasicInfo* info);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderFrameSettingsSetOption")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderFrameSettingsSetOption(IntPtr frameSettings, int option, long value);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderSetFrameLossless")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderSetFrameLossless(IntPtr frameSettings, int lossless);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderSetFrameDistance")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int EncoderSetFrameDistance(IntPtr frameSettings, float distance);

    [LibraryImport(LibraryName, EntryPoint = "JxlEncoderFrameSettingsCreate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr EncoderFrameSettingsCreate(IntPtr encoder, IntPtr source);

    [LibraryImport(LibraryName, EntryPoint = "JxlColorEncodingSetToSRGB")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ColorEncodingSetToSrgb(JxlColorEncoding* encoding, int isGray);

    /// <summary>The <c>JxlThreadParallelRunner</c> exports, bound through function pointers because the library is optional.</summary>
    internal sealed class ThreadRunner(IntPtr run, IntPtr create, IntPtr destroy)
    {
        /// <summary>The <c>JxlThreadParallelRunner</c> callback handed to <c>Jxl*SetParallelRunner</c>.</summary>
        internal IntPtr Run { get; } = run;

        /// <summary>Creates a runner with the given number of worker threads (0 runs on the calling thread).</summary>
        internal IntPtr Create(int workers)
            => ((delegate* unmanaged[Cdecl]<void*, nuint, IntPtr>)create)(null, (nuint)workers);

        /// <summary>Destroys a runner created by <see cref="Create"/>.</summary>
        internal void Destroy(IntPtr runner) => ((delegate* unmanaged[Cdecl]<IntPtr, void>)destroy)(runner);
    }
}

/// <summary><c>JxlPixelFormat</c> (types.h v0.12.0).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JxlPixelFormat
{
    public uint NumChannels;
    public int DataType;
    public int Endianness;
    public nuint Align;
}

/// <summary><c>JxlBasicInfo</c> (codestream_header.h v0.12.0), including its 100-byte reserved tail.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct JxlBasicInfo
{
    public int HaveContainer;
    public uint XSize;
    public uint YSize;
    public uint BitsPerSample;
    public uint ExponentBitsPerSample;
    public float IntensityTarget;
    public float MinNits;
    public int RelativeToMaxDisplay;
    public float LinearBelow;
    public int UsesOriginalProfile;
    public int HavePreview;
    public int HaveAnimation;
    public int Orientation;
    public uint NumColorChannels;
    public uint NumExtraChannels;
    public uint AlphaBits;
    public uint AlphaExponentBits;
    public int AlphaPremultiplied;
    public uint PreviewXSize;
    public uint PreviewYSize;
    public uint TpsNumerator;
    public uint TpsDenominator;
    public uint NumLoops;
    public int HaveTimecodes;
    public uint IntrinsicXSize;
    public uint IntrinsicYSize;
    public fixed byte Padding[100];
}

/// <summary><c>JxlExtraChannelInfo</c> (codestream_header.h v0.12.0).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct JxlExtraChannelInfo
{
    public int Type;
    public uint BitsPerSample;
    public uint ExponentBitsPerSample;
    public uint DimShift;
    public uint NameLength;
    public int AlphaPremultiplied;
    public fixed float SpotColor[4];
    public uint CfaChannel;
}

/// <summary><c>JxlFrameHeader</c> with its nested <c>JxlLayerInfo</c> and <c>JxlBlendInfo</c> (codestream_header.h v0.12.0).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JxlFrameHeader
{
    public uint Duration;
    public uint Timecode;
    public uint NameLength;
    public int IsLast;
    public int HaveCrop;
    public int CropX0;
    public int CropY0;
    public uint XSize;
    public uint YSize;
    public int BlendMode;
    public uint BlendSource;
    public uint BlendAlpha;
    public int BlendClamp;
    public uint SaveAsReference;
}

/// <summary><c>JxlColorEncoding</c> (color_encoding.h v0.12.0).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct JxlColorEncoding
{
    public int ColorSpace;
    public int WhitePoint;
    public fixed double WhitePointXy[2];
    public int Primaries;
    public fixed double PrimariesRedXy[2];
    public fixed double PrimariesGreenXy[2];
    public fixed double PrimariesBlueXy[2];
    public int TransferFunction;
    public double Gamma;
    public int RenderingIntent;
}

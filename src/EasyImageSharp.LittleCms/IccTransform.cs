using Microsoft.Win32.SafeHandles;
using CoreProfile = EasyImageSharp.Metadata.Icc.IccProfile;

namespace EasyImageSharp.LittleCms;

/// <summary>
/// A compiled Little CMS ICC v2/v4 transform supporting RGB, CMYK, gray, Lab and XYZ matrix/LUT profiles.
/// Buffers contain interleaved floats without alpha. Calls are serialized per transform; dispose when finished.
/// Native failures throw instead of falling back to an approximate color conversion.
/// </summary>
public sealed unsafe class IccTransform : IDisposable
{
    private readonly TransformHandle handle;
    private readonly object gate = new();

    private IccTransform(IntPtr handle, IccColorModel input, IccColorModel output)
    {
        this.handle = new TransformHandle(handle);
        this.InputModel = input;
        this.OutputModel = output;
    }

    /// <summary>Gets the input channel model and units.</summary>
    public IccColorModel InputModel { get; }
    /// <summary>Gets the output channel model and units.</summary>
    public IccColorModel OutputModel { get; }
    /// <summary>Gets the number of input color channels.</summary>
    public int InputChannels => ChannelCount(this.InputModel);
    /// <summary>Gets the number of output color channels.</summary>
    public int OutputChannels => ChannelCount(this.OutputModel);
    /// <summary>Gets the loaded native Little CMS version encoded as major*1000 + minor*10 + patch.</summary>
    public static int NativeVersion => NativeCms.Version();

    /// <summary>Creates an sRGB ICC profile using the native engine.</summary>
    public static CoreProfile CreateSrgbProfile()
    {
        IntPtr profile = NativeCms.CreateSrgb();
        if (profile == IntPtr.Zero) throw new InvalidOperationException("Little CMS could not create the sRGB profile.");
        try
        {
            uint size = 0;
            if (NativeCms.SaveProfile(profile, null, ref size) == 0 || size > int.MaxValue)
                throw new InvalidOperationException("Little CMS could not serialize the sRGB profile.");
            byte[] data = new byte[(int)size];
            fixed (byte* pointer = data)
                if (NativeCms.SaveProfile(profile, pointer, ref size) == 0)
                    throw new InvalidOperationException("Little CMS could not serialize the sRGB profile.");
            return new CoreProfile(data);
        }
        finally { NativeCms.CloseProfile(profile); }
    }

    /// <summary>Compiles a transform from two profiles. Rendering-intent fallback follows the ICC/Little CMS rules.</summary>
    public static IccTransform Create(CoreProfile source, CoreProfile destination, IccTransformOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        IccRenderingIntent intent = options?.Intent ?? IccRenderingIntent.RelativeColorimetric;
        if (!Enum.IsDefined(intent)) throw new ArgumentOutOfRangeException(nameof(options));
        ValidateProfile(source);
        ValidateProfile(destination);
        byte[] inputBytes = source.ToByteArray(), outputBytes = destination.ToByteArray();
        fixed (byte* inputData = inputBytes)
        fixed (byte* outputData = outputBytes)
        {
            IntPtr input = NativeCms.OpenProfile(inputData, (uint)inputBytes.Length);
            if (input == IntPtr.Zero) throw new ArgumentException("Little CMS rejected the source ICC profile.", nameof(source));
            try
            {
                IntPtr output = NativeCms.OpenProfile(outputData, (uint)outputBytes.Length);
                if (output == IntPtr.Zero) throw new ArgumentException("Little CMS rejected the destination ICC profile.", nameof(destination));
                try
                {
                    IccColorModel inputModel = Model(NativeCms.ColorSpace(input)), outputModel = Model(NativeCms.ColorSpace(output));
                    // NOCACHE prevents per-pixel cache state; callers are additionally serialized for safe disposal.
                    uint flags = 0x0040u | (options?.BlackPointCompensation == true ? 0x2000u : 0u);
                    IntPtr transform = NativeCms.CreateTransform(input, Format(inputModel), output, Format(outputModel), (uint)intent, flags);
                    if (transform == IntPtr.Zero) throw new NotSupportedException("Little CMS could not construct this profile transform.");
                    return new IccTransform(transform, inputModel, outputModel);
                }
                finally { NativeCms.CloseProfile(output); }
            }
            finally { NativeCms.CloseProfile(input); }
        }
    }

    /// <summary>
    /// Transforms interleaved floats. RGB/gray use 0-1, CMYK uses 0-100 percent, Lab uses natural L*,a*,b*,
    /// and XYZ uses Y=1. Input/output must not overlap. Buffer validation completes before writes;
    /// cancellation may leave completed chunks in the destination. Extra destination elements remain untouched.
    /// </summary>
    public void Transform(ReadOnlySpan<float> source, Span<float> destination, int pixelCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pixelCount);
        long inputLength = (long)pixelCount * this.InputChannels, outputLength = (long)pixelCount * this.OutputChannels;
        if (source.Length < inputLength || destination.Length < outputLength) throw new ArgumentException("The pixel count exceeds a buffer's channel capacity.");
        source = source[..(int)inputLength];
        destination = destination[..(int)outputLength];
        if (source.Overlaps(destination)) throw new ArgumentException("Input and output buffers must not overlap.");
        foreach (float value in source)
            if (!float.IsFinite(value)) throw new ArgumentException("Input color components must be finite.", nameof(source));
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.handle.IsClosed, this);
            cancellationToken.ThrowIfCancellationRequested();
            fixed (float* input = source)
            fixed (float* output = destination)
            {
                for (int start = 0; start < pixelCount;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(16384, pixelCount - start);
                    NativeCms.DoTransform(this.handle.DangerousGetHandle(), input + (start * this.InputChannels), output + (start * this.OutputChannels), (uint)count);
                    start += count;
                }
            }
            GC.KeepAlive(this.handle);
        }
    }

    /// <summary>Releases the native transform after any active call completes.</summary>
    public void Dispose() { lock (this.gate) this.handle.Dispose(); }

    private static void ValidateProfile(CoreProfile profile)
    {
        if (!profile.Header.IsValid || profile.Header.Size < 128 || profile.Header.Size > profile.Length)
            throw new ArgumentException("The ICC header or declared size is invalid.", nameof(profile));
        if (profile.Header.Version.Major is not (2 or 4)) throw new NotSupportedException("Only ICC versions 2 and 4 are supported.");
        if (profile.Header.ProfileClass is not ("mntr" or "scnr" or "prtr" or "spac" or "abst"))
            throw new NotSupportedException("Only input, display, output, abstract and color-space profiles are supported; device links and named colors require a different workflow.");
    }

    private static IccColorModel Model(uint signature) => signature switch
    {
        0x52474220 => IccColorModel.Rgb, 0x434D594B => IccColorModel.Cmyk, 0x47524159 => IccColorModel.Gray,
        0x4C616220 => IccColorModel.Lab, 0x58595A20 => IccColorModel.Xyz,
        _ => throw new NotSupportedException("This profile uses a channel model outside RGB/CMYK/gray/Lab/XYZ."),
    };
    private static int ChannelCount(IccColorModel model) => model == IccColorModel.Gray ? 1 : model == IccColorModel.Cmyk ? 4 : 3;
    private static uint Format(IccColorModel model)
    {
        uint type = model switch { IccColorModel.Gray => 3, IccColorModel.Rgb => 4, IccColorModel.Cmyk => 6, IccColorModel.Xyz => 9, IccColorModel.Lab => 10, _ => throw new ArgumentOutOfRangeException(nameof(model)) };
        return (1u << 22) | (type << 16) | ((uint)ChannelCount(model) << 3) | 4u;
    }

    private sealed class TransformHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal TransformHandle(IntPtr handle) : base(true) => this.SetHandle(handle);
        protected override bool ReleaseHandle() { NativeCms.DeleteTransform(this.handle); return true; }
    }
}

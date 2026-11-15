using System.Numerics;
using EasyImageSharp.ColorSpaces;
using EasyImageSharp.Formats;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Converts images from their embedded ICC profiles to sRGB with the managed matrix/TRC engine
/// (<see cref="IccMatrixProfile"/>), for both the decode-time option and the processing operation.
/// </summary>
/// <remarks>
/// Work happens in three steps so that a strict conversion never leaves an image half converted: every frame's
/// profile is resolved first (and a strict policy throws there), then the pixels are converted, then the metadata
/// is updated. Pixels are converted in single-precision float through the 0-1 scaled vector of the pixel format,
/// so 16-bit and float formats keep their precision; integer formats clamp to their range on the way back, float
/// formats keep out-of-gamut values. Pixel formats without colour channels hold only the decoder's luminance and
/// are converted as neutral greys.
/// </remarks>
internal static class ColorManagementOps
{
    /// <summary>
    /// Applies <see cref="DecoderOptions.ColorProfileHandling"/> to a freshly decoded image, disposing it when the
    /// conversion throws. Returns the same instance.
    /// </summary>
    public static Image<TPixel> ApplyOnLoad<TPixel>(Image<TPixel> image, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (options.ColorProfileHandling != ColorProfileHandling.ConvertToSrgb)
        {
            return image;
        }

        try
        {
            ConversionPlan plan = PlanEmbedded(image, options.UnsupportedColorProfileHandling);
            Execute(image, plan);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>Resolves every frame's effective profile (its own, else the image's) and decides what to do with it.</summary>
    /// <exception cref="NotSupportedException">Strict policy and a frame's profile is outside the matrix/TRC subset.</exception>
    /// <exception cref="InvalidImageContentException">Strict policy and a frame's profile is malformed.</exception>
    public static ConversionPlan PlanEmbedded<TPixel>(Image<TPixel> image, UnsupportedColorProfileHandling handling)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        List<ImageFrame<TPixel>> frames = image.Frames.InnerList;
        var plan = new ConversionPlan(frames.Count, clearAllProfiles: false);
        IccProfile? cachedProfile = null;
        Resolution cached = default;
        for (int i = 0; i < frames.Count; i++)
        {
            OperationExecution.Checkpoint();
            IccProfile? own = frames[i].Metadata.IccProfile;
            IccProfile? effective = own ?? image.Metadata.IccProfile;

            // TIFF pages each carry their own copy of what is usually the same profile: parse it once.
            Resolution resolution;
            if (effective is not null && cachedProfile is not null
                && (ReferenceEquals(effective, cachedProfile) || effective.RawData.SequenceEqual(cachedProfile.RawData)))
            {
                resolution = cached;
            }
            else
            {
                resolution = Resolve(effective);
                cachedProfile = effective;
                cached = resolution;
            }

            if (handling == UnsupportedColorProfileHandling.Throw && resolution.LeftUnconverted)
            {
                throw resolution.CreateStrictException();
            }

            plan.Transforms[i] = resolution.Status == ColorProfileConversionStatus.Converted ? resolution.Transform : null;
            plan.Records[i] = resolution.CreateRecord();
            if (resolution.LeftUnconverted && (own is null || i == 0))
            {
                // Frames without their own profile still rely on the image-level one, and the image-level profile is
                // what encoders write for the root frame, so it stays while either of those is unconverted.
                plan.KeepImageProfile = true;
            }
        }

        return plan;
    }

    /// <summary>Plans a conversion of every frame from an explicitly given profile, ignoring any embedded ones.</summary>
    public static ConversionPlan PlanExplicit(int frameCount, IccMatrixProfile profile)
    {
        var plan = new ConversionPlan(frameCount, clearAllProfiles: true);
        ColorProfileConversionStatus status = profile.IsSrgbEquivalent
            ? ColorProfileConversionStatus.AlreadySrgb
            : ColorProfileConversionStatus.Converted;
        for (int i = 0; i < frameCount; i++)
        {
            plan.Transforms[i] = status == ColorProfileConversionStatus.Converted ? profile : null;
            plan.Records[i] = new ColorProfileConversionMetadata { Status = status, SourceProfileDescription = profile.Description };
        }

        return plan;
    }

    /// <summary>Converts the planned frames' pixels and then updates the profiles and conversion records.</summary>
    public static void Execute<TPixel>(Image<TPixel> image, ConversionPlan plan)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        List<ImageFrame<TPixel>> frames = image.Frames.InnerList;
        for (int i = 0; i < frames.Count; i++)
        {
            if (plan.Transforms[i] is { } transform)
            {
                ConvertFrame(frames[i], transform);
            }
        }

        ColorProfileConversionMetadata? summary = null;
        for (int i = 0; i < frames.Count; i++)
        {
            ColorProfileConversionMetadata record = plan.Records[i];
            if (plan.ClearAllProfiles || record.Status is ColorProfileConversionStatus.Converted or ColorProfileConversionStatus.AlreadySrgb)
            {
                frames[i].Metadata.IccProfile = null;
            }

            frames[i].Metadata.SetFormatMetadata(record);
            if (summary is null || Rank(record.Status) > Rank(summary.Status))
            {
                summary = record;
            }
        }

        if (plan.ClearAllProfiles || !plan.KeepImageProfile)
        {
            image.Metadata.IccProfile = null;
        }

        image.Metadata.SetFormatMetadata(summary is null ? new ColorProfileConversionMetadata() : summary.DeepClone());
    }

    /// <summary>Converts one frame in place, row-parallel under the ambient <see cref="Configuration"/>.</summary>
    public static void ConvertFrame<TPixel>(ImageFrame<TPixel> frame, IccMatrixProfile profile)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (IsEightBitColor<TPixel>())
        {
            float[] tables = profile.LinearTables8;
            ParallelRowIterator.IterateRows(frame.Width, frame.Height, (start, end) => ConvertRows8(frame, profile, tables, start, end));
        }
        else
        {
            ParallelRowIterator.IterateRows(frame.Width, frame.Height, (start, end) => ConvertRowsScaled(frame, profile, start, end));
        }
    }

    /// <summary>
    /// The 8-bit fast path. The table holds the profile curves at <c>i / 255f</c>, which is what the scaled path
    /// evaluates for an 8-bit component, and runs of one colour reuse the previous result, so the output is
    /// bit-identical to <see cref="ConvertRowsScaled{TPixel}"/>.
    /// </summary>
    private static void ConvertRows8<TPixel>(ImageFrame<TPixel> frame, IccMatrixProfile profile, float[] tables, int start, int end)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ReadOnlySpan<float> red = tables.AsSpan(0, 256);
        ReadOnlySpan<float> green = tables.AsSpan(256, 256);
        ReadOnlySpan<float> blue = tables.AsSpan(512, 256);
        Span<TPixel> pixels = frame.PixelSpan;
        int width = frame.Width;
        int previous = -1;
        Vector3 converted = default;
        for (int y = start; y < end; y++)
        {
            OperationExecution.Checkpoint();
            Span<TPixel> row = pixels.Slice(y * width, width);
            for (int x = 0; x < row.Length; x++)
            {
                Rgba32 source = row[x].ToRgba32();
                int key = source.R | (source.G << 8) | (source.B << 16);
                if (key != previous)
                {
                    converted = profile.LinearToSrgbCore(new Vector3(red[source.R], green[source.G], blue[source.B]));
                    previous = key;
                }

                row[x] = TPixel.FromScaledVector4(new Vector4(converted, source.A / 255f));
            }
        }
    }

    /// <summary>The general path: any pixel format, through its 0-1 scaled vector.</summary>
    private static void ConvertRowsScaled<TPixel>(ImageFrame<TPixel> frame, IccMatrixProfile profile, int start, int end)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Span<TPixel> pixels = frame.PixelSpan;
        int width = frame.Width;
        for (int y = start; y < end; y++)
        {
            OperationExecution.Checkpoint();
            Span<TPixel> row = pixels.Slice(y * width, width);
            for (int x = 0; x < row.Length; x++)
            {
                Vector4 source = row[x].ToScaledVector4();
                Vector3 converted = profile.ToSrgbCore(new Vector3(source.X, source.Y, source.Z));
                row[x] = TPixel.FromScaledVector4(new Vector4(converted, source.W));
            }
        }
    }

    /// <summary>True for the formats whose components are exactly 8-bit RGB(A), so <c>ToRgba32</c> is lossless.</summary>
    private static bool IsEightBitColor<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => typeof(TPixel) == typeof(Rgba32) || typeof(TPixel) == typeof(Rgb24) || typeof(TPixel) == typeof(Bgra32)
            || typeof(TPixel) == typeof(Bgr24) || typeof(TPixel) == typeof(Argb32) || typeof(TPixel) == typeof(Abgr32);

    /// <summary>How strongly a frame outcome dominates the image-level summary: unconverted frames first.</summary>
    private static int Rank(ColorProfileConversionStatus status) => status switch
    {
        ColorProfileConversionStatus.Invalid => 4,
        ColorProfileConversionStatus.Unsupported => 3,
        ColorProfileConversionStatus.Converted => 2,
        ColorProfileConversionStatus.AlreadySrgb => 1,
        _ => 0,
    };

    private static Resolution Resolve(IccProfile? profile)
    {
        if (profile is null)
        {
            return new Resolution(ColorProfileConversionStatus.NoProfile, null, null, null);
        }

        string? description = profile.Header.Description;
        try
        {
            IccMatrixProfile transform = IccMatrixProfile.Create(profile);
            return transform.IsSrgbEquivalent
                ? new Resolution(ColorProfileConversionStatus.AlreadySrgb, null, description, null)
                : new Resolution(ColorProfileConversionStatus.Converted, transform, description, null);
        }
        catch (NotSupportedException ex)
        {
            // Some sRGB profiles (the ICC's own sRGB v2 among them) carry LUT transforms next to their matrix/TRC
            // tags. A conforming profile's two descriptions agree, so a matrix/TRC part that is sRGB means the
            // pixels already are; nothing is ever converted through the matrix of a LUT profile.
            return IsSrgbIgnoringLuts(profile)
                ? new Resolution(ColorProfileConversionStatus.AlreadySrgb, null, description, null)
                : new Resolution(ColorProfileConversionStatus.Unsupported, null, description, ex);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            return new Resolution(ColorProfileConversionStatus.Invalid, null, description, ex);
        }
    }

    private static bool IsSrgbIgnoringLuts(IccProfile profile)
    {
        try
        {
            return IccMatrixProfile.Create(profile, ignoreLutTags: true).IsSrgbEquivalent;
        }
        catch (Exception ex) when (ex is NotSupportedException || DecoderGuard.IsMalformedInputSymptom(ex))
        {
            return false;
        }
    }

    /// <summary>What to do with each frame: the transform to apply (or none) and the record to attach.</summary>
    internal sealed class ConversionPlan
    {
        public ConversionPlan(int frameCount, bool clearAllProfiles)
        {
            this.Transforms = new IccMatrixProfile?[frameCount];
            this.Records = new ColorProfileConversionMetadata[frameCount];
            this.ClearAllProfiles = clearAllProfiles;
        }

        /// <summary>Per frame: the profile to convert from, or <see langword="null"/> when the pixels stay as they are.</summary>
        public IccMatrixProfile?[] Transforms { get; }

        /// <summary>Per frame: the conversion record to attach.</summary>
        public ColorProfileConversionMetadata[] Records { get; }

        /// <summary>True for an explicit source profile: every embedded profile is stale afterwards.</summary>
        public bool ClearAllProfiles { get; }

        /// <summary>True when the image-level profile still describes pixels that were left unconverted.</summary>
        public bool KeepImageProfile { get; set; }

        /// <summary>True when at least one frame's pixels will be written.</summary>
        public bool WritesPixels => Array.Exists(this.Transforms, static t => t is not null);
    }

    private readonly struct Resolution
    {
        public Resolution(ColorProfileConversionStatus status, IccMatrixProfile? transform, string? description, Exception? error)
        {
            this.Status = status;
            this.Transform = transform;
            this.Description = description;
            this.Error = error;
        }

        public ColorProfileConversionStatus Status { get; }

        public IccMatrixProfile? Transform { get; }

        public string? Description { get; }

        public Exception? Error { get; }

        public bool LeftUnconverted
            => this.Status is ColorProfileConversionStatus.Unsupported or ColorProfileConversionStatus.Invalid;

        public ColorProfileConversionMetadata CreateRecord() => new()
        {
            Status = this.Status,
            Detail = this.Error?.Message,
            SourceProfileDescription = this.Description,
        };

        public Exception CreateStrictException()
        {
            string name = this.Description is null ? "The embedded ICC profile" : $"The embedded ICC profile '{this.Description}'";
            return this.Status == ColorProfileConversionStatus.Invalid
                ? new InvalidImageContentException($"{name} is malformed and cannot be converted to sRGB: {this.Error?.Message}", this.Error!)
                : new NotSupportedException(
                    $"{name} cannot be converted to sRGB by the managed matrix/TRC engine: {this.Error?.Message} "
                    + "Use UnsupportedColorProfileHandling.Keep to leave such images unconverted.", this.Error);
        }
    }
}

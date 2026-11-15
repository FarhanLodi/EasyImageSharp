using System.Globalization;
using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Microsoft.AspNetCore.Http;

namespace EasyImageSharp.Web.Internal;

/// <summary>A request the middleware refuses with a specific status code and a short, client-safe reason.</summary>
internal sealed class ImageRequestException : Exception
{
    public ImageRequestException(int statusCode, string message)
        : base(message)
        => this.StatusCode = statusCode;

    public int StatusCode { get; }
}

/// <summary>An encoded result of <see cref="ImageProcessor.Process"/>.</summary>
internal readonly record struct ProcessedImage(byte[] Buffer, int Length, IImageOutputFormat Format)
{
    public ReadOnlyMemory<byte> Content => this.Buffer.AsMemory(0, this.Length);
}

/// <summary>Where the resize lands: the options to run (none when the image is kept as is) and the resulting canvas.</summary>
internal readonly record struct ResizePlan(ResizeOptions? Options, int Width, int Height);

/// <summary>Decodes, transforms and encodes one image. Synchronous and CPU-bound; cancellation flows through the execution options.</summary>
internal static class ImageProcessor
{
    /// <summary>Decodes <paramref name="source"/>, applies <paramref name="commands"/> and encodes the result.</summary>
    /// <param name="source">The encoded source.</param>
    /// <param name="commands">The parsed commands.</param>
    /// <param name="settings">The current settings.</param>
    /// <param name="accepted">The negotiable formats the client accepts, in preference order; empty when it accepts none.</param>
    /// <param name="negotiationActive">Whether format negotiation applies to this request.</param>
    /// <param name="decoderOptions">Decoder limits and cancellation.</param>
    /// <param name="execution">Processing budget, parallelism and cancellation.</param>
    public static ProcessedImage Process(
        ReadOnlySpan<byte> source,
        ImageCommands commands,
        WebSettings settings,
        IReadOnlyList<IImageOutputFormat> accepted,
        bool negotiationActive,
        DecoderOptions decoderOptions,
        ImageExecutionOptions execution)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(source, decoderOptions);
        IImageOutputFormat format = ChooseFormat(image, commands, settings, accepted, negotiationActive);

        // bgcolor always composites. Otherwise only an output without alpha needs flattening, and only when the source
        // has transparency: orientation, padding (with an opaque pad colour) and resampling never introduce any.
        Color? background = commands.Background
            ?? (!format.SupportsTransparency && HasTransparency(image) ? settings.DefaultBackground : null);
        Color padColor = commands.Background ?? (format.SupportsTransparency ? Color.Transparent : settings.DefaultBackground);

        if (commands.AutoOrient || commands.Rotate != 0 || commands.FlipHorizontal || commands.FlipVertical)
        {
            image.Mutate(execution, ctx =>
            {
                if (commands.AutoOrient)
                {
                    ctx.AutoOrient();
                }

                if (commands.Rotate != 0)
                {
                    ctx.Rotate(commands.Rotate);
                }

                if (commands.FlipHorizontal)
                {
                    ctx.Flip(FlipMode.Horizontal);
                }

                if (commands.FlipVertical)
                {
                    ctx.Flip(FlipMode.Vertical);
                }
            });
        }

        ResizePlan plan = Plan(image.Width, image.Height, commands, settings.Options.AllowUpscaling);
        EnsureOutputWithinLimits(plan.Width, plan.Height, settings.Options);

        if (plan.Options is not null || background is not null)
        {
            if (plan.Options is ResizeOptions resize)
            {
                resize.PadColor = padColor;
            }

            image.Mutate(execution, ctx =>
            {
                if (plan.Options is not null)
                {
                    ctx.Resize(plan.Options);
                }

                if (background is Color color)
                {
                    ctx.BackgroundColor(color);
                }
            });
        }

        if (settings.Options.StripMetadata)
        {
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
        }

        execution.CancellationToken.ThrowIfCancellationRequested();
        IImageEncoder encoder = format.CreateEncoder(commands.Quality ?? settings.Options.DefaultQuality);

        // Not disposed: the result hands out the stream's own buffer, and a MemoryStream owns nothing else.
        var output = new MemoryStream();
        image.Save(output, encoder);
        return new ProcessedImage(output.GetBuffer(), checked((int)output.Length), format);
    }

    /// <summary>
    /// Computes the resize for a source of <paramref name="sourceWidth"/> × <paramref name="sourceHeight"/> (after
    /// orientation). Without upscaling, a box larger than the source is shrunk uniformly until it fits (so the aspect
    /// ratio the client asked for survives), <c>pad</c> pads around the unscaled image, and <c>max</c>/<c>min</c>
    /// keep the source size.
    /// </summary>
    internal static ResizePlan Plan(int sourceWidth, int sourceHeight, ImageCommands commands, bool allowUpscaling)
    {
        int? requestedWidth = commands.Width;
        int? requestedHeight = commands.Height;
        if (requestedWidth is null && requestedHeight is null)
        {
            return new ResizePlan(null, sourceWidth, sourceHeight);
        }

        if (requestedWidth is null || requestedHeight is null)
        {
            // One side: the other follows the aspect ratio, whatever the mode, so the size is computed here and the
            // resize is an exact-size one.
            int width;
            int height;
            if (requestedWidth is int w)
            {
                width = allowUpscaling ? w : Math.Min(w, sourceWidth);
                height = Scale(sourceHeight, width, sourceWidth);
            }
            else
            {
                height = allowUpscaling ? requestedHeight!.Value : Math.Min(requestedHeight!.Value, sourceHeight);
                width = Scale(sourceWidth, height, sourceHeight);
            }

            return Exact(width, height, ResizeMode.Stretch, commands, sourceWidth, sourceHeight);
        }

        int boxWidth = requestedWidth.Value;
        int boxHeight = requestedHeight.Value;
        double fit = Math.Min((double)boxWidth / sourceWidth, (double)boxHeight / sourceHeight);
        double cover = Math.Max((double)boxWidth / sourceWidth, (double)boxHeight / sourceHeight);

        switch (commands.Mode)
        {
            case ResizeMode.Stretch:
                return Exact(
                    allowUpscaling ? boxWidth : Math.Min(boxWidth, sourceWidth),
                    allowUpscaling ? boxHeight : Math.Min(boxHeight, sourceHeight),
                    ResizeMode.Stretch,
                    commands,
                    sourceWidth,
                    sourceHeight);

            case ResizeMode.Max:
                if (!allowUpscaling && fit > 1)
                {
                    return new ResizePlan(null, sourceWidth, sourceHeight);
                }

                return Exact(
                    Math.Clamp((int)Math.Round(sourceWidth * fit), 1, boxWidth),
                    Math.Clamp((int)Math.Round(sourceHeight * fit), 1, boxHeight),
                    ResizeMode.Stretch,
                    commands,
                    sourceWidth,
                    sourceHeight);

            case ResizeMode.Min:
                if (!allowUpscaling && cover > 1)
                {
                    return new ResizePlan(null, sourceWidth, sourceHeight);
                }

                return Exact(
                    Math.Max(boxWidth, (int)Math.Round(sourceWidth * cover)),
                    Math.Max(boxHeight, (int)Math.Round(sourceHeight * cover)),
                    ResizeMode.Stretch,
                    commands,
                    sourceWidth,
                    sourceHeight);

            case ResizeMode.Crop:
                if (!allowUpscaling && cover > 1)
                {
                    // Rounded rather than floored: 800 / (800 / 300.0) may come out a hair under 300. The clamp keeps
                    // the box inside the source either way.
                    boxWidth = Math.Clamp((int)Math.Round(boxWidth / cover), 1, sourceWidth);
                    boxHeight = Math.Clamp((int)Math.Round(boxHeight / cover), 1, sourceHeight);
                }

                return Exact(boxWidth, boxHeight, ResizeMode.Crop, commands, sourceWidth, sourceHeight);

            case ResizeMode.Pad:
                return Exact(
                    boxWidth,
                    boxHeight,
                    !allowUpscaling && fit > 1 ? ResizeMode.BoxPad : ResizeMode.Pad,
                    commands,
                    sourceWidth,
                    sourceHeight);

            default:
                return Exact(boxWidth, boxHeight, ResizeMode.BoxPad, commands, sourceWidth, sourceHeight);
        }
    }

    internal static void EnsureOutputWithinLimits(int width, int height, EasyImageSharpWebOptions options)
    {
        if (width > options.MaxWidth || height > options.MaxHeight || (long)width * height > options.MaxOutputPixels)
        {
            throw new ImageRequestException(
                StatusCodes.Status422UnprocessableEntity,
                string.Create(CultureInfo.InvariantCulture, $"The output would be {width}x{height}, which exceeds the configured output limits."));
        }
    }

    private static ResizePlan Exact(int width, int height, ResizeMode mode, ImageCommands commands, int sourceWidth, int sourceHeight)
    {
        if (mode == ResizeMode.Stretch && width == sourceWidth && height == sourceHeight)
        {
            return new ResizePlan(null, width, height);
        }

        var options = new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = mode,
            Position = commands.Anchor,
            Sampler = commands.Sampler,
        };

        return new ResizePlan(options, width, height);
    }

    private static int Scale(int side, int target, int reference)
        => (int)Math.Clamp(Math.Round((double)side * target / reference), 1, int.MaxValue);

    private static IImageOutputFormat ChooseFormat(
        Image<Rgba32> image,
        ImageCommands commands,
        WebSettings settings,
        IReadOnlyList<IImageOutputFormat> accepted,
        bool negotiationActive)
    {
        if (commands.Format is IImageOutputFormat requested)
        {
            return requested;
        }

        bool animated = image.Frames.Count > 1;
        if (negotiationActive && !animated && accepted.Count > 0)
        {
            return accepted[0];
        }

        ImageFormat? decoded = image.Metadata.DecodedImageFormat;
        IImageOutputFormat? same = decoded is null ? null : settings.GetAllowed(decoded.Name.ToLowerInvariant());

        // Keep the source format unless it is a negotiable format this client did not ask for: a WebP original is not
        // sent to a browser that never said it can display WebP.
        if (same is not null && !(negotiationActive && settings.Negotiated.Contains(same) && !accepted.Contains(same)))
        {
            return same;
        }

        IImageOutputFormat? fallback = animated
            ? settings.GetAllowed("gif")
            : settings.GetAllowed(HasTransparency(image) ? "png" : "jpeg");
        return fallback ?? settings.GetAllowed("png") ?? settings.FirstAllowed();
    }

    private static bool HasTransparency(Image<Rgba32> image)
    {
        // JPEG has no alpha channel, so the scan is skipped for the most common source.
        if (ReferenceEquals(image.Metadata.DecodedImageFormat, ImageFormat.Jpeg))
        {
            return false;
        }

        ImageFrame<Rgba32> frame = image.Frames.RootFrame;
        for (int y = 0; y < frame.Height; y++)
        {
            foreach (Rgba32 pixel in frame.GetRowSpan(y))
            {
                if (pixel.A != byte.MaxValue)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

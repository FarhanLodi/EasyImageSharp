using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Applies processing operations to every frame of an image.</summary>
internal sealed partial class ImageProcessingContext<TPixel> : IImageProcessingContext
    where TPixel : unmanaged, IPixel<TPixel>
{
    /// <summary>Whether <typeparamref name="TPixel"/> stores an alpha channel (a transparent pixel survives a round trip).</summary>
    private static readonly bool PixelFormatHasAlpha =
        TPixel.FromRgba32(Rgba32.Transparent).ToRgba32().A != byte.MaxValue;

    private readonly Image<TPixel> image;

    /// <summary>
    /// True while the frames still point at another image's pixel buffers (the copy-on-write clone path).
    /// Any operation that writes into a frame duplicates the buffer first; operations that swap in a freshly
    /// built frame - resize, crop, pad, rotate, flip - never need the copy at all.
    /// </summary>
    private bool sharedBuffers;

    public ImageProcessingContext(Image<TPixel> image)
        : this(image, sharedBuffers: false)
    {
    }

    internal ImageProcessingContext(Image<TPixel> image, bool sharedBuffers)
    {
        this.image = image;
        this.sharedBuffers = sharedBuffers;
    }

    public Size GetCurrentSize() => new(this.image.Width, this.image.Height);

    public IImageProcessingContext Resize(ResizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Sampler);
        return this.PerFrameReplace(frame =>
        {
            ResizePlan plan = ComputeResizeTargets(options, frame.Width, frame.Height);
            bool sameContent = plan.ContentWidth == frame.Width && plan.ContentHeight == frame.Height;
            bool sameCanvas = plan.CanvasWidth == plan.ContentWidth && plan.CanvasHeight == plan.ContentHeight
                && plan.OffsetX == 0 && plan.OffsetY == 0;
            if (sameContent && sameCanvas)
            {
                return frame;
            }

            ImageFrame<TPixel> resized = sameContent
                ? frame
                : FrameOps.Resize(frame, plan.ContentWidth, plan.ContentHeight, options.Sampler, options.PremultiplyAlpha, options.Compand);
            if (sameCanvas)
            {
                return resized;
            }

            return FrameOps.PlaceOnCanvas(resized, plan.CanvasWidth, plan.CanvasHeight, plan.OffsetX, plan.OffsetY, options.PadColor);
        });
    }

    public IImageProcessingContext Crop(Rectangle bounds)
        => this.PerFrameReplace(frame => FrameOps.Crop(frame, bounds));

    public IImageProcessingContext Pad(int width, int height, Rgba32 backgroundColor)
        => this.PerFrameReplace(frame => FrameOps.PadToCanvas(frame, width, height, backgroundColor));

    public IImageProcessingContext Rotate(float degrees)
    {
        // Normalize to [0, 360).
        float normalized = degrees % 360f;
        if (normalized < 0)
        {
            normalized += 360f;
        }

        if (normalized == 0f)
        {
            return this;
        }

        return this.PerFrameReplace(frame => normalized switch
        {
            90f => FrameOps.Rotate90(frame),
            180f => FrameOps.Rotate180(frame),
            270f => FrameOps.Rotate270(frame),
            _ => FrameOps.RotateArbitrary(frame, normalized, Rgba32.Transparent),
        });
    }

    public IImageProcessingContext Flip(FlipMode flipMode)
        => flipMode == FlipMode.None ? this : this.PerFrameReplace(frame => FrameOps.Flip(frame, flipMode));

    public IImageProcessingContext Grayscale()
        => this.PerFrame(frame =>
        {
            PixelRunner.Grayscale(frame);
            return frame;
        });

    public IImageProcessingContext Invert()
        => this.ApplyChannelLut(static v => (byte)(255 - v));

    public IImageProcessingContext BackgroundColor(Color color)
    {
        // Formats without alpha are already opaque; a fully transparent background changes nothing.
        if (!PixelFormatHasAlpha || color.A == 0)
        {
            return this;
        }

        Rgba32 background = color.ToRgba32();
        return this.PerFramePixels(new BackgroundOperation(background));
    }

    public IImageProcessingContext Brightness(float amount)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Brightness amount must be non-negative.");
        }

        return this.ApplyChannelLut(v => ClampByte(v * amount));
    }

    public IImageProcessingContext Contrast(float amount)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Contrast amount must be non-negative.");
        }

        return this.ApplyChannelLut(v => ClampByte((((v / 255f) - 0.5f) * amount + 0.5f) * 255f));
    }

    public IImageProcessingContext BinaryThreshold(float threshold)
        => this.PerFramePixels(new BinaryThresholdOperation(ClampByte(threshold * 255f)));

    public IImageProcessingContext OtsuThreshold()
        => this.PerFrame(frame =>
        {
            FilterOps.OtsuThreshold(frame);
            return frame;
        });

    public IImageProcessingContext AdaptiveThreshold(int windowSize, float thresholdLimit)
        => this.PerFrame(frame =>
        {
            FilterOps.BradleyThreshold(frame, windowSize, thresholdLimit);
            return frame;
        });

    public IImageProcessingContext SauvolaThreshold(int windowSize, float k)
        => this.PerFrame(frame =>
        {
            FilterOps.SauvolaThreshold(frame, windowSize, k);
            return frame;
        });

    public IImageProcessingContext GaussianBlur(float sigma)
        => this.PerFrame(frame =>
        {
            FilterOps.GaussianBlur(frame, sigma);
            return frame;
        });

    public IImageProcessingContext GaussianSharpen(float sigma)
        => this.PerFrame(frame =>
        {
            FilterOps.GaussianSharpen(frame, sigma);
            return frame;
        });

    public IImageProcessingContext MedianBlur(int radius)
        => this.PerFrame(frame =>
        {
            FilterOps.MedianBlur(frame, radius);
            return frame;
        });

    public IImageProcessingContext Deskew(float maxAngleDegrees)
        => this.PerFrame(frame => DeskewOps.Deskew(frame, maxAngleDegrees));

    public IImageProcessingContext DrawImage(Image image, Point location, float opacity)
    {
        ArgumentNullException.ThrowIfNull(image);
        return this.PerFrame(frame =>
        {
            FrameOps.DrawImage(frame, image, location, Math.Clamp(opacity, 0f, 1f));
            return frame;
        });
    }

    // ----- Helpers -----

    private IImageProcessingContext PerFrame(Func<ImageFrame<TPixel>, ImageFrame<TPixel>> operation)
    {
        // The operation may write into the frame it is given, so any shared buffer has to be private first.
        this.EnsureOwnBuffers();
        foreach (ImageFrame<TPixel> frame in this.image.Frames.InnerList)
        {
            ImageFrame<TPixel> result = operation(frame);
            if (!ReferenceEquals(result, frame))
            {
                frame.ReplaceBuffer(result.PixelArray, result.Width, result.Height);
            }
        }

        return this;
    }

    /// <summary>
    /// Like <see cref="PerFrame"/> but for operations that only ever read their input frame and return a
    /// newly built one, so a shared buffer can be dropped instead of copied.
    /// </summary>
    private IImageProcessingContext PerFrameReplace(Func<ImageFrame<TPixel>, ImageFrame<TPixel>> operation)
    {
        bool stillShared = false;
        foreach (ImageFrame<TPixel> frame in this.image.Frames.InnerList)
        {
            ImageFrame<TPixel> result = operation(frame);
            if (!ReferenceEquals(result, frame))
            {
                frame.ReplaceBuffer(result.PixelArray, result.Width, result.Height);
            }
            else
            {
                // The operation was a no-op for this frame, so it still points at the original buffer.
                stillShared |= this.sharedBuffers;
            }
        }

        this.sharedBuffers = stillShared;
        return this;
    }

    /// <summary>Gives every frame its own pixel buffer, ending the copy-on-write window.</summary>
    internal void EnsureOwnBuffers()
    {
        if (!this.sharedBuffers)
        {
            return;
        }

        this.sharedBuffers = false;
        foreach (ImageFrame<TPixel> frame in this.image.Frames.InnerList)
        {
            FrameFactory.DuplicateBuffer(frame);
        }
    }

    private IImageProcessingContext PerFramePixels(Func<Rgba32, Rgba32> transform)
        => this.PerFrame(frame =>
        {
            Span<TPixel> pixels = frame.PixelSpan;
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = TPixel.FromRgba32(transform(pixels[i].ToRgba32()));
            }

            return frame;
        });

    /// <summary>Runs a struct-typed per-pixel operation over every frame; the JIT inlines it into the row loop.</summary>
    private IImageProcessingContext PerFramePixels<TOperation>(TOperation operation)
        where TOperation : struct, IPixelOperation
        => this.PerFrame(frame =>
        {
            PixelRunner.ApplyPixels(frame, operation);
            return frame;
        });

    /// <summary>
    /// Runs a channel-independent operation over every frame through a 256-entry table, so the transfer
    /// function is evaluated 256 times per image instead of three times per pixel.
    /// </summary>
    private IImageProcessingContext ApplyChannelLut(Func<byte, byte> channel)
    {
        var lut = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            lut[v] = channel((byte)v);
        }

        return this.PerFrame(frame =>
        {
            PixelRunner.ApplyChannelLut(frame, lut);
            return frame;
        });
    }

    /// <summary>Composites opaque colour under partially transparent pixels.</summary>
    private readonly struct BackgroundOperation : IPixelOperation
    {
        private readonly Rgba32 background;

        public BackgroundOperation(Rgba32 background) => this.background = background;

        public Rgba32 Apply(Rgba32 source)
            => source.A == byte.MaxValue ? source : FrameOps.SourceOver(source, this.background);
    }

    /// <summary>Maps every pixel to white or black by comparing its luminance with a cutoff.</summary>
    private readonly struct BinaryThresholdOperation : IPixelOperation
    {
        private readonly byte cutoff;

        public BinaryThresholdOperation(byte cutoff) => this.cutoff = cutoff;

        public Rgba32 Apply(Rgba32 source)
            => PixelOps.Luminance8(source) >= this.cutoff ? Rgba32.White : Rgba32.Black;
    }

    /// <summary>The scaled content size plus where it lands on the output canvas.</summary>
    private readonly record struct ResizePlan(int ContentWidth, int ContentHeight, int CanvasWidth, int CanvasHeight, int OffsetX, int OffsetY);

    private static ResizePlan ComputeResizeTargets(ResizeOptions options, int sourceWidth, int sourceHeight)
    {
        int requestedW = options.Size.Width;
        int requestedH = options.Size.Height;
        if (requestedW <= 0 && requestedH <= 0)
        {
            throw new ArgumentException("Resize target size must have at least one positive dimension.", nameof(options));
        }

        // A zero dimension is computed from the aspect ratio.
        if (requestedW <= 0)
        {
            requestedW = Math.Max(1, (int)Math.Round((double)sourceWidth * requestedH / sourceHeight));
        }
        else if (requestedH <= 0)
        {
            requestedH = Math.Max(1, (int)Math.Round((double)sourceHeight * requestedW / sourceWidth));
        }

        switch (options.Mode)
        {
            case ResizeMode.Stretch:
                return new ResizePlan(requestedW, requestedH, requestedW, requestedH, 0, 0);

            case ResizeMode.Max:
            {
                (int w, int h) = ScaleToFit(sourceWidth, sourceHeight, requestedW, requestedH);
                return new ResizePlan(w, h, w, h, 0, 0);
            }

            case ResizeMode.Min:
            {
                (int w, int h) = ScaleToCover(sourceWidth, sourceHeight, requestedW, requestedH);
                return new ResizePlan(w, h, w, h, 0, 0);
            }

            case ResizeMode.Pad:
            {
                (int w, int h) = ScaleToFit(sourceWidth, sourceHeight, requestedW, requestedH);
                return new ResizePlan(
                    w, h, requestedW, requestedH,
                    FrameOps.AnchorOffset(options.Position, horizontal: true, requestedW, w),
                    FrameOps.AnchorOffset(options.Position, horizontal: false, requestedH, h));
            }

            case ResizeMode.BoxPad:
            {
                (int w, int h) = sourceWidth <= requestedW && sourceHeight <= requestedH
                    ? (sourceWidth, sourceHeight)
                    : ScaleToFit(sourceWidth, sourceHeight, requestedW, requestedH);
                return new ResizePlan(
                    w, h, requestedW, requestedH,
                    FrameOps.AnchorOffset(options.Position, horizontal: true, requestedW, w),
                    FrameOps.AnchorOffset(options.Position, horizontal: false, requestedH, h));
            }

            case ResizeMode.Crop:
            {
                (int w, int h) = ScaleToCover(sourceWidth, sourceHeight, requestedW, requestedH);
                w = Math.Max(w, requestedW);
                h = Math.Max(h, requestedH);
                int offsetX;
                int offsetY;
                if (options.CenterCoordinates is PointF center)
                {
                    // Put the requested source point at the canvas centre, then clamp so the canvas stays covered.
                    offsetX = (int)Math.Round((requestedW / 2.0) - (Math.Clamp(center.X, 0f, 1f) * w));
                    offsetY = (int)Math.Round((requestedH / 2.0) - (Math.Clamp(center.Y, 0f, 1f) * h));
                    offsetX = Math.Clamp(offsetX, Math.Min(0, requestedW - w), 0);
                    offsetY = Math.Clamp(offsetY, Math.Min(0, requestedH - h), 0);
                }
                else
                {
                    offsetX = FrameOps.AnchorOffset(options.Position, horizontal: true, requestedW, w);
                    offsetY = FrameOps.AnchorOffset(options.Position, horizontal: false, requestedH, h);
                }

                return new ResizePlan(w, h, requestedW, requestedH, offsetX, offsetY);
            }

            case ResizeMode.Manual:
            {
                Rectangle target = options.TargetRectangle;
                if (target.Width <= 0 || target.Height <= 0)
                {
                    throw new ArgumentException("ResizeMode.Manual requires a TargetRectangle with a positive width and height.", nameof(options));
                }

                return new ResizePlan(target.Width, target.Height, requestedW, requestedH, target.X, target.Y);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Mode, "Unknown resize mode.");
        }
    }

    /// <summary>The largest size with the source aspect ratio that fits inside the box.</summary>
    private static (int Width, int Height) ScaleToFit(int sourceWidth, int sourceHeight, int boxWidth, int boxHeight)
    {
        double scale = Math.Min((double)boxWidth / sourceWidth, (double)boxHeight / sourceHeight);
        return (Math.Max(1, (int)Math.Round(sourceWidth * scale)), Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }

    /// <summary>The smallest size with the source aspect ratio that covers the box.</summary>
    private static (int Width, int Height) ScaleToCover(int sourceWidth, int sourceHeight, int boxWidth, int boxHeight)
    {
        double scale = Math.Max((double)boxWidth / sourceWidth, (double)boxHeight / sourceHeight);
        return (Math.Max(1, (int)Math.Round(sourceWidth * scale)), Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }

    private static byte ClampByte(float value) => (byte)Math.Clamp((int)(value + 0.5f), 0, 255);
}

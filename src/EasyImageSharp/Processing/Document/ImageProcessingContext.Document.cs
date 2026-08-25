using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Document-imaging operations.</summary>
internal sealed partial class ImageProcessingContext<TPixel>
{
    // ----- Local thresholds -----

    public IImageProcessingContext NiblackThreshold(int windowSize, float k)
        => this.PerFrameInPlace(frame => LocalThresholdOps.Apply(frame, LocalThresholdOps.Kind.Niblack, windowSize, k));

    public IImageProcessingContext WolfJolionThreshold(int windowSize, float k)
        => this.PerFrameInPlace(frame => LocalThresholdOps.Apply(frame, LocalThresholdOps.Kind.WolfJolion, windowSize, k));

    public IImageProcessingContext PhansalkarThreshold(int windowSize, float k, float p, float q)
        => this.PerFrameInPlace(frame => LocalThresholdOps.Apply(frame, LocalThresholdOps.Kind.Phansalkar, windowSize, k, p, q));

    public IImageProcessingContext NickThreshold(int windowSize, float k)
        => this.PerFrameInPlace(frame => LocalThresholdOps.Apply(frame, LocalThresholdOps.Kind.Nick, windowSize, k));

    public IImageProcessingContext Binarize(BinarizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return this.PerFrameInPlace(frame =>
        {
            BinarizeMethod method = options.Method;
            if (method == BinarizeMethod.Auto)
            {
                byte[] luminance = DocumentPlanes.Luminance(frame);
                method = LocalThresholdOps.ChooseMethod(luminance, frame.Width, frame.Height, options, out _);
            }

            // Each local formula has its own usual k (different signs and magnitudes), so the effective value
            // comes from KFor: the caller's K when they set one, otherwise this method's documented default.
            float k = options.KFor(method);
            switch (method)
            {
                case BinarizeMethod.Otsu:
                    FilterOps.OtsuThreshold(frame);
                    break;
                case BinarizeMethod.Sauvola:
                    FilterOps.SauvolaThreshold(frame, options.WindowSize, k);
                    break;
                case BinarizeMethod.Niblack:
                    LocalThresholdOps.Apply(frame, LocalThresholdOps.Kind.Niblack, options.WindowSize, k);
                    break;
                case BinarizeMethod.WolfJolion:
                    LocalThresholdOps.Apply(frame, LocalThresholdOps.Kind.WolfJolion, options.WindowSize, k);
                    break;
                case BinarizeMethod.Phansalkar:
                    LocalThresholdOps.Apply(frame, LocalThresholdOps.Kind.Phansalkar, options.WindowSize, k);
                    break;
                case BinarizeMethod.Nick:
                    LocalThresholdOps.Apply(frame, LocalThresholdOps.Kind.Nick, options.WindowSize, k);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(options), options.Method, "Unknown binarisation method.");
            }
        });
    }

    // ----- Morphology -----

    public IImageProcessingContext Erode(StructuringElement element) => this.Morphology(element, MorphologyOps.MorphologyKind.Erode);

    public IImageProcessingContext Dilate(StructuringElement element) => this.Morphology(element, MorphologyOps.MorphologyKind.Dilate);

    public IImageProcessingContext Open(StructuringElement element) => this.Morphology(element, MorphologyOps.MorphologyKind.Open);

    public IImageProcessingContext Close(StructuringElement element) => this.Morphology(element, MorphologyOps.MorphologyKind.Close);

    public IImageProcessingContext TopHat(StructuringElement element) => this.Morphology(element, MorphologyOps.MorphologyKind.TopHat);

    public IImageProcessingContext BlackHat(StructuringElement element) => this.Morphology(element, MorphologyOps.MorphologyKind.BlackHat);

    private IImageProcessingContext Morphology(StructuringElement element, MorphologyOps.MorphologyKind kind)
    {
        ArgumentNullException.ThrowIfNull(element);
        return this.PerFrameInPlace(frame => MorphologyOps.ApplyToFrame(frame, element, kind));
    }

    public IImageProcessingContext Despeckle(int maxArea)
    {
        if (maxArea < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxArea), maxArea, "Maximum speck area must be non-negative.");
        }

        return this.PerFrameInPlace(frame => CleanupOps.Despeckle(frame, maxArea));
    }

    public IImageProcessingContext Thin()
        => this.PerFrameInPlace(frame =>
        {
            byte[] mask = DocumentPlanes.InkMask(frame);
            byte[] thinned = MorphologyOps.ZhangSuenThin(mask, frame.Width, frame.Height);
            DocumentPlanes.WriteBinary(frame, thinned);
        });

    // ----- Connected components -----

    public IImageProcessingContext RemoveSmallObjects(int minArea)
    {
        if (minArea < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minArea), minArea, "Minimum area must be non-negative.");
        }

        return this.PerFrameInPlace(frame => CleanupOps.RemoveSmallObjects(frame, minArea));
    }

    public IImageProcessingContext KeepLargestComponent() => this.PerFrameInPlace(CleanupOps.KeepLargestComponent);

    public IImageProcessingContext FillHoles() => this.PerFrameInPlace(CleanupOps.FillHoles);

    // ----- Illumination -----

    public IImageProcessingContext BackgroundNormalize(int backgroundRadius)
    {
        if (backgroundRadius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(backgroundRadius), backgroundRadius, "Radius must be non-negative.");
        }

        return this.PerFrameInPlace(frame => IlluminationOps.BackgroundNormalize(frame, backgroundRadius));
    }

    public IImageProcessingContext RemoveShadows() => this.PerFrameInPlace(IlluminationOps.RemoveShadows);

    public IImageProcessingContext ContrastStretch(float lowPercentile, float highPercentile)
        => this.PerFrameInPlace(frame => IlluminationOps.ContrastStretch(frame, lowPercentile, highPercentile));

    public IImageProcessingContext AutoLevels() => this.PerFrameInPlace(IlluminationOps.AutoLevels);

    public IImageProcessingContext Gamma(float gamma) => this.PerFrameInPlace(frame => IlluminationOps.Gamma(frame, gamma));

    public IImageProcessingContext Normalize() => this.PerFrameInPlace(IlluminationOps.Normalize);

    // ----- Geometry -----

    public float DetectSkew(float maxAngleDegrees)
        => (float)SkewOps.Estimate(this.image.Frames.RootFrame, maxAngleDegrees).AngleDegrees;

    public IImageProcessingContext Deskew(DeskewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Rgba32 fill = options.FillColor.ToRgba32();
        return this.PerFrame(frame =>
        {
            if (options.Method == DeskewMethod.Projection)
            {
                double projected = DeskewOps.EstimateProjectionSkew(frame, options.MaxAngle, out bool significant);
                if (!significant || Math.Abs(projected) < options.MinAngle)
                {
                    return frame;
                }

                return FrameOps.RotateArbitrary(frame, (float)(-projected), fill);
            }

            SkewOps.SkewEstimate estimate = SkewOps.Estimate(frame, options.MaxAngle);
            if (estimate.PointCount == 0 || Math.Abs(estimate.AngleDegrees) < options.MinAngle || estimate.Strength <= 1.02)
            {
                return frame;
            }

            return FrameOps.RotateArbitrary(frame, (float)(-estimate.AngleDegrees), fill);
        });
    }

    public OrientationEstimate DetectOrientation() => OrientationOps.Estimate(this.image.Frames.RootFrame);

    public IImageProcessingContext AutoRotateDocument(float minConfidence)
    {
        OrientationEstimate estimate = this.DetectOrientation();
        if (estimate.Rotation == RotateMode.None || estimate.Confidence < minConfidence)
        {
            return this;
        }

        return this.Rotate((float)(int)estimate.Rotation);
    }

    public PointF[]? DetectPage() => PageDetection.Detect(this.image.Frames.RootFrame);

    public IImageProcessingContext CorrectPerspective(PointF[] quad, Size? targetSize)
    {
        ArgumentNullException.ThrowIfNull(quad);
        if (quad.Length != 4)
        {
            throw new ArgumentException("The quadrilateral must have exactly four corners (TL, TR, BR, BL).", nameof(quad));
        }

        Size size = targetSize ?? PerspectiveWarp.NaturalSize(quad);
        Guard.MustBePositive(size.Width, nameof(targetSize));
        Guard.MustBePositive(size.Height, nameof(targetSize));
        return this.PerFrame(frame => PerspectiveWarp.Warp(frame, quad, size.Width, size.Height));
    }

    public IImageProcessingContext AutoCropPage()
    {
        PointF[]? quad = this.DetectPage();
        if (quad is null)
        {
            return this;
        }

        Size size = PerspectiveWarp.NaturalSize(quad);
        return this.PerFrame(frame =>
        {
            ImageFrame<TPixel> warped = PerspectiveWarp.Warp(frame, quad, size.Width, size.Height);
            byte[] luminance = DocumentPlanes.Luminance(warped);
            Rectangle trim = CleanupOps.TrimDarkMargins(luminance, warped.Width, warped.Height);
            if (trim.Width <= 0 || trim.Height <= 0 || (trim.Width == warped.Width && trim.Height == warped.Height))
            {
                return warped;
            }

            return FrameOps.Crop(warped, trim);
        });
    }

    public IImageProcessingContext RemoveBorders() => this.PerFrameInPlace(CleanupOps.RemoveBorders);

    // ----- Cleanup -----

    public IImageProcessingContext RemoveLines(int minLength, LineOrientation orientation)
        => this.PerFrameInPlace(frame => CleanupOps.RemoveLines(frame, minLength, orientation));

    public IImageProcessingContext RemoveHolePunches() => this.PerFrameInPlace(CleanupOps.RemoveHolePunches);

    public IImageProcessingContext RemoveMarginNoise() => this.PerFrameInPlace(CleanupOps.RemoveMarginNoise);

    // ----- Layout -----

    public TextRegions SegmentText() => LayoutOps.SegmentText(this.image.Frames.RootFrame);

    public IReadOnlyList<Rectangle> SegmentTextLines()
        => this.SegmentText().Lines.Select(line => line.Bounds).ToArray();

    public IReadOnlyList<Rectangle> SegmentWords() => this.SegmentText().Words;

    public IImageProcessingContext NormalizeDpi(float sourceDpi, int targetDpi)
    {
        if (!(sourceDpi > 0) || float.IsInfinity(sourceDpi))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceDpi), sourceDpi, "Source DPI must be positive and finite.");
        }

        Guard.MustBePositive(targetDpi, nameof(targetDpi));
        double scale = targetDpi / (double)sourceDpi;
        if (Math.Abs(scale - 1) < 1e-6)
        {
            return this;
        }

        return this.PerFrame(frame =>
        {
            int width = Math.Max(1, (int)Math.Round(frame.Width * scale));
            int height = Math.Max(1, (int)Math.Round(frame.Height * scale));
            return FrameOps.Resize(frame, width, height, KnownResamplers.Bicubic);
        });
    }

    public IImageProcessingContext PrepareForOcr(OcrPreprocessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.Grayscale();
        if (options.NormalizeBackground)
        {
            this.BackgroundNormalize(options.BackgroundRadius);
        }

        if (options.MedianDenoise)
        {
            this.MedianBlur(1);
        }

        if (options.Deskew)
        {
            this.Deskew(new DeskewOptions { Method = DeskewMethod.Hough, MaxAngle = options.DeskewMaxAngle });
        }

        if (options.Binarize)
        {
            this.Binarize(options.BinarizeOptions ?? new BinarizeOptions());
            if (options.DespeckleMaxArea > 0)
            {
                this.Despeckle(options.DespeckleMaxArea);
            }
        }

        return this;
    }

    // ----- Helpers -----

    private IImageProcessingContext PerFrameInPlace(Action<ImageFrame<TPixel>> operation)
        => this.PerFrame(frame =>
        {
            operation(frame);
            return frame;
        });
}

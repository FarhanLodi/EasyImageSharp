using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Convenience overloads for the document-imaging operations.</summary>
public static partial class ProcessingExtensions
{
    // ----- Local thresholds with the usual parameters -----

    /// <summary>Niblack threshold with a 25 px window and k = -0.2.</summary>
    public static IImageProcessingContext NiblackThreshold(this IImageProcessingContext context, int windowSize = 25, float k = -0.2f)
        => context.NiblackThreshold(windowSize, k);

    /// <summary>Wolf-Jolion threshold with a 25 px window and k = 0.5.</summary>
    public static IImageProcessingContext WolfJolionThreshold(this IImageProcessingContext context, int windowSize = 25, float k = 0.5f)
        => context.WolfJolionThreshold(windowSize, k);

    /// <summary>Phansalkar threshold with a 25 px window, k = 0.25, p = 2 and q = 10.</summary>
    public static IImageProcessingContext PhansalkarThreshold(
        this IImageProcessingContext context, int windowSize = 25, float k = 0.25f, float p = 2f, float q = 10f)
        => context.PhansalkarThreshold(windowSize, k, p, q);

    /// <summary>NICK threshold with a 25 px window and k = -0.1.</summary>
    public static IImageProcessingContext NickThreshold(this IImageProcessingContext context, int windowSize = 25, float k = -0.1f)
        => context.NickThreshold(windowSize, k);

    /// <summary>Automatic binarisation: Otsu for crisp, evenly lit pages, Sauvola otherwise (see <see cref="BinarizeOptions"/>).</summary>
    public static IImageProcessingContext Binarize(this IImageProcessingContext context)
        => context.Binarize(new BinarizeOptions());

    // ----- Morphology with square elements -----

    /// <summary>Erosion with a (2r+1) x (2r+1) square.</summary>
    public static IImageProcessingContext Erode(this IImageProcessingContext context, int radius)
        => context.Erode(StructuringElement.Square(radius));

    /// <summary>Dilation with a (2r+1) x (2r+1) square.</summary>
    public static IImageProcessingContext Dilate(this IImageProcessingContext context, int radius)
        => context.Dilate(StructuringElement.Square(radius));

    /// <summary>Opening with a (2r+1) x (2r+1) square.</summary>
    public static IImageProcessingContext Open(this IImageProcessingContext context, int radius)
        => context.Open(StructuringElement.Square(radius));

    /// <summary>Closing with a (2r+1) x (2r+1) square.</summary>
    public static IImageProcessingContext Close(this IImageProcessingContext context, int radius)
        => context.Close(StructuringElement.Square(radius));

    /// <summary>Top-hat with a (2r+1) x (2r+1) square.</summary>
    public static IImageProcessingContext TopHat(this IImageProcessingContext context, int radius)
        => context.TopHat(StructuringElement.Square(radius));

    /// <summary>Black-hat with a (2r+1) x (2r+1) square.</summary>
    public static IImageProcessingContext BlackHat(this IImageProcessingContext context, int radius)
        => context.BlackHat(StructuringElement.Square(radius));

    // ----- Illumination -----

    /// <summary>Background normalisation with an automatic radius (about 1/25 of the smaller dimension).</summary>
    public static IImageProcessingContext BackgroundNormalize(this IImageProcessingContext context)
        => context.BackgroundNormalize(0);

    /// <summary>Contrast stretch between the 0.5th and 99.5th luminance percentiles.</summary>
    public static IImageProcessingContext ContrastStretch(this IImageProcessingContext context)
        => context.ContrastStretch(0.5f, 99.5f);

    // ----- Geometry -----

    /// <summary>Estimates the clockwise skew within +/-15 degrees.</summary>
    public static float DetectSkew(this IImageProcessingContext context) => context.DetectSkew(15f);

    /// <summary>Deskews with the default <see cref="DeskewOptions"/> (Hough estimator, +/-15 degrees, white fill).</summary>
    public static IImageProcessingContext Deskew(this IImageProcessingContext context, DeskewMethod method)
        => context.Deskew(new DeskewOptions { Method = method });

    /// <summary>Applies the estimated orientation when its confidence is at least 0.2.</summary>
    public static IImageProcessingContext AutoRotateDocument(this IImageProcessingContext context)
        => context.AutoRotateDocument(0.2f);

    /// <summary>Warps the quadrilateral onto a rectangle of its natural size.</summary>
    public static IImageProcessingContext CorrectPerspective(this IImageProcessingContext context, PointF[] quad)
        => context.CorrectPerspective(quad, null);

    // ----- Cleanup -----

    /// <summary>Removes horizontal and vertical rules of at least <paramref name="minLength"/> pixels.</summary>
    public static IImageProcessingContext RemoveLines(this IImageProcessingContext context, int minLength)
        => context.RemoveLines(minLength, LineOrientation.Both);

    // ----- Layout / OCR -----

    /// <summary>The OCR preset with default <see cref="OcrPreprocessOptions"/>.</summary>
    public static IImageProcessingContext PrepareForOcr(this IImageProcessingContext context)
        => context.PrepareForOcr(new OcrPreprocessOptions());

    // ----- Image-level queries (root frame, no mutation) -----

    /// <summary>Estimates the clockwise skew of the root frame within +/-<paramref name="maxAngleDegrees"/> degrees.</summary>
    public static float DetectSkew<TPixel>(this Image<TPixel> image, float maxAngleDegrees = 15f)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        image.EnsureNotDisposed();
        return (float)SkewOps.Estimate(image.Frames.RootFrame, maxAngleDegrees).AngleDegrees;
    }

    /// <summary>Estimates the quarter-turn orientation of the root frame.</summary>
    public static OrientationEstimate DetectOrientation<TPixel>(this Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        image.EnsureNotDisposed();
        return OrientationOps.Estimate(image.Frames.RootFrame);
    }

    /// <summary>Finds the page quadrilateral in the root frame (TL, TR, BR, BL) or <see langword="null"/>.</summary>
    public static PointF[]? DetectPage<TPixel>(this Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        image.EnsureNotDisposed();
        return PageDetection.Detect(image.Frames.RootFrame);
    }

    /// <summary>Segments the root frame into text lines and words.</summary>
    public static TextRegions SegmentText<TPixel>(this Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        image.EnsureNotDisposed();
        return LayoutOps.SegmentText(image.Frames.RootFrame);
    }
}

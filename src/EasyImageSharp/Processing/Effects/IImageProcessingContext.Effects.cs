namespace EasyImageSharp.Processing;

/// <summary>Colour, convolution, compositing and histogram operations.</summary>
public partial interface IImageProcessingContext
{
    // ----- Colour matrices -----

    /// <summary>Applies a <see cref="ColorMatrix"/> to every pixel (straight RGBA in 0-1, clamped).</summary>
    IImageProcessingContext Filter(ColorMatrix matrix);

    /// <summary>Applies a <see cref="ColorMatrix"/> to the pixels inside <paramref name="rectangle"/>.</summary>
    IImageProcessingContext Filter(ColorMatrix matrix, Rectangle rectangle);

    /// <summary>Converts the pixels inside <paramref name="rectangle"/> to grayscale (BT.709 luminance).</summary>
    IImageProcessingContext Grayscale(Rectangle rectangle);

    /// <summary>Inverts the colours inside <paramref name="rectangle"/>.</summary>
    IImageProcessingContext Invert(Rectangle rectangle);

    /// <summary>Multiplies channel values inside <paramref name="rectangle"/>; see <see cref="Brightness(float)"/>.</summary>
    IImageProcessingContext Brightness(float amount, Rectangle rectangle);

    /// <summary>Scales contrast inside <paramref name="rectangle"/>; see <see cref="Contrast(float)"/>.</summary>
    IImageProcessingContext Contrast(float amount, Rectangle rectangle);

    // ----- Blurs -----

    /// <summary>Gaussian blur of the pixels inside <paramref name="rectangle"/>; the region is treated as its own image for edge handling.</summary>
    IImageProcessingContext GaussianBlur(float sigma, Rectangle rectangle);

    /// <summary>Gaussian unsharp mask of the pixels inside <paramref name="rectangle"/>.</summary>
    IImageProcessingContext GaussianSharpen(float sigma, Rectangle rectangle);

    /// <summary>Box blur with a <c>(2 radius + 1)</c> square kernel and edge replication.</summary>
    IImageProcessingContext BoxBlur(int radius);

    /// <summary>Box blur of the pixels inside <paramref name="rectangle"/>.</summary>
    IImageProcessingContext BoxBlur(int radius, Rectangle rectangle);

    /// <summary>
    /// Bokeh (lens) blur with a disc kernel of the given <paramref name="radius"/>, approximated by
    /// <paramref name="components"/> (1-6) complex Gaussian components; <paramref name="gamma"/> (&gt;= 1)
    /// controls how strongly highlights bloom.
    /// </summary>
    IImageProcessingContext BokehBlur(int radius, int components, float gamma);

    /// <summary>Bokeh blur of the pixels inside <paramref name="rectangle"/>.</summary>
    IImageProcessingContext BokehBlur(int radius, int components, float gamma, Rectangle rectangle);

    // ----- Convolution / edge detection -----

    /// <summary>
    /// Convolves the image with a row-major <paramref name="width"/> x <paramref name="height"/> kernel using
    /// edge replication. With <paramref name="preserveAlpha"/> only the colour channels are convolved and the
    /// original alpha is kept; otherwise all four channels are convolved.
    /// </summary>
    IImageProcessingContext Convolve(ReadOnlyMemory<float> kernel, int width, int height, bool preserveAlpha);

    /// <summary>Convolves the image with a separable kernel: <paramref name="kernelX"/> along rows then <paramref name="kernelY"/> along columns.</summary>
    IImageProcessingContext Convolve(ReadOnlyMemory<float> kernelX, ReadOnlyMemory<float> kernelY, bool preserveAlpha);

    /// <summary>Detects edges with a single-kernel operator (e.g. Laplacian); optionally converts to grayscale first.</summary>
    IImageProcessingContext DetectEdges(EdgeDetectorKernel kernel, bool grayscale);

    /// <summary>Detects edges with a gradient-pair operator (result is <c>sqrt(gx² + gy²)</c>); optionally converts to grayscale first.</summary>
    IImageProcessingContext DetectEdges(EdgeDetector2DKernel kernel, bool grayscale);

    /// <summary>Detects edges with a compass operator (result is the maximum directional response); optionally converts to grayscale first.</summary>
    IImageProcessingContext DetectEdges(EdgeDetectorCompassKernel kernel, bool grayscale);

    // ----- Artistic / radial -----

    /// <summary>Oil painting effect with <paramref name="levels"/> intensity bins and a brush of radius <paramref name="brushSize"/>.</summary>
    IImageProcessingContext OilPaint(int levels, int brushSize);

    /// <summary>Oil painting effect inside <paramref name="rectangle"/>.</summary>
    IImageProcessingContext OilPaint(int levels, int brushSize, Rectangle rectangle);

    /// <summary>Replaces every <paramref name="size"/> x <paramref name="size"/> block with its average colour.</summary>
    IImageProcessingContext Pixelate(int size);

    /// <summary>Pixelates the pixels inside <paramref name="rectangle"/>.</summary>
    IImageProcessingContext Pixelate(int size, Rectangle rectangle);

    /// <summary>
    /// Darkens (or tints) towards the edges: blends <paramref name="color"/> with a weight rising from 0 at
    /// the centre to 1 at the corners of the ellipse with radii <paramref name="radiusX"/> / <paramref name="radiusY"/>
    /// (0 or negative selects half the image size), scaled by <see cref="GraphicsOptions.BlendPercentage"/>.
    /// </summary>
    IImageProcessingContext Vignette(Color color, float radiusX, float radiusY, GraphicsOptions options);

    /// <summary>Vignette centred on and confined to <paramref name="rectangle"/>.</summary>
    IImageProcessingContext Vignette(Color color, float radiusX, float radiusY, Rectangle rectangle, GraphicsOptions options);

    /// <summary>
    /// Radial glow: blends <paramref name="color"/> with a weight falling from 1 at the centre to 0 at
    /// <paramref name="radius"/> (0 or negative selects half the smaller image dimension), scaled by
    /// <see cref="GraphicsOptions.BlendPercentage"/>.
    /// </summary>
    IImageProcessingContext Glow(Color color, float radius, GraphicsOptions options);

    /// <summary>Glow centred on and confined to <paramref name="rectangle"/>.</summary>
    IImageProcessingContext Glow(Color color, float radius, Rectangle rectangle, GraphicsOptions options);

    /// <summary>Rebuilds every frame by moving each pixel to the position given by <paramref name="swizzler"/>.</summary>
    IImageProcessingContext Swizzle(ISwizzler swizzler);

    /// <summary>Equalises the luminance histogram (global or adaptive) while preserving chroma.</summary>
    IImageProcessingContext HistogramEqualization(HistogramEqualizationOptions options);

    // ----- Thresholds -----

    /// <summary>
    /// Sets pixels whose <paramref name="mode"/> metric is at least <paramref name="threshold"/> (0-1) to
    /// <paramref name="upperColor"/> and all others to <paramref name="lowerColor"/>.
    /// </summary>
    IImageProcessingContext BinaryThreshold(float threshold, Color upperColor, Color lowerColor, BinaryThresholdMode mode);

    /// <summary>Binary threshold confined to <paramref name="rectangle"/>.</summary>
    IImageProcessingContext BinaryThreshold(float threshold, Color upperColor, Color lowerColor, BinaryThresholdMode mode, Rectangle rectangle);

    /// <summary>
    /// Bradley's local-mean adaptive threshold with an automatic window size, writing
    /// <paramref name="upperColor"/> / <paramref name="lowerColor"/>.
    /// </summary>
    IImageProcessingContext AdaptiveThreshold(Color upperColor, Color lowerColor, float thresholdLimit);

    /// <summary>
    /// Bradley's local-mean adaptive threshold confined to <paramref name="rectangle"/> (window size derived
    /// from the region), writing <paramref name="upperColor"/> / <paramref name="lowerColor"/>.
    /// </summary>
    IImageProcessingContext AdaptiveThreshold(Color upperColor, Color lowerColor, float thresholdLimit, Rectangle rectangle);

    // ----- Compositing -----

    /// <summary>
    /// Composites <paramref name="sourceRectangle"/> of <paramref name="source"/> onto every frame at
    /// <paramref name="location"/> using the given blend and Porter-Duff modes; <paramref name="opacity"/>
    /// (0-1) scales the source alpha. Parts outside either image are clipped.
    /// </summary>
    IImageProcessingContext DrawImage(
        Image source,
        Point location,
        Rectangle sourceRectangle,
        PixelColorBlendingMode colorBlending,
        PixelAlphaCompositionMode alphaComposition,
        float opacity);
}

namespace EasyImageSharp.Processing;

/// <summary>Convenience overloads for the colour, convolution, compositing and histogram operations.</summary>
public static partial class ProcessingExtensions
{
    // ----- Colour matrix filters -----

    /// <summary>Converts to grayscale with the given luma coefficients.</summary>
    public static IImageProcessingContext Grayscale(this IImageProcessingContext context, GrayscaleMode mode)
        => context.Filter(KnownFilterMatrices.CreateGrayscaleFilter(mode, 1f));

    /// <summary>Converts towards grayscale with the given luma coefficients; <paramref name="amount"/> in 0-1 sets the strength.</summary>
    public static IImageProcessingContext Grayscale(this IImageProcessingContext context, GrayscaleMode mode, float amount)
        => context.Filter(KnownFilterMatrices.CreateGrayscaleFilter(mode, amount));

    /// <summary>Converts the pixels inside <paramref name="rectangle"/> towards grayscale.</summary>
    public static IImageProcessingContext Grayscale(this IImageProcessingContext context, GrayscaleMode mode, float amount, Rectangle rectangle)
        => context.Filter(KnownFilterMatrices.CreateGrayscaleFilter(mode, amount), rectangle);

    /// <summary>Applies a high-contrast black and white filter.</summary>
    public static IImageProcessingContext BlackWhite(this IImageProcessingContext context)
        => context.Filter(KnownFilterMatrices.BlackWhiteFilter);

    /// <summary>Applies a high-contrast black and white filter inside <paramref name="rectangle"/>.</summary>
    public static IImageProcessingContext BlackWhite(this IImageProcessingContext context, Rectangle rectangle)
        => context.Filter(KnownFilterMatrices.BlackWhiteFilter, rectangle);

    /// <summary>Rotates the hue of every pixel by <paramref name="degrees"/>.</summary>
    public static IImageProcessingContext Hue(this IImageProcessingContext context, float degrees)
        => context.Filter(KnownFilterMatrices.CreateHueFilter(degrees));

    /// <summary>Rotates the hue inside <paramref name="rectangle"/>.</summary>
    public static IImageProcessingContext Hue(this IImageProcessingContext context, float degrees, Rectangle rectangle)
        => context.Filter(KnownFilterMatrices.CreateHueFilter(degrees), rectangle);

    /// <summary>Scales saturation: 1 keeps the image unchanged, 0 is grayscale, &gt; 1 over-saturates.</summary>
    public static IImageProcessingContext Saturate(this IImageProcessingContext context, float amount)
        => context.Filter(KnownFilterMatrices.CreateSaturateFilter(amount));

    /// <summary>Scales saturation inside <paramref name="rectangle"/>.</summary>
    public static IImageProcessingContext Saturate(this IImageProcessingContext context, float amount, Rectangle rectangle)
        => context.Filter(KnownFilterMatrices.CreateSaturateFilter(amount), rectangle);

    /// <summary>Adds <c>amount - 1</c> to every colour channel: 1 keeps the image unchanged, 0 is black, 2 is white.</summary>
    public static IImageProcessingContext Lightness(this IImageProcessingContext context, float amount)
        => context.Filter(KnownFilterMatrices.CreateLightnessFilter(amount));

    /// <summary>Adjusts lightness inside <paramref name="rectangle"/>.</summary>
    public static IImageProcessingContext Lightness(this IImageProcessingContext context, float amount, Rectangle rectangle)
        => context.Filter(KnownFilterMatrices.CreateLightnessFilter(amount), rectangle);

    /// <summary>Scales alpha by <paramref name="amount"/> in 0-1.</summary>
    public static IImageProcessingContext Opacity(this IImageProcessingContext context, float amount)
        => context.Filter(KnownFilterMatrices.CreateOpacityFilter(amount));

    /// <summary>Scales alpha inside <paramref name="rectangle"/>.</summary>
    public static IImageProcessingContext Opacity(this IImageProcessingContext context, float amount, Rectangle rectangle)
        => context.Filter(KnownFilterMatrices.CreateOpacityFilter(amount), rectangle);

    /// <summary>Applies a full-strength sepia tone.</summary>
    public static IImageProcessingContext Sepia(this IImageProcessingContext context)
        => context.Filter(KnownFilterMatrices.CreateSepiaFilter(1f));

    /// <summary>Applies a sepia tone; <paramref name="amount"/> in 0-1 sets the strength.</summary>
    public static IImageProcessingContext Sepia(this IImageProcessingContext context, float amount)
        => context.Filter(KnownFilterMatrices.CreateSepiaFilter(amount));

    /// <summary>Applies a sepia tone inside <paramref name="rectangle"/>.</summary>
    public static IImageProcessingContext Sepia(this IImageProcessingContext context, float amount, Rectangle rectangle)
        => context.Filter(KnownFilterMatrices.CreateSepiaFilter(amount), rectangle);

    /// <summary>Applies a Kodachrome-style colour filter.</summary>
    public static IImageProcessingContext Kodachrome(this IImageProcessingContext context)
        => context.Filter(KnownFilterMatrices.KodachromeFilter);

    /// <summary>Applies a Kodachrome-style colour filter inside <paramref name="rectangle"/>.</summary>
    public static IImageProcessingContext Kodachrome(this IImageProcessingContext context, Rectangle rectangle)
        => context.Filter(KnownFilterMatrices.KodachromeFilter, rectangle);

    /// <summary>Applies a Lomograph-style look: a colour filter followed by a dark green vignette.</summary>
    public static IImageProcessingContext Lomograph(this IImageProcessingContext context)
        => context.Filter(KnownFilterMatrices.LomographFilter).Vignette(new Color(0, 10, 0));

    /// <summary>Applies a Lomograph-style look inside <paramref name="rectangle"/>.</summary>
    public static IImageProcessingContext Lomograph(this IImageProcessingContext context, Rectangle rectangle)
        => context.Filter(KnownFilterMatrices.LomographFilter, rectangle).Vignette(new Color(0, 10, 0), rectangle);

    /// <summary>Applies a Polaroid-style look: a colour filter followed by a warm vignette and a peach glow.</summary>
    public static IImageProcessingContext Polaroid(this IImageProcessingContext context)
        => context.Filter(KnownFilterMatrices.PolaroidFilter)
            .Vignette(new Color(102, 34, 0))
            .Glow(new Color(255, 153, 102), 0f, new GraphicsOptions { BlendPercentage = 0.5f });

    /// <summary>Applies a Polaroid-style look inside <paramref name="rectangle"/>.</summary>
    public static IImageProcessingContext Polaroid(this IImageProcessingContext context, Rectangle rectangle)
        => context.Filter(KnownFilterMatrices.PolaroidFilter, rectangle)
            .Vignette(new Color(102, 34, 0), rectangle)
            .Glow(new Color(255, 153, 102), 0f, rectangle, new GraphicsOptions { BlendPercentage = 0.5f });

    /// <summary>Simulates a colour vision deficiency.</summary>
    public static IImageProcessingContext ColorBlindness(this IImageProcessingContext context, ColorBlindnessMode mode)
        => context.Filter(KnownFilterMatrices.GetColorBlindnessFilter(mode));

    /// <summary>Simulates a colour vision deficiency inside <paramref name="rectangle"/>.</summary>
    public static IImageProcessingContext ColorBlindness(this IImageProcessingContext context, ColorBlindnessMode mode, Rectangle rectangle)
        => context.Filter(KnownFilterMatrices.GetColorBlindnessFilter(mode), rectangle);

    // ----- Vignette / glow -----

    /// <summary>Applies a black vignette reaching the image corners.</summary>
    public static IImageProcessingContext Vignette(this IImageProcessingContext context)
        => context.Vignette(Color.Black, 0f, 0f, GraphicsOptions.Default);

    /// <summary>Applies a vignette of the given colour reaching the image corners.</summary>
    public static IImageProcessingContext Vignette(this IImageProcessingContext context, Color color)
        => context.Vignette(color, 0f, 0f, GraphicsOptions.Default);

    /// <summary>Applies a black vignette with the given ellipse radii.</summary>
    public static IImageProcessingContext Vignette(this IImageProcessingContext context, float radiusX, float radiusY)
        => context.Vignette(Color.Black, radiusX, radiusY, GraphicsOptions.Default);

    /// <summary>Applies a vignette of the given colour and ellipse radii.</summary>
    public static IImageProcessingContext Vignette(this IImageProcessingContext context, Color color, float radiusX, float radiusY)
        => context.Vignette(color, radiusX, radiusY, GraphicsOptions.Default);

    /// <summary>Applies a black vignette with the given blend options.</summary>
    public static IImageProcessingContext Vignette(this IImageProcessingContext context, GraphicsOptions options)
        => context.Vignette(Color.Black, 0f, 0f, options);

    /// <summary>Applies a vignette of the given colour centred on and confined to <paramref name="rectangle"/>.</summary>
    public static IImageProcessingContext Vignette(this IImageProcessingContext context, Color color, Rectangle rectangle)
        => context.Vignette(color, 0f, 0f, rectangle, GraphicsOptions.Default);

    /// <summary>Applies a black radial glow with radius half the smaller image dimension.</summary>
    public static IImageProcessingContext Glow(this IImageProcessingContext context)
        => context.Glow(Color.Black, 0f, GraphicsOptions.Default);

    /// <summary>Applies a radial glow of the given colour with radius half the smaller image dimension.</summary>
    public static IImageProcessingContext Glow(this IImageProcessingContext context, Color color)
        => context.Glow(color, 0f, GraphicsOptions.Default);

    /// <summary>Applies a black radial glow of the given radius.</summary>
    public static IImageProcessingContext Glow(this IImageProcessingContext context, float radius)
        => context.Glow(Color.Black, radius, GraphicsOptions.Default);

    /// <summary>Applies a radial glow of the given colour and radius.</summary>
    public static IImageProcessingContext Glow(this IImageProcessingContext context, Color color, float radius)
        => context.Glow(color, radius, GraphicsOptions.Default);

    /// <summary>Applies a black radial glow with the given blend options.</summary>
    public static IImageProcessingContext Glow(this IImageProcessingContext context, GraphicsOptions options)
        => context.Glow(Color.Black, 0f, options);

    /// <summary>Applies a radial glow of the given colour centred on and confined to <paramref name="rectangle"/>.</summary>
    public static IImageProcessingContext Glow(this IImageProcessingContext context, Color color, Rectangle rectangle)
        => context.Glow(color, 0f, rectangle, GraphicsOptions.Default);

    // ----- Blurs / artistic -----

    /// <summary>Box blur with radius 7.</summary>
    public static IImageProcessingContext BoxBlur(this IImageProcessingContext context)
        => context.BoxBlur(7);

    /// <summary>Bokeh blur with radius 32, two components and gamma 3.</summary>
    public static IImageProcessingContext BokehBlur(this IImageProcessingContext context)
        => context.BokehBlur(32, 2, 3f);

    /// <summary>Bokeh blur with the given radius, two components and gamma 3.</summary>
    public static IImageProcessingContext BokehBlur(this IImageProcessingContext context, int radius)
        => context.BokehBlur(radius, 2, 3f);

    /// <summary>Oil painting effect with 10 levels and a brush radius of 15.</summary>
    public static IImageProcessingContext OilPaint(this IImageProcessingContext context)
        => context.OilPaint(10, 15);

    /// <summary>Pixelates with 4x4 blocks.</summary>
    public static IImageProcessingContext Pixelate(this IImageProcessingContext context)
        => context.Pixelate(4);

    // ----- Convolution / edge detection -----

    /// <summary>Convolves with a 2-D kernel, convolving all four channels.</summary>
    public static IImageProcessingContext Convolve(this IImageProcessingContext context, ReadOnlyMemory<float> kernel, int width, int height)
        => context.Convolve(kernel, width, height, preserveAlpha: false);

    /// <summary>Convolves with a <see cref="DenseMatrix{T}"/> kernel.</summary>
    public static IImageProcessingContext Convolve(this IImageProcessingContext context, DenseMatrix<float> kernel, bool preserveAlpha = false)
        => context.Convolve(kernel.Memory, kernel.Columns, kernel.Rows, preserveAlpha);

    /// <summary>Convolves with a separable kernel pair, convolving all four channels.</summary>
    public static IImageProcessingContext Convolve(this IImageProcessingContext context, ReadOnlyMemory<float> kernelX, ReadOnlyMemory<float> kernelY)
        => context.Convolve(kernelX, kernelY, preserveAlpha: false);

    /// <summary>Detects edges with the Sobel operator after converting to grayscale.</summary>
    public static IImageProcessingContext DetectEdges(this IImageProcessingContext context)
        => context.DetectEdges(KnownEdgeDetectorKernels.Sobel, grayscale: true);

    /// <summary>Detects edges with the Sobel operator, optionally converting to grayscale first.</summary>
    public static IImageProcessingContext DetectEdges(this IImageProcessingContext context, bool grayscale)
        => context.DetectEdges(KnownEdgeDetectorKernels.Sobel, grayscale);

    /// <summary>Detects edges with a single-kernel operator after converting to grayscale.</summary>
    public static IImageProcessingContext DetectEdges(this IImageProcessingContext context, EdgeDetectorKernel kernel)
        => context.DetectEdges(kernel, grayscale: true);

    /// <summary>Detects edges with a gradient-pair operator after converting to grayscale.</summary>
    public static IImageProcessingContext DetectEdges(this IImageProcessingContext context, EdgeDetector2DKernel kernel)
        => context.DetectEdges(kernel, grayscale: true);

    /// <summary>Detects edges with a compass operator after converting to grayscale.</summary>
    public static IImageProcessingContext DetectEdges(this IImageProcessingContext context, EdgeDetectorCompassKernel kernel)
        => context.DetectEdges(kernel, grayscale: true);

    // ----- Histogram -----

    /// <summary>Global histogram equalization of luminance over 256 levels.</summary>
    public static IImageProcessingContext HistogramEqualization(this IImageProcessingContext context)
        => context.HistogramEqualization(HistogramEqualizationOptions.Default);

    // ----- Thresholds -----

    /// <summary>Binary threshold on the given metric, writing white and black.</summary>
    public static IImageProcessingContext BinaryThreshold(this IImageProcessingContext context, float threshold, BinaryThresholdMode mode)
        => context.BinaryThreshold(threshold, Color.White, Color.Black, mode);

    /// <summary>Binary luminance threshold writing the given colours.</summary>
    public static IImageProcessingContext BinaryThreshold(this IImageProcessingContext context, float threshold, Color upperColor, Color lowerColor)
        => context.BinaryThreshold(threshold, upperColor, lowerColor, BinaryThresholdMode.Luminance);

    /// <summary>Binary luminance threshold writing the given colours inside <paramref name="rectangle"/>.</summary>
    public static IImageProcessingContext BinaryThreshold(this IImageProcessingContext context, float threshold, Color upperColor, Color lowerColor, Rectangle rectangle)
        => context.BinaryThreshold(threshold, upperColor, lowerColor, BinaryThresholdMode.Luminance, rectangle);

    /// <summary>Bradley adaptive threshold (automatic window, 0.85 limit) writing the given colours.</summary>
    public static IImageProcessingContext AdaptiveThreshold(this IImageProcessingContext context, Color upperColor, Color lowerColor)
        => context.AdaptiveThreshold(upperColor, lowerColor, 0.85f);

    /// <summary>Bradley adaptive threshold (automatic window, 0.85 limit) confined to <paramref name="rectangle"/>.</summary>
    public static IImageProcessingContext AdaptiveThreshold(this IImageProcessingContext context, Color upperColor, Color lowerColor, Rectangle rectangle)
        => context.AdaptiveThreshold(upperColor, lowerColor, 0.85f, rectangle);

    // ----- Compositing -----

    /// <summary>Composites <paramref name="source"/> at <paramref name="location"/> with the given blend and Porter-Duff modes.</summary>
    public static IImageProcessingContext DrawImage(
        this IImageProcessingContext context,
        Image source,
        Point location,
        PixelColorBlendingMode colorBlending,
        PixelAlphaCompositionMode alphaComposition,
        float opacity)
    {
        ArgumentNullException.ThrowIfNull(source);
        return context.DrawImage(source, location, new Rectangle(0, 0, source.Width, source.Height), colorBlending, alphaComposition, opacity);
    }

    /// <summary>Composites <paramref name="source"/> at <paramref name="location"/> with the given blend mode and source-over composition.</summary>
    public static IImageProcessingContext DrawImage(this IImageProcessingContext context, Image source, Point location, PixelColorBlendingMode colorBlending, float opacity)
        => context.DrawImage(source, location, colorBlending, PixelAlphaCompositionMode.SrcOver, opacity);

    /// <summary>Composites <paramref name="source"/> at the top-left corner with the given blend mode and source-over composition.</summary>
    public static IImageProcessingContext DrawImage(this IImageProcessingContext context, Image source, PixelColorBlendingMode colorBlending, float opacity)
        => context.DrawImage(source, Point.Empty, colorBlending, PixelAlphaCompositionMode.SrcOver, opacity);

    /// <summary>Composites <paramref name="source"/> at <paramref name="location"/> using <paramref name="options"/>.</summary>
    public static IImageProcessingContext DrawImage(this IImageProcessingContext context, Image source, Point location, GraphicsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return context.DrawImage(source, location, options.ColorBlendingMode, options.AlphaCompositionMode, options.BlendPercentage);
    }

    /// <summary>Composites <paramref name="source"/> at the top-left corner using <paramref name="options"/>.</summary>
    public static IImageProcessingContext DrawImage(this IImageProcessingContext context, Image source, GraphicsOptions options)
        => context.DrawImage(source, Point.Empty, options);

    /// <summary>Composites <paramref name="sourceRectangle"/> of <paramref name="source"/> at <paramref name="location"/> using <paramref name="options"/>.</summary>
    public static IImageProcessingContext DrawImage(this IImageProcessingContext context, Image source, Point location, Rectangle sourceRectangle, GraphicsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return context.DrawImage(source, location, sourceRectangle, options.ColorBlendingMode, options.AlphaCompositionMode, options.BlendPercentage);
    }

    /// <summary>Composites <paramref name="sourceRectangle"/> of <paramref name="source"/> at <paramref name="location"/> with normal source-over blending.</summary>
    public static IImageProcessingContext DrawImage(this IImageProcessingContext context, Image source, Point location, Rectangle sourceRectangle, float opacity)
        => context.DrawImage(source, location, sourceRectangle, PixelColorBlendingMode.Normal, PixelAlphaCompositionMode.SrcOver, opacity);

    /// <summary>Composites <paramref name="sourceRectangle"/> of <paramref name="source"/> at <paramref name="location"/> with the given blend mode.</summary>
    public static IImageProcessingContext DrawImage(this IImageProcessingContext context, Image source, Point location, Rectangle sourceRectangle, PixelColorBlendingMode colorBlending, float opacity)
        => context.DrawImage(source, location, sourceRectangle, colorBlending, PixelAlphaCompositionMode.SrcOver, opacity);
}

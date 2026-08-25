using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Colour, convolution, compositing and histogram operations.</summary>
internal sealed partial class ImageProcessingContext<TPixel>
{
    // ----- Colour matrices -----

    public IImageProcessingContext Filter(ColorMatrix matrix)
        => this.PerFrameRegion(null, (frame, region) => ColorMatrixOps.Apply(frame, region, matrix));

    public IImageProcessingContext Filter(ColorMatrix matrix, Rectangle rectangle)
        => this.PerFrameRegion(rectangle, (frame, region) => ColorMatrixOps.Apply(frame, region, matrix));

    public IImageProcessingContext Grayscale(Rectangle rectangle)
        => this.PerFrameRegion(rectangle, (frame, region) => RowProcessor.ProcessPixels(frame, region, static p =>
        {
            byte l = PixelOps.Luminance8(p);
            return new Rgba32(l, l, l, p.A);
        }));

    public IImageProcessingContext Invert(Rectangle rectangle)
        => this.PerFrameRegion(rectangle, (frame, region) => RowProcessor.ProcessPixels(
            frame, region, static p => new Rgba32((byte)(255 - p.R), (byte)(255 - p.G), (byte)(255 - p.B), p.A)));

    public IImageProcessingContext Brightness(float amount, Rectangle rectangle)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Brightness amount must be non-negative.");
        }

        return this.PerFrameRegion(rectangle, (frame, region) => RowProcessor.ProcessPixels(frame, region, p => new Rgba32(
            ClampByte(p.R * amount),
            ClampByte(p.G * amount),
            ClampByte(p.B * amount),
            p.A)));
    }

    public IImageProcessingContext Contrast(float amount, Rectangle rectangle)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Contrast amount must be non-negative.");
        }

        return this.PerFrameRegion(rectangle, (frame, region) => RowProcessor.ProcessPixels(frame, region, p => new Rgba32(
            ClampByte((((p.R / 255f) - 0.5f) * amount + 0.5f) * 255f),
            ClampByte((((p.G / 255f) - 0.5f) * amount + 0.5f) * 255f),
            ClampByte((((p.B / 255f) - 0.5f) * amount + 0.5f) * 255f),
            p.A)));
    }

    // ----- Blurs -----

    public IImageProcessingContext GaussianBlur(float sigma, Rectangle rectangle)
        => this.PerFrameRegion(rectangle, (frame, region) => FilterOps.GaussianBlur(frame, sigma, region));

    public IImageProcessingContext GaussianSharpen(float sigma, Rectangle rectangle)
        => this.PerFrameRegion(rectangle, (frame, region) => FilterOps.GaussianSharpen(frame, sigma, region));

    public IImageProcessingContext BoxBlur(int radius) => this.BoxBlurCore(radius, null);

    public IImageProcessingContext BoxBlur(int radius, Rectangle rectangle) => this.BoxBlurCore(radius, rectangle);

    private IImageProcessingContext BoxBlurCore(int radius, Rectangle? rectangle)
    {
        if (radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Radius must be non-negative.");
        }

        if (radius == 0)
        {
            return this;
        }

        float[] kernel = ConvolutionOps.BuildBoxKernel(radius);
        return this.PerFrameRegion(rectangle, (frame, region)
            => ConvolutionOps.ConvolveSeparable(frame, region, kernel, kernel, preserveAlpha: false));
    }

    public IImageProcessingContext BokehBlur(int radius, int components, float gamma) => this.BokehBlurCore(radius, components, gamma, null);

    public IImageProcessingContext BokehBlur(int radius, int components, float gamma, Rectangle rectangle) => this.BokehBlurCore(radius, components, gamma, rectangle);

    private IImageProcessingContext BokehBlurCore(int radius, int components, float gamma, Rectangle? rectangle)
    {
        Guard.MustBePositive(radius, nameof(radius));
        if (components is < 1 or > BokehBlurOps.MaxComponents)
        {
            throw new ArgumentOutOfRangeException(nameof(components), components, $"Components must be between 1 and {BokehBlurOps.MaxComponents}.");
        }

        if (!(gamma >= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(gamma), gamma, "Gamma must be at least 1.");
        }

        return this.PerFrameRegion(rectangle, (frame, region) => BokehBlurOps.BokehBlur(frame, region, radius, components, gamma));
    }

    // ----- Convolution / edge detection -----

    public IImageProcessingContext Convolve(ReadOnlyMemory<float> kernel, int width, int height, bool preserveAlpha)
    {
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        if (kernel.Length != width * height)
        {
            throw new ArgumentException($"Expected {width * height} kernel values for a {width}x{height} kernel but got {kernel.Length}.", nameof(kernel));
        }

        float[] values = kernel.ToArray();
        return this.PerFrameRegion(null, (frame, region) => ConvolutionOps.Convolve2D(frame, region, values, width, height, preserveAlpha));
    }

    public IImageProcessingContext Convolve(ReadOnlyMemory<float> kernelX, ReadOnlyMemory<float> kernelY, bool preserveAlpha)
    {
        if (kernelX.IsEmpty)
        {
            throw new ArgumentException("The horizontal kernel must not be empty.", nameof(kernelX));
        }

        if (kernelY.IsEmpty)
        {
            throw new ArgumentException("The vertical kernel must not be empty.", nameof(kernelY));
        }

        float[] kx = kernelX.ToArray();
        float[] ky = kernelY.ToArray();
        return this.PerFrameRegion(null, (frame, region) => ConvolutionOps.ConvolveSeparable(frame, region, kx, ky, preserveAlpha));
    }

    public IImageProcessingContext DetectEdges(EdgeDetectorKernel kernel, bool grayscale)
    {
        if (kernel.Kernel.Count == 0)
        {
            throw new ArgumentException("The kernel must not be empty.", nameof(kernel));
        }

        this.GrayscaleIf(grayscale);
        return this.PerFrameRegion(null, (frame, region) => ConvolutionOps.DetectEdges(frame, region, kernel.Kernel));
    }

    public IImageProcessingContext DetectEdges(EdgeDetector2DKernel kernel, bool grayscale)
    {
        if (kernel.KernelX.Count == 0 || kernel.KernelY.Count == 0)
        {
            throw new ArgumentException("The kernels must not be empty.", nameof(kernel));
        }

        this.GrayscaleIf(grayscale);
        return this.PerFrameRegion(null, (frame, region) => ConvolutionOps.DetectEdges(frame, region, kernel.KernelX, kernel.KernelY));
    }

    public IImageProcessingContext DetectEdges(EdgeDetectorCompassKernel kernel, bool grayscale)
    {
        DenseMatrix<float>[] kernels = kernel.Flatten();
        foreach (DenseMatrix<float> k in kernels)
        {
            if (k.Count == 0)
            {
                throw new ArgumentException("The kernels must not be empty.", nameof(kernel));
            }
        }

        this.GrayscaleIf(grayscale);
        return this.PerFrameRegion(null, (frame, region) => ConvolutionOps.DetectEdges(frame, region, kernels));
    }

    private void GrayscaleIf(bool grayscale)
    {
        if (grayscale)
        {
            this.Grayscale();
        }
    }

    // ----- Artistic / radial -----

    public IImageProcessingContext OilPaint(int levels, int brushSize) => this.OilPaintCore(levels, brushSize, null);

    public IImageProcessingContext OilPaint(int levels, int brushSize, Rectangle rectangle) => this.OilPaintCore(levels, brushSize, rectangle);

    private IImageProcessingContext OilPaintCore(int levels, int brushSize, Rectangle? rectangle)
    {
        Guard.MustBePositive(levels, nameof(levels));
        Guard.MustBePositive(brushSize, nameof(brushSize));
        return this.PerFrameRegion(rectangle, (frame, region) => EffectOps.OilPaint(frame, region, levels, brushSize));
    }

    public IImageProcessingContext Pixelate(int size) => this.PixelateCore(size, null);

    public IImageProcessingContext Pixelate(int size, Rectangle rectangle) => this.PixelateCore(size, rectangle);

    private IImageProcessingContext PixelateCore(int size, Rectangle? rectangle)
    {
        Guard.MustBePositive(size, nameof(size));
        return this.PerFrameRegion(rectangle, (frame, region) => EffectOps.Pixelate(frame, region, size));
    }

    public IImageProcessingContext Vignette(Color color, float radiusX, float radiusY, GraphicsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return this.PerFrameRegion(null, (frame, region) => EffectOps.Vignette(frame, region, color, radiusX, radiusY, options));
    }

    public IImageProcessingContext Vignette(Color color, float radiusX, float radiusY, Rectangle rectangle, GraphicsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return this.PerFrameRegion(rectangle, (frame, region) => EffectOps.Vignette(frame, region, color, radiusX, radiusY, options));
    }

    public IImageProcessingContext Glow(Color color, float radius, GraphicsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return this.PerFrameRegion(null, (frame, region) => EffectOps.Glow(frame, region, color, radius, options));
    }

    public IImageProcessingContext Glow(Color color, float radius, Rectangle rectangle, GraphicsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return this.PerFrameRegion(rectangle, (frame, region) => EffectOps.Glow(frame, region, color, radius, options));
    }

    public IImageProcessingContext Swizzle(ISwizzler swizzler)
    {
        ArgumentNullException.ThrowIfNull(swizzler);
        return this.PerFrame(frame => EffectOps.Swizzle(frame, swizzler));
    }

    public IImageProcessingContext HistogramEqualization(HistogramEqualizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return this.PerFrame(frame =>
        {
            HistogramEqualizationOps.Equalize(frame, options);
            return frame;
        });
    }

    // ----- Thresholds -----

    public IImageProcessingContext BinaryThreshold(float threshold, Color upperColor, Color lowerColor, BinaryThresholdMode mode)
        => this.PerFrameRegion(null, (frame, region)
            => ThresholdOps.BinaryThreshold(frame, region, threshold, upperColor.ToRgba32(), lowerColor.ToRgba32(), mode));

    public IImageProcessingContext BinaryThreshold(float threshold, Color upperColor, Color lowerColor, BinaryThresholdMode mode, Rectangle rectangle)
        => this.PerFrameRegion(rectangle, (frame, region)
            => ThresholdOps.BinaryThreshold(frame, region, threshold, upperColor.ToRgba32(), lowerColor.ToRgba32(), mode));

    public IImageProcessingContext AdaptiveThreshold(Color upperColor, Color lowerColor, float thresholdLimit)
        => this.PerFrameRegion(null, (frame, region)
            => FilterOps.BradleyThreshold(frame, region, upperColor.ToRgba32(), lowerColor.ToRgba32(), 0, thresholdLimit));

    public IImageProcessingContext AdaptiveThreshold(Color upperColor, Color lowerColor, float thresholdLimit, Rectangle rectangle)
        => this.PerFrameRegion(rectangle, (frame, region)
            => FilterOps.BradleyThreshold(frame, region, upperColor.ToRgba32(), lowerColor.ToRgba32(), 0, thresholdLimit));

    // ----- Compositing -----

    public IImageProcessingContext DrawImage(
        Image source,
        Point location,
        Rectangle sourceRectangle,
        PixelColorBlendingMode colorBlending,
        PixelAlphaCompositionMode alphaComposition,
        float opacity)
    {
        ArgumentNullException.ThrowIfNull(source);
        return this.PerFrame(frame =>
        {
            CompositingOps.DrawImage(frame, source, location, sourceRectangle, colorBlending, alphaComposition, opacity);
            return frame;
        });
    }

    // ----- Helpers -----

    /// <summary>
    /// Runs <paramref name="operation"/> on every frame with the given rectangle clamped to the frame (or the
    /// whole frame when <see langword="null"/>); frames the rectangle misses entirely are skipped.
    /// </summary>
    private IImageProcessingContext PerFrameRegion(Rectangle? rectangle, Action<ImageFrame<TPixel>, Rectangle> operation)
        => this.PerFrame(frame =>
        {
            Rectangle region = rectangle is null
                ? RowProcessor.Bounds(frame)
                : RowProcessor.ClampToFrame(frame, rectangle.Value);
            if (region.Width > 0 && region.Height > 0)
            {
                operation(frame, region);
            }

            return frame;
        });
}

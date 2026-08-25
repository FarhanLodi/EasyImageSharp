using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// A fluent pipeline of operations applied to an image by
/// <see cref="ProcessingExtensions.Mutate{TPixel}"/> or <see cref="ProcessingExtensions.Clone{TPixel}"/>.
/// Operations apply to every frame of the image.
/// </summary>
public partial interface IImageProcessingContext
{
    /// <summary>The current size of the image's root frame, reflecting prior operations.</summary>
    Size GetCurrentSize();

    IImageProcessingContext Resize(ResizeOptions options);

    IImageProcessingContext Crop(Rectangle bounds);

    /// <summary>Centers the image on a canvas of the given size filled with <paramref name="backgroundColor"/>.</summary>
    IImageProcessingContext Pad(int width, int height, Rgba32 backgroundColor);

    /// <summary>Rotates clockwise by any angle, expanding the canvas. Multiples of 90 use a lossless fast path.</summary>
    IImageProcessingContext Rotate(float degrees);

    IImageProcessingContext Flip(FlipMode flipMode);

    IImageProcessingContext Grayscale();

    IImageProcessingContext Invert();

    /// <summary>
    /// Composites the image over a solid <paramref name="color"/> (source-over), replacing transparent and
    /// semi-transparent areas. With an opaque color the result is fully opaque; the color's own alpha is
    /// honored otherwise. Pixel formats without an alpha channel are left unchanged.
    /// </summary>
    IImageProcessingContext BackgroundColor(Color color);

    /// <summary>Multiplies channel values: 1 keeps the image unchanged; &lt; 1 darkens; &gt; 1 brightens.</summary>
    IImageProcessingContext Brightness(float amount);

    /// <summary>Scales contrast around mid-gray: 1 keeps the image unchanged; 0 is uniform gray.</summary>
    IImageProcessingContext Contrast(float amount);

    /// <summary>Binarizes on luminance: values at or above <paramref name="threshold"/> (0-1) become white.</summary>
    IImageProcessingContext BinaryThreshold(float threshold);

    /// <summary>Binarizes using a global threshold computed by Otsu's method.</summary>
    IImageProcessingContext OtsuThreshold();

    /// <summary>Binarizes using Bradley's local-mean adaptive threshold; robust to uneven lighting.</summary>
    /// <param name="windowSize">Local window size in pixels; 0 chooses one from the image size.</param>
    /// <param name="thresholdLimit">Fraction of the local mean below which pixels turn black.</param>
    IImageProcessingContext AdaptiveThreshold(int windowSize, float thresholdLimit);

    /// <summary>Binarizes using Sauvola's local method; the standard choice for document/OCR binarization.</summary>
    IImageProcessingContext SauvolaThreshold(int windowSize, float k);

    IImageProcessingContext GaussianBlur(float sigma);

    /// <summary>Sharpens with an unsharp mask built from a Gaussian blur of the given sigma.</summary>
    IImageProcessingContext GaussianSharpen(float sigma);

    /// <summary>Median filter; removes salt-and-pepper noise. Radius 1-7.</summary>
    IImageProcessingContext MedianBlur(int radius);

    /// <summary>Detects and corrects small document rotation using projection profiling; fills with white.</summary>
    IImageProcessingContext Deskew(float maxAngleDegrees);

    /// <summary>Alpha-blends another image over this one at the given location.</summary>
    IImageProcessingContext DrawImage(Image image, Point location, float opacity);
}

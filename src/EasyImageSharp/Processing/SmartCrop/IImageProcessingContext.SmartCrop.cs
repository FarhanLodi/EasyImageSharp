namespace EasyImageSharp.Processing;

/// <summary>Content-aware cropping.</summary>
public partial interface IImageProcessingContext
{
    /// <summary>
    /// Finds the crop of the current root frame with the aspect ratio <paramref name="width"/> : <paramref name="height"/>
    /// that best frames its content (edge detail, saturation, skin-like colour and the boosts in
    /// <paramref name="options"/>; see <see cref="SmartCropOptions"/>). The rectangle is in root-frame pixels and the
    /// image is not changed. Deterministic: the same pixels and options always give the same rectangle.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The processing context does not implement smart cropping.</exception>
    Rectangle FindSmartCrop(int width, int height, SmartCropOptions options)
        => throw new NotSupportedException("This processing context does not support smart cropping.");

    /// <summary>
    /// Crops every frame to the rectangle <see cref="FindSmartCrop(int, int, SmartCropOptions)"/> finds on the root
    /// frame and resizes the result to exactly <paramref name="width"/> x <paramref name="height"/> with
    /// <see cref="SmartCropOptions.Sampler"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The processing context does not implement smart cropping.</exception>
    IImageProcessingContext SmartCrop(int width, int height, SmartCropOptions options)
        => throw new NotSupportedException("This processing context does not support smart cropping.");
}

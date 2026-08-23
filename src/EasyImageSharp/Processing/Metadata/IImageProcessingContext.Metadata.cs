namespace EasyImageSharp.Processing;

/// <content>Metadata-driven operations.</content>
public partial interface IImageProcessingContext
{
    /// <summary>
    /// Rotates and/or flips every frame so that it displays upright according to the EXIF
    /// <c>Orientation</c> tag (values 2-8), then resets the tag to 1 (top-left). Images without an EXIF
    /// profile or with orientation 1 are left unchanged. The 90-degree rotations and flips are lossless.
    /// </summary>
    IImageProcessingContext AutoOrient();
}

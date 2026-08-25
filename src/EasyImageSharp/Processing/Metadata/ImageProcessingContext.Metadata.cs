using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <content>Metadata-driven operations.</content>
internal sealed partial class ImageProcessingContext<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    public IImageProcessingContext AutoOrient()
    {
        ExifProfile? profile = this.image.Metadata.ExifProfile;
        if (profile is null || !profile.TryGetValue(ExifTag.Orientation, out IExifValue<ushort>? orientationValue))
        {
            return this;
        }

        ushort orientation = orientationValue.Value;
        // EXIF orientation: 1 top-left, 2 top-right (mirror), 3 bottom-right (180), 4 bottom-left (vertical
        // flip), 5 left-top (transpose), 6 right-top (90 CW), 7 right-bottom (transverse), 8 left-bottom (270 CW).
        switch (orientation)
        {
            case 2:
                this.Flip(FlipMode.Horizontal);
                break;
            case 3:
                this.Rotate(180f);
                break;
            case 4:
                this.Flip(FlipMode.Vertical);
                break;
            case 5:
                this.Rotate(90f).Flip(FlipMode.Horizontal);
                break;
            case 6:
                this.Rotate(90f);
                break;
            case 7:
                this.Rotate(270f).Flip(FlipMode.Horizontal);
                break;
            case 8:
                this.Rotate(270f);
                break;
            default:
                return this; // 1 (normal) or an invalid value: nothing to do, keep the tag as it is.
        }

        orientationValue.Value = 1;

        // Orientations 5-8 swap the axes; keep the EXIF pixel dimensions consistent when both are present.
        if (orientation >= 5
            && profile.TryGetValue(ExifTag.PixelXDimension, out IExifValue<uint>? pixelX)
            && profile.TryGetValue(ExifTag.PixelYDimension, out IExifValue<uint>? pixelY))
        {
            (pixelX.Value, pixelY.Value) = (pixelY.Value, pixelX.Value);
        }

        return this;
    }
}

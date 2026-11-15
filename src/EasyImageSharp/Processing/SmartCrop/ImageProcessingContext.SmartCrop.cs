namespace EasyImageSharp.Processing;

/// <summary>Content-aware cropping; see <c>IImageProcessingContext.SmartCrop.cs</c> for the contract.</summary>
internal sealed partial class ImageProcessingContext<TPixel>
{
    public Rectangle FindSmartCrop(int width, int height, SmartCropOptions options)
        => SmartCropOps.Find(this.image.Frames.RootFrame, width, height, options);

    public IImageProcessingContext SmartCrop(int width, int height, SmartCropOptions options)
    {
        Rectangle crop = SmartCropOps.Find(this.image.Frames.RootFrame, width, height, options);
        this.PerFrameReplace(frame =>
        {
            Rectangle bounds = Rectangle.Intersect(crop, new Rectangle(0, 0, frame.Width, frame.Height));
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                bounds = new Rectangle(0, 0, frame.Width, frame.Height);
            }

            return bounds.X == 0 && bounds.Y == 0 && bounds.Width == frame.Width && bounds.Height == frame.Height
                ? frame
                : FrameOps.Crop(frame, bounds);
        });
        return this.Resize(new ResizeOptions { Size = new Size(width, height), Sampler = options.Sampler });
    }
}

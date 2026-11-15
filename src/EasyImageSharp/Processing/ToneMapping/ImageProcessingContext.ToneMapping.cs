namespace EasyImageSharp.Processing;

/// <summary>High dynamic range tone mapping.</summary>
internal sealed partial class ImageProcessingContext<TPixel>
{
    public IImageProcessingContext ToneMap(ToneMappingOptions options)
    {
        ToneMappingOps.Validate(options);
        return this.PerFrame(frame =>
        {
            ToneMappingOps.Apply(frame, options);
            return frame;
        });
    }
}

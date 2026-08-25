namespace EasyImageSharp.AI;

/// <summary>Controls how a saliency mask is applied by <c>RemoveBackground</c>.</summary>
public sealed class BackgroundRemovalOptions
{
    /// <summary>The defaults: transparent background on alpha formats, white on opaque formats, no crop.</summary>
    public static BackgroundRemovalOptions Default { get; } = new();

    /// <summary>
    /// Colour the background is blended towards. <c>null</c> (default) makes the background transparent when the
    /// pixel format has an alpha channel (mask multiplied into alpha) and white otherwise. Set a colour to
    /// composite over it regardless of the pixel format.
    /// </summary>
    public Color? BackgroundColor { get; set; }

    /// <summary>Also crop the image to the bounding box of the foreground (mask &gt;= <see cref="ForegroundThreshold"/>). Default false.</summary>
    public bool CropToForeground { get; set; }

    /// <summary>Mask value (0-1) above which a pixel counts as foreground for cropping. Default 0.5.</summary>
    public float ForegroundThreshold { get; set; } = 0.5f;

    /// <summary>Extra pixels kept around the foreground bounding box when cropping. Default 0.</summary>
    public int CropPadding { get; set; }
}

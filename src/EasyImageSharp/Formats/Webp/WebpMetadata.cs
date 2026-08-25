using EasyImageSharp.Metadata;

namespace EasyImageSharp.Formats.Webp;

/// <summary>How the pixels of a WebP animation frame are combined with the canvas underneath.</summary>
public enum WebpBlendMethod : byte
{
    /// <summary>Alpha-blend the frame over whatever the canvas already holds.</summary>
    AlphaBlend = 0,

    /// <summary>Overwrite the frame's rectangle, alpha included.</summary>
    DoNotBlend = 1,
}

/// <summary>What happens to a WebP animation frame's rectangle once the frame has been shown.</summary>
public enum WebpDisposalMethod : byte
{
    /// <summary>Leave the frame in place.</summary>
    DoNotDispose = 0,

    /// <summary>
    /// Clear the frame's rectangle to the background before the next frame is drawn. The library, like web
    /// browsers and the reference decoder, always clears to transparent black rather than to the colour in
    /// the ANIM chunk (which is exposed as <see cref="WebpMetadata.BackgroundColor"/>).
    /// </summary>
    DisposeToBackground = 1,
}

/// <summary>WebP-specific image metadata read from the RIFF container.</summary>
public sealed class WebpMetadata : IFormatMetadata
{
    /// <summary>Creates WebP metadata with default values.</summary>
    public WebpMetadata()
    {
    }

    private WebpMetadata(WebpMetadata other)
    {
        this.IsLossless = other.IsLossless;
        this.HasAlpha = other.HasAlpha;
        this.IsAnimated = other.IsAnimated;
        this.RepeatCount = other.RepeatCount;
        this.BackgroundColor = other.BackgroundColor;
    }

    /// <summary>True when the still image (or the first animation frame) used the VP8L lossless bitstream.</summary>
    public bool IsLossless { get; set; }

    /// <summary>True when the file carries an alpha channel.</summary>
    public bool HasAlpha { get; set; }

    /// <summary>True when the file is an animation (a VP8X file with the ANIM/ANMF chunks).</summary>
    public bool IsAnimated { get; set; }

    /// <summary>The number of times the animation repeats; 0 means loop forever. Still images report 1.</summary>
    public ushort RepeatCount { get; set; } = 1;

    /// <summary>
    /// The background colour from the ANIM chunk as a packed 0xAARRGGBB value. Purely informational: the
    /// decoder disposes frames to transparent black, matching browsers and the reference decoder.
    /// </summary>
    public uint BackgroundColor { get; set; }

    /// <summary>Creates a deep copy of this metadata.</summary>
    public WebpMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}

/// <summary>Per-frame WebP metadata read from an ANMF chunk.</summary>
public sealed class WebpFrameMetadata : IFormatMetadata
{
    /// <summary>Creates frame metadata with default values.</summary>
    public WebpFrameMetadata()
    {
    }

    private WebpFrameMetadata(WebpFrameMetadata other)
    {
        this.FrameDelay = other.FrameDelay;
        this.X = other.X;
        this.Y = other.Y;
        this.Width = other.Width;
        this.Height = other.Height;
        this.BlendMethod = other.BlendMethod;
        this.DisposalMethod = other.DisposalMethod;
    }

    /// <summary>How long the frame is shown, in milliseconds.</summary>
    public int FrameDelay { get; set; }

    /// <summary>The frame rectangle's left edge on the canvas, in pixels (always even).</summary>
    public int X { get; set; }

    /// <summary>The frame rectangle's top edge on the canvas, in pixels (always even).</summary>
    public int Y { get; set; }

    /// <summary>The width of the frame rectangle, which may be smaller than the canvas.</summary>
    public int Width { get; set; }

    /// <summary>The height of the frame rectangle, which may be smaller than the canvas.</summary>
    public int Height { get; set; }

    /// <summary>How the frame was combined with the canvas underneath.</summary>
    public WebpBlendMethod BlendMethod { get; set; }

    /// <summary>What happened to the frame's rectangle after it was shown.</summary>
    public WebpDisposalMethod DisposalMethod { get; set; }

    /// <summary>Creates a deep copy of this metadata.</summary>
    public WebpFrameMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}

namespace EasyImageSharp.Processing;

/// <summary>Options that control how drawing and compositing operations blend into the image.</summary>
public sealed class GraphicsOptions
{
    private float blendPercentage = 1f;
    private int antialiasSubpixelDepth = 16;

    /// <summary>The default options: antialiased, fully opaque, normal blending, source-over composition.</summary>
    public static GraphicsOptions Default { get; } = new();

    /// <summary>Whether shapes and edges are drawn antialiased. Defaults to <see langword="true"/>.</summary>
    public bool Antialias { get; set; } = true;

    /// <summary>The number of subpixel samples per pixel side used when antialiasing. Defaults to 16.</summary>
    public int AntialiasSubpixelDepth
    {
        get => this.antialiasSubpixelDepth;
        set => this.antialiasSubpixelDepth = Math.Max(1, value);
    }

    /// <summary>The overall opacity applied to the source, 0-1. Defaults to 1; values are clamped.</summary>
    public float BlendPercentage
    {
        get => this.blendPercentage;
        set => this.blendPercentage = float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 1f);
    }

    /// <summary>How the source and backdrop colours are combined. Defaults to <see cref="PixelColorBlendingMode.Normal"/>.</summary>
    public PixelColorBlendingMode ColorBlendingMode { get; set; } = PixelColorBlendingMode.Normal;

    /// <summary>The Porter-Duff operator used to composite alpha. Defaults to <see cref="PixelAlphaCompositionMode.SrcOver"/>.</summary>
    public PixelAlphaCompositionMode AlphaCompositionMode { get; set; } = PixelAlphaCompositionMode.SrcOver;

    /// <summary>Returns a copy of these options.</summary>
    public GraphicsOptions DeepClone() => new()
    {
        Antialias = this.Antialias,
        AntialiasSubpixelDepth = this.AntialiasSubpixelDepth,
        BlendPercentage = this.BlendPercentage,
        ColorBlendingMode = this.ColorBlendingMode,
        AlphaCompositionMode = this.AlphaCompositionMode,
    };
}

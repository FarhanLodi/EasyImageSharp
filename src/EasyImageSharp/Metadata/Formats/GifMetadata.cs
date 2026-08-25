namespace EasyImageSharp.Metadata;

/// <summary>What happens to a GIF frame's area after it has been displayed (Graphic Control Extension).</summary>
public enum GifDisposalMethod : byte
{
    /// <summary>No disposal specified; the decoder is not required to take any action.</summary>
    Unspecified = 0,

    /// <summary>Leave the frame in place.</summary>
    NotDispose = 1,

    /// <summary>Restore the frame's area to the background (transparent).</summary>
    RestoreToBackground = 2,

    /// <summary>Restore the frame's area to what was there before the frame was drawn.</summary>
    RestoreToPrevious = 3,
}

/// <summary>GIF-specific image metadata: loop count, global colour table size and comments.</summary>
public sealed class GifMetadata : IFormatMetadata
{
    /// <summary>Creates GIF metadata with default values.</summary>
    public GifMetadata()
    {
    }

    private GifMetadata(GifMetadata other)
    {
        this.RepeatCount = other.RepeatCount;
        this.GlobalColorTableLength = other.GlobalColorTableLength;
        this.BackgroundColorIndex = other.BackgroundColorIndex;
        this.Comments = new List<string>(other.Comments);
    }

    /// <summary>
    /// The number of times the animation repeats, from the NETSCAPE2.0 application extension: 0 means loop forever.
    /// Files without the extension report 1 (play once).
    /// </summary>
    public ushort RepeatCount { get; set; } = 1;

    /// <summary>The number of entries in the global colour table, or 0 when the file has none.</summary>
    public int GlobalColorTableLength { get; set; }

    /// <summary>The background colour index from the logical screen descriptor.</summary>
    public byte BackgroundColorIndex { get; set; }

    /// <summary>The comment extension texts in file order.</summary>
    public IList<string> Comments { get; } = new List<string>();

    public GifMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}

/// <summary>Per-frame GIF metadata from the frame's Graphic Control Extension and image descriptor.</summary>
public sealed class GifFrameMetadata : IFormatMetadata
{
    /// <summary>Creates frame metadata with default values.</summary>
    public GifFrameMetadata()
    {
    }

    private GifFrameMetadata(GifFrameMetadata other)
    {
        this.FrameDelay = other.FrameDelay;
        this.DisposalMethod = other.DisposalMethod;
        this.HasTransparency = other.HasTransparency;
        this.TransparencyIndex = other.TransparencyIndex;
        this.LocalColorTableLength = other.LocalColorTableLength;
    }

    /// <summary>The delay before the next frame in hundredths of a second (0 when unspecified).</summary>
    public int FrameDelay { get; set; }

    /// <summary>The disposal method that applies after this frame is shown.</summary>
    public GifDisposalMethod DisposalMethod { get; set; }

    /// <summary>True when the frame declared a transparent colour index.</summary>
    public bool HasTransparency { get; set; }

    /// <summary>The transparent colour index (meaningful when <see cref="HasTransparency"/> is true).</summary>
    public byte TransparencyIndex { get; set; }

    /// <summary>The number of entries in the frame's local colour table, or 0 when the frame uses the global table.</summary>
    public int LocalColorTableLength { get; set; }

    public GifFrameMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}

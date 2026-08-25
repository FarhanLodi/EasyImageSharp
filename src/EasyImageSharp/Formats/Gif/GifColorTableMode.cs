namespace EasyImageSharp.Formats.Gif;

/// <summary>Chooses how the <see cref="GifEncoder"/> assigns colour tables to frames.</summary>
public enum GifColorTableMode
{
    /// <summary>
    /// One global colour table, quantized from all frames together, shared by every frame. Smallest files;
    /// animations with many distinct colours per frame are approximated.
    /// </summary>
    Global,

    /// <summary>
    /// Every frame is quantized on its own: the root frame's palette becomes the global table and each further
    /// frame carries a local colour table. Best fidelity for animations whose colours change between frames.
    /// </summary>
    Local,
}

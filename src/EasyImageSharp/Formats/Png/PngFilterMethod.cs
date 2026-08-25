namespace EasyImageSharp.Formats.Png;

/// <summary>The scanline filter the encoder applies before deflate compression.</summary>
public enum PngFilterMethod
{
    /// <summary>No filtering (filter type 0 on every scanline).</summary>
    None,

    /// <summary>Filter type 1: predict from the byte to the left.</summary>
    Sub,

    /// <summary>Filter type 2: predict from the byte above.</summary>
    Up,

    /// <summary>Filter type 3: predict from the average of left and above.</summary>
    Average,

    /// <summary>Filter type 4: the Paeth predictor.</summary>
    Paeth,

    /// <summary>Choose per scanline the filter whose output has the smallest sum of absolute values (the libpng heuristic).</summary>
    Adaptive,
}

namespace EasyImageSharp.Drawing;

/// <summary>Decides which points are inside a path whose figures overlap or intersect themselves.</summary>
public enum FillRule
{
    /// <summary>
    /// A point is inside when the figures wind around it a non-zero number of times, counting each crossing
    /// by its direction. Overlapping figures drawn in the same direction union; a figure drawn in the opposite
    /// direction cuts a hole. This is the default.
    /// </summary>
    NonZero = 0,

    /// <summary>A point is inside when a ray from it crosses the path an odd number of times, whatever the directions.</summary>
    EvenOdd = 1,
}

/// <summary>The shape drawn where two segments of a stroked path meet.</summary>
public enum LineJoin
{
    /// <summary>
    /// The outer edges are extended until they meet in a sharp point; beyond the pen's
    /// <see cref="Pen.MiterLimit"/> the corner is bevelled instead. This is the default.
    /// </summary>
    Miter = 0,

    /// <summary>The corner is rounded with a circular arc whose diameter is the stroke width.</summary>
    Round = 1,

    /// <summary>The corner is cut off by a straight line between the outer edges.</summary>
    Bevel = 2,
}

/// <summary>The shape drawn at the two ends of an open stroked figure and of every dash.</summary>
public enum LineCap
{
    /// <summary>The stroke ends flush with the end point. This is the default.</summary>
    Butt = 0,

    /// <summary>The stroke ends in a half disc centred on the end point.</summary>
    Round = 1,

    /// <summary>The stroke extends past the end point by half the stroke width and ends square.</summary>
    Square = 2,
}

/// <summary>How a gradient or an image brush paints outside its defined range.</summary>
public enum WrapMode
{
    /// <summary>The edge colour (the end stop, or the edge pixel) extends indefinitely; SVG calls this <c>pad</c>.</summary>
    Clamp = 0,

    /// <summary>The pattern repeats.</summary>
    Repeat = 1,

    /// <summary>The pattern repeats, every other copy mirrored, so there is no seam.</summary>
    Reflect = 2,

    /// <summary>Nothing is painted outside the defined range.</summary>
    Transparent = 3,
}

/// <summary>The colour space in which a gradient blends between its colour stops.</summary>
public enum GradientInterpolation
{
    /// <summary>
    /// Blends the encoded sRGB values directly, which is what CSS, SVG and most graphics libraries do. This
    /// is the default.
    /// </summary>
    Srgb = 0,

    /// <summary>
    /// Decodes the stops to linear light, blends there, and re-encodes as sRGB. Transitions between saturated
    /// colours stay brighter and look physically even, at the cost of matching fewer other renderers.
    /// </summary>
    LinearLight = 1,
}

/// <summary>How an <see cref="ImageBrush"/> reads its source image between pixel centres.</summary>
public enum ImageBrushSampling
{
    /// <summary>Blends the four nearest source pixels (premultiplied by alpha). This is the default.</summary>
    Bilinear = 0,

    /// <summary>Takes the source pixel the sample point falls in; crisp for pixel-art patterns.</summary>
    NearestNeighbor = 1,
}

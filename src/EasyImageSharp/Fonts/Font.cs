using System.Globalization;

namespace EasyImageSharp.Fonts;

/// <summary>The unit a <see cref="Font"/> size is given in.</summary>
public enum FontSizeUnit
{
    /// <summary>Pixels: the em square is this many pixels tall.</summary>
    Pixel,

    /// <summary>Typographic points (1/72 inch), converted to pixels with <see cref="Font.Dpi"/>.</summary>
    Point,
}

/// <summary>
/// A <see cref="FontFace"/> at a size: what text is measured and drawn with. Immutable and safe to share
/// between threads.
/// </summary>
public sealed class Font
{
    /// <summary>The largest em size accepted, in pixels; it bounds the memory one glyph can take to render.</summary>
    public const float MaxSizeInPixels = 4096f;

    /// <summary>Creates a font whose em square is <paramref name="sizeInPixels"/> pixels tall.</summary>
    /// <param name="face">The face to draw with.</param>
    /// <param name="sizeInPixels">The em size in pixels, greater than 0 and at most <see cref="MaxSizeInPixels"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The size is not positive and finite, or exceeds <see cref="MaxSizeInPixels"/>.</exception>
    public Font(FontFace face, float sizeInPixels)
        : this(face, sizeInPixels, FontSizeUnit.Pixel, 72f)
    {
    }

    /// <summary>Creates a font with a size in pixels or points.</summary>
    /// <param name="face">The face to draw with.</param>
    /// <param name="size">The em size in <paramref name="unit"/>.</param>
    /// <param name="unit">Whether <paramref name="size"/> is in pixels or points.</param>
    /// <param name="dpi">Dots per inch for converting points to pixels (pixels = points * dpi / 72); ignored for pixel sizes.</param>
    /// <exception cref="ArgumentOutOfRangeException">The size or DPI is not positive and finite, or the pixel size exceeds <see cref="MaxSizeInPixels"/>.</exception>
    public Font(FontFace face, float size, FontSizeUnit unit, float dpi)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (!(size > 0f) || !float.IsFinite(size))
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Font size must be a positive finite number.");
        }

        if (!(dpi > 0f) || !float.IsFinite(dpi))
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "DPI must be a positive finite number.");
        }

        if (unit is not (FontSizeUnit.Pixel or FontSizeUnit.Point))
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown font size unit.");
        }

        float pixels = unit == FontSizeUnit.Point ? size * dpi / 72f : size;
        if (pixels > MaxSizeInPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, $"The font size is {pixels} px; the largest supported size is {MaxSizeInPixels} px.");
        }

        this.Face = face;
        this.Size = size;
        this.Unit = unit;
        this.Dpi = dpi;
        this.SizeInPixels = pixels;
    }

    /// <summary>The face this font draws with.</summary>
    public FontFace Face { get; }

    /// <summary>The size in <see cref="Unit"/>.</summary>
    public float Size { get; }

    /// <summary>The unit of <see cref="Size"/>.</summary>
    public FontSizeUnit Unit { get; }

    /// <summary>The DPI used to convert points to pixels (72 for fonts sized in pixels, where it has no effect).</summary>
    public float Dpi { get; }

    /// <summary>The em size in pixels.</summary>
    public float SizeInPixels { get; }

    /// <summary>Pixels per font design unit.</summary>
    public float Scale => this.SizeInPixels / this.Face.UnitsPerEm;

    /// <summary>The ascent in pixels above the baseline.</summary>
    public float Ascent => this.Face.Ascender * this.Scale;

    /// <summary>The descent in pixels below the baseline (a positive distance).</summary>
    public float Descent => -this.Face.Descender * this.Scale;

    /// <summary>The distance between consecutive baselines in pixels: ascent, descent and line gap.</summary>
    public float LineHeight => (this.Face.Ascender - this.Face.Descender + this.Face.LineGap) * this.Scale;

    /// <summary>Returns a font with the same face, unit and DPI at another size.</summary>
    public Font WithSize(float size) => new(this.Face, size, this.Unit, this.Dpi);

    /// <inheritdoc/>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{this.Face.FullName} {this.Size}{(this.Unit == FontSizeUnit.Point ? "pt" : "px")}");
}

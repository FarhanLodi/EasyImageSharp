using System.Numerics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>How a 3D colour lookup table is sampled between its lattice points.</summary>
public enum LutInterpolation
{
    /// <summary>
    /// Splits each lattice cell into six tetrahedra and blends the four corners of the one containing the
    /// colour. Preserves the grey axis exactly and is the usual choice for colour grading LUTs.
    /// </summary>
    Tetrahedral,

    /// <summary>Blends the eight corners of the lattice cell; slightly softer than tetrahedral.</summary>
    Trilinear,
}

/// <summary>
/// A colour lookup table: an optional per-channel 1D table (a "shaper") followed by an optional 3D lattice, as
/// stored by Adobe/Resolve <c>.cube</c> files and Hald CLUT images. Apply it with
/// <see cref="IImageProcessingContext.ApplyLut(ColorLut, LutInterpolation, float)"/>.
/// </summary>
/// <remarks>
/// Instances are immutable and thread-safe. Lattice values are stored in single precision and may lie
/// outside 0-1 (HDR LUTs); results are clamped only by the pixel format that receives them.
/// </remarks>
public sealed partial class ColorLut
{
    /// <summary>The largest 3D lattice size the <c>.cube</c> specification allows (256 points per axis).</summary>
    public const int MaxCubeSize = 256;

    /// <summary>The largest 1D table length the <c>.cube</c> specification allows.</summary>
    public const int MaxShaperSize = 65536;

    private readonly float[]? shaper;
    private readonly float[]? lattice;
    private readonly Vector3 shaperMin;
    private readonly Vector3 shaperScale;
    private readonly Vector3 latticeMin;
    private readonly Vector3 latticeScale;

    private ColorLut(string? title, float[]? shaper, int shaperSize, Vector3 shaperMin, Vector3 shaperMax,
        float[]? lattice, int cubeSize, Vector3 latticeMin, Vector3 latticeMax)
    {
        this.Title = title;
        this.shaper = shaper;
        this.ShaperSize = shaperSize;
        this.lattice = lattice;
        this.CubeSize = cubeSize;
        this.shaperMin = shaperMin;
        this.latticeMin = latticeMin;
        this.shaperScale = shaperSize > 0 ? new Vector3(shaperSize - 1) / (shaperMax - shaperMin) : Vector3.Zero;
        this.latticeScale = cubeSize > 0 ? new Vector3(cubeSize - 1) / (latticeMax - latticeMin) : Vector3.Zero;
        this.DomainMin = shaperSize > 0 ? shaperMin : latticeMin;
        this.DomainMax = shaperSize > 0 ? shaperMax : latticeMax;
    }

    /// <summary>The <c>TITLE</c> of a <c>.cube</c> file, or <see langword="null"/>.</summary>
    public string? Title { get; }

    /// <summary>Points per axis of the 3D lattice, or 0 when the table is 1D only.</summary>
    public int CubeSize { get; }

    /// <summary>Entries of the per-channel 1D table, or 0 when there is none.</summary>
    public int ShaperSize { get; }

    /// <summary>The input value mapped to the first table entry, per channel (0 unless the file sets a domain).</summary>
    public Vector3 DomainMin { get; }

    /// <summary>The input value mapped to the last table entry, per channel (1 unless the file sets a domain).</summary>
    public Vector3 DomainMax { get; }

    /// <summary>Creates the identity 3D lattice of the given size (2-256 points per axis).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is outside 2-256.</exception>
    public static ColorLut CreateIdentity(int size)
    {
        if (size is < 2 or > MaxCubeSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A 3D LUT has between 2 and 256 points per axis.");
        }

        float[] table = new float[size * size * size * 3];
        int i = 0;
        for (int b = 0; b < size; b++)
        {
            for (int g = 0; g < size; g++)
            {
                for (int r = 0; r < size; r++)
                {
                    table[i++] = r / (float)(size - 1);
                    table[i++] = g / (float)(size - 1);
                    table[i++] = b / (float)(size - 1);
                }
            }
        }

        return new ColorLut(null, null, 0, Vector3.Zero, Vector3.One, table, size, Vector3.Zero, Vector3.One);
    }

    /// <summary>
    /// Reads a Hald CLUT image: a square of <c>level^3</c> pixels per side (level 2-16) holding a lattice of
    /// <c>level^2</c> points per axis with red varying fastest, then green, then blue, row by row. Wide pixel
    /// formats keep their full precision. Alpha is ignored.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="hald"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The image is not a Hald CLUT (not square with a cube-number side).</exception>
    public static ColorLut FromHaldClut<TPixel>(Image<TPixel> hald)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(hald);
        hald.EnsureNotDisposed();
        int side = hald.Width;
        int level = 0;
        for (int l = 2; l <= 16; l++)
        {
            if (l * l * l == side)
            {
                level = l;
            }
        }

        if (level == 0 || hald.Height != side)
        {
            throw new ArgumentException(FormattableString.Invariant(
                $"A Hald CLUT is a square image of level^3 pixels per side (8x8, 27x27, ..., 4096x4096); this one is {hald.Width}x{hald.Height}."), nameof(hald));
        }

        int size = level * level;
        float[] table = new float[size * size * size * 3];
        ImageFrame<TPixel> frame = hald.Frames.RootFrame;
        for (int y = 0; y < side; y++)
        {
            Span<TPixel> row = frame.GetRowSpan(y);
            for (int x = 0; x < side; x++)
            {
                Vector4 v = row[x].ToScaledVector4();
                int i = ((y * side) + x) * 3;
                table[i] = v.X;
                table[i + 1] = v.Y;
                table[i + 2] = v.Z;
            }
        }

        return new ColorLut(null, null, 0, Vector3.Zero, Vector3.One, table, size, Vector3.Zero, Vector3.One);
    }

    /// <summary>
    /// Maps one colour (normalised R, G, B) through the table: the 1D shaper first, then the 3D lattice. Inputs
    /// outside the domain are clamped to its edges; the output is not clamped.
    /// </summary>
    public Vector3 Apply(Vector3 rgb, LutInterpolation interpolation = LutInterpolation.Tetrahedral)
    {
        if (interpolation is not (LutInterpolation.Tetrahedral or LutInterpolation.Trilinear))
        {
            throw new ArgumentOutOfRangeException(nameof(interpolation), interpolation, "Unknown interpolation.");
        }

        return this.Map(rgb, interpolation == LutInterpolation.Tetrahedral);
    }

    internal Vector3 Map(Vector3 rgb, bool tetrahedral)
    {
        if (this.shaper is not null)
        {
            rgb = this.MapShaper(rgb);
        }

        if (this.lattice is not null)
        {
            rgb = this.MapLattice(rgb, tetrahedral);
        }

        return rgb;
    }

    private Vector3 MapShaper(Vector3 rgb)
    {
        float[] table = this.shaper!;
        int last = this.ShaperSize - 1;
        Vector3 position = Vector3.Clamp((rgb - this.shaperMin) * this.shaperScale, Vector3.Zero, new Vector3(last));
        return new Vector3(
            Lerp1D(table, position.X, last, 0),
            Lerp1D(table, position.Y, last, 1),
            Lerp1D(table, position.Z, last, 2));
    }

    private static float Lerp1D(float[] table, float position, int last, int channel)
    {
        if (float.IsNaN(position))
        {
            position = 0;
        }

        int i = Math.Min((int)position, last - 1);
        float t = position - i;
        float a = table[(i * 3) + channel];
        float b = table[((i + 1) * 3) + channel];
        return a + ((b - a) * t);
    }

    private Vector3 MapLattice(Vector3 rgb, bool tetrahedral)
    {
        float[] table = this.lattice!;
        int n = this.CubeSize;
        int last = n - 1;
        Vector3 p = Vector3.Clamp((rgb - this.latticeMin) * this.latticeScale, Vector3.Zero, new Vector3(last));
        float px = float.IsNaN(p.X) ? 0 : p.X;
        float py = float.IsNaN(p.Y) ? 0 : p.Y;
        float pz = float.IsNaN(p.Z) ? 0 : p.Z;
        int r0 = Math.Min((int)px, last - 1);
        int g0 = Math.Min((int)py, last - 1);
        int b0 = Math.Min((int)pz, last - 1);
        float fr = px - r0;
        float fg = py - g0;
        float fb = pz - b0;

        // Offsets of the eight cell corners; index = (r + g n + b n^2) * 3.
        int c000 = (r0 + (g0 * n) + (b0 * n * n)) * 3;
        int dr = 3;
        int dg = n * 3;
        int db = n * n * 3;
        Vector3 v000 = Load(table, c000);
        Vector3 v111 = Load(table, c000 + dr + dg + db);
        if (!tetrahedral)
        {
            Vector3 v100 = Load(table, c000 + dr);
            Vector3 v010 = Load(table, c000 + dg);
            Vector3 v110 = Load(table, c000 + dr + dg);
            Vector3 v001 = Load(table, c000 + db);
            Vector3 v101 = Load(table, c000 + dr + db);
            Vector3 v011 = Load(table, c000 + dg + db);
            Vector3 c00 = Vector3.Lerp(v000, v100, fr);
            Vector3 c10 = Vector3.Lerp(v010, v110, fr);
            Vector3 c01 = Vector3.Lerp(v001, v101, fr);
            Vector3 c11 = Vector3.Lerp(v011, v111, fr);
            return Vector3.Lerp(Vector3.Lerp(c00, c10, fg), Vector3.Lerp(c01, c11, fg), fb);
        }

        // The six tetrahedra of the cell, selected by the ordering of the fractional coordinates.
        if (fr > fg)
        {
            if (fg > fb)
            {
                return ((1 - fr) * v000) + ((fr - fg) * Load(table, c000 + dr)) + ((fg - fb) * Load(table, c000 + dr + dg)) + (fb * v111);
            }

            if (fr > fb)
            {
                return ((1 - fr) * v000) + ((fr - fb) * Load(table, c000 + dr)) + ((fb - fg) * Load(table, c000 + dr + db)) + (fg * v111);
            }

            return ((1 - fb) * v000) + ((fb - fr) * Load(table, c000 + db)) + ((fr - fg) * Load(table, c000 + dr + db)) + (fg * v111);
        }

        if (fb > fg)
        {
            return ((1 - fb) * v000) + ((fb - fg) * Load(table, c000 + db)) + ((fg - fr) * Load(table, c000 + dg + db)) + (fr * v111);
        }

        if (fb > fr)
        {
            return ((1 - fg) * v000) + ((fg - fb) * Load(table, c000 + dg)) + ((fb - fr) * Load(table, c000 + dg + db)) + (fr * v111);
        }

        return ((1 - fg) * v000) + ((fg - fr) * Load(table, c000 + dg)) + ((fr - fb) * Load(table, c000 + dr + dg)) + (fb * v111);
    }

    private static Vector3 Load(float[] table, int index) => new(table[index], table[index + 1], table[index + 2]);
}

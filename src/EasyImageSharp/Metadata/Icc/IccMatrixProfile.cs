using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EasyImageSharp.ColorSpaces;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Metadata.Icc;

/// <summary>
/// A relative-colorimetric RGB matrix/TRC ICC v2/v4 converter with D50 XYZ connection space.
/// Supports monotonic curveType and parametricCurveType 0-4. LUT profiles, other color spaces,
/// absolute/perceptual intents and black-point compensation are not supported. Device RGB is clipped to 0-1.
/// </summary>
public sealed class IccMatrixProfile
{
    private readonly Matrix4x4 matrix;
    private readonly Matrix4x4 inverse;
    private readonly Curve red;
    private readonly Curve green;
    private readonly Curve blue;

    /// <summary>Lazily built per-channel TRC tables for 8-bit input; see <see cref="LinearTables8"/>.</summary>
    private float[]? linearTables8;

    /// <summary>0 = not yet measured, 1 = equivalent to sRGB, 2 = not equivalent.</summary>
    private int srgbEquivalence;

    private IccMatrixProfile(Matrix4x4 matrix, Matrix4x4 inverse, Curve red, Curve green, Curve blue)
    {
        this.matrix = matrix;
        this.inverse = inverse;
        this.red = red;
        this.green = green;
        this.blue = blue;
    }

    /// <summary>Validates and reads the supported matrix/TRC subset; unsupported profiles throw NotSupportedException.</summary>
    public static IccMatrixProfile Create(IccProfile profile) => Create(profile, ignoreLutTags: false);

    /// <summary>
    /// Reads the matrix/TRC subset. With <paramref name="ignoreLutTags"/> a profile that also carries A2B/B2A/D2B/B2D
    /// transforms is read from its matrix/TRC tags instead of being rejected; the library only does that to recognise
    /// such a profile as plain sRGB, never to convert pixels through it.
    /// </summary>
    internal static IccMatrixProfile Create(IccProfile profile, bool ignoreLutTags)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ReadOnlySpan<byte> bytes = profile.RawData;
        if (!profile.Header.IsValid || bytes.Length < 132 || profile.Header.Size < 132 || profile.Header.Size > bytes.Length)
            throw new ArgumentException("Invalid ICC header or declared size.", nameof(profile));
        if (profile.Header.ColorSpace != "RGB " || profile.Header.ConnectionSpace != "XYZ "
            || profile.Header.Version.Major is not (2 or 4)
            || profile.Header.ProfileClass is not ("mntr" or "scnr"))
            throw new NotSupportedException("Only ICC v2/v4 RGB display/input matrix profiles with XYZ PCS are supported.");
        bytes = bytes[..(int)profile.Header.Size];
        uint count = BinaryPrimitives.ReadUInt32BigEndian(bytes[128..]);
        long end = 132 + (12L * count);
        if (count > 4096 || end > bytes.Length) throw new ArgumentException("Invalid ICC tag table.", nameof(profile));
        // Keep extents only: ICC permits multiple entries to reference the same payload. Copying every
        // tag would amplify a small profile into many duplicate large buffers before any conversion.
        var tags = new Dictionary<string, (int Offset, int Size)>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            int entry = 132 + (12 * i);
            string name = Encoding.ASCII.GetString(bytes.Slice(entry, 4));
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(bytes[(entry + 4)..]);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes[(entry + 8)..]);
            if (offset < end || (long)offset + size > bytes.Length || size < 8)
                throw new ArgumentException("Invalid ICC tag extent.", nameof(profile));
            if (!ignoreLutTags
                && (name.StartsWith("A2B", StringComparison.Ordinal) || name.StartsWith("B2A", StringComparison.Ordinal)
                || name.StartsWith("D2B", StringComparison.Ordinal) || name.StartsWith("B2D", StringComparison.Ordinal)))
                throw new NotSupportedException("LUT and multi-process ICC transforms require a full color-management engine.");
            if (!tags.TryAdd(name, ((int)offset, (int)size)))
                throw new ArgumentException("Duplicate ICC tag signature.", nameof(profile));
        }

        Vector3 r = Xyz(Tag("rXYZ")), g = Xyz(Tag("gXYZ")), b = Xyz(Tag("bXYZ"));
        var matrix = new Matrix4x4(r.X, r.Y, r.Z, 0, g.X, g.Y, g.Z, 0, b.X, b.Y, b.Z, 0, 0, 0, 0, 1);
        if (!Matrix4x4.Invert(matrix, out Matrix4x4 inverse) || MathF.Abs(matrix.GetDeterminant()) < 1e-10f)
            throw new ArgumentException("The ICC colorant matrix is singular.", nameof(profile));
        return new(matrix, inverse, Curve.Parse(Tag("rTRC")), Curve.Parse(Tag("gTRC")), Curve.Parse(Tag("bTRC")))
        {
            Description = profile.Header.Description,
        };

        ReadOnlySpan<byte> Tag(string name) => tags.TryGetValue(name, out var extent)
            ? profile.RawData.Slice(extent.Offset, extent.Size)
            : throw new NotSupportedException($"Required matrix/TRC tag '{name}' is missing.");
    }

    /// <summary>The description tag of the profile this converter was read from, or <see langword="null"/>.</summary>
    internal string? Description { get; private init; }

    /// <summary>Converts encoded device RGB to media-relative D50 XYZ (Y=1 for reference white).</summary>
    public Vector3 ToXyzD50(Vector3 rgb)
    {
        Finite(rgb);
        return Vector3.Transform(new Vector3(this.red.Evaluate(rgb.X), this.green.Evaluate(rgb.Y), this.blue.Evaluate(rgb.Z)), this.matrix);
    }

    /// <summary>
    /// Converts media-relative D50 XYZ to encoded device RGB, clipping channels to the profile gamut.
    /// Flat curve segments use a deterministic lower-bound inverse; clipped or discontinuous curves are not lossless.
    /// </summary>
    public Vector3 FromXyzD50(Vector3 xyz)
    {
        Finite(xyz);
        Vector3 linear = Vector3.Transform(xyz, this.inverse);
        return new(this.red.Invert(linear.X), this.green.Invert(linear.Y), this.blue.Invert(linear.Z));
    }

    /// <summary>Converts device RGB to encoded sRGB through D50/D65 Bradford adaptation, without sRGB gamut clipping.</summary>
    public Vector3 ToSrgb(Vector3 rgb) => ColorSpaceConverter.XyzToSrgb(ColorSpaceConverter.XyzD50ToD65(this.ToXyzD50(rgb)));

    /// <summary>Converts encoded sRGB to device RGB through Bradford adaptation.</summary>
    public Vector3 FromSrgb(Vector3 rgb) => this.FromXyzD50(ColorSpaceConverter.XyzD65ToD50(ColorSpaceConverter.SrgbToXyz(rgb)));

    /// <summary>
    /// Converts all frames from this profile to encoded sRGB in a new image, retaining alpha and source metadata.
    /// Clears the copied ICC profile because its device-space encoding no longer describes the output pixels.
    /// </summary>
    public Image<TPixel> ConvertToSrgb<TPixel>(Image<TPixel> source) where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(source);
        Image<TPixel> result = source.Clone();
        try
        {
            foreach (ImageFrame<TPixel> frame in result.Frames)
            {
                frame.Metadata.IccProfile = null;
                for (int y = 0; y < frame.Height; y++)
                {
                    OperationExecution.Checkpoint();
                    Span<TPixel> row = frame.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        Vector4 value = row[x].ToScaledVector4();
                        Vector3 converted = this.ToSrgb(new(value.X, value.Y, value.Z));
                        row[x] = TPixel.FromScaledVector4(new Vector4(converted, value.W));
                    }
                }
            }
            result.Metadata.IccProfile = null;
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True when converting through this profile would leave every sampled colour within half an 8-bit code value of
    /// where it started: the profile is sRGB in all but its encoding (an ICC v2 table curve, s15Fixed16 rounding of
    /// the colorants). Measured once over the full 8-bit ramp of each channel and of grey, plus a 9x9x9 lattice.
    /// </summary>
    internal bool IsSrgbEquivalent
    {
        get
        {
            int state = Volatile.Read(ref this.srgbEquivalence);
            if (state == 0)
            {
                state = this.MaxDeviationFromSrgb() <= SrgbEquivalenceTolerance ? 1 : 2;
                Volatile.Write(ref this.srgbEquivalence, state);
            }

            return state == 1;
        }
    }

    /// <summary>The largest per-channel deviation, in encoded 0-1 units, that still counts as sRGB: half an 8-bit step.</summary>
    internal const float SrgbEquivalenceTolerance = 0.5f / 255f;

    /// <summary>
    /// Per-channel TRC lookup for 8-bit input, red then green then blue, 256 entries each: entry <c>i</c> is the
    /// curve evaluated at <c>i / 255f</c>, which is exactly what the per-pixel path evaluates for an 8-bit
    /// component, so the table is a bit-exact shortcut rather than an approximation.
    /// </summary>
    internal float[] LinearTables8
    {
        get
        {
            float[]? tables = Volatile.Read(ref this.linearTables8);
            if (tables is null)
            {
                tables = new float[3 * 256];
                for (int i = 0; i < 256; i++)
                {
                    float value = i / 255f;
                    tables[i] = this.red.Evaluate(value);
                    tables[256 + i] = this.green.Evaluate(value);
                    tables[512 + i] = this.blue.Evaluate(value);
                }

                // A racing thread builds an identical table; either copy may win.
                Volatile.Write(ref this.linearTables8, tables);
            }

            return tables;
        }
    }

    /// <summary>
    /// The per-pixel form of <see cref="ToSrgb"/>: bit-identical for finite input, but without the finiteness
    /// checks, and a NaN component is read as 0 instead of throwing. Out-of-range input is clamped by the curves.
    /// </summary>
    internal Vector3 ToSrgbCore(Vector3 rgb)
        => this.LinearToSrgbCore(new Vector3(
            this.red.Evaluate(float.IsNaN(rgb.X) ? 0 : rgb.X),
            this.green.Evaluate(float.IsNaN(rgb.Y) ? 0 : rgb.Y),
            this.blue.Evaluate(float.IsNaN(rgb.Z) ? 0 : rgb.Z)));

    /// <summary>Converts device RGB whose TRCs have already been applied (see <see cref="LinearTables8"/>) to encoded sRGB.</summary>
    internal Vector3 LinearToSrgbCore(Vector3 linear)
        => ColorSpaceConverter.XyzD50ToSrgbCore(Vector3.Transform(linear, this.matrix));

    private float MaxDeviationFromSrgb()
    {
        float worst = 0;
        for (int i = 0; i <= 255; i++)
        {
            float v = i / 255f;
            worst = MathF.Max(worst, this.Deviation(new Vector3(v, 0, 0)));
            worst = MathF.Max(worst, this.Deviation(new Vector3(0, v, 0)));
            worst = MathF.Max(worst, this.Deviation(new Vector3(0, 0, v)));
            worst = MathF.Max(worst, this.Deviation(new Vector3(v)));
        }

        for (int r = 0; r <= 8; r++)
        {
            for (int g = 0; g <= 8; g++)
            {
                for (int b = 0; b <= 8; b++)
                {
                    worst = MathF.Max(worst, this.Deviation(new Vector3(r, g, b) / 8f));
                }
            }
        }

        return worst;
    }

    private float Deviation(Vector3 rgb)
    {
        Vector3 difference = Vector3.Abs(this.ToSrgbCore(rgb) - rgb);
        float result = MathF.Max(difference.X, MathF.Max(difference.Y, difference.Z));
        return float.IsNaN(result) ? float.PositiveInfinity : result;
    }

    private static Vector3 Xyz(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20 || !data[..4].SequenceEqual("XYZ "u8)) throw new ArgumentException("Invalid XYZ colorant tag.");
        return new(Fixed(data[8..]), Fixed(data[12..]), Fixed(data[16..]));
    }
    private static float Fixed(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadInt32BigEndian(value) / 65536f;
    private static void Finite(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(nameof(value), "Color components must be finite.");
    }

    private sealed class Curve
    {
        private readonly float[] values;
        private readonly int kind;
        private Curve(int kind, float[] values) { this.kind = kind; this.values = values; }
        internal static Curve Parse(ReadOnlySpan<byte> data)
        {
            if (data.Length < 12) throw new ArgumentException("Truncated ICC curve.");
            Curve curve;
            if (data[..4].SequenceEqual("curv"u8))
            {
                uint count = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
                if (count > (data.Length - 12L) / 2) throw new ArgumentException("Truncated ICC curve samples.");
                if (count == 0) return new Curve(0, [1]);
                if (count == 1) curve = new Curve(0, [BinaryPrimitives.ReadUInt16BigEndian(data[12..]) / 256f]);
                else
                {
                    float[] values = new float[(int)count];
                    for (int i = 0; i < values.Length; i++)
                    {
                        values[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(12 + (2 * i))..]) / 65535f;
                        if (i > 0 && values[i] < values[i - 1]) throw new NotSupportedException("Nonmonotonic ICC curves cannot be inverted.");
                    }
                    curve = new Curve(-1, values);
                }
            }
            else if (data[..4].SequenceEqual("para"u8))
            {
                int kind = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
                int count = kind switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => throw new NotSupportedException("Unsupported ICC parametric curve type.") };
                if (data.Length < 12 + (4 * count)) throw new ArgumentException("Truncated ICC parametric curve.");
                float[] values = new float[count];
                for (int i = 0; i < count; i++) values[i] = Fixed(data[(12 + (4 * i))..]);
                curve = new Curve(kind, values);
            }
            else throw new NotSupportedException("Unsupported ICC curve tag type.");

            if (curve.kind >= 0 && (curve.values[0] <= 0 || (curve.kind >= 1 && curve.values[1] <= 0)
                || (curve.kind >= 3 && (curve.values[3] < 0 || curve.values[4] < 0 || curve.values[4] > 1
                    || (curve.values[1] * curve.values[4]) + curve.values[2] < 0))))
                throw new NotSupportedException("Invalid or nonmonotonic ICC curve parameters.");
            float previous = curve.Evaluate(0);
            if (!float.IsFinite(previous)) throw new NotSupportedException("Invalid ICC curve endpoint.");
            if (curve.kind >= 3)
            {
                float[] p = curve.values;
                float below = (p[3] * p[4]) + (curve.kind == 4 ? p[6] : 0);
                float above = MathF.Pow((p[1] * p[4]) + p[2], p[0]) + (curve.kind == 4 ? p[5] : 0);
                if (above + 1e-6f < below) throw new NotSupportedException("Nonmonotonic ICC curve breakpoint.");
            }
            for (int i = 1; i <= 4096; i++)
            {
                float current = curve.Evaluate(i / 4096f);
                if (!float.IsFinite(current) || current + 1e-6f < previous) throw new NotSupportedException("Invalid or nonmonotonic ICC curve.");
                previous = current;
            }
            return curve;
        }

        internal float Evaluate(float x)
        {
            x = Math.Clamp(x, 0, 1);
            float[] p = this.values;
            float value;
            if (this.kind == -1)
            {
                float position = x * (p.Length - 1);
                int index = Math.Min((int)position, p.Length - 2);
                return p[index] + ((p[index + 1] - p[index]) * (position - index));
            }
            if (this.kind == 0) value = MathF.Pow(x, p[0]);
            else if (this.kind <= 2) value = x >= -p[2] / p[1] ? MathF.Pow((p[1] * x) + p[2], p[0]) : 0;
            else value = x >= p[4] ? MathF.Pow((p[1] * x) + p[2], p[0]) + (this.kind == 4 ? p[5] : 0)
                : (p[3] * x) + (this.kind == 4 ? p[6] : 0);
            if (this.kind == 2) value += p[3];
            return Math.Clamp(value, 0, 1);
        }

        internal float Invert(float value)
        {
            if (value <= this.Evaluate(0)) return 0;
            if (value >= this.Evaluate(1)) return 1;
            float low = 0, high = 1;
            for (int i = 0; i < 25; i++)
            {
                float middle = (low + high) / 2;
                if (this.Evaluate(middle) < value) low = middle; else high = middle;
            }
            return (low + high) / 2;
        }
    }
}

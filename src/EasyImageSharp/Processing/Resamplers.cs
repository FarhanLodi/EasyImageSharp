namespace EasyImageSharp.Processing;

/// <summary>A resampling kernel used by resize and geometric transforms.</summary>
public interface IResampler
{
    /// <summary>The kernel radius in source pixels.</summary>
    float Radius { get; }

    /// <summary>Evaluates the kernel at the given distance from the center.</summary>
    float GetValue(float x);
}

/// <summary>
/// The resamplers shipped with the library. Every kernel is symmetric, evaluates to 1 at the origin (except
/// <see cref="Box"/> which is 1 over its whole support) and is normalised per output pixel by the caller, so
/// kernels that are not an exact partition of unity (Lanczos, Welch) do not shift brightness.
/// </summary>
public static class KnownResamplers
{
    /// <summary>Point sampling; fastest, blocky results, exact for integer scale factors and right angles.</summary>
    public static IResampler NearestNeighbor { get; } = new NearestNeighborResampler();

    /// <summary>Box (area-average) kernel of radius 0.5; the ideal kernel for integer-factor downscaling.</summary>
    public static IResampler Box { get; } = new BoxResampler();

    /// <summary>Tent kernel of radius 1; equivalent to bilinear interpolation.</summary>
    public static IResampler Triangle { get; } = new TriangleResampler();

    /// <summary>Alias for <see cref="Triangle"/>.</summary>
    public static IResampler Bilinear => Triangle;

    /// <summary>Cubic Hermite kernel of radius 1 (B = 0, C = 0): smoothstep between neighbours, no overshoot.</summary>
    public static IResampler Hermite { get; } = new HermiteResampler();

    /// <summary>Keys cubic with a = -0.5 (radius 2); the library default. Numerically identical to Catmull-Rom.</summary>
    public static IResampler Bicubic { get; } = new BicubicResampler();

    /// <summary>Catmull-Rom cubic (B = 0, C = 0.5, radius 2); interpolating, mildly sharp.</summary>
    public static IResampler CatmullRom { get; } = new CatmullRomResampler();

    /// <summary>Mitchell-Netravali cubic (B = C = 1/3, radius 2); the classic compromise between blur and ringing.</summary>
    public static IResampler MitchellNetravali { get; } = new MitchellNetravaliResampler();

    /// <summary>Robidoux cubic (radius 2); a Keys cubic tuned to behave like Lanczos on EWA resampling.</summary>
    public static IResampler Robidoux { get; } = new RobidouxResampler();

    /// <summary>Sharper Robidoux cubic (radius 2).</summary>
    public static IResampler RobidouxSharp { get; } = new RobidouxSharpResampler();

    /// <summary>Cubic B-spline (B = 1, C = 0, radius 2); smoothest cubic, blurs but never rings.</summary>
    public static IResampler Spline { get; } = new SplineResampler();

    /// <summary>Lanczos windowed sinc with radius 2.</summary>
    public static IResampler Lanczos2 { get; } = new Lanczos2Resampler();

    /// <summary>Lanczos windowed sinc with radius 3; sharpest common choice for downscaling.</summary>
    public static IResampler Lanczos3 { get; } = new Lanczos3Resampler();

    /// <summary>Lanczos windowed sinc with radius 5.</summary>
    public static IResampler Lanczos5 { get; } = new Lanczos5Resampler();

    /// <summary>Lanczos windowed sinc with radius 8; slowest, closest to an ideal low-pass filter.</summary>
    public static IResampler Lanczos8 { get; } = new Lanczos8Resampler();

    /// <summary>Welch-windowed sinc with radius 3.</summary>
    public static IResampler Welch { get; } = new WelchResampler();
}

/// <summary>Point sampling; fastest, blocky results. <c>k(x) = 1</c> for <c>-0.5 &lt; x &lt;= 0.5</c>, else 0.</summary>
public sealed class NearestNeighborResampler : IResampler
{
    /// <inheritdoc/>
    public float Radius => 1f;

    /// <inheritdoc/>
    public float GetValue(float x) => x is > -0.5f and <= 0.5f ? 1f : 0f;
}

/// <summary>Box kernel: <c>k(x) = 1</c> for <c>-0.5 &lt; x &lt;= 0.5</c>, else 0; radius 0.5.</summary>
public sealed class BoxResampler : IResampler
{
    /// <inheritdoc/>
    public float Radius => 0.5f;

    /// <inheritdoc/>
    public float GetValue(float x) => x is > -0.5f and <= 0.5f ? 1f : 0f;
}

/// <summary>Tent kernel <c>k(x) = 1 - |x|</c> for <c>|x| &lt; 1</c>; equivalent to bilinear interpolation.</summary>
public sealed class TriangleResampler : IResampler
{
    /// <inheritdoc/>
    public float Radius => 1f;

    /// <inheritdoc/>
    public float GetValue(float x)
    {
        x = MathF.Abs(x);
        return x < 1f ? 1f - x : 0f;
    }
}

/// <summary>Cubic Hermite kernel <c>k(x) = 2|x|^3 - 3|x|^2 + 1</c> for <c>|x| &lt; 1</c> (BC-spline with B = 0, C = 0).</summary>
public sealed class HermiteResampler : IResampler
{
    /// <inheritdoc/>
    public float Radius => 1f;

    /// <inheritdoc/>
    public float GetValue(float x)
    {
        x = MathF.Abs(x);
        return x < 1f ? (((2f * x) - 3f) * x * x) + 1f : 0f;
    }
}

/// <summary>
/// Keys cubic kernel with a = -0.5:
/// <c>k(x) = 1.5|x|^3 - 2.5|x|^2 + 1</c> for <c>|x| &lt; 1</c>,
/// <c>-0.5(|x|^3 - 5|x|^2 + 8|x| - 4)</c> for <c>1 &lt;= |x| &lt; 2</c>; a good general-purpose default.
/// </summary>
public sealed class BicubicResampler : IResampler
{
    /// <inheritdoc/>
    public float Radius => 2f;

    /// <inheritdoc/>
    public float GetValue(float x)
    {
        x = MathF.Abs(x);
        if (x < 1f)
        {
            return (1.5f * x * x * x) - (2.5f * x * x) + 1f;
        }

        if (x < 2f)
        {
            return -0.5f * ((x * x * x) - (5f * x * x) + (8f * x) - 4f);
        }

        return 0f;
    }
}

/// <summary>
/// The Mitchell-Netravali family of cubic kernels parameterised by (B, C):
/// <code>
/// |x| &lt; 1: ((12 - 9B - 6C)|x|^3 + (-18 + 12B + 6C)|x|^2 + (6 - 2B)) / 6
/// 1 &lt;= |x| &lt; 2: ((-B - 6C)|x|^3 + (6B + 30C)|x|^2 + (-12B - 48C)|x| + (8B + 24C)) / 6
/// </code>
/// Radius 2. Catmull-Rom is (0, 0.5), Mitchell-Netravali is (1/3, 1/3), the B-spline is (1, 0).
/// </summary>
public abstract class CubicResampler : IResampler
{
    private readonly float p0;
    private readonly float p2;
    private readonly float p3;
    private readonly float q0;
    private readonly float q1;
    private readonly float q2;
    private readonly float q3;

    /// <summary>Initializes the kernel for the given B and C parameters.</summary>
    protected CubicResampler(float b, float c)
    {
        this.B = b;
        this.C = c;
        this.p0 = (6f - (2f * b)) / 6f;
        this.p2 = (-18f + (12f * b) + (6f * c)) / 6f;
        this.p3 = (12f - (9f * b) - (6f * c)) / 6f;
        this.q0 = ((8f * b) + (24f * c)) / 6f;
        this.q1 = ((-12f * b) - (48f * c)) / 6f;
        this.q2 = ((6f * b) + (30f * c)) / 6f;
        this.q3 = (-b - (6f * c)) / 6f;
    }

    /// <summary>The B parameter (blur).</summary>
    public float B { get; }

    /// <summary>The C parameter (ringing).</summary>
    public float C { get; }

    /// <inheritdoc/>
    public float Radius => 2f;

    /// <inheritdoc/>
    public float GetValue(float x)
    {
        x = MathF.Abs(x);
        if (x < 1f)
        {
            return (((this.p3 * x) + this.p2) * x * x) + this.p0;
        }

        if (x < 2f)
        {
            return (((((this.q3 * x) + this.q2) * x) + this.q1) * x) + this.q0;
        }

        return 0f;
    }
}

/// <summary>Catmull-Rom spline: <see cref="CubicResampler"/> with B = 0, C = 0.5.</summary>
public sealed class CatmullRomResampler : CubicResampler
{
    public CatmullRomResampler()
        : base(0f, 0.5f)
    {
    }
}

/// <summary>Mitchell-Netravali "no. 2" kernel: <see cref="CubicResampler"/> with B = C = 1/3.</summary>
public sealed class MitchellNetravaliResampler : CubicResampler
{
    public MitchellNetravaliResampler()
        : base(1f / 3f, 1f / 3f)
    {
    }
}

/// <summary>Robidoux kernel: <see cref="CubicResampler"/> with B = 12 / (19 + 9 sqrt 2), C = 113 / (58 + 216 sqrt 2).</summary>
public sealed class RobidouxResampler : CubicResampler
{
    public RobidouxResampler()
        : base(0.37821575509399867f, 0.31089212245300067f)
    {
    }
}

/// <summary>Robidoux-sharp kernel: <see cref="CubicResampler"/> with B = 6 / (13 + 7 sqrt 2), C = 7 / (2 + 12 sqrt 2).</summary>
public sealed class RobidouxSharpResampler : CubicResampler
{
    public RobidouxSharpResampler()
        : base(0.2620145123990142f, 0.3689927438004929f)
    {
    }
}

/// <summary>Cubic B-spline: <see cref="CubicResampler"/> with B = 1, C = 0.</summary>
public sealed class SplineResampler : CubicResampler
{
    public SplineResampler()
        : base(1f, 0f)
    {
    }
}

/// <summary>
/// Lanczos kernel <c>k(x) = sinc(x) sinc(x / r)</c> for <c>|x| &lt; r</c> where <c>sinc(x) = sin(pi x) / (pi x)</c>.
/// </summary>
public abstract class LanczosResampler : IResampler
{
    /// <summary>Initializes the kernel with the given radius (the window's half-width in lobes).</summary>
    protected LanczosResampler(float radius) => this.Radius = radius;

    /// <inheritdoc/>
    public float Radius { get; }

    /// <inheritdoc/>
    public float GetValue(float x)
    {
        x = MathF.Abs(x);
        if (x >= this.Radius)
        {
            return 0f;
        }

        if (x < 1e-6f)
        {
            return 1f;
        }

        return Sinc(x) * Sinc(x / this.Radius);
    }

    /// <summary>The normalised sinc function <c>sin(pi x) / (pi x)</c>.</summary>
    internal static float Sinc(float x)
    {
        float pix = MathF.PI * x;
        return MathF.Sin(pix) / pix;
    }
}

/// <summary>Lanczos kernel with radius 2.</summary>
public sealed class Lanczos2Resampler : LanczosResampler
{
    public Lanczos2Resampler()
        : base(2f)
    {
    }
}

/// <summary>Lanczos kernel with radius 3; sharpest results for downscaling.</summary>
public sealed class Lanczos3Resampler : LanczosResampler
{
    public Lanczos3Resampler()
        : base(3f)
    {
    }
}

/// <summary>Lanczos kernel with radius 5.</summary>
public sealed class Lanczos5Resampler : LanczosResampler
{
    public Lanczos5Resampler()
        : base(5f)
    {
    }
}

/// <summary>Lanczos kernel with radius 8.</summary>
public sealed class Lanczos8Resampler : LanczosResampler
{
    public Lanczos8Resampler()
        : base(8f)
    {
    }
}

/// <summary>Welch-windowed sinc: <c>k(x) = sinc(x) (1 - (x / 3)^2)</c> for <c>|x| &lt; 3</c>.</summary>
public sealed class WelchResampler : IResampler
{
    /// <inheritdoc/>
    public float Radius => 3f;

    /// <inheritdoc/>
    public float GetValue(float x)
    {
        x = MathF.Abs(x);
        if (x >= 3f)
        {
            return 0f;
        }

        if (x < 1e-6f)
        {
            return 1f;
        }

        return LanczosResampler.Sinc(x) * (1f - (x * x / 9f));
    }
}

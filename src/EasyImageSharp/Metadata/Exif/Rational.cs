using System.Globalization;

namespace EasyImageSharp.Metadata.Exif;

/// <summary>An unsigned rational number as stored by EXIF/TIFF (two 32-bit unsigned integers).</summary>
public readonly struct Rational : IEquatable<Rational>
{
    /// <summary>Creates a rational from a numerator and a denominator.</summary>
    public Rational(uint numerator, uint denominator)
    {
        this.Numerator = numerator;
        this.Denominator = denominator;
    }

    /// <summary>Creates a rational from an integer value (denominator 1).</summary>
    public Rational(uint value)
        : this(value, 1)
    {
    }

    /// <summary>Creates the closest representable rational to <paramref name="value"/> (must be finite and non-negative).</summary>
    public Rational(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "An unsigned rational must be a finite, non-negative number.");
        }

        (ulong n, ulong d) = RationalMath.Approximate(value, uint.MaxValue);
        this.Numerator = (uint)n;
        this.Denominator = (uint)d;
    }

    /// <summary>The numerator.</summary>
    public uint Numerator { get; }

    /// <summary>The denominator (zero denotes an undefined value; EXIF writers occasionally emit 0/0).</summary>
    public uint Denominator { get; }

    /// <summary>Converts the value to a <see cref="double"/>. A zero denominator yields <see cref="double.NaN"/> (0/0) or infinity.</summary>
    public double ToDouble()
        => this.Denominator != 0 ? (double)this.Numerator / this.Denominator
            : this.Numerator == 0 ? double.NaN : double.PositiveInfinity;

    public bool Equals(Rational other) => this.Numerator == other.Numerator && this.Denominator == other.Denominator;

    public override bool Equals(object? obj) => obj is Rational other && this.Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.Numerator, this.Denominator);

    public static bool operator ==(Rational left, Rational right) => left.Equals(right);

    public static bool operator !=(Rational left, Rational right) => !left.Equals(right);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{this.Numerator}/{this.Denominator}");
}

/// <summary>A signed rational number as stored by EXIF/TIFF (two 32-bit signed integers).</summary>
public readonly struct SignedRational : IEquatable<SignedRational>
{
    /// <summary>Creates a rational from a numerator and a denominator.</summary>
    public SignedRational(int numerator, int denominator)
    {
        this.Numerator = numerator;
        this.Denominator = denominator;
    }

    /// <summary>Creates a rational from an integer value (denominator 1).</summary>
    public SignedRational(int value)
        : this(value, 1)
    {
    }

    /// <summary>Creates the closest representable rational to <paramref name="value"/> (must be finite).</summary>
    public SignedRational(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A signed rational must be a finite number.");
        }

        (ulong n, ulong d) = RationalMath.Approximate(Math.Abs(value), int.MaxValue);
        this.Numerator = value < 0 ? -(int)n : (int)n;
        this.Denominator = (int)d;
    }

    /// <summary>The numerator.</summary>
    public int Numerator { get; }

    /// <summary>The denominator (zero denotes an undefined value).</summary>
    public int Denominator { get; }

    /// <summary>Converts the value to a <see cref="double"/>. A zero denominator yields <see cref="double.NaN"/> (0/0) or infinity.</summary>
    public double ToDouble()
        => this.Denominator != 0 ? (double)this.Numerator / this.Denominator
            : this.Numerator == 0 ? double.NaN : this.Numerator > 0 ? double.PositiveInfinity : double.NegativeInfinity;

    public bool Equals(SignedRational other) => this.Numerator == other.Numerator && this.Denominator == other.Denominator;

    public override bool Equals(object? obj) => obj is SignedRational other && this.Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.Numerator, this.Denominator);

    public static bool operator ==(SignedRational left, SignedRational right) => left.Equals(right);

    public static bool operator !=(SignedRational left, SignedRational right) => !left.Equals(right);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{this.Numerator}/{this.Denominator}");
}

/// <summary>Continued-fraction approximation shared by the two rational types.</summary>
internal static class RationalMath
{
    /// <summary>
    /// Finds numerator/denominator approximating a non-negative <paramref name="value"/> with both terms at most
    /// <paramref name="limit"/>. Values with a short decimal expansion (72, 2.5, 0.001) come out as the obvious
    /// fraction, and every result is a best rational approximation of its size.
    /// </summary>
    public static (ulong Numerator, ulong Denominator) Approximate(double value, ulong limit)
    {
        if (value == 0)
        {
            return (0, 1);
        }

        if (value >= limit)
        {
            return (limit, 1);
        }

        // Exact integer.
        if (value == Math.Floor(value))
        {
            return ((ulong)value, 1);
        }

        // Continued fraction expansion with convergents h/k.
        ulong h0 = 0, h1 = 1, k0 = 1, k1 = 0;
        double x = value;
        for (int i = 0; i < 64; i++)
        {
            double floor = Math.Floor(x);
            if (floor > limit)
            {
                break;
            }

            ulong a = (ulong)floor;
            ulong h2, k2;
            try
            {
                h2 = checked((a * h1) + h0);
                k2 = checked((a * k1) + k0);
            }
            catch (OverflowException)
            {
                break;
            }

            if (h2 > limit || k2 > limit)
            {
                break;
            }

            h0 = h1;
            h1 = h2;
            k0 = k1;
            k1 = k2;

            double remainder = x - floor;
            if (remainder < 1e-12 || Math.Abs(((double)h1 / k1) - value) <= value * 1e-15)
            {
                break;
            }

            x = 1.0 / remainder;
        }

        if (k1 == 0)
        {
            return (h1 == 0 ? 0UL : Math.Min(h1, limit), 1);
        }

        return (h1, k1);
    }
}

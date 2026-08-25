using System.Globalization;
using System.Text;

namespace EasyImageSharp.Metadata.Exif;

/// <summary>
/// Conversions between the CLR representations of EXIF values: scalar/array reshaping, integer widening and
/// narrowing with range checks, integer/float/rational bridging. Everything is written with explicit type
/// switches so it stays trimming- and AOT-safe.
/// </summary>
internal static class ExifValueConverter
{
    /// <summary>The field type used when a value of the given CLR type is written and nothing better is known.</summary>
    public static ExifDataType DefaultDataType(Type clrType, bool undefinedByConvention)
    {
        if (clrType == typeof(byte) || clrType == typeof(byte[]))
        {
            return undefinedByConvention ? ExifDataType.Undefined : ExifDataType.Byte;
        }

        if (clrType == typeof(string))
        {
            return undefinedByConvention ? ExifDataType.Undefined : ExifDataType.Ascii;
        }

        if (clrType == typeof(sbyte) || clrType == typeof(sbyte[]))
        {
            return ExifDataType.SignedByte;
        }

        if (clrType == typeof(ushort) || clrType == typeof(ushort[]))
        {
            return ExifDataType.Short;
        }

        if (clrType == typeof(short) || clrType == typeof(short[]))
        {
            return ExifDataType.SignedShort;
        }

        if (clrType == typeof(uint) || clrType == typeof(uint[]))
        {
            return ExifDataType.Long;
        }

        if (clrType == typeof(int) || clrType == typeof(int[]))
        {
            return ExifDataType.SignedLong;
        }

        if (clrType == typeof(float) || clrType == typeof(float[]))
        {
            return ExifDataType.SingleFloat;
        }

        if (clrType == typeof(double) || clrType == typeof(double[]))
        {
            return ExifDataType.DoubleFloat;
        }

        if (clrType == typeof(Rational) || clrType == typeof(Rational[]))
        {
            return ExifDataType.Rational;
        }

        if (clrType == typeof(SignedRational) || clrType == typeof(SignedRational[]))
        {
            return ExifDataType.SignedRational;
        }

        return ExifDataType.Unknown;
    }

    /// <summary>True when values of <paramref name="clrType"/> can be stored in a profile.</summary>
    public static bool IsSupportedType(Type clrType) => DefaultDataType(clrType, false) != ExifDataType.Unknown;

    /// <summary>
    /// Chooses the field type to remember for a value read from a file: the file's own type when it is a legal
    /// alternative encoding of the value's CLR type (so a round trip reproduces it), the CLR default otherwise.
    /// </summary>
    public static ExifDataType ReconcileDataType(ExifDataType actual, ExifDataType preferred)
    {
        if (actual == preferred)
        {
            return actual;
        }

        bool compatible = preferred switch
        {
            ExifDataType.Byte => actual == ExifDataType.Undefined,
            ExifDataType.Undefined => actual is ExifDataType.Byte or ExifDataType.Ascii,
            ExifDataType.Ascii => actual is ExifDataType.Undefined or ExifDataType.Byte,
            ExifDataType.Long => actual is ExifDataType.Short or ExifDataType.Byte or ExifDataType.Ifd,
            ExifDataType.Short => actual is ExifDataType.Long or ExifDataType.Byte,
            ExifDataType.SignedLong => actual is ExifDataType.SignedShort or ExifDataType.SignedByte,
            ExifDataType.SignedShort => actual is ExifDataType.SignedByte,
            ExifDataType.SingleFloat => actual == ExifDataType.DoubleFloat,
            ExifDataType.DoubleFloat => actual == ExifDataType.SingleFloat,
            _ => false,
        };
        return compatible ? actual : preferred;
    }

    /// <summary>Converts an arbitrary supported value into <typeparamref name="T"/>, reshaping and range-checking as needed.</summary>
    public static bool TryConvert<T>(object? source, out T? result)
    {
        // The runtime treats arrays of same-sized primitives as assignment compatible, so "sbyte[] is byte[]"
        // is true; array sources must therefore match the target's element type exactly to take this shortcut.
        if (source is T direct && (!typeof(T).IsArray || source.GetType() == typeof(T)))
        {
            result = direct;
            return true;
        }

        result = default;
        if (source is null)
        {
            return false;
        }

        if (typeof(T) == typeof(string))
        {
            return false; // Only strings convert to strings, and that case was handled above.
        }

        if (source is string)
        {
            return false;
        }

        if (!TryDecompose(source, out List<Element>? elements) || elements is null)
        {
            return false;
        }

        return TryBuild(elements, out result);
    }

    /// <summary>Returns a deep copy of an array value, or the value itself for scalars and strings.</summary>
    public static T? CloneValue<T>(T? value)
        => value is Array array ? (T)(object)array.Clone() : value;

    /// <summary>Human-readable rendering used by <see cref="IExifValue"/>.ToString().</summary>
    public static string Format(object? value)
    {
        switch (value)
        {
            case null:
                return "(null)";
            case string s:
                return s;
            case byte[] bytes when bytes.Length > 16:
                return $"byte[{bytes.Length}]";
            case Array array:
            {
                var builder = new StringBuilder("[");
                for (int i = 0; i < array.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(Convert.ToString(array.GetValue(i), CultureInfo.InvariantCulture));
                }

                return builder.Append(']').ToString();
            }

            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    // ----- Element model -----

    private enum Kind
    {
        Integer,
        Float,
        UnsignedRational,
        SignedRational,
    }

    private readonly struct Element
    {
        public readonly Kind Kind;
        public readonly long Integer;
        public readonly double Float;
        public readonly long Numerator;
        public readonly long Denominator;

        private Element(Kind kind, long integer, double floating, long numerator, long denominator)
        {
            this.Kind = kind;
            this.Integer = integer;
            this.Float = floating;
            this.Numerator = numerator;
            this.Denominator = denominator;
        }

        public static Element FromInteger(long value) => new(Kind.Integer, value, value, value, 1);

        public static Element FromFloat(double value) => new(Kind.Float, 0, value, 0, 0);

        public static Element FromRational(Rational value) => new(Kind.UnsignedRational, 0, value.ToDouble(), value.Numerator, value.Denominator);

        public static Element FromRational(SignedRational value) => new(Kind.SignedRational, 0, value.ToDouble(), value.Numerator, value.Denominator);

        /// <summary>Tries to view the element as an exact integer.</summary>
        public bool TryGetInteger(out long value)
        {
            switch (this.Kind)
            {
                case Kind.Integer:
                    value = this.Integer;
                    return true;
                case Kind.Float:
                    if (double.IsFinite(this.Float) && this.Float == Math.Floor(this.Float) && Math.Abs(this.Float) < 9.2e18)
                    {
                        value = (long)this.Float;
                        return true;
                    }

                    break;
                default:
                    if (this.Denominator != 0 && this.Numerator % this.Denominator == 0)
                    {
                        value = this.Numerator / this.Denominator;
                        return true;
                    }

                    break;
            }

            value = 0;
            return false;
        }

        public double ToDouble() => this.Kind == Kind.Integer ? this.Integer : this.Float;

        public bool TryGetRational(out Rational value)
        {
            switch (this.Kind)
            {
                case Kind.Integer when this.Integer >= 0 && this.Integer <= uint.MaxValue:
                    value = new Rational((uint)this.Integer, 1);
                    return true;
                case Kind.Float when double.IsFinite(this.Float) && this.Float >= 0:
                    value = new Rational(this.Float);
                    return true;
                case Kind.UnsignedRational:
                    value = new Rational((uint)this.Numerator, (uint)this.Denominator);
                    return true;
                case Kind.SignedRational when this.Numerator >= 0 && this.Denominator >= 0:
                    value = new Rational((uint)this.Numerator, (uint)this.Denominator);
                    return true;
                default:
                    value = default;
                    return false;
            }
        }

        public bool TryGetSignedRational(out SignedRational value)
        {
            switch (this.Kind)
            {
                case Kind.Integer when this.Integer >= int.MinValue && this.Integer <= int.MaxValue:
                    value = new SignedRational((int)this.Integer, 1);
                    return true;
                case Kind.Float when double.IsFinite(this.Float):
                    value = new SignedRational(this.Float);
                    return true;
                case Kind.UnsignedRational when this.Numerator <= int.MaxValue && this.Denominator <= int.MaxValue:
                    value = new SignedRational((int)this.Numerator, (int)this.Denominator);
                    return true;
                case Kind.SignedRational:
                    value = new SignedRational((int)this.Numerator, (int)this.Denominator);
                    return true;
                default:
                    value = default;
                    return false;
            }
        }
    }

    private static bool TryDecompose(object source, out List<Element>? elements)
    {
        elements = null;
        switch (source)
        {
            case byte v: elements = new List<Element>(1) { Element.FromInteger(v) }; return true;
            case sbyte v: elements = new List<Element>(1) { Element.FromInteger(v) }; return true;
            case ushort v: elements = new List<Element>(1) { Element.FromInteger(v) }; return true;
            case short v: elements = new List<Element>(1) { Element.FromInteger(v) }; return true;
            case uint v: elements = new List<Element>(1) { Element.FromInteger(v) }; return true;
            case int v: elements = new List<Element>(1) { Element.FromInteger(v) }; return true;
            case long v: elements = new List<Element>(1) { Element.FromInteger(v) }; return true;
            case ulong v when v <= long.MaxValue: elements = new List<Element>(1) { Element.FromInteger((long)v) }; return true;
            case float v: elements = new List<Element>(1) { Element.FromFloat(v) }; return true;
            case double v: elements = new List<Element>(1) { Element.FromFloat(v) }; return true;
            case Rational v: elements = new List<Element>(1) { Element.FromRational(v) }; return true;
            case SignedRational v: elements = new List<Element>(1) { Element.FromRational(v) }; return true;
            default: return TryDecomposeArray(source, out elements);
        }
    }

    /// <summary>
    /// Decomposes an array value. The element type is compared exactly rather than pattern matched, because the
    /// runtime treats arrays of same-sized primitives as assignment compatible: an <see cref="sbyte"/>[] matches
    /// <c>is byte[]</c>, so pattern matching would read signed values as unsigned ones.
    /// </summary>
    private static bool TryDecomposeArray(object source, out List<Element>? elements)
    {
        elements = null;
        if (source.GetType() is not { IsArray: true } arrayType)
        {
            return false;
        }

        Type element = arrayType.GetElementType()!;
        if (element == typeof(byte)) { elements = Map((byte[])source, static v => Element.FromInteger(v)); return true; }
        if (element == typeof(sbyte)) { elements = Map((sbyte[])source, static v => Element.FromInteger(v)); return true; }
        if (element == typeof(ushort)) { elements = Map((ushort[])source, static v => Element.FromInteger(v)); return true; }
        if (element == typeof(short)) { elements = Map((short[])source, static v => Element.FromInteger(v)); return true; }
        if (element == typeof(uint)) { elements = Map((uint[])source, static v => Element.FromInteger(v)); return true; }
        if (element == typeof(int)) { elements = Map((int[])source, static v => Element.FromInteger(v)); return true; }
        if (element == typeof(long)) { elements = Map((long[])source, static v => Element.FromInteger(v)); return true; }
        if (element == typeof(float)) { elements = Map((float[])source, static v => Element.FromFloat(v)); return true; }
        if (element == typeof(double)) { elements = Map((double[])source, static v => Element.FromFloat(v)); return true; }
        if (element == typeof(Rational)) { elements = Map((Rational[])source, static v => Element.FromRational(v)); return true; }
        if (element == typeof(SignedRational)) { elements = Map((SignedRational[])source, static v => Element.FromRational(v)); return true; }
        return false;
    }

    private static List<Element> Map<TSource>(TSource[] source, Func<TSource, Element> selector)
    {
        var list = new List<Element>(source.Length);
        foreach (TSource item in source)
        {
            list.Add(selector(item));
        }

        return list;
    }

    private static bool TryBuild<T>(List<Element> elements, out T? result)
    {
        result = default;
        Type target = typeof(T);
        bool isArray = target.IsArray;
        if (!isArray && elements.Count == 0)
        {
            return false;
        }

        // Scalars take the first element (lenient towards writers that store a count of 2 for a scalar tag).
        if (!isArray)
        {
            Element e = elements[0];
            if (target == typeof(byte)) { return TryInt(e, byte.MinValue, byte.MaxValue, out long v) && Box(ref result, (byte)v); }
            if (target == typeof(sbyte)) { return TryInt(e, sbyte.MinValue, sbyte.MaxValue, out long v) && Box(ref result, (sbyte)v); }
            if (target == typeof(ushort)) { return TryInt(e, ushort.MinValue, ushort.MaxValue, out long v) && Box(ref result, (ushort)v); }
            if (target == typeof(short)) { return TryInt(e, short.MinValue, short.MaxValue, out long v) && Box(ref result, (short)v); }
            if (target == typeof(uint)) { return TryInt(e, uint.MinValue, uint.MaxValue, out long v) && Box(ref result, (uint)v); }
            if (target == typeof(int)) { return TryInt(e, int.MinValue, int.MaxValue, out long v) && Box(ref result, (int)v); }
            if (target == typeof(long)) { return e.TryGetInteger(out long v) && Box(ref result, v); }
            if (target == typeof(float)) { return Box(ref result, (float)e.ToDouble()); }
            if (target == typeof(double)) { return Box(ref result, e.ToDouble()); }
            if (target == typeof(Rational)) { return e.TryGetRational(out Rational r) && Box(ref result, r); }
            if (target == typeof(SignedRational)) { return e.TryGetSignedRational(out SignedRational r) && Box(ref result, r); }
            return false;
        }

        int n = elements.Count;
        if (target == typeof(byte[])) { var a = new byte[n]; for (int i = 0; i < n; i++) { if (!TryInt(elements[i], byte.MinValue, byte.MaxValue, out long v)) { return false; } a[i] = (byte)v; } return Box(ref result, a); }
        if (target == typeof(sbyte[])) { var a = new sbyte[n]; for (int i = 0; i < n; i++) { if (!TryInt(elements[i], sbyte.MinValue, sbyte.MaxValue, out long v)) { return false; } a[i] = (sbyte)v; } return Box(ref result, a); }
        if (target == typeof(ushort[])) { var a = new ushort[n]; for (int i = 0; i < n; i++) { if (!TryInt(elements[i], ushort.MinValue, ushort.MaxValue, out long v)) { return false; } a[i] = (ushort)v; } return Box(ref result, a); }
        if (target == typeof(short[])) { var a = new short[n]; for (int i = 0; i < n; i++) { if (!TryInt(elements[i], short.MinValue, short.MaxValue, out long v)) { return false; } a[i] = (short)v; } return Box(ref result, a); }
        if (target == typeof(uint[])) { var a = new uint[n]; for (int i = 0; i < n; i++) { if (!TryInt(elements[i], uint.MinValue, uint.MaxValue, out long v)) { return false; } a[i] = (uint)v; } return Box(ref result, a); }
        if (target == typeof(int[])) { var a = new int[n]; for (int i = 0; i < n; i++) { if (!TryInt(elements[i], int.MinValue, int.MaxValue, out long v)) { return false; } a[i] = (int)v; } return Box(ref result, a); }
        if (target == typeof(long[])) { var a = new long[n]; for (int i = 0; i < n; i++) { if (!elements[i].TryGetInteger(out a[i])) { return false; } } return Box(ref result, a); }
        if (target == typeof(float[])) { var a = new float[n]; for (int i = 0; i < n; i++) { a[i] = (float)elements[i].ToDouble(); } return Box(ref result, a); }
        if (target == typeof(double[])) { var a = new double[n]; for (int i = 0; i < n; i++) { a[i] = elements[i].ToDouble(); } return Box(ref result, a); }
        if (target == typeof(Rational[])) { var a = new Rational[n]; for (int i = 0; i < n; i++) { if (!elements[i].TryGetRational(out a[i])) { return false; } } return Box(ref result, a); }
        if (target == typeof(SignedRational[])) { var a = new SignedRational[n]; for (int i = 0; i < n; i++) { if (!elements[i].TryGetSignedRational(out a[i])) { return false; } } return Box(ref result, a); }
        return false;
    }

    private static bool TryInt(in Element e, long min, long max, out long value)
        => e.TryGetInteger(out value) && value >= min && value <= max;

    private static bool Box<T>(ref T? result, object value)
    {
        result = (T)value;
        return true;
    }
}

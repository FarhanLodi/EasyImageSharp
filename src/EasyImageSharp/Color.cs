using EasyImageSharp.PixelFormats;

namespace EasyImageSharp;

/// <summary>
/// A 32-bit RGBA color value with 8 bits per channel, independent of any pixel format.
/// Converts implicitly to and from <see cref="Rgba32"/>, and to any pixel format via
/// <see cref="ToPixel{TPixel}"/>. Named CSS colors are exposed as static properties, and
/// <see cref="Parse"/> / <see cref="TryParse"/> accept both names and hex strings.
/// </summary>
public readonly struct Color : IEquatable<Color>
{
    /// <summary>Initializes a new color from red, green, blue and (optionally) alpha components.</summary>
    /// <param name="r">The red component (0-255).</param>
    /// <param name="g">The green component (0-255).</param>
    /// <param name="b">The blue component (0-255).</param>
    /// <param name="a">The alpha component (0-255); 255 is fully opaque.</param>
    public Color(byte r, byte g, byte b, byte a = byte.MaxValue)
    {
        this.R = r;
        this.G = g;
        this.B = b;
        this.A = a;
    }

    /// <summary>The red component (0-255).</summary>
    public byte R { get; }

    /// <summary>The green component (0-255).</summary>
    public byte G { get; }

    /// <summary>The blue component (0-255).</summary>
    public byte B { get; }

    /// <summary>The alpha component (0-255); 255 is fully opaque.</summary>
    public byte A { get; }

    // ----- Factories -----

    /// <summary>Creates an opaque color from red, green and blue components.</summary>
    public static Color FromRgb(byte r, byte g, byte b) => new(r, g, b);

    /// <summary>Creates a color from red, green, blue and alpha components.</summary>
    public static Color FromRgba(byte r, byte g, byte b, byte a) => new(r, g, b, a);

    /// <summary>Creates a color from any pixel value via its <see cref="Rgba32"/> representation.</summary>
    public static Color FromPixel<TPixel>(TPixel pixel)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Rgba32 value = pixel.ToRgba32();
        return new Color(value.R, value.G, value.B, value.A);
    }

    /// <summary>Returns this color with a different alpha component.</summary>
    public Color WithAlpha(byte alpha) => new(this.R, this.G, this.B, alpha);

    // ----- Conversions -----

    /// <summary>Converts this color to its <see cref="Rgba32"/> representation.</summary>
    public Rgba32 ToRgba32() => new(this.R, this.G, this.B, this.A);

    /// <summary>Converts this color to the given pixel format.</summary>
    public TPixel ToPixel<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => TPixel.FromRgba32(this.ToRgba32());

    /// <summary>Converts a color to an <see cref="Rgba32"/> pixel.</summary>
    public static implicit operator Rgba32(Color color) => color.ToRgba32();

    /// <summary>Converts an <see cref="Rgba32"/> pixel to a color.</summary>
    public static implicit operator Color(Rgba32 pixel) => new(pixel.R, pixel.G, pixel.B, pixel.A);

    // ----- Parsing / formatting -----

    /// <summary>
    /// Parses a color from a named CSS color (case-insensitive, e.g. <c>"CornflowerBlue"</c>) or a hex
    /// string in any of the forms accepted by <see cref="ParseHex"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="value"/> is neither a known name nor a valid hex string.</exception>
    public static Color Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return TryParse(value, out Color color)
            ? color
            : throw new FormatException($"'{value}' is not a named color or a valid hex color string.");
    }

    /// <summary>
    /// Tries to parse a color from a named CSS color (case-insensitive) or a hex string in any of the
    /// forms accepted by <see cref="TryParseHex"/>. Surrounding whitespace is ignored.
    /// </summary>
    public static bool TryParse(string? value, out Color color)
    {
        if (value is not null)
        {
            string trimmed = value.Trim();
            if (NamedColors.TryGetValue(trimmed, out color) || TryParseHex(trimmed, out color))
            {
                return true;
            }
        }

        color = default;
        return false;
    }

    /// <summary>
    /// Parses a hex color string in <c>RGB</c>, <c>RGBA</c>, <c>RRGGBB</c> or <c>RRGGBBAA</c> form, with or
    /// without a leading <c>#</c>. Three- and four-digit forms expand each digit (<c>#F80</c> is <c>#FF8800</c>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="hex"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="hex"/> is not a valid hex color string.</exception>
    public static Color ParseHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        return TryParseHex(hex, out Color color)
            ? color
            : throw new FormatException($"'{hex}' is not a valid hex color string (expected RGB, RGBA, RRGGBB or RRGGBBAA, optionally prefixed with '#').");
    }

    /// <summary>
    /// Tries to parse a hex color string in <c>RGB</c>, <c>RGBA</c>, <c>RRGGBB</c> or <c>RRGGBBAA</c> form,
    /// with or without a leading <c>#</c>. Surrounding whitespace is ignored.
    /// </summary>
    public static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (hex is null)
        {
            return false;
        }

        ReadOnlySpan<char> s = hex.AsSpan().Trim();
        if (s.Length > 0 && s[0] == '#')
        {
            s = s[1..];
        }

        switch (s.Length)
        {
            case 3:
            case 4:
            {
                Span<byte> nibbles = stackalloc byte[4];
                nibbles[3] = 0xF;
                for (int i = 0; i < s.Length; i++)
                {
                    int n = HexValue(s[i]);
                    if (n < 0)
                    {
                        return false;
                    }

                    nibbles[i] = (byte)n;
                }

                color = new Color(
                    (byte)(nibbles[0] * 17),
                    (byte)(nibbles[1] * 17),
                    (byte)(nibbles[2] * 17),
                    (byte)(nibbles[3] * 17));
                return true;
            }

            case 6:
            case 8:
            {
                Span<byte> bytes = stackalloc byte[4];
                bytes[3] = byte.MaxValue;
                for (int i = 0; i < s.Length; i += 2)
                {
                    int hi = HexValue(s[i]);
                    int lo = HexValue(s[i + 1]);
                    if (hi < 0 || lo < 0)
                    {
                        return false;
                    }

                    bytes[i / 2] = (byte)((hi << 4) | lo);
                }

                color = new Color(bytes[0], bytes[1], bytes[2], bytes[3]);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>Formats this color as an uppercase <c>RRGGBBAA</c> hex string without a leading <c>#</c>.</summary>
    public string ToHex()
        => string.Create(8, this, static (span, c) =>
        {
            WriteHexByte(span, c.R);
            WriteHexByte(span[2..], c.G);
            WriteHexByte(span[4..], c.B);
            WriteHexByte(span[6..], c.A);
        });

    // ----- Equality -----

    /// <inheritdoc/>
    public bool Equals(Color other) => this.R == other.R && this.G == other.G && this.B == other.B && this.A == other.A;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Color c && this.Equals(c);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(this.R, this.G, this.B, this.A);

    /// <inheritdoc/>
    public override string ToString() => $"Color [ R={this.R}, G={this.G}, B={this.B}, A={this.A} ]";

    /// <summary>Whether two colors have identical components.</summary>
    public static bool operator ==(Color left, Color right) => left.Equals(right);

    /// <summary>Whether two colors differ in any component.</summary>
    public static bool operator !=(Color left, Color right) => !left.Equals(right);

    // ----- Helpers -----

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static void WriteHexByte(Span<char> destination, byte value)
    {
        const string Digits = "0123456789ABCDEF";
        destination[0] = Digits[value >> 4];
        destination[1] = Digits[value & 0xF];
    }

    // ----- Named colors (CSS Color Level 4 / X11) -----

    /// <summary>Fully transparent black (#00000000).</summary>
    public static Color Transparent => new(0x00, 0x00, 0x00, 0x00);

    /// <summary>#F0F8FF.</summary>
    public static Color AliceBlue => new(0xF0, 0xF8, 0xFF);

    /// <summary>#FAEBD7.</summary>
    public static Color AntiqueWhite => new(0xFA, 0xEB, 0xD7);

    /// <summary>#00FFFF (same as <see cref="Cyan"/>).</summary>
    public static Color Aqua => new(0x00, 0xFF, 0xFF);

    /// <summary>#7FFFD4.</summary>
    public static Color Aquamarine => new(0x7F, 0xFF, 0xD4);

    /// <summary>#F0FFFF.</summary>
    public static Color Azure => new(0xF0, 0xFF, 0xFF);

    /// <summary>#F5F5DC.</summary>
    public static Color Beige => new(0xF5, 0xF5, 0xDC);

    /// <summary>#FFE4C4.</summary>
    public static Color Bisque => new(0xFF, 0xE4, 0xC4);

    /// <summary>#000000.</summary>
    public static Color Black => new(0x00, 0x00, 0x00);

    /// <summary>#FFEBCD.</summary>
    public static Color BlanchedAlmond => new(0xFF, 0xEB, 0xCD);

    /// <summary>#0000FF.</summary>
    public static Color Blue => new(0x00, 0x00, 0xFF);

    /// <summary>#8A2BE2.</summary>
    public static Color BlueViolet => new(0x8A, 0x2B, 0xE2);

    /// <summary>#A52A2A.</summary>
    public static Color Brown => new(0xA5, 0x2A, 0x2A);

    /// <summary>#DEB887.</summary>
    public static Color BurlyWood => new(0xDE, 0xB8, 0x87);

    /// <summary>#5F9EA0.</summary>
    public static Color CadetBlue => new(0x5F, 0x9E, 0xA0);

    /// <summary>#7FFF00.</summary>
    public static Color Chartreuse => new(0x7F, 0xFF, 0x00);

    /// <summary>#D2691E.</summary>
    public static Color Chocolate => new(0xD2, 0x69, 0x1E);

    /// <summary>#FF7F50.</summary>
    public static Color Coral => new(0xFF, 0x7F, 0x50);

    /// <summary>#6495ED.</summary>
    public static Color CornflowerBlue => new(0x64, 0x95, 0xED);

    /// <summary>#FFF8DC.</summary>
    public static Color Cornsilk => new(0xFF, 0xF8, 0xDC);

    /// <summary>#DC143C.</summary>
    public static Color Crimson => new(0xDC, 0x14, 0x3C);

    /// <summary>#00FFFF (same as <see cref="Aqua"/>).</summary>
    public static Color Cyan => new(0x00, 0xFF, 0xFF);

    /// <summary>#00008B.</summary>
    public static Color DarkBlue => new(0x00, 0x00, 0x8B);

    /// <summary>#008B8B.</summary>
    public static Color DarkCyan => new(0x00, 0x8B, 0x8B);

    /// <summary>#B8860B.</summary>
    public static Color DarkGoldenrod => new(0xB8, 0x86, 0x0B);

    /// <summary>#A9A9A9 (same as <see cref="DarkGrey"/>).</summary>
    public static Color DarkGray => new(0xA9, 0xA9, 0xA9);

    /// <summary>#006400.</summary>
    public static Color DarkGreen => new(0x00, 0x64, 0x00);

    /// <summary>#A9A9A9 (same as <see cref="DarkGray"/>).</summary>
    public static Color DarkGrey => new(0xA9, 0xA9, 0xA9);

    /// <summary>#BDB76B.</summary>
    public static Color DarkKhaki => new(0xBD, 0xB7, 0x6B);

    /// <summary>#8B008B.</summary>
    public static Color DarkMagenta => new(0x8B, 0x00, 0x8B);

    /// <summary>#556B2F.</summary>
    public static Color DarkOliveGreen => new(0x55, 0x6B, 0x2F);

    /// <summary>#FF8C00.</summary>
    public static Color DarkOrange => new(0xFF, 0x8C, 0x00);

    /// <summary>#9932CC.</summary>
    public static Color DarkOrchid => new(0x99, 0x32, 0xCC);

    /// <summary>#8B0000.</summary>
    public static Color DarkRed => new(0x8B, 0x00, 0x00);

    /// <summary>#E9967A.</summary>
    public static Color DarkSalmon => new(0xE9, 0x96, 0x7A);

    /// <summary>#8FBC8F.</summary>
    public static Color DarkSeaGreen => new(0x8F, 0xBC, 0x8F);

    /// <summary>#483D8B.</summary>
    public static Color DarkSlateBlue => new(0x48, 0x3D, 0x8B);

    /// <summary>#2F4F4F (same as <see cref="DarkSlateGrey"/>).</summary>
    public static Color DarkSlateGray => new(0x2F, 0x4F, 0x4F);

    /// <summary>#2F4F4F (same as <see cref="DarkSlateGray"/>).</summary>
    public static Color DarkSlateGrey => new(0x2F, 0x4F, 0x4F);

    /// <summary>#00CED1.</summary>
    public static Color DarkTurquoise => new(0x00, 0xCE, 0xD1);

    /// <summary>#9400D3.</summary>
    public static Color DarkViolet => new(0x94, 0x00, 0xD3);

    /// <summary>#FF1493.</summary>
    public static Color DeepPink => new(0xFF, 0x14, 0x93);

    /// <summary>#00BFFF.</summary>
    public static Color DeepSkyBlue => new(0x00, 0xBF, 0xFF);

    /// <summary>#696969 (same as <see cref="DimGrey"/>).</summary>
    public static Color DimGray => new(0x69, 0x69, 0x69);

    /// <summary>#696969 (same as <see cref="DimGray"/>).</summary>
    public static Color DimGrey => new(0x69, 0x69, 0x69);

    /// <summary>#1E90FF.</summary>
    public static Color DodgerBlue => new(0x1E, 0x90, 0xFF);

    /// <summary>#B22222.</summary>
    public static Color Firebrick => new(0xB2, 0x22, 0x22);

    /// <summary>#FFFAF0.</summary>
    public static Color FloralWhite => new(0xFF, 0xFA, 0xF0);

    /// <summary>#228B22.</summary>
    public static Color ForestGreen => new(0x22, 0x8B, 0x22);

    /// <summary>#FF00FF (same as <see cref="Magenta"/>).</summary>
    public static Color Fuchsia => new(0xFF, 0x00, 0xFF);

    /// <summary>#DCDCDC.</summary>
    public static Color Gainsboro => new(0xDC, 0xDC, 0xDC);

    /// <summary>#F8F8FF.</summary>
    public static Color GhostWhite => new(0xF8, 0xF8, 0xFF);

    /// <summary>#FFD700.</summary>
    public static Color Gold => new(0xFF, 0xD7, 0x00);

    /// <summary>#DAA520.</summary>
    public static Color Goldenrod => new(0xDA, 0xA5, 0x20);

    /// <summary>#808080 (same as <see cref="Grey"/>).</summary>
    public static Color Gray => new(0x80, 0x80, 0x80);

    /// <summary>#008000.</summary>
    public static Color Green => new(0x00, 0x80, 0x00);

    /// <summary>#ADFF2F.</summary>
    public static Color GreenYellow => new(0xAD, 0xFF, 0x2F);

    /// <summary>#808080 (same as <see cref="Gray"/>).</summary>
    public static Color Grey => new(0x80, 0x80, 0x80);

    /// <summary>#F0FFF0.</summary>
    public static Color Honeydew => new(0xF0, 0xFF, 0xF0);

    /// <summary>#FF69B4.</summary>
    public static Color HotPink => new(0xFF, 0x69, 0xB4);

    /// <summary>#CD5C5C.</summary>
    public static Color IndianRed => new(0xCD, 0x5C, 0x5C);

    /// <summary>#4B0082.</summary>
    public static Color Indigo => new(0x4B, 0x00, 0x82);

    /// <summary>#FFFFF0.</summary>
    public static Color Ivory => new(0xFF, 0xFF, 0xF0);

    /// <summary>#F0E68C.</summary>
    public static Color Khaki => new(0xF0, 0xE6, 0x8C);

    /// <summary>#E6E6FA.</summary>
    public static Color Lavender => new(0xE6, 0xE6, 0xFA);

    /// <summary>#FFF0F5.</summary>
    public static Color LavenderBlush => new(0xFF, 0xF0, 0xF5);

    /// <summary>#7CFC00.</summary>
    public static Color LawnGreen => new(0x7C, 0xFC, 0x00);

    /// <summary>#FFFACD.</summary>
    public static Color LemonChiffon => new(0xFF, 0xFA, 0xCD);

    /// <summary>#ADD8E6.</summary>
    public static Color LightBlue => new(0xAD, 0xD8, 0xE6);

    /// <summary>#F08080.</summary>
    public static Color LightCoral => new(0xF0, 0x80, 0x80);

    /// <summary>#E0FFFF.</summary>
    public static Color LightCyan => new(0xE0, 0xFF, 0xFF);

    /// <summary>#FAFAD2.</summary>
    public static Color LightGoldenrodYellow => new(0xFA, 0xFA, 0xD2);

    /// <summary>#D3D3D3 (same as <see cref="LightGrey"/>).</summary>
    public static Color LightGray => new(0xD3, 0xD3, 0xD3);

    /// <summary>#90EE90.</summary>
    public static Color LightGreen => new(0x90, 0xEE, 0x90);

    /// <summary>#D3D3D3 (same as <see cref="LightGray"/>).</summary>
    public static Color LightGrey => new(0xD3, 0xD3, 0xD3);

    /// <summary>#FFB6C1.</summary>
    public static Color LightPink => new(0xFF, 0xB6, 0xC1);

    /// <summary>#FFA07A.</summary>
    public static Color LightSalmon => new(0xFF, 0xA0, 0x7A);

    /// <summary>#20B2AA.</summary>
    public static Color LightSeaGreen => new(0x20, 0xB2, 0xAA);

    /// <summary>#87CEFA.</summary>
    public static Color LightSkyBlue => new(0x87, 0xCE, 0xFA);

    /// <summary>#778899 (same as <see cref="LightSlateGrey"/>).</summary>
    public static Color LightSlateGray => new(0x77, 0x88, 0x99);

    /// <summary>#778899 (same as <see cref="LightSlateGray"/>).</summary>
    public static Color LightSlateGrey => new(0x77, 0x88, 0x99);

    /// <summary>#B0C4DE.</summary>
    public static Color LightSteelBlue => new(0xB0, 0xC4, 0xDE);

    /// <summary>#FFFFE0.</summary>
    public static Color LightYellow => new(0xFF, 0xFF, 0xE0);

    /// <summary>#00FF00.</summary>
    public static Color Lime => new(0x00, 0xFF, 0x00);

    /// <summary>#32CD32.</summary>
    public static Color LimeGreen => new(0x32, 0xCD, 0x32);

    /// <summary>#FAF0E6.</summary>
    public static Color Linen => new(0xFA, 0xF0, 0xE6);

    /// <summary>#FF00FF (same as <see cref="Fuchsia"/>).</summary>
    public static Color Magenta => new(0xFF, 0x00, 0xFF);

    /// <summary>#800000.</summary>
    public static Color Maroon => new(0x80, 0x00, 0x00);

    /// <summary>#66CDAA.</summary>
    public static Color MediumAquamarine => new(0x66, 0xCD, 0xAA);

    /// <summary>#0000CD.</summary>
    public static Color MediumBlue => new(0x00, 0x00, 0xCD);

    /// <summary>#BA55D3.</summary>
    public static Color MediumOrchid => new(0xBA, 0x55, 0xD3);

    /// <summary>#9370DB.</summary>
    public static Color MediumPurple => new(0x93, 0x70, 0xDB);

    /// <summary>#3CB371.</summary>
    public static Color MediumSeaGreen => new(0x3C, 0xB3, 0x71);

    /// <summary>#7B68EE.</summary>
    public static Color MediumSlateBlue => new(0x7B, 0x68, 0xEE);

    /// <summary>#00FA9A.</summary>
    public static Color MediumSpringGreen => new(0x00, 0xFA, 0x9A);

    /// <summary>#48D1CC.</summary>
    public static Color MediumTurquoise => new(0x48, 0xD1, 0xCC);

    /// <summary>#C71585.</summary>
    public static Color MediumVioletRed => new(0xC7, 0x15, 0x85);

    /// <summary>#191970.</summary>
    public static Color MidnightBlue => new(0x19, 0x19, 0x70);

    /// <summary>#F5FFFA.</summary>
    public static Color MintCream => new(0xF5, 0xFF, 0xFA);

    /// <summary>#FFE4E1.</summary>
    public static Color MistyRose => new(0xFF, 0xE4, 0xE1);

    /// <summary>#FFE4B5.</summary>
    public static Color Moccasin => new(0xFF, 0xE4, 0xB5);

    /// <summary>#FFDEAD.</summary>
    public static Color NavajoWhite => new(0xFF, 0xDE, 0xAD);

    /// <summary>#000080.</summary>
    public static Color Navy => new(0x00, 0x00, 0x80);

    /// <summary>#FDF5E6.</summary>
    public static Color OldLace => new(0xFD, 0xF5, 0xE6);

    /// <summary>#808000.</summary>
    public static Color Olive => new(0x80, 0x80, 0x00);

    /// <summary>#6B8E23.</summary>
    public static Color OliveDrab => new(0x6B, 0x8E, 0x23);

    /// <summary>#FFA500.</summary>
    public static Color Orange => new(0xFF, 0xA5, 0x00);

    /// <summary>#FF4500.</summary>
    public static Color OrangeRed => new(0xFF, 0x45, 0x00);

    /// <summary>#DA70D6.</summary>
    public static Color Orchid => new(0xDA, 0x70, 0xD6);

    /// <summary>#EEE8AA.</summary>
    public static Color PaleGoldenrod => new(0xEE, 0xE8, 0xAA);

    /// <summary>#98FB98.</summary>
    public static Color PaleGreen => new(0x98, 0xFB, 0x98);

    /// <summary>#AFEEEE.</summary>
    public static Color PaleTurquoise => new(0xAF, 0xEE, 0xEE);

    /// <summary>#DB7093.</summary>
    public static Color PaleVioletRed => new(0xDB, 0x70, 0x93);

    /// <summary>#FFEFD5.</summary>
    public static Color PapayaWhip => new(0xFF, 0xEF, 0xD5);

    /// <summary>#FFDAB9.</summary>
    public static Color PeachPuff => new(0xFF, 0xDA, 0xB9);

    /// <summary>#CD853F.</summary>
    public static Color Peru => new(0xCD, 0x85, 0x3F);

    /// <summary>#FFC0CB.</summary>
    public static Color Pink => new(0xFF, 0xC0, 0xCB);

    /// <summary>#DDA0DD.</summary>
    public static Color Plum => new(0xDD, 0xA0, 0xDD);

    /// <summary>#B0E0E6.</summary>
    public static Color PowderBlue => new(0xB0, 0xE0, 0xE6);

    /// <summary>#800080.</summary>
    public static Color Purple => new(0x80, 0x00, 0x80);

    /// <summary>#663399.</summary>
    public static Color RebeccaPurple => new(0x66, 0x33, 0x99);

    /// <summary>#FF0000.</summary>
    public static Color Red => new(0xFF, 0x00, 0x00);

    /// <summary>#BC8F8F.</summary>
    public static Color RosyBrown => new(0xBC, 0x8F, 0x8F);

    /// <summary>#4169E1.</summary>
    public static Color RoyalBlue => new(0x41, 0x69, 0xE1);

    /// <summary>#8B4513.</summary>
    public static Color SaddleBrown => new(0x8B, 0x45, 0x13);

    /// <summary>#FA8072.</summary>
    public static Color Salmon => new(0xFA, 0x80, 0x72);

    /// <summary>#F4A460.</summary>
    public static Color SandyBrown => new(0xF4, 0xA4, 0x60);

    /// <summary>#2E8B57.</summary>
    public static Color SeaGreen => new(0x2E, 0x8B, 0x57);

    /// <summary>#FFF5EE.</summary>
    public static Color SeaShell => new(0xFF, 0xF5, 0xEE);

    /// <summary>#A0522D.</summary>
    public static Color Sienna => new(0xA0, 0x52, 0x2D);

    /// <summary>#C0C0C0.</summary>
    public static Color Silver => new(0xC0, 0xC0, 0xC0);

    /// <summary>#87CEEB.</summary>
    public static Color SkyBlue => new(0x87, 0xCE, 0xEB);

    /// <summary>#6A5ACD.</summary>
    public static Color SlateBlue => new(0x6A, 0x5A, 0xCD);

    /// <summary>#708090 (same as <see cref="SlateGrey"/>).</summary>
    public static Color SlateGray => new(0x70, 0x80, 0x90);

    /// <summary>#708090 (same as <see cref="SlateGray"/>).</summary>
    public static Color SlateGrey => new(0x70, 0x80, 0x90);

    /// <summary>#FFFAFA.</summary>
    public static Color Snow => new(0xFF, 0xFA, 0xFA);

    /// <summary>#00FF7F.</summary>
    public static Color SpringGreen => new(0x00, 0xFF, 0x7F);

    /// <summary>#4682B4.</summary>
    public static Color SteelBlue => new(0x46, 0x82, 0xB4);

    /// <summary>#D2B48C.</summary>
    public static Color Tan => new(0xD2, 0xB4, 0x8C);

    /// <summary>#008080.</summary>
    public static Color Teal => new(0x00, 0x80, 0x80);

    /// <summary>#D8BFD8.</summary>
    public static Color Thistle => new(0xD8, 0xBF, 0xD8);

    /// <summary>#FF6347.</summary>
    public static Color Tomato => new(0xFF, 0x63, 0x47);

    /// <summary>#40E0D0.</summary>
    public static Color Turquoise => new(0x40, 0xE0, 0xD0);

    /// <summary>#EE82EE.</summary>
    public static Color Violet => new(0xEE, 0x82, 0xEE);

    /// <summary>#F5DEB3.</summary>
    public static Color Wheat => new(0xF5, 0xDE, 0xB3);

    /// <summary>#FFFFFF.</summary>
    public static Color White => new(0xFF, 0xFF, 0xFF);

    /// <summary>#F5F5F5.</summary>
    public static Color WhiteSmoke => new(0xF5, 0xF5, 0xF5);

    /// <summary>#FFFF00.</summary>
    public static Color Yellow => new(0xFF, 0xFF, 0x00);

    /// <summary>#9ACD32.</summary>
    public static Color YellowGreen => new(0x9A, 0xCD, 0x32);

    /// <summary>Case-insensitive lookup from CSS color name to value; keys are the property names.</summary>
    private static readonly Dictionary<string, Color> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(Transparent)] = Transparent,
        [nameof(AliceBlue)] = AliceBlue,
        [nameof(AntiqueWhite)] = AntiqueWhite,
        [nameof(Aqua)] = Aqua,
        [nameof(Aquamarine)] = Aquamarine,
        [nameof(Azure)] = Azure,
        [nameof(Beige)] = Beige,
        [nameof(Bisque)] = Bisque,
        [nameof(Black)] = Black,
        [nameof(BlanchedAlmond)] = BlanchedAlmond,
        [nameof(Blue)] = Blue,
        [nameof(BlueViolet)] = BlueViolet,
        [nameof(Brown)] = Brown,
        [nameof(BurlyWood)] = BurlyWood,
        [nameof(CadetBlue)] = CadetBlue,
        [nameof(Chartreuse)] = Chartreuse,
        [nameof(Chocolate)] = Chocolate,
        [nameof(Coral)] = Coral,
        [nameof(CornflowerBlue)] = CornflowerBlue,
        [nameof(Cornsilk)] = Cornsilk,
        [nameof(Crimson)] = Crimson,
        [nameof(Cyan)] = Cyan,
        [nameof(DarkBlue)] = DarkBlue,
        [nameof(DarkCyan)] = DarkCyan,
        [nameof(DarkGoldenrod)] = DarkGoldenrod,
        [nameof(DarkGray)] = DarkGray,
        [nameof(DarkGreen)] = DarkGreen,
        [nameof(DarkGrey)] = DarkGrey,
        [nameof(DarkKhaki)] = DarkKhaki,
        [nameof(DarkMagenta)] = DarkMagenta,
        [nameof(DarkOliveGreen)] = DarkOliveGreen,
        [nameof(DarkOrange)] = DarkOrange,
        [nameof(DarkOrchid)] = DarkOrchid,
        [nameof(DarkRed)] = DarkRed,
        [nameof(DarkSalmon)] = DarkSalmon,
        [nameof(DarkSeaGreen)] = DarkSeaGreen,
        [nameof(DarkSlateBlue)] = DarkSlateBlue,
        [nameof(DarkSlateGray)] = DarkSlateGray,
        [nameof(DarkSlateGrey)] = DarkSlateGrey,
        [nameof(DarkTurquoise)] = DarkTurquoise,
        [nameof(DarkViolet)] = DarkViolet,
        [nameof(DeepPink)] = DeepPink,
        [nameof(DeepSkyBlue)] = DeepSkyBlue,
        [nameof(DimGray)] = DimGray,
        [nameof(DimGrey)] = DimGrey,
        [nameof(DodgerBlue)] = DodgerBlue,
        [nameof(Firebrick)] = Firebrick,
        [nameof(FloralWhite)] = FloralWhite,
        [nameof(ForestGreen)] = ForestGreen,
        [nameof(Fuchsia)] = Fuchsia,
        [nameof(Gainsboro)] = Gainsboro,
        [nameof(GhostWhite)] = GhostWhite,
        [nameof(Gold)] = Gold,
        [nameof(Goldenrod)] = Goldenrod,
        [nameof(Gray)] = Gray,
        [nameof(Green)] = Green,
        [nameof(GreenYellow)] = GreenYellow,
        [nameof(Grey)] = Grey,
        [nameof(Honeydew)] = Honeydew,
        [nameof(HotPink)] = HotPink,
        [nameof(IndianRed)] = IndianRed,
        [nameof(Indigo)] = Indigo,
        [nameof(Ivory)] = Ivory,
        [nameof(Khaki)] = Khaki,
        [nameof(Lavender)] = Lavender,
        [nameof(LavenderBlush)] = LavenderBlush,
        [nameof(LawnGreen)] = LawnGreen,
        [nameof(LemonChiffon)] = LemonChiffon,
        [nameof(LightBlue)] = LightBlue,
        [nameof(LightCoral)] = LightCoral,
        [nameof(LightCyan)] = LightCyan,
        [nameof(LightGoldenrodYellow)] = LightGoldenrodYellow,
        [nameof(LightGray)] = LightGray,
        [nameof(LightGreen)] = LightGreen,
        [nameof(LightGrey)] = LightGrey,
        [nameof(LightPink)] = LightPink,
        [nameof(LightSalmon)] = LightSalmon,
        [nameof(LightSeaGreen)] = LightSeaGreen,
        [nameof(LightSkyBlue)] = LightSkyBlue,
        [nameof(LightSlateGray)] = LightSlateGray,
        [nameof(LightSlateGrey)] = LightSlateGrey,
        [nameof(LightSteelBlue)] = LightSteelBlue,
        [nameof(LightYellow)] = LightYellow,
        [nameof(Lime)] = Lime,
        [nameof(LimeGreen)] = LimeGreen,
        [nameof(Linen)] = Linen,
        [nameof(Magenta)] = Magenta,
        [nameof(Maroon)] = Maroon,
        [nameof(MediumAquamarine)] = MediumAquamarine,
        [nameof(MediumBlue)] = MediumBlue,
        [nameof(MediumOrchid)] = MediumOrchid,
        [nameof(MediumPurple)] = MediumPurple,
        [nameof(MediumSeaGreen)] = MediumSeaGreen,
        [nameof(MediumSlateBlue)] = MediumSlateBlue,
        [nameof(MediumSpringGreen)] = MediumSpringGreen,
        [nameof(MediumTurquoise)] = MediumTurquoise,
        [nameof(MediumVioletRed)] = MediumVioletRed,
        [nameof(MidnightBlue)] = MidnightBlue,
        [nameof(MintCream)] = MintCream,
        [nameof(MistyRose)] = MistyRose,
        [nameof(Moccasin)] = Moccasin,
        [nameof(NavajoWhite)] = NavajoWhite,
        [nameof(Navy)] = Navy,
        [nameof(OldLace)] = OldLace,
        [nameof(Olive)] = Olive,
        [nameof(OliveDrab)] = OliveDrab,
        [nameof(Orange)] = Orange,
        [nameof(OrangeRed)] = OrangeRed,
        [nameof(Orchid)] = Orchid,
        [nameof(PaleGoldenrod)] = PaleGoldenrod,
        [nameof(PaleGreen)] = PaleGreen,
        [nameof(PaleTurquoise)] = PaleTurquoise,
        [nameof(PaleVioletRed)] = PaleVioletRed,
        [nameof(PapayaWhip)] = PapayaWhip,
        [nameof(PeachPuff)] = PeachPuff,
        [nameof(Peru)] = Peru,
        [nameof(Pink)] = Pink,
        [nameof(Plum)] = Plum,
        [nameof(PowderBlue)] = PowderBlue,
        [nameof(Purple)] = Purple,
        [nameof(RebeccaPurple)] = RebeccaPurple,
        [nameof(Red)] = Red,
        [nameof(RosyBrown)] = RosyBrown,
        [nameof(RoyalBlue)] = RoyalBlue,
        [nameof(SaddleBrown)] = SaddleBrown,
        [nameof(Salmon)] = Salmon,
        [nameof(SandyBrown)] = SandyBrown,
        [nameof(SeaGreen)] = SeaGreen,
        [nameof(SeaShell)] = SeaShell,
        [nameof(Sienna)] = Sienna,
        [nameof(Silver)] = Silver,
        [nameof(SkyBlue)] = SkyBlue,
        [nameof(SlateBlue)] = SlateBlue,
        [nameof(SlateGray)] = SlateGray,
        [nameof(SlateGrey)] = SlateGrey,
        [nameof(Snow)] = Snow,
        [nameof(SpringGreen)] = SpringGreen,
        [nameof(SteelBlue)] = SteelBlue,
        [nameof(Tan)] = Tan,
        [nameof(Teal)] = Teal,
        [nameof(Thistle)] = Thistle,
        [nameof(Tomato)] = Tomato,
        [nameof(Turquoise)] = Turquoise,
        [nameof(Violet)] = Violet,
        [nameof(Wheat)] = Wheat,
        [nameof(White)] = White,
        [nameof(WhiteSmoke)] = WhiteSmoke,
        [nameof(Yellow)] = Yellow,
        [nameof(YellowGreen)] = YellowGreen,
    };
}

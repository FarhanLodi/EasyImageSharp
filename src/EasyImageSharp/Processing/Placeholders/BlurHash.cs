using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Encodes and decodes BlurHash placeholders: a short base-83 string holding the average colour and up to
/// 9 x 9 cosine components of an image, decoded client-side into a blurred preview.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm follows the published BlurHash specification (<c>Algorithm.md</c> of the reference
/// repository): colours are linearised from sRGB, projected onto <c>cos(pi i x / w) cos(pi j y / h)</c>, the
/// AC terms are normalised by their largest absolute value and quantised with a square-root curve. All
/// arithmetic is double precision, as in the reference TypeScript implementation; the reference C encoder
/// computes in single precision and produces the same strings on the vectors in the test suite.
/// </para>
/// <para>
/// Encoding reads the 8-bit RGB of the root frame (alpha is ignored, as in the reference) and costs
/// <c>width * height * componentsX * componentsY</c> multiply-adds, so encode a thumbnail (for example 32-64
/// pixels across) rather than a full-resolution photo; the hash cannot represent more detail anyway.
/// </para>
/// </remarks>
public static class BlurHash
{
    /// <summary>The largest number of components along either axis the format can store.</summary>
    public const int MaxComponents = 9;

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";

    /// <summary>sRGB-to-linear values of the 256 byte codes, evaluated exactly as the reference does.</summary>
    private static readonly double[] SrgbToLinearTable = BuildLinearTable();

    /// <summary>
    /// Encodes the root frame as a BlurHash with <paramref name="componentsX"/> horizontal and
    /// <paramref name="componentsY"/> vertical components (each 1-9; 4 x 3 is the customary choice).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A component count is outside 1-9.</exception>
    public static string Encode<TPixel>(Image<TPixel> image, int componentsX = 4, int componentsY = 3)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        image.EnsureNotDisposed();
        ValidateComponents(componentsX, nameof(componentsX));
        ValidateComponents(componentsY, nameof(componentsY));

        ImageFrame<TPixel> frame = image.Frames.RootFrame;
        int width = frame.Width;
        int height = frame.Height;
        Rgba32[] pixels = RowProcessor.ReadRegion(frame, new Rectangle(0, 0, width, height));

        // Cosine tables, computed with the same expression and operand order as the reference so the sums
        // below are bit-identical to it.
        double[] cosX = new double[componentsX * width];
        double[] cosY = new double[componentsY * height];
        for (int i = 0; i < componentsX; i++)
        {
            for (int x = 0; x < width; x++)
            {
                cosX[(i * width) + x] = Math.Cos(Math.PI * i * x / width);
            }
        }

        for (int j = 0; j < componentsY; j++)
        {
            for (int y = 0; y < height; y++)
            {
                cosY[(j * height) + y] = Math.Cos(Math.PI * j * y / height);
            }
        }

        int count = componentsX * componentsY;
        double[] factors = new double[count * 3];
        double scale = 1.0 / (width * height);
        for (int j = 0; j < componentsY; j++)
        {
            for (int i = 0; i < componentsX; i++)
            {
                OperationExecution.Checkpoint();
                double normalisation = i == 0 && j == 0 ? 1 : 2;
                double r = 0, g = 0, b = 0;

                // The reference sums column by column; keeping that order keeps the floating-point result identical.
                for (int x = 0; x < width; x++)
                {
                    double horizontal = normalisation * cosX[(i * width) + x];
                    for (int y = 0; y < height; y++)
                    {
                        double basis = horizontal * cosY[(j * height) + y];
                        Rgba32 p = pixels[(y * width) + x];
                        r += basis * SrgbToLinearTable[p.R];
                        g += basis * SrgbToLinearTable[p.G];
                        b += basis * SrgbToLinearTable[p.B];
                    }
                }

                int index = ((j * componentsX) + i) * 3;
                factors[index] = r * scale;
                factors[index + 1] = g * scale;
                factors[index + 2] = b * scale;
            }
        }

        var hash = new System.Text.StringBuilder(4 + (2 * count));
        AppendBase83(hash, (componentsX - 1) + ((componentsY - 1) * 9), 1);

        double maximumValue;
        if (count > 1)
        {
            double actualMaximum = 0;
            for (int i = 3; i < factors.Length; i++)
            {
                actualMaximum = Math.Max(actualMaximum, Math.Abs(factors[i]));
            }

            int quantisedMaximum = (int)Math.Max(0, Math.Min(82, Math.Floor((actualMaximum * 166) - 0.5)));
            maximumValue = (quantisedMaximum + 1) / 166.0;
            AppendBase83(hash, quantisedMaximum, 1);
        }
        else
        {
            maximumValue = 1;
            AppendBase83(hash, 0, 1);
        }

        int dc = (LinearToSrgb(factors[0]) << 16) + (LinearToSrgb(factors[1]) << 8) + LinearToSrgb(factors[2]);
        AppendBase83(hash, dc, 4);
        for (int c = 1; c < count; c++)
        {
            int ac = (QuantiseAc(factors[c * 3], maximumValue) * 19 * 19)
                + (QuantiseAc(factors[(c * 3) + 1], maximumValue) * 19)
                + QuantiseAc(factors[(c * 3) + 2], maximumValue);
            AppendBase83(hash, ac, 2);
        }

        return hash.ToString();
    }

    /// <summary>
    /// Decodes <paramref name="hash"/> into an opaque image of the given size. <paramref name="punch"/> scales
    /// the AC components: 1 reproduces the encoded contrast, larger values exaggerate it.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="hash"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The size is not positive or <paramref name="punch"/> is not a positive finite number.</exception>
    /// <exception cref="FormatException"><paramref name="hash"/> is not a well-formed BlurHash.</exception>
    public static Image<Rgba32> Decode(string hash, int width, int height, float punch = 1f)
    {
        ArgumentNullException.ThrowIfNull(hash);
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        if (!(punch > 0f) || !float.IsFinite(punch))
        {
            throw new ArgumentOutOfRangeException(nameof(punch), punch, "Punch must be a positive finite number.");
        }

        (int componentsX, int componentsY) = ParseComponents(hash);
        double maximumValue = (DecodeBase83(hash, 1, 1) + 1) / 166.0 * punch;
        int count = componentsX * componentsY;
        double[] colors = new double[count * 3];
        int dc = DecodeBase83(hash, 2, 4);
        colors[0] = SrgbToLinearTable[dc >> 16];
        colors[1] = SrgbToLinearTable[(dc >> 8) & 255];
        colors[2] = SrgbToLinearTable[dc & 255];
        for (int c = 1; c < count; c++)
        {
            int value = DecodeBase83(hash, 4 + (c * 2), 2);
            if (value >= 19 * 19 * 19)
            {
                throw new FormatException("The BlurHash contains an AC component outside the quantisation range.");
            }

            colors[c * 3] = SignPow(((value / (19 * 19)) - 9) / 9.0, 2) * maximumValue;
            colors[(c * 3) + 1] = SignPow((((value / 19) % 19) - 9) / 9.0, 2) * maximumValue;
            colors[(c * 3) + 2] = SignPow(((value % 19) - 9) / 9.0, 2) * maximumValue;
        }

        double[] cosX = new double[componentsX * width];
        double[] cosY = new double[componentsY * height];
        for (int x = 0; x < width; x++)
        {
            for (int i = 0; i < componentsX; i++)
            {
                cosX[(x * componentsX) + i] = Math.Cos(Math.PI * x * i / width);
            }
        }

        for (int y = 0; y < height; y++)
        {
            for (int j = 0; j < componentsY; j++)
            {
                cosY[(y * componentsY) + j] = Math.Cos(Math.PI * y * j / height);
            }
        }

        var image = new Image<Rgba32>(width, height);
        ImageFrame<Rgba32> frame = image.Frames.RootFrame;
        try
        {
            ParallelRowIterator.IterateRows(width, height, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    Span<Rgba32> row = frame.GetRowSpan(y);
                    for (int x = 0; x < width; x++)
                    {
                        double r = 0, g = 0, b = 0;
                        for (int j = 0; j < componentsY; j++)
                        {
                            double vertical = cosY[(y * componentsY) + j];
                            for (int i = 0; i < componentsX; i++)
                            {
                                double basis = cosX[(x * componentsX) + i] * vertical;
                                int c = ((j * componentsX) + i) * 3;
                                r += colors[c] * basis;
                                g += colors[c + 1] * basis;
                                b += colors[c + 2] * basis;
                            }
                        }

                        row[x] = new Rgba32((byte)LinearToSrgb(r), (byte)LinearToSrgb(g), (byte)LinearToSrgb(b), byte.MaxValue);
                    }
                }
            });
        }
        catch
        {
            image.Dispose();
            throw;
        }

        return image;
    }

    /// <summary>Whether <paramref name="hash"/> is structurally valid: known characters and a length matching its component counts.</summary>
    public static bool IsValid(string? hash)
    {
        if (hash is null)
        {
            return false;
        }

        try
        {
            (int componentsX, int componentsY) = ParseComponents(hash);
            for (int c = 1; c < componentsX * componentsY; c++)
            {
                if (DecodeBase83(hash, 4 + (c * 2), 2) >= 19 * 19 * 19)
                {
                    return false;
                }
            }

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Reads the horizontal and vertical component counts a hash declares.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="hash"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="hash"/> is not a well-formed BlurHash.</exception>
    public static (int ComponentsX, int ComponentsY) GetComponents(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return ParseComponents(hash);
    }

    private static (int ComponentsX, int ComponentsY) ParseComponents(string hash)
    {
        if (hash.Length < 6)
        {
            throw new FormatException("A BlurHash is at least 6 characters long.");
        }

        int sizeFlag = DecodeBase83(hash, 0, 1);
        int componentsY = (sizeFlag / 9) + 1;
        int componentsX = (sizeFlag % 9) + 1;
        if (componentsY > MaxComponents)
        {
            throw new FormatException("The BlurHash size flag is out of range.");
        }

        int expected = 4 + (2 * componentsX * componentsY);
        if (hash.Length != expected)
        {
            throw new FormatException(FormattableString.Invariant(
                $"The BlurHash declares {componentsX}x{componentsY} components and must be {expected} characters long, but is {hash.Length}."));
        }

        // Validates every remaining character once, so decoding can index freely.
        for (int i = 1; i < hash.Length; i++)
        {
            DigitOf(hash[i]);
        }

        if (DecodeBase83(hash, 2, 4) > 0xFFFFFF)
        {
            throw new FormatException("The BlurHash average colour is outside the 24-bit range.");
        }

        return (componentsX, componentsY);
    }

    private static void ValidateComponents(int value, string parameterName)
    {
        if (value is < 1 or > MaxComponents)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "BlurHash component counts must be between 1 and 9.");
        }
    }

    private static int QuantiseAc(double value, double maximumValue)
        => (int)Math.Floor(Math.Max(0, Math.Min(18, Math.Floor((SignPow(value / maximumValue, 0.5) * 9) + 9.5))));

    private static double SignPow(double value, double exponent)
        => (value < 0 ? -1 : 1) * Math.Pow(Math.Abs(value), exponent);

    /// <summary>The reference linear-to-sRGB conversion: clamp, encode, scale to 0-255 and round half up by truncation.</summary>
    private static int LinearToSrgb(double value)
    {
        double v = Math.Max(0, Math.Min(1, value));
        return v <= 0.0031308
            ? (int)Math.Truncate((v * 12.92 * 255) + 0.5)
            : (int)Math.Truncate((((1.055 * Math.Pow(v, 1 / 2.4)) - 0.055) * 255) + 0.5);
    }

    private static double[] BuildLinearTable()
    {
        var table = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0;
            table[i] = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return table;
    }

    private static void AppendBase83(System.Text.StringBuilder destination, int value, int length)
    {
        int divisor = 1;
        for (int i = 1; i < length; i++)
        {
            divisor *= 83;
        }

        for (int i = 0; i < length; i++)
        {
            destination.Append(Alphabet[(value / divisor) % 83]);
            divisor /= 83;
        }
    }

    private static int DecodeBase83(string text, int start, int length)
    {
        int value = 0;
        for (int i = start; i < start + length; i++)
        {
            value = (value * 83) + DigitOf(text[i]);
        }

        return value;
    }

    private static int DigitOf(char c)
    {
        int digit = c < 128 ? Alphabet.IndexOf(c, StringComparison.Ordinal) : -1;
        return digit >= 0 ? digit : throw new FormatException($"'{c}' is not a BlurHash base-83 character.");
    }
}

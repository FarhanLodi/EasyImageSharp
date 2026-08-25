namespace EasyImageSharp.Processing;

/// <summary>
/// Well-known <see cref="ColorMatrix"/> filters. The colour and photographic filters follow the definitions
/// used by the W3C Filter Effects specification (grayscale, sepia, saturate, hue-rotate, brightness, contrast,
/// invert, opacity); the colour-blindness matrices are the widely published simulation matrices.
/// </summary>
public static class KnownFilterMatrices
{
    // ----- Colour blindness simulation -----

    /// <summary>Simulates achromatomaly (partial colour desensitivity).</summary>
    public static ColorMatrix AchromatomalyFilter { get; } = new(
        0.618f, 0.163f, 0.163f, 0,
        0.320f, 0.775f, 0.320f, 0,
        0.062f, 0.062f, 0.516f, 0,
        0, 0, 0, 1,
        0, 0, 0, 0);

    /// <summary>Simulates achromatopsia (complete colour desensitivity).</summary>
    public static ColorMatrix AchromatopsiaFilter { get; } = new(
        0.299f, 0.299f, 0.299f, 0,
        0.587f, 0.587f, 0.587f, 0,
        0.114f, 0.114f, 0.114f, 0,
        0, 0, 0, 1,
        0, 0, 0, 0);

    /// <summary>Simulates deuteranomaly (green-weak vision).</summary>
    public static ColorMatrix DeuteranomalyFilter { get; } = new(
        0.8f, 0.258f, 0, 0,
        0.2f, 0.742f, 0.142f, 0,
        0, 0, 0.858f, 0,
        0, 0, 0, 1,
        0, 0, 0, 0);

    /// <summary>Simulates deuteranopia (green-blind vision).</summary>
    public static ColorMatrix DeuteranopiaFilter { get; } = new(
        0.625f, 0.7f, 0, 0,
        0.375f, 0.3f, 0.3f, 0,
        0, 0, 0.7f, 0,
        0, 0, 0, 1,
        0, 0, 0, 0);

    /// <summary>Simulates protanomaly (red-weak vision).</summary>
    public static ColorMatrix ProtanomalyFilter { get; } = new(
        0.817f, 0.333f, 0, 0,
        0.183f, 0.667f, 0.125f, 0,
        0, 0, 0.875f, 0,
        0, 0, 0, 1,
        0, 0, 0, 0);

    /// <summary>Simulates protanopia (red-blind vision).</summary>
    public static ColorMatrix ProtanopiaFilter { get; } = new(
        0.567f, 0.558f, 0, 0,
        0.433f, 0.442f, 0.242f, 0,
        0, 0, 0.758f, 0,
        0, 0, 0, 1,
        0, 0, 0, 0);

    /// <summary>Simulates tritanomaly (blue-weak vision).</summary>
    public static ColorMatrix TritanomalyFilter { get; } = new(
        0.967f, 0, 0, 0,
        0.033f, 0.733f, 0.183f, 0,
        0, 0.267f, 0.817f, 0,
        0, 0, 0, 1,
        0, 0, 0, 0);

    /// <summary>Simulates tritanopia (blue-blind vision).</summary>
    public static ColorMatrix TritanopiaFilter { get; } = new(
        0.95f, 0, 0, 0,
        0.05f, 0.433f, 0.475f, 0,
        0, 0.567f, 0.525f, 0,
        0, 0, 0, 1,
        0, 0, 0, 0);

    /// <summary>Returns the simulation matrix for the given colour vision deficiency.</summary>
    public static ColorMatrix GetColorBlindnessFilter(ColorBlindnessMode mode) => mode switch
    {
        ColorBlindnessMode.Achromatomaly => AchromatomalyFilter,
        ColorBlindnessMode.Achromatopsia => AchromatopsiaFilter,
        ColorBlindnessMode.Deuteranomaly => DeuteranomalyFilter,
        ColorBlindnessMode.Deuteranopia => DeuteranopiaFilter,
        ColorBlindnessMode.Protanomaly => ProtanomalyFilter,
        ColorBlindnessMode.Protanopia => ProtanopiaFilter,
        ColorBlindnessMode.Tritanomaly => TritanomalyFilter,
        ColorBlindnessMode.Tritanopia => TritanopiaFilter,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown colour blindness mode."),
    };

    // ----- Photographic looks -----

    /// <summary>A high-contrast black and white look: luminance is stretched by 1.5 and offset by -1.</summary>
    public static ColorMatrix BlackWhiteFilter { get; } = new(
        1.5f, 1.5f, 1.5f, 0,
        1.5f, 1.5f, 1.5f, 0,
        1.5f, 1.5f, 1.5f, 0,
        0, 0, 0, 1,
        -1, -1, -1, 0);

    /// <summary>A Kodachrome-style look: reduced per-channel gain with a warm offset.</summary>
    public static ColorMatrix KodachromeFilter { get; } = new(
        0.7297023f, 0, 0, 0,
        0, 0.6109577f, 0, 0,
        0, 0, 0.597218f, 0,
        0, 0, 0, 1,
        0.105f, -0.104f, -0.02f, 0);

    /// <summary>A Lomograph-style look: boosted contrast and saturation with a slight colour cast.</summary>
    public static ColorMatrix LomographFilter { get; } = new(
        1.5f, 0, 0, 0,
        0, 1.45f, 0, 0,
        0, 0, 1.11f, 0,
        0, 0, 0, 1,
        -0.1f, 0.05f, -0.08f, 0);

    /// <summary>A Polaroid-style look: cross-channel bleed with a cool cast.</summary>
    public static ColorMatrix PolaroidFilter { get; } = new(
        1.538f, -0.062f, -0.262f, 0,
        -0.022f, 1.578f, -0.022f, 0,
        0.216f, -0.16f, 1.5831f, 0,
        0, 0, 0, 1,
        0.02f, -0.05f, -0.05f, 0);

    // ----- Parameterised filters -----

    /// <summary>Scales every colour channel by <paramref name="amount"/>; 1 leaves the image unchanged.</summary>
    public static ColorMatrix CreateBrightnessFilter(float amount)
    {
        MustBeNonNegative(amount);
        return new ColorMatrix(
            amount, 0, 0, 0,
            0, amount, 0, 0,
            0, 0, amount, 0,
            0, 0, 0, 1,
            0, 0, 0, 0);
    }

    /// <summary>Scales contrast around mid-grey by <paramref name="amount"/>; 1 leaves the image unchanged, 0 is uniform grey.</summary>
    public static ColorMatrix CreateContrastFilter(float amount)
    {
        MustBeNonNegative(amount);
        float offset = (-0.5f * amount) + 0.5f;
        return new ColorMatrix(
            amount, 0, 0, 0,
            0, amount, 0, 0,
            0, 0, amount, 0,
            0, 0, 0, 1,
            offset, offset, offset, 0);
    }

    /// <summary>Converts to grayscale with BT.601 coefficients; <paramref name="amount"/> in 0-1 sets the strength.</summary>
    public static ColorMatrix CreateGrayscaleBt601Filter(float amount)
    {
        MustBeUnit(amount);
        float s = 1f - amount;
        return new ColorMatrix(
            0.299f + (0.701f * s), 0.299f - (0.299f * s), 0.299f - (0.299f * s), 0,
            0.587f - (0.587f * s), 0.587f + (0.413f * s), 0.587f - (0.587f * s), 0,
            0.114f - (0.114f * s), 0.114f - (0.114f * s), 0.114f + (0.886f * s), 0,
            0, 0, 0, 1,
            0, 0, 0, 0);
    }

    /// <summary>Converts to grayscale with BT.709 coefficients; <paramref name="amount"/> in 0-1 sets the strength.</summary>
    public static ColorMatrix CreateGrayscaleBt709Filter(float amount)
    {
        MustBeUnit(amount);
        float s = 1f - amount;
        return new ColorMatrix(
            0.2126f + (0.7874f * s), 0.2126f - (0.2126f * s), 0.2126f - (0.2126f * s), 0,
            0.7152f - (0.7152f * s), 0.7152f + (0.2848f * s), 0.7152f - (0.7152f * s), 0,
            0.0722f - (0.0722f * s), 0.0722f - (0.0722f * s), 0.0722f + (0.9278f * s), 0,
            0, 0, 0, 1,
            0, 0, 0, 0);
    }

    /// <summary>Returns the grayscale filter for the given <paramref name="mode"/> and strength.</summary>
    public static ColorMatrix CreateGrayscaleFilter(GrayscaleMode mode, float amount) => mode switch
    {
        GrayscaleMode.Bt709 => CreateGrayscaleBt709Filter(amount),
        GrayscaleMode.Bt601 => CreateGrayscaleBt601Filter(amount),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown grayscale mode."),
    };

    /// <summary>Rotates hue by <paramref name="degrees"/> (W3C hue-rotate).</summary>
    public static ColorMatrix CreateHueFilter(float degrees)
    {
        float radians = degrees * (MathF.PI / 180f);
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return new ColorMatrix(
            0.213f + (cos * 0.787f) - (sin * 0.213f), 0.213f - (cos * 0.213f) + (sin * 0.143f), 0.213f - (cos * 0.213f) - (sin * 0.787f), 0,
            0.715f - (cos * 0.715f) - (sin * 0.715f), 0.715f + (cos * 0.285f) + (sin * 0.140f), 0.715f - (cos * 0.715f) + (sin * 0.715f), 0,
            0.072f - (cos * 0.072f) + (sin * 0.928f), 0.072f - (cos * 0.072f) - (sin * 0.283f), 0.072f + (cos * 0.928f) + (sin * 0.072f), 0,
            0, 0, 0, 1,
            0, 0, 0, 0);
    }

    /// <summary>Inverts colours; <paramref name="amount"/> in 0-1 sets the strength (1 is a full negative).</summary>
    public static ColorMatrix CreateInvertFilter(float amount)
    {
        MustBeUnit(amount);
        float scale = 1f - (2f * amount);
        return new ColorMatrix(
            scale, 0, 0, 0,
            0, scale, 0, 0,
            0, 0, scale, 0,
            0, 0, 0, 1,
            amount, amount, amount, 0);
    }

    /// <summary>Adds <c>amount - 1</c> to every colour channel; 1 leaves the image unchanged, 0 is black, 2 is white.</summary>
    public static ColorMatrix CreateLightnessFilter(float amount)
    {
        MustBeNonNegative(amount);
        float offset = amount - 1f;
        return new ColorMatrix(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
            offset, offset, offset, 0);
    }

    /// <summary>Scales alpha by <paramref name="amount"/> in 0-1.</summary>
    public static ColorMatrix CreateOpacityFilter(float amount)
    {
        MustBeUnit(amount);
        return new ColorMatrix(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, amount,
            0, 0, 0, 0);
    }

    /// <summary>Scales saturation by <paramref name="amount"/> (W3C saturate); 1 leaves the image unchanged, 0 is grayscale.</summary>
    public static ColorMatrix CreateSaturateFilter(float amount)
    {
        MustBeNonNegative(amount);
        float s = amount;
        return new ColorMatrix(
            0.213f + (0.787f * s), 0.213f - (0.213f * s), 0.213f - (0.213f * s), 0,
            0.715f - (0.715f * s), 0.715f + (0.285f * s), 0.715f - (0.715f * s), 0,
            0.072f - (0.072f * s), 0.072f - (0.072f * s), 0.072f + (0.928f * s), 0,
            0, 0, 0, 1,
            0, 0, 0, 0);
    }

    /// <summary>Applies a sepia tone (W3C sepia); <paramref name="amount"/> in 0-1 sets the strength.</summary>
    public static ColorMatrix CreateSepiaFilter(float amount)
    {
        MustBeUnit(amount);
        float s = 1f - amount;
        return new ColorMatrix(
            0.393f + (0.607f * s), 0.349f - (0.349f * s), 0.272f - (0.272f * s), 0,
            0.769f - (0.769f * s), 0.686f + (0.314f * s), 0.534f - (0.534f * s), 0,
            0.189f - (0.189f * s), 0.168f - (0.168f * s), 0.131f + (0.869f * s), 0,
            0, 0, 0, 1,
            0, 0, 0, 0);
    }

    private static void MustBeNonNegative(float amount, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(amount))] string? name = null)
    {
        if (!(amount >= 0f))
        {
            throw new ArgumentOutOfRangeException(name, amount, "Amount must be non-negative.");
        }
    }

    private static void MustBeUnit(float amount, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(amount))] string? name = null)
    {
        if (!(amount >= 0f && amount <= 1f))
        {
            throw new ArgumentOutOfRangeException(name, amount, "Amount must be between 0 and 1.");
        }
    }
}

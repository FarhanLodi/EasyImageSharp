using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace AotSmoke;

/// <summary>
/// Deterministic source images. Nothing here reads a file, the clock or a random seed, so a failure this
/// sample reports reproduces byte for byte on the next run and on the next machine. Every builder writes
/// through <c>ProcessPixelRows</c> so the ref-struct accessor path is statically compiled and executed.
/// </summary>
internal static class Synth
{
    private const uint LcgMultiplier = 1664525u;
    private const uint LcgIncrement = 1013904223u;

    /// <summary>
    /// A high-entropy source with a varying alpha channel: two sine gradients mixed with a linear
    /// congruential noise stream, so a codec that drops a channel or transposes rows cannot pass by luck.
    /// Call sites use odd, prime-ish sizes such as 97x61 that fall outside every SIMD block width, which
    /// makes the scalar tails in the pixel and filter kernels run rather than only the vector bodies.
    /// </summary>
    internal static Image<Rgba32> Photo(int width, int height) => Build(width, height, 0x5EED0001u, 0.45, translucent: true);

    /// <summary>The same high-entropy source with every pixel opaque, for the codecs that cannot store alpha.</summary>
    internal static Image<Rgba32> Opaque(int width, int height) => Build(width, height, 0x5EED0002u, 0.45, translucent: false);

    /// <summary>A smooth opaque source with no noise at all, which is what a lossy DCT codec can reproduce closely.</summary>
    internal static Image<Rgba32> Gradient(int width, int height) => Build(width, height, 0x5EED0003u, 0.0, translucent: false);

    /// <summary>An opaque source drawn from exactly <paramref name="colorCount"/> colours, which a palette codec can hold.</summary>
    internal static Image<Rgba32> Flat(int width, int height, int colorCount)
    {
        Rgba32[] palette = Palette(colorCount);
        var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor => FillFlat(accessor, palette, 0));
        return image;
    }

    /// <summary>
    /// A palette-limited animation. Every frame draws from the same colours, so one global colour table
    /// holds all of them and the quantiser is never asked to merge across frames.
    /// </summary>
    internal static Image<Rgba32> FlatFrames(int width, int height, int colorCount, int frameCount)
    {
        Rgba32[] palette = Palette(colorCount);
        Image<Rgba32> image = Flat(width, height, colorCount);
        for (int i = 1; i < frameCount; i++)
        {
            int offset = i * 11;
            ImageFrame<Rgba32> frame = image.Frames.AddFrame(image.Frames.RootFrame);
            frame.ProcessPixelRows(accessor => FillFlat(accessor, palette, offset));
        }

        return image;
    }

    /// <summary>An 8-bit grayscale ramp.</summary>
    internal static Image<L8> Gray(int width, int height)
    {
        var image = new Image<L8>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<L8> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = new L8((byte)(((x * 5) + (y * 11)) & 0xFF));
                }
            }
        });

        return image;
    }

    /// <summary>A two-frame source whose second frame is the first with its channels rotated and a block moved.</summary>
    internal static Image<Rgba32> TwoFrames(int width, int height) => Animation(width, height, 2);

    /// <summary>A square source small enough for an ICO entry, which the encoder caps at 256x256.</summary>
    internal static Image<Rgba32> Square(int size) => Build(size, size, 0x5EED0004u, 0.45, translucent: true);

    /// <summary>
    /// An animation whose frames differ both in colour (channels rotated per frame) and in geometry (an
    /// opaque block that moves), so a codec that silently repeats a frame cannot pass.
    /// </summary>
    internal static Image<Rgba32> Animation(int width, int height, int frameCount)
    {
        Image<Rgba32> image = Build(width, height, 0x5EED0005u, 0.45, translucent: true);
        for (int i = 1; i < frameCount; i++)
        {
            int index = i;
            ImageFrame<Rgba32> frame = image.Frames.AddFrame(image.Frames.RootFrame);
            frame.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    Span<Rgba32> row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        Rgba32 pixel = row[x];
                        row[x] = (index % 3) switch
                        {
                            1 => new Rgba32(pixel.G, pixel.B, pixel.R, pixel.A),
                            2 => new Rgba32(pixel.B, pixel.R, pixel.G, pixel.A),
                            _ => new Rgba32((byte)(255 - pixel.R), pixel.G, pixel.B, pixel.A),
                        };
                    }
                }

                int size = Math.Min(8, Math.Min(accessor.Width, accessor.Height));
                int left = (index * 5) % Math.Max(1, accessor.Width - size + 1);
                int top = (index * 3) % Math.Max(1, accessor.Height - size + 1);
                for (int y = top; y < top + size; y++)
                {
                    Span<Rgba32> row = accessor.GetRowSpan(y);
                    for (int x = left; x < left + size; x++)
                    {
                        row[x] = new Rgba32(255, 216, 0, 255);
                    }
                }
            });
        }

        return image;
    }

    private static Image<Rgba32> Build(int width, int height, uint seed, double noiseWeight, bool translucent)
    {
        var image = new Image<Rgba32>(width, height);
        uint state = seed;
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                double fy = accessor.Height == 1 ? 0.0 : (double)y / (accessor.Height - 1);
                for (int x = 0; x < row.Length; x++)
                {
                    state = (state * LcgMultiplier) + LcgIncrement;
                    byte noise = (byte)(state >> 24);
                    double fx = row.Length == 1 ? 0.0 : (double)x / (row.Length - 1);
                    byte r = Mix(Wave((fx * 5.3) + (fy * 1.7)), noise, noiseWeight);
                    byte g = Mix(Wave((fy * 4.1) - (fx * 2.9) + 1.1), (byte)(noise * 3), noiseWeight);
                    byte b = Mix(Wave((fx * 2.3) + (fy * 6.7) + 2.4), (byte)(noise ^ 0x5A), noiseWeight);
                    byte a = translucent ? (byte)(40 + (((x * 7) + (y * 13)) % 216)) : (byte)255;
                    row[x] = new Rgba32(r, g, b, a);
                }
            }
        });

        return image;
    }

    private static Rgba32[] Palette(int colorCount)
    {
        var palette = new Rgba32[colorCount];
        uint state = 0x13579BDFu;
        for (int i = 0; i < colorCount; i++)
        {
            state = (state * LcgMultiplier) + LcgIncrement;
            palette[i] = new Rgba32((byte)(state >> 24), (byte)(state >> 16), (byte)(state >> 8), 255);
        }

        return palette;
    }

    private static void FillFlat(PixelAccessor<Rgba32> accessor, Rgba32[] palette, int offset)
    {
        for (int y = 0; y < accessor.Height; y++)
        {
            Span<Rgba32> row = accessor.GetRowSpan(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = palette[(((y / 3) * 7) + (x / 3) + offset) % palette.Length];
            }
        }
    }

    /// <summary>A sine mapped onto the 0-255 range.</summary>
    private static double Wave(double t) => (Math.Sin(t) + 1.0) * 127.5;

    private static byte Mix(double gradient, byte noise, double noiseWeight)
        => (byte)Math.Clamp((int)((gradient * (1.0 - noiseWeight)) + (noise * noiseWeight)), 0, 255);
}

using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Locks the correctness of the optimised code paths. Every optimised kernel keeps a scalar reference
/// implementation and every optimised loop can run on one or many threads, so each test here asserts that
/// the fast path is byte-identical to the reference: SIMD versus <see cref="SimdConfig.ForceScalarFallback"/>,
/// and <see cref="Configuration.MaxDegreeOfParallelism"/> of 1 versus 4.
/// </summary>
public class PerformanceRegressionTests
{
    // ----- Harness -----

    /// <summary>Runs <paramref name="produce"/> with the SIMD kernels on and then forced off, and compares.</summary>
    private static void AssertSimdMatchesScalar<T>(Func<T[]> produce, string what)
        where T : unmanaged, IEquatable<T>
    {
        T[] vectorized = RunWithScalarFallback(false, produce);
        T[] scalar = RunWithScalarFallback(true, produce);
        Assert.Equal(scalar.Length, vectorized.Length);
        for (int i = 0; i < scalar.Length; i++)
        {
            if (!scalar[i].Equals(vectorized[i]))
            {
                Assert.Fail($"{what}: element {i} differs, scalar {scalar[i]} vs vectorized {vectorized[i]}.");
            }
        }
    }

    /// <summary>Runs <paramref name="produce"/> single-threaded and then with four threads, and compares.</summary>
    private static void AssertParallelMatchesSerial<T>(Func<T[]> produce, string what)
        where T : unmanaged, IEquatable<T>
    {
        T[] serial = RunWithParallelism(1, produce);
        T[] parallel = RunWithParallelism(4, produce);
        Assert.Equal(serial.Length, parallel.Length);
        for (int i = 0; i < serial.Length; i++)
        {
            if (!serial[i].Equals(parallel[i]))
            {
                Assert.Fail($"{what}: element {i} differs, serial {serial[i]} vs parallel {parallel[i]}.");
            }
        }
    }

    /// <summary>Runs both comparisons for the same producer.</summary>
    private static void AssertPathsAgree<T>(Func<T[]> produce, string what)
        where T : unmanaged, IEquatable<T>
    {
        AssertSimdMatchesScalar(produce, what);
        AssertParallelMatchesSerial(produce, what);
    }

    private static T RunWithScalarFallback<T>(bool forceScalar, Func<T> body)
    {
        bool previous = SimdConfig.ForceScalarFallback;
        SimdConfig.ForceScalarFallback = forceScalar;
        try
        {
            return body();
        }
        finally
        {
            SimdConfig.ForceScalarFallback = previous;
        }
    }

    private static T RunWithParallelism<T>(int degree, Func<T> body)
    {
        int previous = Configuration.Default.MaxDegreeOfParallelism;
        Configuration.Default.MaxDegreeOfParallelism = degree;
        try
        {
            return body();
        }
        finally
        {
            Configuration.Default.MaxDegreeOfParallelism = previous;
        }
    }

    /// <summary>A deterministic, high-entropy test image so no kernel can pass by accident.</summary>
    private static Image<Rgba32> CreateSource(int width = 137, int height = 91)
    {
        var image = new Image<Rgba32>(width, height);
        uint state = 0x9E3779B9u;
        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                state = (state * 1664525u) + 1013904223u;
                row[x] = new Rgba32((byte)(state >> 24), (byte)(state >> 16), (byte)(state >> 8), (byte)(state | 1));
            }
        }

        return image;
    }

    private static Rgba32[] Pixels(Image<Rgba32> image) => image.Frames.RootFrame.PixelSpan.ToArray();

    // ----- Bulk pixel conversion -----

    [Fact]
    public void PixelConversion_VectorAndScalarPathsAgree()
    {
        // Every ordered pair of built-in formats, over a length that exercises the vector body and the tail.
        var source = new Rgba32[251];
        uint state = 12345u;
        for (int i = 0; i < source.Length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            source[i] = new Rgba32((byte)(state >> 24), (byte)(state >> 16), (byte)(state >> 8), (byte)state);
        }

        AssertSimdMatchesScalar(() => Roundtrip<Rgb24>(source), "Rgba32<->Rgb24");
        AssertSimdMatchesScalar(() => Roundtrip<Bgr24>(source), "Rgba32<->Bgr24");
        AssertSimdMatchesScalar(() => Roundtrip<Bgra32>(source), "Rgba32<->Bgra32");
        AssertSimdMatchesScalar(() => Roundtrip<L8>(source), "Rgba32<->L8");

        AssertSimdMatchesScalar(() => Convert<Rgba32, Rgb24>(source), "Rgba32->Rgb24");
        AssertSimdMatchesScalar(() => Convert<Rgba32, Bgr24>(source), "Rgba32->Bgr24");
        AssertSimdMatchesScalar(() => Convert<Rgba32, Bgra32>(source), "Rgba32->Bgra32");
        AssertSimdMatchesScalar(() => Convert<Rgba32, L8>(source), "Rgba32->L8");

        Rgb24[] rgb = Convert<Rgba32, Rgb24>(source);
        AssertSimdMatchesScalar(() => Convert<Rgb24, Bgr24>(rgb), "Rgb24->Bgr24");
        AssertSimdMatchesScalar(() => Convert<Rgb24, Bgra32>(rgb), "Rgb24->Bgra32");
        AssertSimdMatchesScalar(() => Convert<Rgb24, Rgba32>(rgb), "Rgb24->Rgba32");
        AssertSimdMatchesScalar(() => Convert<Rgb24, L8>(rgb), "Rgb24->L8");

        Bgra32[] bgra = Convert<Rgba32, Bgra32>(source);
        AssertSimdMatchesScalar(() => Convert<Bgra32, Rgba32>(bgra), "Bgra32->Rgba32");
        AssertSimdMatchesScalar(() => Convert<Bgra32, Rgb24>(bgra), "Bgra32->Rgb24");
        AssertSimdMatchesScalar(() => Convert<Bgra32, L8>(bgra), "Bgra32->L8");

        L8[] gray = Convert<Rgba32, L8>(source);
        AssertSimdMatchesScalar(() => Convert<L8, Rgba32>(gray), "L8->Rgba32");
        AssertSimdMatchesScalar(() => Convert<L8, Rgb24>(gray), "L8->Rgb24");
        AssertSimdMatchesScalar(() => Convert<L8, Bgr24>(gray), "L8->Bgr24");
        AssertSimdMatchesScalar(() => Convert<L8, Bgra32>(gray), "L8->Bgra32");

        static TDest[] Convert<TSrc, TDest>(TSrc[] input)
            where TSrc : unmanaged, IPixel<TSrc>
            where TDest : unmanaged, IPixel<TDest>
        {
            var output = new TDest[input.Length];
            PixelOps.Convert<TSrc, TDest>(input, output);
            return output;
        }

        static Rgba32[] Roundtrip<TMiddle>(Rgba32[] input)
            where TMiddle : unmanaged, IPixel<TMiddle>
            => Convert<TMiddle, Rgba32>(Convert<Rgba32, TMiddle>(input));
    }

    [Fact]
    public void PixelConversion_VectorLuminance_MatchesTheL8Conversion()
    {
        // Exhaustive over the grey diagonal plus a sweep of colours: the vector kernel must reproduce
        // L8.FromRgba32 exactly, not merely approximately.
        var source = new Rgba32[4096];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = new Rgba32((byte)(i & 0xFF), (byte)((i >> 2) & 0xFF), (byte)((i * 7) & 0xFF), 255);
        }

        var vectorized = new L8[source.Length];
        PixelOps.Convert<Rgba32, L8>(source, vectorized);
        for (int i = 0; i < source.Length; i++)
        {
            Assert.Equal(L8.FromRgba32(source[i]), vectorized[i]);
            Assert.Equal(PixelOps.Luminance8(source[i]), vectorized[i].PackedValue);
        }
    }

    // ----- Processing operations -----

    [Fact]
    public void Grayscale_PathsAgree()
    {
        using Image<Rgba32> source = CreateSource();
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.Grayscale())), "Grayscale");
    }

    [Fact]
    public void Invert_PathsAgree()
    {
        using Image<Rgba32> source = CreateSource();
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.Invert())), "Invert");
    }

    [Fact]
    public void BrightnessAndContrast_PathsAgree()
    {
        using Image<Rgba32> source = CreateSource();
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.Brightness(1.37f))), "Brightness");
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.Contrast(0.63f))), "Contrast");
    }

    [Fact]
    public void Thresholds_PathsAgree()
    {
        using Image<Rgba32> source = CreateSource();
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.BinaryThreshold(0.42f))), "BinaryThreshold");
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.OtsuThreshold())), "OtsuThreshold");
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.AdaptiveThreshold(15, 0.85f))), "AdaptiveThreshold");
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.SauvolaThreshold(15, 0.2f))), "SauvolaThreshold");
    }

    [Fact]
    public void Resize_PathsAgree()
    {
        using Image<Rgba32> source = CreateSource(200, 150);
        foreach (IResampler sampler in new[] { KnownResamplers.Bicubic, KnownResamplers.Lanczos3, KnownResamplers.Triangle, KnownResamplers.Box })
        {
            AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.Resize(83, 61, sampler))), $"Resize down {sampler.GetType().Name}");
            AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.Resize(311, 233, sampler))), $"Resize up {sampler.GetType().Name}");
        }

        AssertPathsAgree(
            () => Pixels(source.Clone(ctx => ctx.Resize(new ResizeOptions { Size = new Size(77, 55), Compand = true }))),
            "Resize companded");
    }

    [Fact]
    public void ResizeOfOpaqueFormats_PathsAgree()
    {
        using Image<Rgba32> rgba = CreateSource(200, 150);
        using Image<Rgb24> rgb = rgba.CloneAs<Rgb24>();
        using Image<L8> gray = rgba.CloneAs<L8>();
        AssertPathsAgree(
            () => rgb.Clone(ctx => ctx.Resize(83, 61, KnownResamplers.Bicubic)).Frames.RootFrame.PixelSpan.ToArray(),
            "Resize Rgb24");
        AssertPathsAgree(
            () => gray.Clone(ctx => ctx.Resize(83, 61, KnownResamplers.Lanczos3)).Frames.RootFrame.PixelSpan.ToArray(),
            "Resize L8");
        AssertPathsAgree(
            () => gray.Clone(ctx => ctx.Resize(277, 199, KnownResamplers.Bicubic)).Frames.RootFrame.PixelSpan.ToArray(),
            "Resize L8 up");
    }

    [Fact]
    public void RotateAndFlip_PathsAgree()
    {
        using Image<Rgba32> source = CreateSource(200, 150);
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.Rotate(RotateMode.Rotate90))), "Rotate90");
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.Rotate(RotateMode.Rotate180))), "Rotate180");
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.Rotate(RotateMode.Rotate270))), "Rotate270");
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.Flip(FlipMode.Horizontal))), "FlipHorizontal");
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.Flip(FlipMode.Vertical))), "FlipVertical");
    }

    [Fact]
    public void Filters_PathsAgree()
    {
        using Image<Rgba32> source = CreateSource(120, 90);
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.GaussianBlur(2.5f))), "GaussianBlur");
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.GaussianSharpen(2.5f))), "GaussianSharpen");
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.MedianBlur(3))), "MedianBlur");
    }

    [Fact]
    public void DrawImage_PathsAgree()
    {
        using Image<Rgba32> source = CreateSource(200, 150);
        using Image<Rgba32> overlay = CreateSource(90, 70);
        AssertPathsAgree(() => Pixels(source.Clone(ctx => ctx.DrawImage(overlay, new Point(37, 23), 0.6f))), "DrawImage");
    }

    // ----- Copy-on-write cloning -----

    [Fact]
    public void CloneWithOperation_NeverAliasesTheSource()
    {
        // Clone(op) starts out sharing the source's buffers; every path must end with the clone owning its
        // own pixels, whether the operation replaced the buffer, wrote in place, or did nothing at all.
        foreach ((string name, Action<IImageProcessingContext> operation) in new (string, Action<IImageProcessingContext>)[]
        {
            ("resize", ctx => ctx.Resize(83, 61)),
            ("resize to the same size", ctx => ctx.Resize(137, 91)),
            ("crop", ctx => ctx.Crop(new Rectangle(3, 5, 40, 30))),
            ("rotate", ctx => ctx.Rotate(RotateMode.Rotate90)),
            ("flip", ctx => ctx.Flip(FlipMode.Horizontal)),
            ("grayscale", ctx => ctx.Grayscale()),
            ("resize then grayscale", ctx => ctx.Resize(83, 61).Grayscale()),
            ("nothing", _ => { }),
        })
        {
            using Image<Rgba32> source = CreateSource();
            Rgba32[] before = Pixels(source);
            using Image<Rgba32> clone = source.Clone(operation);

            Assert.Equal(before, Pixels(source));

            // Writing through the clone must not reach the source.
            clone.Frames.RootFrame.GetRowSpan(0)[0] = new Rgba32(1, 2, 3, 4);
            Assert.Equal(before, Pixels(source));
            Assert.False(
                ReferenceEquals(source.Frames.RootFrame.PixelArray, clone.Frames.RootFrame.PixelArray),
                $"{name}: the clone still shares the source's buffer.");
        }
    }

    [Fact]
    public void CloneWithOperation_MatchesADeepCopyFollowedByMutate()
    {
        using Image<Rgba32> source = CreateSource();
        foreach ((string name, Action<IImageProcessingContext> operation) in new (string, Action<IImageProcessingContext>)[]
        {
            ("resize", ctx => ctx.Resize(83, 61)),
            ("crop", ctx => ctx.Crop(new Rectangle(3, 5, 40, 30))),
            ("grayscale", ctx => ctx.Grayscale()),
            ("brightness then rotate", ctx => ctx.Brightness(1.2f).Rotate(RotateMode.Rotate270)),
        })
        {
            using Image<Rgba32> viaClone = source.Clone(operation);
            using Image<Rgba32> viaMutate = source.Clone();
            viaMutate.Mutate(operation);
            Assert.Equal(viaMutate.Width, viaClone.Width);
            Assert.Equal(viaMutate.Height, viaClone.Height);
            Assert.True(Pixels(viaMutate).AsSpan().SequenceEqual(Pixels(viaClone)), $"{name}: clone and mutate disagree.");
        }
    }

    // ----- Codecs -----

    [Fact]
    public void PngRoundtrip_PathsAgree()
    {
        using Image<Rgba32> source = CreateSource(200, 150);
        byte[] encoded = Encode(source, new Formats.Png.PngEncoder());
        AssertPathsAgree(() => Pixels(Image.Load<Rgba32>(encoded)), "PNG decode");
        AssertSimdMatchesScalar(() => Encode(source, new Formats.Png.PngEncoder()), "PNG encode");
        AssertParallelMatchesSerial(() => Encode(source, new Formats.Png.PngEncoder()), "PNG encode");
    }

    [Fact]
    public void JpegDecode_PathsAgree()
    {
        using Image<Rgba32> source = CreateSmoothSource(200, 150);
        byte[] encoded = Encode(source, new Formats.Jpeg.JpegEncoder { Quality = 90 });
        AssertPathsAgree(() => Pixels(Image.Load<Rgba32>(encoded)), "JPEG decode");
        AssertPathsAgree(() => Image.Load<L8>(encoded).Frames.RootFrame.PixelSpan.ToArray(), "JPEG decode to L8");
    }

    [Fact]
    public void BmpAndTiffRoundtrip_PathsAgree()
    {
        using Image<Rgba32> source = CreateSource(200, 150);
        byte[] bmp = Encode(source, new Formats.Bmp.BmpEncoder());
        byte[] tiff = Encode(source, new Formats.Tiff.TiffEncoder());
        AssertPathsAgree(() => Pixels(Image.Load<Rgba32>(bmp)), "BMP decode");
        AssertPathsAgree(() => Pixels(Image.Load<Rgba32>(tiff)), "TIFF decode");
        AssertSimdMatchesScalar(() => Encode(source, new Formats.Tiff.TiffEncoder()), "TIFF encode");
    }

    // ----- Tensors -----

    [Fact]
    public void TensorConversion_PathsAgree()
    {
        using Image<Rgba32> source = CreateSource(64, 48);
        using Image<Rgb24> rgb = source.CloneAs<Rgb24>();
        using Image<L8> gray = source.CloneAs<L8>();
        float[] mean = { 0.485f, 0.456f, 0.406f };
        float[] std = { 0.229f, 0.224f, 0.225f };

        AssertPathsAgree(() => source.ToChwTensor(), "ToChwTensor");
        AssertPathsAgree(() => source.ToChwTensor(mean, std), "ToChwTensor normalised");
        AssertPathsAgree(() => source.ToHwcTensor(mean, std), "ToHwcTensor normalised");
        AssertPathsAgree(() => source.ToGrayscaleTensor(0.5f, 0.25f), "ToGrayscaleTensor");
        AssertPathsAgree(() => rgb.ToChwTensor(mean, std), "ToChwTensor Rgb24");
        AssertPathsAgree(() => gray.ToHwcTensor(), "ToHwcTensor L8");
        AssertPathsAgree(() => gray.ToGrayscaleTensor(), "ToGrayscaleTensor L8");
    }

    // ----- Helpers -----

    private static byte[] Encode<TPixel>(Image<TPixel> image, Formats.IImageEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    /// <summary>A smooth image, so JPEG has something compressible to work with.</summary>
    private static Image<Rgba32> CreateSmoothSource(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba32(
                    (byte)((x * 255) / width),
                    (byte)((y * 255) / height),
                    (byte)(((x + y) * 255) / (width + height)),
                    255);
            }
        }

        return image;
    }
}

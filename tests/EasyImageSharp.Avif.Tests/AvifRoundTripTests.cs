using EasyImageSharp.Formats;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Avif.Tests;

/// <summary>
/// Encoder/decoder round trips through the native runtime. Lossless 8-bit results must be bit-exact, wide lossless
/// results exact at the coded bit depth, and lossy results inside an explicit error budget. Independent ground
/// truth from a separate libavif build lives in <see cref="AvifFixtureTests"/>.
/// </summary>
public class AvifRoundTripTests
{
    private static readonly DecoderOptions Options = new() { AdditionalFormats = [AvifFormat.Instance] };

    [NativeFact]
    public void NativeRuntime_ReportsVersionAndBothCodecDirections()
    {
        Assert.True(AvifFormat.IsNativeLibraryAvailable);
        string[] parts = AvifFormat.NativeVersion.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.True(int.Parse(parts[0]) >= 1, AvifFormat.NativeVersion);
        Assert.True(AvifFormat.CanDecode);
        Assert.True(AvifFormat.CanEncode);
        Assert.Contains("aom", AvifFormat.NativeCodecVersions, StringComparison.OrdinalIgnoreCase);
    }

    [NativeFact]
    public void Lossless8BitRgba_RoundTripsExactlyThroughTheFormatProvider()
    {
        using Image<Rgba32> source = Pattern<Rgba32>(37, 23, (x, y) => new Rgba32((byte)(x * 7), (byte)(y * 11), (byte)((x * y) % 251), (byte)(255 - (x * 3))));
        byte[] encoded = Encode(source, new AvifEncoder { Lossless = true, Speed = 9 });
        Assert.True(AvifFormat.IsAvif(encoded));

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded, Options);
        AssertPixelsEqual(source, decoded);
        Assert.Same(AvifFormat.Instance, decoded.Metadata.DecodedImageFormat);
        AvifMetadata avif = decoded.Metadata.GetFormatMetadata<AvifMetadata>();
        Assert.True(avif.HasAlpha);
        Assert.Equal(8, avif.BitDepth);
        Assert.Equal(AvifChromaSubsampling.Yuv444, avif.ChromaSubsampling);
        Assert.Equal(0, avif.MatrixCoefficients); // Identity: RGB stored directly.
        Assert.True(avif.FullRange);
    }

    [NativeTheory]
    [InlineData(10)]
    [InlineData(12)]
    public void WideLossless_IsExactAtTheCodedBitDepth(int bitDepth)
    {
        using Image<Rgba64> wide = Pattern<Rgba64>(19, 13, (x, y) => new Rgba64((ushort)(x * 3011), (ushort)(y * 4999), (ushort)(x * y * 97), (ushort)(65535 - (x * 1013))));
        byte[] encoded = Encode(wide, new AvifEncoder { Lossless = true, BitDepth = bitDepth, Speed = 9 });
        using Image<Rgba64> decoded = new AvifDecoder().Decode<Rgba64>(encoded, DecoderOptions.Default);
        Assert.Equal(bitDepth, decoded.Metadata.GetFormatMetadata<AvifMetadata>().BitDepth);

        // Quantising 16 bits to N and back moves a sample by at most half an N-bit step.
        int step = 65535 / ((1 << bitDepth) - 1);
        int budget = (step / 2) + 1;
        int worst = 0;
        for (int y = 0; y < wide.Height; y++)
        {
            for (int x = 0; x < wide.Width; x++)
            {
                Rgba64 a = wide[x, y], b = decoded[x, y];
                worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A))));
            }
        }

        Assert.True(worst <= budget, $"{bitDepth}-bit lossless moved a sample by {worst}, budget {budget}.");

        // Quantising once more is the identity: the decoded values are exactly representable at that depth.
        byte[] again = Encode(decoded, new AvifEncoder { Lossless = true, BitDepth = bitDepth, Speed = 9 });
        using Image<Rgba64> twice = new AvifDecoder().Decode<Rgba64>(again, DecoderOptions.Default);
        AssertPixelsEqual(decoded, twice);
    }

    [NativeFact]
    public void GreyAndOpaqueImages_StoreOneColourChannelAndNoAlpha()
    {
        using Image<L8> grey = Pattern<L8>(16, 9, (x, y) => new L8((byte)((x * 16) + y)));
        byte[] encoded = Encode(grey, new AvifEncoder { Lossless = true, Speed = 9 });
        using Image<L8> decoded = Image.Load<L8>(encoded, Options);
        AssertPixelsEqual(grey, decoded);
        Assert.Equal(AvifChromaSubsampling.Yuv400, decoded.Metadata.GetFormatMetadata<AvifMetadata>().ChromaSubsampling);

        using Image<Rgba32> opaque = Pattern<Rgba32>(8, 8, (x, y) => new Rgba32((byte)x, (byte)y, 9, 255));
        ImageInfo info = Image.Identify(Encode(opaque, new AvifEncoder { Lossless = true, Speed = 9 }), Options);
        Assert.False(info.Metadata.GetFormatMetadata<AvifMetadata>().HasAlpha);
    }

    [NativeFact]
    public void Lossy_StaysCloseAndShrinksWithLowerQuality()
    {
        using Image<Rgb24> source = Pattern<Rgb24>(64, 48, (x, y) => new Rgb24((byte)(128 + (100 * Math.Sin(x / 5.0))), (byte)(128 + (100 * Math.Cos(y / 7.0))), (byte)((x + y) * 2)));
        byte[] fine = Encode(source, new AvifEncoder { Quality = 90, Speed = 8 });
        byte[] coarse = Encode(source, new AvifEncoder { Quality = 20, Speed = 8 });
        Assert.True(coarse.Length < fine.Length, $"quality 20 ({coarse.Length} bytes) should be smaller than quality 90 ({fine.Length} bytes)");

        using Image<Rgb24> decoded = Image.Load<Rgb24>(fine, Options);
        double psnr = Psnr(source, decoded);
        Assert.True(psnr > 38, $"PSNR {psnr:F2} dB at quality 90");
    }

    [NativeFact]
    public void Animation_KeepsFramesDurationsAndRepetitionCount()
    {
        using var image = new Image<Rgba32>(12, 10, new Rgba32(255, 0, 0, 255));
        image.Frames.CreateFrame(12, 10).Metadata.SetFormatMetadata(new AvifFrameMetadata { Duration = TimeSpan.FromMilliseconds(250) });
        image.Frames.CreateFrame(12, 10).Metadata.SetFormatMetadata(new AvifFrameMetadata { Duration = TimeSpan.FromMilliseconds(40) });
        image.Frames.RootFrame.Metadata.SetFormatMetadata(new AvifFrameMetadata { Duration = TimeSpan.FromMilliseconds(100) });
        for (int x = 0; x < 12; x++)
        {
            image.Frames[1][x, 3] = new Rgba32(0, 255, 0, 128);
            image.Frames[2][x, 7] = new Rgba32(0, 0, 255, 255);
        }

        byte[] encoded = Encode(image, new AvifEncoder { Lossless = true, RepetitionCount = 3, Speed = 9 });
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded, Options);
        Assert.Equal(3, decoded.Frames.Count);
        for (int i = 0; i < 3; i++)
        {
            AssertFramesEqual(image.Frames[i], decoded.Frames[i]);
        }

        Assert.Equal([100.0, 250.0, 40.0], decoded.Frames.Select(f => Math.Round(f.Metadata.GetFormatMetadata<AvifFrameMetadata>().Duration.TotalMilliseconds)));
        AvifMetadata metadata = decoded.Metadata.GetFormatMetadata<AvifMetadata>();
        Assert.True(metadata.IsAnimated);
        Assert.Equal(3, metadata.RepetitionCount);
        Assert.Equal(3, Image.Identify(encoded, Options).FrameCount);

        using Image<Rgba32> limited = Image.Load<Rgba32>(encoded, new DecoderOptions { AdditionalFormats = [AvifFormat.Instance], MaxFrames = 2 });
        Assert.Equal(2, limited.Frames.Count);
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(encoded,
            new DecoderOptions { AdditionalFormats = [AvifFormat.Instance], MaxTotalPixels = (12 * 10 * 2) + 1 }));
    }

    [NativeFact]
    public void Metadata_IccExifAndXmpRoundTrip_AndSkipMetadataDropsThem()
    {
        byte[] icc = SyntheticIcc.CreateRgbMatrixProfile();
        using Image<Rgb24> image = Pattern<Rgb24>(10, 6, (x, y) => new Rgb24((byte)(x * 20), (byte)(y * 40), 77));
        image.Metadata.IccProfile = new IccProfile(icc);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Software, "EasyImageSharp.Avif test");
        image.Metadata.XmpProfile = new XmpProfile("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"/></x:xmpmeta>");

        byte[] encoded = Encode(image, new AvifEncoder { Lossless = true, Speed = 9 });
        using Image<Rgb24> decoded = Image.Load<Rgb24>(encoded, Options);
        AssertPixelsEqual(image, decoded);
        Assert.Equal(icc, decoded.Metadata.IccProfile!.ToByteArray());
        Assert.Equal("EasyImageSharp.Avif test", decoded.Metadata.ExifProfile!.GetValue(ExifTag.Software)!.Value);
        Assert.Equal(image.Metadata.XmpProfile.ToByteArray(), decoded.Metadata.XmpProfile!.ToByteArray());

        ImageInfo info = Image.Identify(encoded, Options);
        Assert.Equal(icc, info.Metadata.IccProfile!.ToByteArray());
        Assert.NotNull(info.Metadata.ExifProfile);
        Assert.NotNull(info.Metadata.XmpProfile);

        using Image<Rgb24> plain = Image.Load<Rgb24>(Encode(image, new AvifEncoder { Lossless = true, SkipMetadata = true, Speed = 9 }), Options);
        Assert.Null(plain.Metadata.IccProfile);
        Assert.Null(plain.Metadata.ExifProfile);
        Assert.Null(plain.Metadata.XmpProfile);
    }

    [NativeFact]
    public void Limits_AreEnforcedBeforePixelsAndIdentifyIgnoresThem()
    {
        using Image<Rgba32> image = Pattern<Rgba32>(40, 30, (x, y) => new Rgba32((byte)x, (byte)y, 0, 255));
        byte[] encoded = Encode(image, new AvifEncoder { Lossless = true, Speed = 9 });
        var tight = new DecoderOptions { AdditionalFormats = [AvifFormat.Instance], MaxPixels = (40 * 30) - 1 };
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(encoded, tight));
        ImageInfo info = Image.Identify(encoded, tight);
        Assert.Equal(40, info.Width);
        Assert.Equal(30, info.Height);
        Assert.Equal(1, info.FrameCount);
        Assert.Same(AvifFormat.Instance, info.Format);

        Assert.Throws<ImageSizeLimitExceededException>(() =>
            new AvifDecoder().Decode<Rgba32>(encoded, new DecoderOptions { MaxEncodedBytes = encoded.Length - 1 }));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var options = new DecoderOptions { ExecutionOptions = new ImageExecutionOptions { CancellationToken = cancelled.Token } };
        Assert.ThrowsAny<OperationCanceledException>(() => new AvifDecoder().Decode<Rgba32>(encoded, options));
    }

    [NativeFact]
    public void MalformedInput_StaysInsideTheExceptionContract()
    {
        using Image<Rgba32> image = Pattern<Rgba32>(33, 17, (x, y) => new Rgba32((byte)(x * y), (byte)x, (byte)y, 200));
        byte[] encoded = Encode(image, new AvifEncoder { Lossless = true, Speed = 10 });
        var decoder = new AvifDecoder();
        var random = new Random(1234);
        for (int i = 0; i < 200; i++)
        {
            byte[] mutated = (byte[])encoded.Clone();
            int length = i % 3 == 0 ? random.Next(12, mutated.Length) : mutated.Length;
            for (int flips = 0; flips < 1 + (i % 4); flips++)
            {
                mutated[random.Next(12, length)] ^= (byte)(1 << random.Next(8));
            }

            byte[] input = mutated.AsSpan(0, length).ToArray();
            try
            {
                using Image<Rgba32> decoded = decoder.Decode<Rgba32>(input, DecoderOptions.Default);
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
            {
            }

            try
            {
                _ = decoder.Identify(input, DecoderOptions.Default);
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
            {
            }
        }

        Assert.Throws<InvalidImageContentException>(() => decoder.Decode<Rgba32>(encoded.AsSpan(0, encoded.Length / 2), DecoderOptions.Default));
        Assert.Throws<UnknownImageFormatException>(() => decoder.Decode<Rgba32>(new byte[] { 0, 0, 0, 12, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c' }, DecoderOptions.Default));
        Assert.False(AvifFormat.IsAvif("not an image"u8));
    }

    [Fact]
    public void EncoderOptions_RejectOutOfRangeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AvifEncoder { Quality = 101 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AvifEncoder { AlphaQuality = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AvifEncoder { Speed = 11 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AvifEncoder { BitDepth = 9 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AvifEncoder { MaxThreads = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AvifEncoder { FrameDuration = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AvifEncoder { RepetitionCount = -2 });
    }

    [NativeFact]
    public void Lossless_RequiresFullChroma()
    {
        using Image<Rgb24> image = Pattern<Rgb24>(8, 8, (x, y) => new Rgb24((byte)x, (byte)y, 0));
        Assert.Throws<NotSupportedException>(() => Encode(image, new AvifEncoder { Lossless = true, ChromaSubsampling = AvifChromaSubsampling.Yuv420 }));
    }

    [NativeFact]
    public async Task SaveAsAvif_WritesFilesAndStreams()
    {
        using Image<Rgba32> image = Pattern<Rgba32>(5, 4, (x, y) => new Rgba32((byte)(x * 50), (byte)(y * 60), 1, 255));
        string path = Path.Combine(Path.GetTempPath(), $"easyimagesharp-avif-{Guid.NewGuid():N}.avif");
        try
        {
            image.SaveAsAvif(path, new AvifEncoder { Lossless = true, Speed = 9 });
            using (Image<Rgba32> decoded = Image.Load<Rgba32>(path, Options))
            {
                AssertPixelsEqual(image, decoded);
            }

            await image.SaveAsAvifAsync(path, new AvifEncoder { Lossless = true, Speed = 9 }, CancellationToken.None);
            Assert.True(AvifFormat.IsAvif(await File.ReadAllBytesAsync(path)));
            using var stream = new MemoryStream();
            image.Save(stream, AvifFormat.Instance);
            Assert.True(AvifFormat.IsAvif(stream.ToArray()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static Image<TPixel> Pattern<TPixel>(int width, int height, Func<int, int, TPixel> pixel)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var image = new Image<TPixel>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = pixel(x, y);
            }
        }

        return image;
    }

    internal static void AssertPixelsEqual<TPixel>(Image<TPixel> expected, Image<TPixel> actual)
        where TPixel : unmanaged, IPixel<TPixel>
        => AssertFramesEqual(expected.Frames.RootFrame, actual.Frames.RootFrame);

    internal static double Psnr(Image<Rgb24> expected, Image<Rgb24> actual)
    {
        double sum = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgb24 a = expected[x, y], b = actual[x, y];
                sum += Square(a.R - b.R) + Square(a.G - b.G) + Square(a.B - b.B);
            }
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / (sum / (expected.Width * expected.Height * 3)));
    }

    private static void AssertFramesEqual<TPixel>(ImageFrame<TPixel> expected, ImageFrame<TPixel> actual)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Assert.True(expected[x, y].Equals(actual[x, y]), $"pixel ({x},{y}): expected {expected[x, y]}, got {actual[x, y]}");
            }
        }
    }

    private static byte[] Encode(Image image, AvifEncoder encoder)
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    private static double Square(int value) => (double)value * value;
}

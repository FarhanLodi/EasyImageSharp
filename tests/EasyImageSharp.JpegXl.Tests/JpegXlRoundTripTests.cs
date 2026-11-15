using EasyImageSharp.Formats;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.JpegXl.Tests;

/// <summary>
/// Encoder/decoder round trips through the native runtime. Lossless results must be bit-exact; lossy results are
/// bounded by an explicit error budget. Independent decoder ground truth lives in <see cref="JpegXlFixtureTests"/>.
/// </summary>
public class JpegXlRoundTripTests
{
    private static readonly DecoderOptions Options = new() { AdditionalFormats = [JpegXlFormat.Instance] };

    [NativeFact]
    public void NativeRuntime_ReportsASupportedVersion()
    {
        Assert.True(JpegXlFormat.IsNativeLibraryAvailable);
        string[] parts = JpegXlFormat.NativeVersion.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.True(int.Parse(parts[0]) > 0 || int.Parse(parts[1]) >= 9);
    }

    [NativeFact]
    public void Lossless8BitRgba_RoundTripsExactlyThroughTheFormatProvider()
    {
        using Image<Rgba32> source = Pattern<Rgba32>(37, 23, (x, y) => new Rgba32((byte)(x * 7), (byte)(y * 11), (byte)((x * y) % 251), (byte)(255 - (x * 3))));
        using var stream = new MemoryStream();
        source.Save(stream, new JpegXlEncoder { Lossless = true, Effort = 3 });
        byte[] encoded = stream.ToArray();
        Assert.True(JpegXlFormat.IsJpegXl(encoded));

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded, Options);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        AssertPixelsEqual(source, decoded);
        Assert.Same(JpegXlFormat.Instance, decoded.Metadata.DecodedImageFormat);
        JpegXlMetadata jxl = decoded.Metadata.GetFormatMetadata<JpegXlMetadata>();
        Assert.True(jxl.HasAlpha);
        Assert.True(jxl.UsesOriginalProfile);
        Assert.Equal(8, jxl.BitsPerSample);
        Assert.Null(decoded.Metadata.IccProfile);
    }

    [NativeFact]
    public void Lossless16BitAndFloat_KeepFullPrecision()
    {
        using Image<Rgba64> wide = Pattern<Rgba64>(19, 13, (x, y) => new Rgba64((ushort)(x * 3011), (ushort)(y * 4999), (ushort)(x * y * 97), (ushort)(65535 - (x * 1013))));
        using var stream = new MemoryStream();
        wide.Save(stream, new JpegXlEncoder { Lossless = true, Effort = 2 });
        using Image<Rgba64> decoded = new JpegXlDecoder().Decode<Rgba64>(stream.ToArray(), DecoderOptions.Default);
        AssertPixelsEqual(wide, decoded);
        Assert.Equal(16, decoded.Metadata.GetFormatMetadata<JpegXlMetadata>().BitsPerSample);

        using Image<RgbaVector> floats = Pattern<RgbaVector>(9, 7, (x, y) => new RgbaVector(x / 9f, y / 7f, 0.123f + (x * 0.01f), 1f - (y * 0.05f)));
        using var floatStream = new MemoryStream();
        floats.Save(floatStream, new JpegXlEncoder { Lossless = true, Effort = 2 });
        using Image<RgbaVector> decodedFloats = new JpegXlDecoder().Decode<RgbaVector>(floatStream.ToArray(), DecoderOptions.Default);
        AssertPixelsEqual(floats, decodedFloats);
        Assert.Equal(8, decodedFloats.Metadata.GetFormatMetadata<JpegXlMetadata>().ExponentBitsPerSample);
    }

    [NativeFact]
    public void GreyAndOpaqueImages_StoreOneColourChannelAndNoAlpha()
    {
        using Image<L8> grey = Pattern<L8>(16, 9, (x, y) => new L8((byte)((x * 16) + y)));
        using var stream = new MemoryStream();
        grey.Save(stream, new JpegXlEncoder { Lossless = true });
        using Image<L8> decoded = Image.Load<L8>(stream.ToArray(), Options);
        AssertPixelsEqual(grey, decoded);
        JpegXlMetadata metadata = decoded.Metadata.GetFormatMetadata<JpegXlMetadata>();
        Assert.True(metadata.IsGray);
        Assert.False(metadata.HasAlpha);

        using Image<Rgba32> opaque = Pattern<Rgba32>(8, 8, (x, y) => new Rgba32((byte)x, (byte)y, 9, 255));
        using var opaqueStream = new MemoryStream();
        opaque.Save(opaqueStream, new JpegXlEncoder { Lossless = true });
        ImageInfo info = Image.Identify(opaqueStream.ToArray(), Options);
        Assert.False(info.Metadata.GetFormatMetadata<JpegXlMetadata>().HasAlpha);
        Assert.Equal(24, info.PixelType.BitsPerPixel);
    }

    [NativeFact]
    public void LossyDistance_StaysCloseAndShrinksWithDistance()
    {
        using Image<Rgb24> source = Pattern<Rgb24>(64, 48, (x, y) => new Rgb24((byte)(128 + (100 * Math.Sin(x / 5.0))), (byte)(128 + (100 * Math.Cos(y / 7.0))), (byte)((x + y) * 2)));
        byte[] fine = Encode(source, new JpegXlEncoder { Distance = 0.5f, Effort = 3 });
        byte[] coarse = Encode(source, new JpegXlEncoder { Distance = 8f, Effort = 3 });
        Assert.True(coarse.Length < fine.Length, $"distance 8 ({coarse.Length} bytes) should be smaller than distance 0.5 ({fine.Length} bytes)");
        using Image<Rgb24> decoded = Image.Load<Rgb24>(fine, Options);
        Assert.False(decoded.Metadata.GetFormatMetadata<JpegXlMetadata>().UsesOriginalProfile);
        double sum = 0;
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Rgb24 a = source[x, y], b = decoded[x, y];
                sum += Square(a.R - b.R) + Square(a.G - b.G) + Square(a.B - b.B);
            }
        }

        double psnr = 10 * Math.Log10(255.0 * 255.0 / (sum / (source.Width * source.Height * 3)));
        Assert.True(psnr > 38, $"PSNR {psnr:F2} dB at distance 0.5");
    }

    [NativeFact]
    public void Animation_KeepsFramesDurationsAndLoopCount()
    {
        using var image = new Image<Rgba32>(12, 10, new Rgba32(255, 0, 0, 255));
        image.Frames.CreateFrame(12, 10).Metadata.SetFormatMetadata(new JpegXlFrameMetadata { Duration = TimeSpan.FromMilliseconds(250) });
        image.Frames.CreateFrame(12, 10).Metadata.SetFormatMetadata(new JpegXlFrameMetadata { Duration = TimeSpan.FromMilliseconds(40) });
        image.Frames.RootFrame.Metadata.SetFormatMetadata(new JpegXlFrameMetadata { Duration = TimeSpan.FromMilliseconds(100) });
        for (int x = 0; x < 12; x++)
        {
            image.Frames[1][x, 3] = new Rgba32(0, 255, 0, 128);
            image.Frames[2][x, 7] = new Rgba32(0, 0, 255, 255);
        }

        byte[] encoded = Encode(image, new JpegXlEncoder { Lossless = true, LoopCount = 3 });
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded, Options);
        Assert.Equal(3, decoded.Frames.Count);
        for (int i = 0; i < 3; i++)
        {
            AssertFramesEqual(image.Frames[i], decoded.Frames[i]);
        }

        Assert.Equal([100.0, 250.0, 40.0], decoded.Frames.Select(f => f.Metadata.GetFormatMetadata<JpegXlFrameMetadata>().Duration.TotalMilliseconds));
        JpegXlMetadata metadata = decoded.Metadata.GetFormatMetadata<JpegXlMetadata>();
        Assert.True(metadata.IsAnimated);
        Assert.Equal(3, metadata.LoopCount);
        Assert.Equal(3, Image.Identify(encoded, Options).FrameCount);

        using Image<Rgba32> limited = Image.Load<Rgba32>(encoded, new DecoderOptions { AdditionalFormats = [JpegXlFormat.Instance], MaxFrames = 2 });
        Assert.Equal(2, limited.Frames.Count);
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(encoded,
            new DecoderOptions { AdditionalFormats = [JpegXlFormat.Instance], MaxTotalPixels = (12 * 10 * 2) + 1 }));
    }

    [NativeFact]
    public void Metadata_IccExifAndXmpRoundTripInBoxes()
    {
        byte[] icc = SyntheticIcc.CreateRgbMatrixProfile();
        using Image<Rgb24> image = Pattern<Rgb24>(10, 6, (x, y) => new Rgb24((byte)(x * 20), (byte)(y * 40), 77));
        image.Metadata.IccProfile = new IccProfile(icc);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Software, "EasyImageSharp.JpegXl test");
        image.Metadata.XmpProfile = new XmpProfile("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"/></x:xmpmeta>");

        foreach (bool compress in new[] { false, true })
        {
            byte[] encoded = Encode(image, new JpegXlEncoder { Lossless = true, CompressMetadataBoxes = compress });
            using Image<Rgb24> decoded = Image.Load<Rgb24>(encoded, Options);
            AssertPixelsEqual(image, decoded);
            Assert.Equal(icc, decoded.Metadata.IccProfile!.ToByteArray());
            Assert.Equal("EasyImageSharp.JpegXl test", decoded.Metadata.ExifProfile!.GetValue(ExifTag.Software)!.Value);
            Assert.Equal(image.Metadata.XmpProfile.ToByteArray(), decoded.Metadata.XmpProfile!.ToByteArray());
            Assert.True(decoded.Metadata.GetFormatMetadata<JpegXlMetadata>().HasContainer);

            ImageInfo info = Image.Identify(encoded, Options);
            Assert.NotNull(info.Metadata.ExifProfile);
            Assert.NotNull(info.Metadata.XmpProfile);
            Assert.Equal(icc, info.Metadata.IccProfile!.ToByteArray());
        }

        byte[] stripped = Encode(image, new JpegXlEncoder { Lossless = true, SkipMetadata = true });
        using Image<Rgb24> plain = Image.Load<Rgb24>(stripped, Options);
        Assert.Null(plain.Metadata.IccProfile);
        Assert.Null(plain.Metadata.ExifProfile);
        Assert.Null(plain.Metadata.XmpProfile);
    }

    [NativeFact]
    public void Limits_AreEnforcedBeforePixelsAndIdentifyIgnoresThem()
    {
        using Image<Rgba32> image = Pattern<Rgba32>(40, 30, (x, y) => new Rgba32((byte)x, (byte)y, 0, 255));
        byte[] encoded = Encode(image, new JpegXlEncoder { Lossless = true });
        var tight = new DecoderOptions { AdditionalFormats = [JpegXlFormat.Instance], MaxPixels = (40 * 30) - 1 };
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(encoded, tight));
        ImageInfo info = Image.Identify(encoded, tight);
        Assert.Equal(40, info.Width);
        Assert.Equal(30, info.Height);
        Assert.Equal(1, info.FrameCount);
        Assert.Same(JpegXlFormat.Instance, info.Format);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var options = new DecoderOptions { ExecutionOptions = new ImageExecutionOptions { CancellationToken = cancelled.Token } };
        Assert.ThrowsAny<OperationCanceledException>(() => new JpegXlDecoder().Decode<Rgba32>(encoded, options));
    }

    [NativeFact]
    public void MalformedInput_StaysInsideTheExceptionContract()
    {
        using Image<Rgba32> image = Pattern<Rgba32>(33, 17, (x, y) => new Rgba32((byte)(x * y), (byte)x, (byte)y, 200));
        byte[] encoded = Encode(image, new JpegXlEncoder { Lossless = true, Effort = 1 });
        var decoder = new JpegXlDecoder();
        var random = new Random(1234);
        for (int i = 0; i < 200; i++)
        {
            byte[] mutated = (byte[])encoded.Clone();
            int length = i % 3 == 0 ? random.Next(2, mutated.Length) : mutated.Length;
            for (int flips = 0; flips < 1 + (i % 4); flips++)
            {
                mutated[random.Next(2, length)] ^= (byte)(1 << random.Next(8));
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
        Assert.Throws<InvalidImageContentException>(() => decoder.Decode<Rgba32>(new byte[] { 0xFF, 0x0A }, DecoderOptions.Default));
    }

    [NativeFact]
    public async Task SaveAsJxl_WritesFilesAndStreams()
    {
        using Image<Rgba32> image = Pattern<Rgba32>(5, 4, (x, y) => new Rgba32((byte)(x * 50), (byte)(y * 60), 1, 255));
        string path = Path.Combine(Path.GetTempPath(), $"easyimagesharp-jxl-{Guid.NewGuid():N}.jxl");
        try
        {
            image.SaveAsJxl(path, new JpegXlEncoder { Lossless = true });
            using (Image<Rgba32> decoded = Image.Load<Rgba32>(path, Options))
            {
                AssertPixelsEqual(image, decoded);
            }

            await image.SaveAsJxlAsync(path, new JpegXlEncoder { Lossless = true }, CancellationToken.None);
            Assert.True(JpegXlFormat.IsJpegXl(await File.ReadAllBytesAsync(path)));
            using var stream = new MemoryStream();
            image.Save(stream, JpegXlFormat.Instance);
            Assert.True(JpegXlFormat.IsJpegXl(stream.ToArray()));
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

    private static byte[] Encode(Image image, JpegXlEncoder encoder)
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    private static double Square(int value) => (double)value * value;
}

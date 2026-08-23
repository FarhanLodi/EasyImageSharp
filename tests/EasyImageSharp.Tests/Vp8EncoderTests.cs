using System.Buffers.Binary;
using System.Text;
using EasyImageSharp.Formats.Webp;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests;

/// <summary>
/// Tests for the VP8 lossy key-frame encoder. Every encoded frame is wrapped in a minimal RIFF container
/// here (the muxer lives elsewhere) and decoded again with the library's own VP8 decoder, which is verified
/// against libwebp by the WebP fixture tests.
/// </summary>
public class Vp8EncoderTests
{
    private readonly ITestOutputHelper output;

    public Vp8EncoderTests(ITestOutputHelper output) => this.output = output;

    // ----- Boolean entropy coder -----

    [Theory]
    [InlineData(1234, 200_000)]
    [InlineData(99, 300_000)]
    [InlineData(7, 150_000)]
    public void BoolWriterRoundTripsThroughTheDecoder(int seed, int count)
    {
        var random = new Random(seed);
        int[] bits = new int[count];
        int[] probs = new int[count];
        for (int i = 0; i < count; i++)
        {
            probs[i] = random.Next(1, 256);
            bits[i] = random.Next(0, 256) >= probs[i] ? 1 : 0;
        }

        var writer = new Vp8BoolWriter(count / 8);
        for (int i = 0; i < count; i++)
        {
            writer.PutBit(bits[i], probs[i]);
        }

        byte[] encoded = writer.Finish();

        var reader = new Vp8BitReader(encoded, 0, encoded.Length);
        for (int i = 0; i < count; i++)
        {
            int decoded = reader.GetBit(probs[i]);
            if (decoded != bits[i])
            {
                Assert.Fail($"Bit {i} decoded as {decoded}, expected {bits[i]} (prob {probs[i]}).");
            }
        }

        Assert.False(reader.Eof);
    }

    [Fact]
    public void BoolWriterRoundTripsPathologicalRuns()
    {
        // Long runs of highly skewed bits exercise the carry propagation over pending 0xff bytes.
        var writer = new Vp8BoolWriter(64);
        var bits = new List<int>();
        var probs = new List<int>();
        for (int i = 0; i < 20_000; i++)
        {
            int prob = (i % 3) == 0 ? 1 : (i % 3) == 1 ? 255 : 254;
            int bit = (i % 997) == 0 ? 1 : 0;
            bits.Add(bit);
            probs.Add(prob);
            writer.PutBit(bit, prob);
        }

        byte[] encoded = writer.Finish();
        var reader = new Vp8BitReader(encoded, 0, encoded.Length);
        for (int i = 0; i < bits.Count; i++)
        {
            Assert.Equal(bits[i], reader.GetBit(probs[i]));
        }
    }

    [Fact]
    public void BoolWriterRoundTripsLiteralsAndSignedValues()
    {
        var random = new Random(4242);
        var writer = new Vp8BoolWriter(1024);
        var literals = new List<(int Value, int Bits)>();
        var signed = new List<int>();
        for (int i = 0; i < 5000; i++)
        {
            int nbits = random.Next(1, 17);
            int v = random.Next(0, 1 << nbits);
            literals.Add((v, nbits));
            writer.PutValue(v, nbits);

            int s = random.Next(-15, 16);
            signed.Add(s);
            writer.PutSignedValue(s, 4);
        }

        byte[] encoded = writer.Finish();
        var reader = new Vp8BitReader(encoded, 0, encoded.Length);
        for (int i = 0; i < literals.Count; i++)
        {
            Assert.Equal(literals[i].Value, reader.GetValue(literals[i].Bits));
            Assert.Equal(signed[i], reader.GetSignedValue(4));
        }
    }

    // ----- Transforms -----

    [Fact]
    public void ForwardDctRoundTripsThroughTheDecoderInverse()
    {
        const int Bps = 32;
        var random = new Random(31337);
        var source = new byte[Bps * 8];
        var prediction = new byte[Bps * 8];
        var coefficients = new short[16];
        int worst = 0;

        for (int trial = 0; trial < 4000; trial++)
        {
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    source[(y * Bps) + x] = (byte)random.Next(256);
                    prediction[(y * Bps) + x] = (byte)random.Next(256);
                }
            }

            var reconstruction = (byte[])prediction.Clone();
            Vp8EncoderDsp.FTransform(source, 0, prediction, 0, coefficients, 0);
            Vp8Dsp.TransformOne(coefficients, 0, reconstruction, 0);

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    worst = Math.Max(worst, Math.Abs(reconstruction[(y * Bps) + x] - source[(y * Bps) + x]));
                }
            }
        }

        this.output.WriteLine($"Worst unquantized DCT round-trip error: {worst}");
        Assert.True(worst <= 2, $"The forward DCT is not a close inverse of the decoder transform (error {worst}).");
    }

    [Fact]
    public void ForwardWalshHadamardRoundTripsThroughTheDecoderTransform()
    {
        var random = new Random(90210);
        var blocks = new short[16 * 16];
        var coefficients = new short[16];
        var restored = new short[16 * 16];
        int worst = 0;

        for (int trial = 0; trial < 5000; trial++)
        {
            for (int n = 0; n < 16; n++)
            {
                // The forward transform reads the DC of each of the sixteen sub-blocks.
                blocks[n * 16] = (short)random.Next(-2040, 2041);
            }

            Vp8EncoderDsp.FTransformWht(blocks, coefficients);
            Vp8Dsp.TransformWht(coefficients, restored);

            for (int n = 0; n < 16; n++)
            {
                worst = Math.Max(worst, Math.Abs(blocks[n * 16] - restored[n * 16]));
            }
        }

        // The forward transform halves its output, so an odd butterfly sum costs at most one unit,
        // which the inverse spreads over the sixteen sub-block DC values.
        this.output.WriteLine($"Worst Walsh-Hadamard round-trip error: {worst}");
        Assert.True(worst <= 1, $"The Walsh-Hadamard round trip drifted by {worst}.");
    }

    // ----- End to end -----

    public static IEnumerable<object[]> RoundTripCases()
    {
        foreach (int quality in new[] { 20, 50, 75, 95 })
        {
            foreach (int method in new[] { 0, 2, 4 })
            {
                yield return new object[] { quality, method };
            }
        }
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void PhotographRoundTripsWithRisingQuality(int quality, int method)
    {
        Rgba32[] pixels = Vp8TestSupport.LoadFixture("vp8enc/photo.png", out int w, out int h);
        byte[] frame = Vp8TestSupport.Encode(pixels, w, h, quality, method);
        Rgba32[] decoded = Vp8TestSupport.DecodeFrame(frame, w, h);
        double psnr = Vp8TestSupport.Psnr(pixels, decoded);
        this.output.WriteLine($"q={quality} m={method}: {frame.Length} bytes, PSNR {psnr:F2} dB");
        Assert.True(psnr > 24.0, $"PSNR {psnr:F2} dB is too low at quality {quality}, method {method}.");
    }

    [Fact]
    public void QualityMonotonicallyTradesSizeForFidelity()
    {
        Rgba32[] pixels = Vp8TestSupport.LoadFixture("vp8enc/photo.png", out int w, out int h);
        int previousSize = 0;
        double previousPsnr = 0;
        foreach (int quality in new[] { 10, 30, 50, 70, 90, 98 })
        {
            byte[] frame = Vp8TestSupport.Encode(pixels, w, h, quality, 4);
            Rgba32[] decoded = Vp8TestSupport.DecodeFrame(frame, w, h);
            double psnr = Vp8TestSupport.Psnr(pixels, decoded);
            this.output.WriteLine($"q={quality}: {frame.Length} bytes, PSNR {psnr:F2} dB");
            Assert.True(frame.Length > previousSize, $"Quality {quality} did not grow the frame.");
            Assert.True(psnr > previousPsnr, $"Quality {quality} did not improve the PSNR.");
            previousSize = frame.Length;
            previousPsnr = psnr;
        }

        Assert.True(previousPsnr > 40.0, $"The highest quality only reached {previousPsnr:F2} dB.");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(1, 17)]
    [InlineData(17, 9)]
    [InlineData(16, 16)]
    [InlineData(31, 33)]
    [InlineData(255, 255)]
    public void OddDimensionsRoundTrip(int width, int height)
    {
        Rgba32[] pixels = Vp8TestSupport.Gradient(width, height, 5);
        foreach (int method in new[] { 0, 1, 4 })
        {
            byte[] frame = Vp8TestSupport.Encode(pixels, width, height, 80, method);
            Rgba32[] decoded = Vp8TestSupport.DecodeFrame(frame, width, height);
            Assert.Equal(width * height, decoded.Length);
            double luma = Vp8TestSupport.LumaPsnr(pixels, decoded);

            // A frame smaller than one macroblock is mostly replicated padding, so a single quantizer
            // step on one of a handful of real pixels moves the metric by several decibels. libwebp
            // scores 25.5 dB on 3x2 and 26.3 dB on 2x1 with the same input and quality, so the bar for
            // sub-macroblock frames is set where the format itself lands.
            double floor = width >= 16 && height >= 16 ? 32.0 : 24.0;
            Assert.True(luma > floor, $"{width}x{height} method {method}: luma PSNR {luma:F2} dB.");
        }
    }

    [Fact]
    public void FlatColourCompressesToAlmostNothingAndStaysFlat()
    {
        const int W = 128;
        const int H = 96;
        var pixels = new Rgba32[W * H];
        Array.Fill(pixels, new Rgba32(31, 200, 117, 255));

        byte[] frame = Vp8TestSupport.Encode(pixels, W, H, 90, 4);
        Rgba32[] decoded = Vp8TestSupport.DecodeFrame(frame, W, H);
        this.output.WriteLine($"flat colour: {frame.Length} bytes");

        Assert.True(frame.Length < 800, $"A flat frame took {frame.Length} bytes.");
        foreach (Rgba32 p in decoded)
        {
            Assert.True(Math.Abs(p.R - 31) <= 4 && Math.Abs(p.G - 200) <= 4 && Math.Abs(p.B - 117) <= 4,
                $"Flat colour drifted to ({p.R},{p.G},{p.B}).");
        }
    }

    [Fact]
    public void PureNoiseStaysDecodable()
    {
        const int W = 96;
        const int H = 80;
        var random = new Random(2718);
        var pixels = new Rgba32[W * H];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
        }

        foreach (int quality in new[] { 10, 60, 100 })
        {
            byte[] frame = Vp8TestSupport.Encode(pixels, W, H, quality, 4);
            Rgba32[] decoded = Vp8TestSupport.DecodeFrame(frame, W, H);
            double psnr = Vp8TestSupport.Psnr(pixels, decoded);
            this.output.WriteLine($"noise q={quality}: {frame.Length} bytes, PSNR {psnr:F2} dB");
            Assert.Equal(W * H, decoded.Length);
        }
    }

    [Fact]
    public void SharpEdgedSyntheticImageKeepsItsEdges()
    {
        Rgba32[] pixels = Vp8TestSupport.LoadFixture("vp8enc/sharp.png", out int w, out int h);
        byte[] frame = Vp8TestSupport.Encode(pixels, w, h, 95, 4);
        Rgba32[] decoded = Vp8TestSupport.DecodeFrame(frame, w, h);
        double psnr = Vp8TestSupport.Psnr(pixels, decoded);
        double luma = Vp8TestSupport.LumaPsnr(pixels, decoded);
        this.output.WriteLine($"sharp q=95: {frame.Length} bytes, PSNR {psnr:F2} dB, luma {luma:F2} dB");

        // 4:2:0 alone caps the RGB fidelity of an image whose colours change every few pixels, so the
        // encoder is judged on the luma plane, which it reproduces at full resolution.
        Assert.True(luma > 34.0, $"Sharp edges only reached {luma:F2} dB of luma.");
        Assert.True(psnr > 19.0, $"Sharp edges only reached {psnr:F2} dB.");
    }

    [Fact]
    public void EveryMethodProducesADecodableFrame()
    {
        Rgba32[] pixels = Vp8TestSupport.LoadFixture("vp8enc/sharp.png", out int w, out int h);
        for (int method = 0; method <= 6; method++)
        {
            byte[] frame = Vp8TestSupport.Encode(pixels, w, h, 75, method);
            Rgba32[] decoded = Vp8TestSupport.DecodeFrame(frame, w, h);
            double luma = Vp8TestSupport.LumaPsnr(pixels, decoded);
            this.output.WriteLine($"method {method}: {frame.Length} bytes, luma PSNR {luma:F2} dB");
            Assert.True(luma > 30.0, $"Method {method} only reached {luma:F2} dB of luma.");
        }
    }

    [Fact]
    public void HeaderFieldsMatchTheSpecification()
    {
        Rgba32[] pixels = Vp8TestSupport.Gradient(37, 23, 9);
        byte[] y = Vp8TestSupport.ToYuv(pixels, 37, 23, out byte[] u, out byte[] v);
        byte[] frame = new Vp8Encoder().EncodeKeyFrame(y, u, v, 37, 23, 75, 4);

        uint tag = (uint)(frame[0] | (frame[1] << 8) | (frame[2] << 16));
        Assert.Equal(0u, tag & 1);                 // Key frame.
        Assert.Equal(0u, (tag >> 1) & 7);          // Profile 0.
        Assert.Equal(1u, (tag >> 4) & 1);          // Shown.
        int firstPartition = (int)(tag >> 5);
        Assert.InRange(firstPartition, 1, frame.Length - 10);
        Assert.Equal(0x9d, frame[3]);
        Assert.Equal(0x01, frame[4]);
        Assert.Equal(0x2a, frame[5]);
        Assert.Equal(37, ((frame[7] << 8) | frame[6]) & 0x3fff);
        Assert.Equal(23, ((frame[9] << 8) | frame[8]) & 0x3fff);
        Assert.Equal(0, frame[7] >> 6);            // No horizontal scaling.
        Assert.Equal(0, frame[9] >> 6);            // No vertical scaling.

        Assert.True(Vp8Decoder.TryReadHeader(frame, out int w, out int h));
        Assert.Equal(37, w);
        Assert.Equal(23, h);
    }

    [Fact]
    public void RandomFramesSurviveAFuzzLoop()
    {
        var random = new Random(9001);
        for (int i = 0; i < 40; i++)
        {
            int w = random.Next(1, 40);
            int h = random.Next(1, 40);
            var pixels = new Rgba32[w * h];
            int style = random.Next(4);
            for (int p = 0; p < pixels.Length; p++)
            {
                pixels[p] = style switch
                {
                    0 => new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255),
                    1 => new Rgba32(200, 40, 90, 255),
                    2 => new Rgba32((byte)(p % 256), (byte)((p * 3) % 256), (byte)((p * 7) % 256), 255),
                    _ => new Rgba32((byte)((p / Math.Max(w, 1)) % 2 == 0 ? 0 : 255), 128, 64, 255),
                };
            }

            int quality = random.Next(1, 101);
            int method = random.Next(0, 7);
            byte[] frame = Vp8TestSupport.Encode(pixels, w, h, quality, method);
            Rgba32[] decoded = Vp8TestSupport.DecodeFrame(frame, w, h);
            Assert.Equal(w * h, decoded.Length);
        }
    }

    [Fact]
    public void InvalidArgumentsAreRejected()
    {
        var encoder = new Vp8Encoder();
        var plane = new byte[16];
        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.EncodeKeyFrame(plane, plane, plane, 0, 4, 75, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.EncodeKeyFrame(plane, plane, plane, 4, 0, 75, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.EncodeKeyFrame(plane, plane, plane, 16384, 4, 75, 4));
        Assert.Throws<ArgumentException>(() => encoder.EncodeKeyFrame(plane, plane, plane, 64, 64, 75, 4));
    }

    /// <summary>
    /// Writes the encoded frames of the corpus to the directory named by the <c>EIS_VP8ENC_OUT</c>
    /// environment variable so they can be cross-checked with an independent libwebp build. The test is a
    /// no-op when the variable is unset, which is the normal case.
    /// </summary>
    [Fact]
    public void DumpsCorpusForExternalCrossCheck()
    {
        string? dir = Environment.GetEnvironmentVariable("EIS_VP8ENC_OUT");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach ((string name, Rgba32[] pixels, int w, int h) in Vp8TestSupport.Corpus())
        {
            foreach (int quality in new[] { 10, 20, 30, 40, 50, 60, 70, 75, 80, 85, 90, 95, 98, 100 })
            {
                foreach (int method in new[] { 0, 1, 2, 4, 6 })
                {
                    byte[] frame = Vp8TestSupport.Encode(pixels, w, h, quality, method);
                    File.WriteAllBytes(Path.Combine(dir, $"{name}_q{quality}_m{method}.webp"),
                        Vp8TestSupport.BuildRiff(frame));

                    Rgba32[] decoded = Vp8TestSupport.DecodeFrame(frame, w, h);
                    var raw = new byte[decoded.Length * 4];
                    for (int i = 0; i < decoded.Length; i++)
                    {
                        raw[(i * 4) + 0] = decoded[i].R;
                        raw[(i * 4) + 1] = decoded[i].G;
                        raw[(i * 4) + 2] = decoded[i].B;
                        raw[(i * 4) + 3] = decoded[i].A;
                    }

                    File.WriteAllBytes(Path.Combine(dir, $"{name}_q{quality}_m{method}.ours.rgba"), raw);
                }
            }

            var source = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                source[(i * 4) + 0] = pixels[i].R;
                source[(i * 4) + 1] = pixels[i].G;
                source[(i * 4) + 2] = pixels[i].B;
                source[(i * 4) + 3] = pixels[i].A;
            }

            File.WriteAllBytes(Path.Combine(dir, $"{name}.source.rgba"), source);
            File.WriteAllText(Path.Combine(dir, $"{name}.size.txt"), $"{w} {h}", Encoding.ASCII);
        }
    }
}

/// <summary>Helpers shared by the VP8 encoder tests: colour conversion, RIFF muxing and metrics.</summary>
internal static class Vp8TestSupport
{
    /// <summary>Loads a fixture image as RGBA.</summary>
    public static Rgba32[] LoadFixture(string relativePath, out int width, out int height)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Read(relativePath));
        width = image.Width;
        height = image.Height;
        var pixels = new Rgba32[width * height];
        for (int y = 0; y < height; y++)
        {
            image.Frames.RootFrame.GetRowSpan(y).CopyTo(pixels.AsSpan(y * width, width));
        }

        return pixels;
    }

    /// <summary>The images the corpus dump and the comparison script use.</summary>
    public static IEnumerable<(string Name, Rgba32[] Pixels, int Width, int Height)> Corpus()
    {
        Rgba32[] photo = LoadFixture("vp8enc/photo.png", out int pw, out int ph);
        yield return ("photo", photo, pw, ph);

        Rgba32[] sharp = LoadFixture("vp8enc/sharp.png", out int sw, out int sh);
        yield return ("sharp", sharp, sw, sh);

        yield return ("gradient", Gradient(160, 112, 3), 160, 112);

        var flat = new Rgba32[128 * 96];
        Array.Fill(flat, new Rgba32(31, 200, 117, 255));
        yield return ("flat", flat, 128, 96);

        yield return ("noise", Noise(96, 80, 2718), 96, 80);

        // The awkward geometries: a single pixel, a sub-macroblock frame, a partial macroblock in both
        // directions, and a frame whose last macroblock row and column are both partial.
        foreach ((int w, int h) in new[] { (1, 1), (3, 2), (17, 9), (255, 255) })
        {
            yield return ($"odd{w}x{h}", Gradient(w, h, 5), w, h);
        }
    }

    /// <summary>Deterministic pure noise, the worst case for any predictive coder.</summary>
    public static Rgba32[] Noise(int width, int height, int seed)
    {
        var random = new Random(seed);
        var pixels = new Rgba32[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
        }

        return pixels;
    }

    /// <summary>A smooth deterministic test pattern.</summary>
    public static Rgba32[] Gradient(int width, int height, int seed)
    {
        var pixels = new Rgba32[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Kept strictly monotone and inside 0..255 so that even a 3x2 frame stays smooth:
                // a wrap-around would put a full-scale edge into a two-pixel-wide chroma plane.
                int r = 20 + ((200 * x) / Math.Max(width - 1, 1));
                int g = 30 + seed + ((180 * y) / Math.Max(height - 1, 1));
                int b = 50 + ((150 * (x + y)) / Math.Max(width + height - 2, 1));
                pixels[(y * width) + x] = new Rgba32(
                    (byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), 255);
            }
        }

        return pixels;
    }

    /// <summary>
    /// Converts RGBA to the 4:2:0 planes the encoder consumes, using libwebp's fixed-point BT.601 matrix
    /// so that a size comparison against libwebp starts from the same samples.
    /// </summary>
    public static byte[] ToYuv(Rgba32[] pixels, int width, int height, out byte[] u, out byte[] v)
    {
        int uvWidth = (width + 1) / 2;
        int uvHeight = (height + 1) / 2;
        var y = new byte[width * height];
        u = new byte[uvWidth * uvHeight];
        v = new byte[uvWidth * uvHeight];

        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                Rgba32 p = pixels[(j * width) + i];
                y[(j * width) + i] = (byte)Math.Clamp(
                    ((16839 * p.R) + (33059 * p.G) + (6420 * p.B) + 32768 + (16 << 16)) >> 16, 0, 255);
            }
        }

        for (int j = 0; j < uvHeight; j++)
        {
            for (int i = 0; i < uvWidth; i++)
            {
                int r = 0;
                int g = 0;
                int b = 0;
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        Rgba32 p = pixels[(Math.Min((j * 2) + dy, height - 1) * width) + Math.Min((i * 2) + dx, width - 1)];
                        r += p.R;
                        g += p.G;
                        b += p.B;
                    }
                }

                u[(j * uvWidth) + i] = ClipUv((-9719 * r) - (19081 * g) + (28800 * b));
                v[(j * uvWidth) + i] = ClipUv((28800 * r) - (24116 * g) - (4684 * b));
            }
        }

        return y;
    }

    /// <summary>Encodes RGBA pixels and returns the raw VP8 bitstream.</summary>
    public static byte[] Encode(Rgba32[] pixels, int width, int height, int quality, int method)
    {
        byte[] y = ToYuv(pixels, width, height, out byte[] u, out byte[] v);
        return new Vp8Encoder().EncodeKeyFrame(y, u, v, width, height, quality, method);
    }

    /// <summary>Wraps a raw VP8 bitstream in the smallest RIFF/WEBP container that holds it.</summary>
    public static byte[] BuildRiff(byte[] vp8)
    {
        int padded = vp8.Length + (vp8.Length & 1);
        var riff = new byte[12 + 8 + padded];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(riff, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)(4 + 8 + padded));
        Encoding.ASCII.GetBytes("WEBP").CopyTo(riff, 8);
        Encoding.ASCII.GetBytes("VP8 ").CopyTo(riff, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(16), (uint)vp8.Length);
        vp8.CopyTo(riff, 20);
        return riff;
    }

    /// <summary>Decodes a raw VP8 bitstream back to RGBA through the library's WebP decoder.</summary>
    public static Rgba32[] DecodeFrame(byte[] vp8, int width, int height)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(BuildRiff(vp8));
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        var pixels = new Rgba32[width * height];
        for (int y = 0; y < height; y++)
        {
            image.Frames.RootFrame.GetRowSpan(y).CopyTo(pixels.AsSpan(y * width, width));
        }

        return pixels;
    }

    /// <summary>
    /// Peak signal-to-noise ratio of the luma plane, in decibels. Chroma is carried at half resolution by
    /// the format itself, so the luma is the part the encoder is actually responsible for.
    /// </summary>
    public static double LumaPsnr(Rgba32[] a, Rgba32[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = Luma(a[i]) - Luma(b[i]);
            sum += d * d;
        }

        if (sum == 0)
        {
            return 99.0;
        }

        return 10.0 * Math.Log10(255.0 * 255.0 / (sum / a.Length));
    }

    private static double Luma(Rgba32 p) => (0.2126 * p.R) + (0.7152 * p.G) + (0.0722 * p.B);

    /// <summary>Peak signal-to-noise ratio over the RGB channels, in decibels.</summary>
    public static double Psnr(Rgba32[] a, Rgba32[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double dr = a[i].R - b[i].R;
            double dg = a[i].G - b[i].G;
            double db = a[i].B - b[i].B;
            sum += (dr * dr) + (dg * dg) + (db * db);
        }

        if (sum == 0)
        {
            return 99.0;
        }

        double mse = sum / (a.Length * 3.0);
        return 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static byte ClipUv(int uv)
    {
        // The accumulator holds four pixels, so the rounding term is scaled by four as well.
        int value = (uv + (32768 << 2) + (128 << (16 + 2))) >> (16 + 2);
        return (byte)Math.Clamp(value, 0, 255);
    }
}

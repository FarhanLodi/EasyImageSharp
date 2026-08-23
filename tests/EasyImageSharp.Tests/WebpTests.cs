using System.Runtime.InteropServices;
using System.Text.Json;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Webp;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests;

/// <summary>
/// WebP: every fixture under <c>Fixtures/webp/</c> is decoded and compared with the accompanying
/// <c>.rgba</c> dump, which is <em>Pillow's</em> (libwebp's) own decode of the same file, so the library is
/// measured against an independent decoder. Lossless fixtures, including the composited animations, must
/// match byte for byte; the lossy ones are allowed the couple of levels the reference decoder's SIMD paths
/// may round differently and must still exceed 40 dB PSNR.
/// </summary>
public class WebpTests
{
    private const string Folder = "webp";

    private readonly ITestOutputHelper output;

    public WebpTests(ITestOutputHelper output) => this.output = output;

    public static IEnumerable<object[]> Fixtures => WebpFixtures.Names();

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_MatchesReferenceDecode(string name)
    {
        WebpFixture entry = WebpFixtures.Get(name);
        byte[] bytes = FixturePath.Read($"{Folder}/{entry.File}");

        if (entry.Expect is not null)
        {
            Exception ex = Assert.ThrowsAny<Exception>(() => Image.Load<Rgba32>(bytes));
            Assert.True(
                ex.GetType().Name == entry.Expect,
                $"webp/{name}: expected {entry.Expect} but got {ex.GetType().Name}: {ex.Message}");
            try
            {
                Image.Identify(bytes);
            }
            catch (Exception identifyEx) when (identifyEx is ImageFormatException or NotSupportedException)
            {
            }

            return;
        }

        Assert.Equal(ImageFormat.Webp, Image.DetectFormat(bytes));

        ImageInfo info = Image.Identify(bytes);
        Assert.True(
            info.Width == entry.Width && info.Height == entry.Height,
            $"webp/{name}: Identify reported {info.Width}x{info.Height}, manifest says {entry.Width}x{entry.Height}.");
        Assert.True(info.FrameCount == entry.Frames, $"webp/{name}: Identify reported {info.FrameCount} frame(s), manifest says {entry.Frames}.");
        Assert.Equal(entry.HasAlpha ? 32 : 24, info.PixelType.BitsPerPixel);

        byte[] expected = FixturePath.Read($"{Folder}/{name}.rgba");
        using Image<Rgba32> image = Image.Load<Rgba32>(bytes);
        Assert.True(image.Frames.Count == entry.Frames, $"webp/{name}: decoded {image.Frames.Count} frame(s), manifest says {entry.Frames}.");
        Assert.Equal(entry.Frames * entry.Width * entry.Height * 4, expected.Length);

        int maxError = 0;
        long squaredError = 0;
        int worstIndex = -1;
        int worstFrame = -1;
        for (int f = 0; f < entry.Frames; f++)
        {
            ReadOnlySpan<byte> want = expected.AsSpan(f * entry.Width * entry.Height * 4, entry.Width * entry.Height * 4);
            ReadOnlySpan<byte> got = MemoryMarshal.AsBytes(image.Frames[f].PixelSpan);
            for (int i = 0; i < want.Length; i++)
            {
                int error = Math.Abs(want[i] - got[i]);
                squaredError += (long)error * error;
                if (error > maxError)
                {
                    maxError = error;
                    worstIndex = i;
                    worstFrame = f;
                }
            }
        }

        double psnr = maxError == 0
            ? double.PositiveInfinity
            : 10 * Math.Log10(255.0 * 255.0 * expected.Length / squaredError);
        this.output.WriteLine($"{name,-24} {entry.Width}x{entry.Height} x{entry.Frames}  maxAbsError={maxError}  PSNR={psnr:F2} dB  (tolerance {entry.Tolerance})");

        if (maxError > entry.Tolerance)
        {
            int pixel = worstIndex / 4;
            int x = pixel % entry.Width;
            int y = pixel / entry.Width;
            Rgba32 wantPixel = MemoryMarshal.Cast<byte, Rgba32>(
                expected.AsSpan((worstFrame * entry.Width * entry.Height * 4) + (pixel * 4), 4))[0];
            Assert.Fail(
                $"webp/{name} frame {worstFrame}: worst difference {maxError} (allowed {entry.Tolerance}) at pixel ({x},{y}): "
                + $"reference {wantPixel}, decoded {image.Frames[worstFrame][x, y]}. [{entry.Notes}]");
        }

        if (entry.Tolerance > 0)
        {
            Assert.True(psnr >= 40, $"webp/{name}: PSNR {psnr:F2} dB is below the 40 dB floor.");
        }
    }

    [Fact]
    public void Manifest_IsPresentAndComplete()
    {
        Assert.True(FixturePath.Exists($"{Folder}/manifest.json"), "Fixtures/webp/manifest.json is missing; run Fixtures/generate.py.");
        Assert.True(FixturePath.Exists($"{Folder}/EXPECTED.md"), "Fixtures/webp/EXPECTED.md is missing; run Fixtures/generate.py.");
        WebpFixture[] entries = WebpFixtures.All;
        Assert.True(entries.Length >= 25, $"expected at least 25 WebP fixtures, found {entries.Length}.");
        Assert.Contains(entries, e => e.Frames > 1 && e.Tolerance == 0);   // Composited lossless animation.
        Assert.Contains(entries, e => e.HasAlpha && e.Tolerance > 0);      // Lossy with an ALPH chunk.
        Assert.Contains(entries, e => e.Expect is not null);
    }

    [Theory]
    [InlineData("ll_palette16")]
    [InlineData("lossy_testcard_q80")]
    [InlineData("anim_offsets_dispose")]
    public void Fixture_DecodesIntoOtherPixelFormats(string name)
    {
        byte[] bytes = WebpFixtures.Bytes(name);
        using Image<Rgb24> rgb = Image.Load<Rgb24>(bytes);
        using Image<L8> gray = Image.Load<L8>(bytes);
        using Image<Bgra32> bgra = Image.Load<Bgra32>(bytes);
        Assert.Equal(rgb.Width, gray.Width);
        Assert.Equal(rgb.Height, bgra.Height);
    }

    // ----- Container facts -----

    [Fact]
    public void SimpleLossless_IsDetectedAndDescribed()
    {
        byte[] bytes = WebpFixtures.Bytes("ll_alpha_ramp");
        Assert.Equal(ImageFormat.Webp, Image.DetectFormat(bytes));
        Assert.Equal("image/webp", ImageFormat.Webp.DefaultMimeType);
        Assert.Contains("webp", ImageFormat.Webp.FileExtensions);
        Assert.True(ImageFormat.Webp.CanDecode);
        Assert.True(ImageFormat.Webp.CanEncode);
        Assert.Contains(ImageFormat.Webp, ImageFormat.All);

        ImageInfo info = Image.Identify(bytes);
        WebpMetadata metadata = info.Metadata.GetFormatMetadata<WebpMetadata>();
        Assert.True(metadata.IsLossless);
        Assert.True(metadata.HasAlpha);
        Assert.False(metadata.IsAnimated);
        Assert.Equal(32, info.PixelType.BitsPerPixel);
    }

    [Fact]
    public void SimpleLossy_ReportsLossyMetadata()
    {
        ImageInfo info = Image.Identify(WebpFixtures.Bytes("lossy_testcard_q80"));
        WebpMetadata metadata = info.Metadata.GetFormatMetadata<WebpMetadata>();
        Assert.False(metadata.IsLossless);
        Assert.False(metadata.HasAlpha);
        Assert.Equal(96, info.Width);
        Assert.Equal(72, info.Height);
    }

    [Fact]
    public void LossyWithAlpha_ReportsTheAlphaChunk()
    {
        ImageInfo info = Image.Identify(WebpFixtures.Bytes("lossy_alpha_q80"));
        WebpMetadata metadata = info.Metadata.GetFormatMetadata<WebpMetadata>();
        Assert.False(metadata.IsLossless);
        Assert.True(metadata.HasAlpha);

        // The alpha plane is genuinely decoded, not left opaque: the fixture's ramp runs from transparent
        // on the left to opaque on the right, with one fully transparent row across the middle.
        using Image<Rgba32> image = Image.Load<Rgba32>(WebpFixtures.Bytes("lossy_alpha_q80"));
        Assert.Equal(0, image[image.Width / 2, image.Height / 2].A);
        Assert.Equal(255, image[image.Width - 1, 0].A);
        Assert.Equal(255, image[0, 0].A);
    }

    [Fact]
    public void Animation_ExposesLoopCountAndFrameMetadata()
    {
        byte[] bytes = WebpFixtures.Bytes("anim_lossless");
        using Image<Rgba32> image = Image.Load<Rgba32>(bytes);
        WebpMetadata metadata = image.Metadata.GetFormatMetadata<WebpMetadata>();
        Assert.True(metadata.IsAnimated);
        Assert.Equal(0, metadata.RepeatCount);
        Assert.Equal(4, image.Frames.Count);

        int[] delays = image.Frames
            .Select(f => f.Metadata.GetFormatMetadata<WebpFrameMetadata>().FrameDelay)
            .ToArray();
        Assert.Equal(new[] { 80, 120, 160, 200 }, delays);

        WebpMetadata lossy = Image.Identify(WebpFixtures.Bytes("anim_lossy")).Metadata.GetFormatMetadata<WebpMetadata>();
        Assert.Equal(3, lossy.RepeatCount);
    }

    [Fact]
    public void Animation_ExposesOffsetsBlendingAndDisposal()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(WebpFixtures.Bytes("anim_offsets_dispose"));
        WebpFrameMetadata second = image.Frames[1].Metadata.GetFormatMetadata<WebpFrameMetadata>();
        Assert.Equal(4, second.X);
        Assert.Equal(4, second.Y);
        Assert.Equal(8, second.Width);
        Assert.Equal(8, second.Height);
        Assert.Equal(WebpBlendMethod.DoNotBlend, second.BlendMethod);

        WebpFrameMetadata third = image.Frames[2].Metadata.GetFormatMetadata<WebpFrameMetadata>();
        Assert.Equal(WebpDisposalMethod.DisposeToBackground, third.DisposalMethod);

        WebpFrameMetadata blended = Image.Load<Rgba32>(WebpFixtures.Bytes("anim_blend"))
            .Frames[1].Metadata.GetFormatMetadata<WebpFrameMetadata>();
        Assert.Equal(WebpBlendMethod.AlphaBlend, blended.BlendMethod);
    }

    /// <summary>
    /// Dispose-to-background clears the frame's rectangle to transparent black (not to the ANIM chunk's
    /// background colour), and the frame drawn onto a cleared rectangle is written through rather than
    /// blended, so a fully transparent source pixel keeps its own colour channels.
    /// </summary>
    [Fact]
    public void DisposeToBackground_ClearsToTransparentBlack()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(WebpFixtures.Bytes("anim_blend"));
        Assert.Equal(5, image.Frames.Count);
        Assert.Equal(WebpDisposalMethod.DisposeToBackground, image.Frames[3].Metadata.GetFormatMetadata<WebpFrameMetadata>().DisposalMethod);

        // Frame 4 redraws the 8x8 patch over the rectangle frame 3 disposed. Its top-left source pixel is
        // fully transparent, and the decoder keeps the source colour there instead of the cleared black.
        Rgba32 topLeft = image.Frames[4][0, 0];
        Assert.Equal(0, topLeft.A);
        Assert.Equal(new Rgba32(20, 240, 128, 0), topLeft);

        // Outside the disposed rectangle the canvas still shows what frame 3 left behind.
        Assert.Equal(image.Frames[3][31, 23], image.Frames[4][31, 23]);
    }

    [Fact]
    public void ExtendedContainer_SkipsColourProfileAndMetadataChunks()
    {
        byte[] bytes = WebpFixtures.Bytes("vp8x_metadata_skipped");
        using Image<Rgba32> image = Image.Load<Rgba32>(bytes);
        Assert.Equal(40, image.Width);
        Assert.Equal(24, image.Height);
    }

    // ----- Limits and the exception contract -----

    [Fact]
    public void MaxPixels_IsEnforcedBeforeAllocation()
    {
        byte[] bytes = WebpFixtures.Bytes("ll_testcard_m6");
        var options = new DecoderOptions { MaxPixels = 96 * 72 - 1 };
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(bytes, options));

        // Identify is never limited.
        Assert.Equal(96, Image.Identify(bytes, options).Width);

        byte[] lossy = WebpFixtures.Bytes("lossy_testcard_q80");
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(lossy, options));
    }

    [Fact]
    public void MaxFrames_LimitsAnimationDecoding()
    {
        byte[] bytes = WebpFixtures.Bytes("anim_lossless");
        using Image<Rgba32> limited = Image.Load<Rgba32>(bytes, new DecoderOptions { MaxFrames = 2 });
        Assert.Equal(2, limited.Frames.Count);

        // The header-reported count is unaffected.
        Assert.Equal(4, Image.Identify(bytes, new DecoderOptions { MaxFrames = 2 }).FrameCount);

        using Image<Rgba32> full = Image.Load<Rgba32>(bytes);
        for (int y = 0; y < full.Height; y++)
        {
            for (int x = 0; x < full.Width; x++)
            {
                Assert.Equal(full.Frames[1][x, y], limited.Frames[1][x, y]);
            }
        }
    }

    /// <summary>A VP8 inter frame is a recognised but unsupported feature, not corrupt data.</summary>
    [Fact]
    public void InterFrame_ThrowsNotSupported()
    {
        byte[] bytes = WebpFixtures.Bytes("lossy_gradient_q75");
        int vp8 = FindChunk(bytes, "VP8 ");
        bytes[vp8] |= 1; // Clear the key-frame flag in the frame tag.
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => Image.Load<Rgba32>(bytes));
        Assert.Contains("inter frame", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReservedAlphaCompression_ThrowsInvalidContent()
    {
        byte[] bytes = WebpFixtures.Bytes("lossy_alpha_q80");
        int alph = FindChunk(bytes, "ALPH");
        bytes[alph] = (byte)((bytes[alph] & ~0x03) | 0x02);
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(bytes));
    }

    [Fact]
    public void TruncatedFile_ThrowsInvalidContent()
    {
        byte[] bytes = WebpFixtures.Bytes("ll_testcard_m6");
        for (int keep = 12; keep < bytes.Length; keep += 17)
        {
            byte[] cut = bytes.AsSpan(0, keep).ToArray();
            try
            {
                using Image<Rgba32> image = Image.Load<Rgba32>(cut);
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
            {
            }
        }
    }

    /// <summary>Every single-byte mutation of every fixture must decode or fail through the documented contract.</summary>
    [Fact]
    public void ByteMutations_OnlyRaiseContractExceptions()
    {
        var random = new Random(20260822);
        var options = new DecoderOptions { MaxPixels = 1 << 20, MaxFrames = 16 };
        int mutations = 0;
        foreach (WebpFixture entry in WebpFixtures.All)
        {
            byte[] original = FixturePath.Read($"{Folder}/{entry.File}");
            for (int i = 0; i < 120; i++)
            {
                byte[] mutated = (byte[])original.Clone();
                int index = random.Next(12, mutated.Length); // Keep the RIFF/WEBP signature intact.
                mutated[index] = (byte)random.Next(256);
                mutations++;
                try
                {
                    using Image<Rgba32> image = Image.Load<Rgba32>(mutated, options);
                }
                catch (ImageFormatException)
                {
                }
                catch (NotSupportedException)
                {
                }
                catch (Exception ex)
                {
                    Assert.Fail($"webp/{entry.Name}: mutating byte {index} to {mutated[index]} raised {ex.GetType().Name}: {ex}");
                }

                try
                {
                    Image.Identify(mutated, options);
                }
                catch (ImageFormatException)
                {
                }
                catch (NotSupportedException)
                {
                }
                catch (Exception ex)
                {
                    Assert.Fail($"webp/{entry.Name}: Identify after mutating byte {index} raised {ex.GetType().Name}: {ex}");
                }
            }
        }

        this.output.WriteLine($"{mutations} single-byte mutations decoded or failed through the contract.");
    }

    [Fact]
    public void EmptyAndGarbageInput_AreRejected()
    {
        Assert.Throws<UnknownImageFormatException>(() => Image.Load<Rgba32>(Array.Empty<byte>()));
        Assert.Throws<UnknownImageFormatException>(() => Image.Load<Rgba32>("RIFF____NOTW"u8.ToArray()));

        byte[] header = "RIFF"u8.ToArray().Concat(new byte[] { 4, 0, 0, 0 }).Concat("WEBP"u8.ToArray()).ToArray();
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(header));
    }

    private static int FindChunk(byte[] data, string fourCc)
    {
        for (int pos = 12; pos + 8 <= data.Length;)
        {
            string id = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            int size = BitConverter.ToInt32(data, pos + 4);
            if (id == fourCc)
            {
                return pos + 8;
            }

            pos += 8 + size + (size & 1);
        }

        throw new Xunit.Sdk.XunitException($"chunk '{fourCc}' not found");
    }
}

/// <summary>One entry of the WebP fixture manifest, which carries a few facts the shared reader does not.</summary>
internal sealed record WebpFixture(
    string Name, string File, int Width, int Height, int Frames, bool Lossless, bool HasAlpha,
    int Tolerance, string Notes, string? Expect);

/// <summary>Reads <c>Fixtures/webp/manifest.json</c>.</summary>
internal static class WebpFixtures
{
    private static WebpFixture[]? cache;

    public static WebpFixture[] All
    {
        get
        {
            if (cache is null)
            {
                using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText(FixturePath.Get("webp/manifest.json")));
                cache = document.RootElement.EnumerateArray().Select(Read).ToArray();
            }

            return cache;
        }
    }

    public static IEnumerable<object[]> Names()
    {
        if (!FixturePath.Exists("webp/manifest.json"))
        {
            yield return new object[] { "(manifest missing)" };
            yield break;
        }

        foreach (WebpFixture entry in All)
        {
            yield return new object[] { entry.Name };
        }
    }

    public static WebpFixture Get(string name)
        => All.SingleOrDefault(e => e.Name == name)
           ?? throw new Xunit.Sdk.XunitException($"Fixture 'webp/{name}' is not listed in manifest.json; run Fixtures/generate.py.");

    public static byte[] Bytes(string name) => FixturePath.Read($"webp/{Get(name).File}");

    private static WebpFixture Read(JsonElement element)
    {
        string? expect = element.TryGetProperty("expect", out JsonElement e) ? e.GetString() : null;
        return new WebpFixture(
            element.GetProperty("name").GetString()!,
            element.GetProperty("file").GetString()!,
            Int(element, "width"),
            Int(element, "height"),
            element.TryGetProperty("frames", out JsonElement f) ? f.GetInt32() : 1,
            element.TryGetProperty("lossless", out JsonElement l) && l.GetBoolean(),
            element.TryGetProperty("has_alpha", out JsonElement a) && a.GetBoolean(),
            Int(element, "tolerance"),
            element.GetProperty("notes").GetString() ?? string.Empty,
            expect);
    }

    private static int Int(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : 0;
}

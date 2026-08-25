using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Decodes every fixture listed in <c>Fixtures/&lt;format&gt;/manifest.json</c> (files written by Pillow or
/// assembled byte-by-byte by <c>generate.py</c>, never by this library's encoders) and compares the result
/// pixel-for-pixel with the accompanying <c>.rgba</c> ground-truth dump. Entries flagged with
/// <c>"expect"</c> exercise features the library deliberately does not implement and must fail with
/// exactly that exception type.
/// </summary>
public class FixtureDecodeTests
{
    public static IEnumerable<object[]> PngFixtures => FixtureNames("png");

    public static IEnumerable<object[]> BmpFixtures => FixtureNames("bmp");

    public static IEnumerable<object[]> TiffFixtures => FixtureNames("tiff");

    [Theory]
    [MemberData(nameof(PngFixtures))]
    public void Png_DecodesToReference(string name) => VerifyFixture("png", name);

    [Theory]
    [MemberData(nameof(BmpFixtures))]
    public void Bmp_DecodesToReference(string name) => VerifyFixture("bmp", name);

    [Theory]
    [MemberData(nameof(TiffFixtures))]
    public void Tiff_DecodesToReference(string name) => VerifyFixture("tiff", name);

    [Fact]
    public void Manifests_ArePresentAndNonEmpty()
    {
        foreach (string format in new[] { "png", "bmp", "tiff" })
        {
            Assert.True(FixturePath.Exists($"{format}/manifest.json"), $"Fixtures/{format}/manifest.json is missing; run Fixtures/generate.py.");
            Assert.NotEmpty(Manifest.Load(format));
        }
    }

    /// <summary>Every fixture must also decode into the non-RGBA pixel formats without throwing.</summary>
    [Theory]
    [InlineData("png", "rgba8")]
    [InlineData("png", "gray16_trns_key")]
    [InlineData("bmp", "rle8_absolute")]
    [InlineData("tiff", "hand_rgb16_mm_lzw_pred2")]
    public void Fixture_DecodesIntoOtherPixelFormats(string format, string name)
    {
        byte[] bytes = FixturePath.Read($"{format}/{Manifest.Load(format).Single(e => e.Name == name).File}");
        using Image<Rgb24> rgb = Image.Load<Rgb24>(bytes);
        using Image<L8> gray = Image.Load<L8>(bytes);
        using Image<Bgra32> bgra = Image.Load<Bgra32>(bytes);
        Assert.Equal(rgb.Width, gray.Width);
        Assert.Equal(rgb.Height, bgra.Height);
    }

    private static IEnumerable<object[]> FixtureNames(string format)
    {
        if (!FixturePath.Exists($"{format}/manifest.json"))
        {
            // Surface a clear failure through Manifests_ArePresentAndNonEmpty rather than an empty theory.
            yield return new object[] { "(manifest missing)" };
            yield break;
        }

        foreach (FixtureEntry entry in Manifest.Load(format))
        {
            yield return new object[] { entry.Name };
        }
    }

    private static void VerifyFixture(string format, string name)
    {
        FixtureEntry entry = Manifest.Load(format).SingleOrDefault(e => e.Name == name)
            ?? throw new Xunit.Sdk.XunitException($"Fixture '{format}/{name}' is not listed in manifest.json; run Fixtures/generate.py.");
        byte[] bytes = FixturePath.Read($"{format}/{entry.File}");

        if (entry.Expect is not null)
        {
            Exception ex = Assert.ThrowsAny<Exception>(() => Image.Load<Rgba32>(bytes));
            Assert.True(
                ex.GetType().Name == entry.Expect,
                $"{format}/{name}: expected {entry.Expect} but got {ex.GetType().Name}: {ex.Message}");

            // Header inspection must still behave (either succeed or fail through the documented contract).
            try
            {
                Image.Identify(bytes);
            }
            catch (Exception identifyEx) when (identifyEx is ImageFormatException or NotSupportedException)
            {
            }

            return;
        }

        ImageInfo info = Image.Identify(bytes);
        Assert.True(info.Width == entry.Width && info.Height == entry.Height,
            $"{format}/{name}: Identify reported {info.Width}x{info.Height}, manifest says {entry.Width}x{entry.Height}.");
        Assert.True(info.FrameCount == entry.Frames, $"{format}/{name}: Identify reported {info.FrameCount} frame(s), manifest says {entry.Frames}.");

        byte[] expected = FixturePath.Read($"{format}/{entry.Name}.rgba");
        using Image<Rgba32> image = Image.Load<Rgba32>(bytes);
        Assert.True(image.Frames.Count == entry.Frames, $"{format}/{name}: decoded {image.Frames.Count} frame(s), manifest says {entry.Frames}.");

        int offset = 0;
        for (int f = 0; f < entry.Frames; f++)
        {
            (int frameWidth, int frameHeight) = entry.FrameSizes is { Length: > 0 } sizes
                ? (sizes[f][0], sizes[f][1])
                : (entry.Width, entry.Height);
            ImageFrame<Rgba32> frame = image.Frames[f];
            Assert.True(frame.Width == frameWidth && frame.Height == frameHeight,
                $"{format}/{name} frame {f}: decoded {frame.Width}x{frame.Height}, expected {frameWidth}x{frameHeight}.");

            int byteCount = frameWidth * frameHeight * 4;
            Assert.True(offset + byteCount <= expected.Length, $"{format}/{name}: .rgba dump is shorter than the manifest implies.");
            ReadOnlySpan<byte> want = expected.AsSpan(offset, byteCount);
            ReadOnlySpan<byte> got = MemoryMarshal.AsBytes(frame.PixelSpan);
            if (!want.SequenceEqual(got))
            {
                int i = want.CommonPrefixLength(got) / 4;
                int x = i % frameWidth;
                int y = i / frameWidth;
                Rgba32 wantPixel = MemoryMarshal.Cast<byte, Rgba32>(want)[i];
                Rgba32 gotPixel = frame[x, y];
                Assert.Fail(
                    $"{format}/{name} frame {f}: first mismatch at pixel #{i} ({x},{y}): expected {wantPixel}, decoded {gotPixel}. [{entry.Notes}]");
            }

            offset += byteCount;
        }

        Assert.True(offset == expected.Length, $"{format}/{name}: .rgba dump has {expected.Length - offset} unexpected trailing bytes.");
    }

    /// <summary>One entry of a fixture manifest.</summary>
    internal sealed class FixtureEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("frames")]
        public int Frames { get; set; } = 1;

        [JsonPropertyName("notes")]
        public string Notes { get; set; } = string.Empty;

        [JsonPropertyName("expect")]
        public string? Expect { get; set; }

        [JsonPropertyName("frame_sizes")]
        public int[][]? FrameSizes { get; set; }
    }

    /// <summary>Loads and caches the per-format manifests.</summary>
    internal static class Manifest
    {
        private static readonly Dictionary<string, FixtureEntry[]> Cache = new();

        public static FixtureEntry[] Load(string format)
        {
            lock (Cache)
            {
                if (!Cache.TryGetValue(format, out FixtureEntry[]? entries))
                {
                    string json = File.ReadAllText(FixturePath.Get($"{format}/manifest.json"));
                    entries = JsonSerializer.Deserialize<FixtureEntry[]>(json) ?? Array.Empty<FixtureEntry>();
                    Cache[format] = entries;
                }

                return entries;
            }
        }

        /// <summary>All fixture files (format/file) that decode successfully, i.e. valid seeds for fuzzing.</summary>
        public static IEnumerable<string> ValidFiles(string format)
            => Load(format).Where(e => e.Expect is null).Select(e => $"{format}/{e.File}");
    }
}

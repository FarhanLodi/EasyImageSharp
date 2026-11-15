using System.Text.Json;
using System.Text.Json.Serialization;
using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Avif.Tests;

/// <summary>
/// Files written and decoded by Pillow's own libavif build (<c>Fixtures/gen_avif.py</c>), decoded here through the
/// separately built libavif this package ships. AV1 reconstruction is bit-exact by specification, but chroma
/// upsampling and the YUV to RGB rounding are not specified: a libavif built with libyuv filters chroma differently
/// from one without. So 4:4:4 files must match within <see cref="Tolerance"/> levels per channel, while subsampled
/// files are held to <see cref="MinSubsampledPsnr"/>, the floor the JPEG conformance suites use. The measured worst
/// difference and PSNR are reported either way.
/// </summary>
public class AvifFixtureTests
{
    /// <summary>Largest allowed per-channel difference from the independent decode.</summary>
    private const int Tolerance = 2;

    /// <summary>PSNR floor, in dB, for chroma-subsampled files.</summary>
    private const double MinSubsampledPsnr = 40.0;

    private static readonly Lazy<FixtureEntry[]> Manifest = new(() =>
        JsonSerializer.Deserialize<FixtureEntry[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "manifest.json")))!);

    private readonly ITestOutputHelper output;

    public AvifFixtureTests(ITestOutputHelper output) => this.output = output;

    public static IEnumerable<object[]> Names => Manifest.Value.Select(e => new object[] { e.Name });

    [NativeTheory]
    [MemberData(nameof(Names))]
    public void Fixture_MatchesTheIndependentDecode(string name)
    {
        FixtureEntry entry = Manifest.Value.Single(e => e.Name == name);
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", entry.File));
        byte[] reference = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", entry.Reference));
        Assert.True(AvifFormat.IsAvif(data));

        var options = new DecoderOptions { AdditionalFormats = [AvifFormat.Instance] };
        ImageInfo info = Image.Identify(data, options);
        Assert.Equal(entry.Width, info.Width);
        Assert.Equal(entry.Height, info.Height);
        Assert.Equal(entry.Frames, info.FrameCount);

        using Image<Rgba32> image = Image.Load<Rgba32>(data, options);
        Assert.Equal(entry.Frames, image.Frames.Count);
        Assert.Equal(entry.HasAlpha, image.Metadata.GetFormatMetadata<AvifMetadata>().HasAlpha);
        int frameBytes = entry.Width * entry.Height * 4;
        Assert.Equal(frameBytes * entry.Frames, reference.Length);

        bool subsampled = entry.Subsampling != "4:4:4";
        int worst = 0;
        double squaredError = 0;
        for (int f = 0; f < entry.Frames; f++)
        {
            ImageFrame<Rgba32> frame = image.Frames[f];
            for (int y = 0; y < entry.Height; y++)
            {
                Span<Rgba32> row = frame.GetRowSpan(y);
                for (int x = 0; x < entry.Width; x++)
                {
                    int o = (f * frameBytes) + (((y * entry.Width) + x) * 4);
                    Rgba32 p = row[x];
                    int d = Math.Max(
                        Math.Max(Math.Abs(p.R - reference[o]), Math.Abs(p.G - reference[o + 1])),
                        Math.Max(Math.Abs(p.B - reference[o + 2]), Math.Abs(p.A - reference[o + 3])));
                    Assert.True(subsampled || d <= Tolerance, $"{entry.File} frame {f} pixel ({x},{y}): decoded {p}, reference ({reference[o]}, {reference[o + 1]}, {reference[o + 2]}, {reference[o + 3]}).");
                    worst = Math.Max(worst, d);
                    squaredError += Square(p.R - reference[o]) + Square(p.G - reference[o + 1]) + Square(p.B - reference[o + 2]) + Square(p.A - reference[o + 3]);
                }
            }

            if (entry.DurationsMs is { } durations)
            {
                Assert.Equal(durations[f], Math.Round(frame.Metadata.GetFormatMetadata<AvifFrameMetadata>().Duration.TotalMilliseconds));
            }
        }

        double psnr = squaredError == 0 ? double.PositiveInfinity
            : 10 * Math.Log10(255.0 * 255.0 / (squaredError / ((double)frameBytes * entry.Frames)));
        this.output.WriteLine($"{entry.File} ({entry.Subsampling}): worst channel difference {worst}, PSNR {psnr:F2} dB against {entry.Oracle}.");
        Assert.True(!subsampled || psnr >= MinSubsampledPsnr, $"{entry.File}: PSNR {psnr:F2} dB is below {MinSubsampledPsnr} dB.");
    }

    private static double Square(int value) => (double)value * value;

    private sealed class FixtureEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("reference")]
        public string Reference { get; set; } = string.Empty;

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("frames")]
        public int Frames { get; set; }

        [JsonPropertyName("durations_ms")]
        public double[]? DurationsMs { get; set; }

        [JsonPropertyName("has_alpha")]
        public bool HasAlpha { get; set; }

        [JsonPropertyName("subsampling")]
        public string Subsampling { get; set; } = "4:4:4";

        [JsonPropertyName("oracle")]
        public string Oracle { get; set; } = string.Empty;
    }
}

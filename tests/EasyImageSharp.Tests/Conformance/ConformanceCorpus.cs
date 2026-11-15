using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Bmp;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Pbm;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.Formats.Webp;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests.Conformance;

/// <summary>One entry of a <c>Fixtures/conformance/&lt;suite&gt;/manifest.json</c>, written by <c>build_conformance.py</c>.</summary>
internal sealed class ConformanceEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("frames")]
    public int? Frames { get; set; }

    /// <summary>The ground-truth dump (<c>.rgba</c>, or gzip-wrapped <c>.rgba.gz</c>), or null when no independent decoder reads the file.</summary>
    [JsonPropertyName("reference")]
    public string? Reference { get; set; }

    /// <summary>Largest allowed absolute per-channel difference; null leaves it unchecked (JPEG, which is held to <see cref="MinPsnr"/>).</summary>
    [JsonPropertyName("tolerance")]
    public int? Tolerance { get; set; }

    /// <summary>PSNR floor in dB for lossy formats; null when <see cref="Tolerance"/> alone decides.</summary>
    [JsonPropertyName("min_psnr")]
    public double? MinPsnr { get; set; }

    /// <summary>The exception type <c>Image.Load</c> must throw: the file is invalid by design.</summary>
    [JsonPropertyName("expect")]
    public string? Expect { get; set; }

    /// <summary>The exception type the format's own decoder must throw when called directly.</summary>
    [JsonPropertyName("expect_direct")]
    public string? ExpectDirect { get; set; }

    /// <summary>No reference exists, so only the decoder contract is asserted.</summary>
    [JsonPropertyName("contract_only")]
    public bool ContractOnly { get; set; }

    [JsonPropertyName("oracle")]
    public string Oracle { get; set; } = string.Empty;

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;

    public override string ToString() => this.Name;
}

/// <summary>
/// Loads the third-party conformance suites under <c>Fixtures/conformance/</c> and checks a decode against them. The
/// references were produced by independent decoders (Pillow, tifffile, or a PNG reader written from the
/// specification); see <c>Fixtures/conformance/README.md</c>.
/// </summary>
internal static class ConformanceCorpus
{
    /// <summary>A hang detector, not a speed limit: the same allowance <c>FuzzSmokeTests</c> and <c>CorruptInputTests</c> use.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, ConformanceEntry[]> Cache = new();

    /// <summary>True when <paramref name="suite"/> has been built into the fixture tree (always, except for the git-ignored libwebp set).</summary>
    public static bool IsPresent(string suite) => FixturePath.Exists($"conformance/{suite}/manifest.json");

    public static ConformanceEntry[] Load(string suite)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(suite, out ConformanceEntry[]? entries))
            {
                string json = File.ReadAllText(FixturePath.Get($"conformance/{suite}/manifest.json"));
                entries = JsonSerializer.Deserialize<ConformanceEntry[]>(json) ?? Array.Empty<ConformanceEntry>();
                Cache[suite] = entries;
            }

            return entries;
        }
    }

    public static ConformanceEntry Get(string suite, string name)
        => Load(suite).SingleOrDefault(e => e.Name == name)
            ?? throw new Xunit.Sdk.XunitException($"conformance/{suite}/{name} is not in manifest.json; run build_conformance.py.");

    /// <summary>Theory rows for every entry of <paramref name="suite"/> that is not listed in <paramref name="knownFailures"/>.</summary>
    public static IEnumerable<object[]> Rows(string suite, IReadOnlyDictionary<string, string> knownFailures)
        => Load(suite).Where(e => !knownFailures.ContainsKey(e.Name)).Select(e => new object[] { e.Name });

    /// <summary>Theory rows for the known failures in one category, for the skipped theories that document them.</summary>
    public static IEnumerable<object[]> KnownFailureRows(IReadOnlyDictionary<string, string> knownFailures, string category)
        => knownFailures.Where(kv => kv.Value == category).Select(kv => new object[] { kv.Key });

    /// <summary>Runs every assertion a manifest entry implies.</summary>
    public static void Verify(string suite, string name, ITestOutputHelper output)
    {
        ConformanceEntry entry = Get(suite, name);
        byte[] bytes = FixturePath.Read($"conformance/{suite}/{entry.File}");
        string label = $"conformance/{suite}/{entry.File}";

        if (entry.Expect is not null)
        {
            Exception ex = Assert.ThrowsAny<Exception>(() => WithinDeadline(label, () => Image.Load<Rgba32>(bytes).Dispose()));
            Assert.True(ex.GetType().Name == entry.Expect, $"{label}: Image.Load should throw {entry.Expect} but threw {ex.GetType().Name}: {ex.Message}");
            AssertContract(label + " Identify", () => Image.Identify(bytes));
            if (entry.ExpectDirect is not null)
            {
                IImageDecoder decoder = DecoderFor(entry.Format);
                Exception direct = Assert.ThrowsAny<Exception>(() => WithinDeadline(label, () => decoder.Decode<Rgba32>(bytes).Dispose()));
                Assert.True(
                    direct.GetType().Name == entry.ExpectDirect,
                    $"{label}: {decoder.GetType().Name}.Decode should throw {entry.ExpectDirect} but threw {direct.GetType().Name}: {direct.Message}");
            }

            output.WriteLine($"{label}: rejected with {ex.GetType().Name} as required ({entry.Notes})");
            return;
        }

        if (entry.ContractOnly)
        {
            string identify = AssertContract(label + " Identify", () => Image.Identify(bytes));
            string load = AssertContract(label + " Load", () => Image.Load<Rgba32>(bytes).Dispose());
            output.WriteLine($"{label}: no independent reference ({entry.Oracle}); Identify {identify}, Load {load}. [{entry.Notes}]");
            return;
        }

        int width = entry.Width ?? throw new Xunit.Sdk.XunitException($"{label}: manifest has a reference but no width.");
        int height = entry.Height ?? throw new Xunit.Sdk.XunitException($"{label}: manifest has a reference but no height.");
        int frames = entry.Frames ?? 1;

        ImageInfo info = WithinDeadline(label, () => Image.Identify(bytes));
        Assert.True(info.Width == width && info.Height == height, $"{label}: Identify reported {info.Width}x{info.Height}, the reference is {width}x{height}.");

        byte[] expected = ReadReference(suite, entry.Reference!);
        int frameBytes = width * height * 4;
        Assert.True(expected.Length == frameBytes * frames, $"{label}: the reference holds {expected.Length} bytes, expected {frameBytes * frames}.");

        using Image<Rgba32> image = WithinDeadline(label, () => Image.Load<Rgba32>(bytes));
        Assert.True(image.Frames.Count == frames, $"{label}: decoded {image.Frames.Count} frame(s), the reference has {frames}.");

        byte[] got = new byte[frameBytes];
        for (int f = 0; f < frames; f++)
        {
            ImageFrame<Rgba32> frame = image.Frames[f];
            Assert.True(frame.Width == width && frame.Height == height, $"{label} frame {f}: decoded {frame.Width}x{frame.Height}, expected {width}x{height}.");
            frame.CopyPixelDataTo(got);
            Compare(label, f, entry, expected.AsSpan(f * frameBytes, frameBytes), got, width, output);
        }
    }

    private static void Compare(string label, int frame, ConformanceEntry entry, ReadOnlySpan<byte> want, ReadOnlySpan<byte> got, int width, ITestOutputHelper output)
    {
        int maxError = 0;
        int worst = -1;
        long squaredError = 0;
        for (int i = 0; i < want.Length; i++)
        {
            int error = Math.Abs(want[i] - got[i]);
            squaredError += (long)error * error;
            if (error > maxError)
            {
                maxError = error;
                worst = i;
            }
        }

        double psnr = squaredError == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 * want.Length / squaredError);
        output.WriteLine($"{label} frame {frame}: maxAbsError={maxError} PSNR={psnr:F2} dB (tolerance {entry.Tolerance?.ToString() ?? "-"}, min PSNR {entry.MinPsnr?.ToString("F1") ?? "-"}); oracle: {entry.Oracle}");

        string where = worst < 0
            ? "no differing pixel"
            : $"pixel ({worst / 4 % width},{worst / 4 / width}) channel {"RGBA"[worst % 4]}: reference {want[worst]}, decoded {got[worst]}";

        if (entry.Tolerance is int tolerance)
        {
            Assert.True(maxError <= tolerance, $"{label} frame {frame}: worst difference {maxError} exceeds {tolerance} at {where}. [{entry.Notes}]");
        }

        if (entry.MinPsnr is double floor)
        {
            Assert.True(psnr >= floor, $"{label} frame {frame}: PSNR {psnr:F2} dB is below {floor:F1} dB (worst at {where}). [{entry.Notes}]");
        }
    }

    /// <summary>
    /// Runs a call that may succeed or fail through the documented contract (<see cref="ImageFormatException"/> or
    /// <see cref="NotSupportedException"/>) and describes the outcome; anything else fails the test.
    /// </summary>
    private static string AssertContract(string label, Action action)
    {
        try
        {
            WithinDeadline(label, () =>
            {
                action();
                return 0;
            });
            return "succeeded";
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            return $"threw {ex.GetType().Name}";
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"{label}: {ex.GetType().FullName} escaped the decoder contract: {ex.Message}");
        }
    }

    /// <summary>Runs <paramref name="call"/> with a deadline, rethrowing whatever it throws unwrapped.</summary>
    private static T WithinDeadline<T>(string label, Func<T> call)
    {
        Task<T> task = Task.Run(call);
        bool completed;
        try
        {
            completed = task.Wait(Deadline);
        }
        catch (AggregateException)
        {
            completed = true; // Faulted: GetResult below rethrows the original exception rather than the aggregate.
        }

        if (!completed)
        {
            throw new Xunit.Sdk.XunitException($"{label}: still running after {Deadline.TotalSeconds:F0} s.");
        }

        return task.GetAwaiter().GetResult();
    }

    private static void WithinDeadline(string label, Action call)
        => WithinDeadline(label, () =>
        {
            call();
            return 0;
        });

    private static byte[] ReadReference(string suite, string reference)
    {
        byte[] blob = FixturePath.Read($"conformance/{suite}/{reference}");
        if (!reference.EndsWith(".gz", StringComparison.Ordinal))
        {
            return blob;
        }

        using var compressed = new MemoryStream(blob);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        gzip.CopyTo(raw);
        return raw.ToArray();
    }

    private static IImageDecoder DecoderFor(string format) => format switch
    {
        "png" => new PngDecoder(),
        "tiff" => new TiffDecoder(),
        "jpeg" => new JpegDecoder(),
        "bmp" => new BmpDecoder(),
        "pnm" => new PbmDecoder(),
        "webp" => new WebpDecoder(),
        _ => throw new Xunit.Sdk.XunitException($"No decoder mapping for manifest format '{format}'."),
    };
}

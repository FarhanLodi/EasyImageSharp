using System.Diagnostics;
using System.Globalization;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests;

/// <summary>
/// Fixed-seed mutation fuzzing over every fixture file and the library's own encoder output. The only
/// acceptable outcomes of <c>Image.Load</c> / <c>Image.Identify</c> on a mutated file are success,
/// <see cref="ImageFormatException"/> (any subclass) or <see cref="NotSupportedException"/>; every call
/// must finish within <see cref="PerCallTimeout"/> and allocate less than <see cref="MaxAllocationPerCall"/>.
/// Failing inputs are written to the temp directory so they can be replayed and turned into
/// <see cref="CorruptInputTests"/> regressions.
/// </summary>
public class FuzzSmokeTests
{
    /// <summary>
    /// JPEG seeds are excluded until the JPEG decoder hardening branch merges; flip to true afterwards.
    /// (A static readonly rather than a const so the compiler does not flag the guarded block as unreachable.)
    /// </summary>
    private static readonly bool IncludeJpeg = true;

    /// <summary>
    /// RNG seed for the mutation stream. Overridden by <c>EASYIMAGESHARP_FUZZ_SEED</c>; the nightly workflow
    /// passes the GitHub run id so each run explores new inputs instead of re-testing the same ones.
    /// </summary>
    private static readonly int Seed = EnvSeed("EASYIMAGESHARP_FUZZ_SEED", 12345);

    /// <summary>
    /// Mutations per seed file. With ~290 seeds this is ~230,000 decoder calls, which take a few seconds per
    /// target framework; raise it via <c>EASYIMAGESHARP_FUZZ_MUTATIONS</c> (or vary <see cref="Seed"/>) for a
    /// deeper run. The nightly workflow sets both, which is why neither is a compile-time constant.
    /// </summary>
    private static readonly int MutationsPerSeed = EnvInt("EASYIMAGESHARP_FUZZ_MUTATIONS", 400);

    private const long MaxAllocationPerCall = 100L * 1024 * 1024;

    // A hang detector, not a speed limit (see CorruptInputTests): generous enough that a loaded CI machine
    // cannot turn slowness into a false failure, tight enough that a runaway loop still fails the run.
    private static readonly TimeSpan PerCallTimeout = TimeSpan.FromSeconds(30);

    private static readonly DecoderOptions FuzzOptions = new() { MaxPixels = 4_000_000 };

    private static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : fallback;

    // GitHub's run_id is ~1.5e10 and overflows Int32, so parse as long and narrow deliberately.
    // int.TryParse would fail silently and every nightly run would re-test the same 12345 corpus.
    private static int EnvSeed(string name, int fallback)
        => long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? unchecked((int)value)
            : fallback;

    /// <summary>
    /// Whether <paramref name="name"/> supplied the value in use, so a nightly failure records whether it came
    /// from the workflow or from the compiled-in default. A value that is set but unusable is reported as
    /// ignored rather than as honoured: silently reading "from EASYIMAGESHARP_FUZZ_SEED=..." next to the
    /// default seed is exactly the confusion this line exists to prevent.
    /// </summary>
    /// <param name="name">The environment variable to describe.</param>
    /// <param name="applied">Whether the variable's value was actually parsed and used.</param>
    private static string Describe(string name, bool applied)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } raw
            ? applied ? $"from {name}={raw}" : $"default; ignored unusable {name}={raw}"
            : "default";

    /// <summary>Whether the variable holds a value <see cref="EnvSeed"/> would accept.</summary>
    private static bool SeedApplied(string name)
        => long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    /// <summary>Whether the variable holds a value <see cref="EnvInt"/> would accept.</summary>
    private static bool IntApplied(string name)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0;

    private readonly ITestOutputHelper output;

    public FuzzSmokeTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task MutatedInputs_NeverEscapeTheDecoderContract()
    {
        this.output.WriteLine(
            $"Fuzz configuration: seed={Seed} ({Describe("EASYIMAGESHARP_FUZZ_SEED", SeedApplied("EASYIMAGESHARP_FUZZ_SEED"))}), " +
            $"mutations={MutationsPerSeed} ({Describe("EASYIMAGESHARP_FUZZ_MUTATIONS", IntApplied("EASYIMAGESHARP_FUZZ_MUTATIONS"))}).");

        List<(string Name, byte[] Bytes)> seeds = CollectSeeds();
        Assert.NotEmpty(seeds);

        var random = new Random(Seed);
        var stats = new Dictionary<string, Outcomes>();
        var failures = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        TimeSpan slowest = TimeSpan.Zero;
        string slowestName = string.Empty;
        long largestAllocation = 0;

        foreach ((string name, byte[] bytes) in seeds)
        {
            string format = name[..name.IndexOf('/')];
            Outcomes outcomes = stats.TryGetValue(format, out Outcomes? existing) ? existing : stats[format] = new Outcomes();

            for (int i = 0; i < MutationsPerSeed; i++)
            {
                byte[] mutated = Mutate(bytes, random, out string mutation);
                foreach ((string call, Action<byte[]> action) in Calls)
                {
                    CallResult result = await RunGuarded(action, mutated);
                    outcomes.Record(result);
                    if (result.Elapsed > slowest)
                    {
                        slowest = result.Elapsed;
                        slowestName = $"{name} #{i} {call}";
                    }

                    largestAllocation = Math.Max(largestAllocation, result.AllocatedBytes);
                    if (result.Failure is not null)
                    {
                        string saved = SaveFailingInput(name, i, mutated);
                        failures.Add($"{name} mutation #{i} ({mutation}) via {call}: {result.Failure} [input saved to {saved}]");
                    }
                }
            }
        }

        stopwatch.Stop();
        this.output.WriteLine($"Fuzzed {seeds.Count} seed files x {MutationsPerSeed} mutations x {Calls.Length} calls in {stopwatch.Elapsed.TotalSeconds:F1} s (seed {Seed}).");
        foreach ((string format, Outcomes o) in stats.OrderBy(kv => kv.Key))
        {
            this.output.WriteLine($"  {format,-5} success={o.Success,6} formatException={o.FormatException,6} notSupported={o.NotSupported,6} unknownFormat={o.UnknownFormat,5} failures={o.Failures,3}");
        }

        this.output.WriteLine($"  slowest call: {slowest.TotalMilliseconds:F1} ms ({slowestName}); largest allocation: {largestAllocation / 1024.0 / 1024.0:F1} MB");
        foreach (string failure in failures.Take(25))
        {
            this.output.WriteLine("  FAIL " + failure);
        }

        Assert.True(failures.Count == 0, $"{failures.Count} fuzz failure(s):{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(25))}");
    }

    private static readonly (string Name, Action<byte[]> Action)[] Calls =
    {
        ("Load", bytes => Image.Load<Rgba32>(bytes, FuzzOptions).Dispose()),
        ("Identify", bytes => Image.Identify(bytes, FuzzOptions)),
    };

    // ----- Seeds -----

    private static List<(string Name, byte[] Bytes)> CollectSeeds()
    {
        var seeds = new List<(string, byte[])>();
        foreach (string format in new[] { "png", "apng", "bmp", "tiff", "smallformats/tga", "smallformats/pbm", "smallformats/qoi", "smallformats/ico" })
        {
            foreach (FixtureDecodeTests.FixtureEntry entry in FixtureDecodeTests.Manifest.Load(format))
            {
                seeds.Add(($"{format}/{entry.File}", FixturePath.Read($"{format}/{entry.File}")));
            }
        }

        // The library's own encoders exercise code paths (e.g. filter heuristics, deflate levels) the fixtures may not.
        using Image<Rgb24> gradient = TestImages.Gradient(37, 29);
        using Image<Rgba32> alpha = TestImages.AlphaGradient(23, 31);
        using var gray = new Image<L8>(40, 12);
        for (int y = 0; y < gray.Height; y++)
        {
            for (int x = 0; x < gray.Width; x++)
            {
                gray[x, y] = new L8((byte)((x * 6) + y));
            }
        }

        using Image<Rgb24> multi = gradient.Clone();
        multi.Frames.AddFrame(gradient.Frames.RootFrame);

        seeds.Add(("png/encoder-rgb", Encode(gradient, (img, ms) => img.SaveAsPng(ms))));
        seeds.Add(("png/encoder-rgba", Encode(alpha, (img, ms) => img.SaveAsPng(ms))));
        seeds.Add(("png/encoder-gray", Encode(gray, (img, ms) => img.SaveAsPng(ms))));
        seeds.Add(("bmp/encoder-rgb", Encode(gradient, (img, ms) => img.SaveAsBmp(ms))));
        seeds.Add(("tiff/encoder-deflate", Encode(gradient, (img, ms) => img.Save(ms, new TiffEncoder { Compression = TiffCompression.Deflate }))));
        seeds.Add(("tiff/encoder-lzw", Encode(alpha, (img, ms) => img.Save(ms, new TiffEncoder { Compression = TiffCompression.Lzw }))));
        seeds.Add(("tiff/encoder-none-gray", Encode(gray, (img, ms) => img.Save(ms, new TiffEncoder { Compression = TiffCompression.None }))));
        seeds.Add(("tiff/encoder-multipage", Encode(multi, (img, ms) => img.SaveAsTiff(ms))));

        if (IncludeJpeg)
        {
            seeds.Add(("jpeg/encoder-rgb", Encode(gradient, (img, ms) => img.SaveAsJpeg(ms, 85))));
            seeds.Add(("jpeg/encoder-gray", Encode(gray, (img, ms) => img.SaveAsJpeg(ms, 60))));
            if (FixturePath.Exists("jpeg/manifest.json"))
            {
                foreach (FixtureDecodeTests.FixtureEntry entry in FixtureDecodeTests.Manifest.Load("jpeg"))
                {
                    seeds.Add(($"jpeg/{entry.File}", FixturePath.Read($"jpeg/{entry.File}")));
                }
            }
        }

        return seeds;
    }

    private static byte[] Encode<TPixel>(Image<TPixel> image, Action<Image<TPixel>, MemoryStream> save)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var ms = new MemoryStream();
        save(image, ms);
        return ms.ToArray();
    }

    // ----- Mutation -----

    private static readonly uint[] InterestingValues =
    {
        0, 1, 2, 0x7F, 0x80, 0xFF, 0x100, 0x7FFF, 0x8000, 0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0xFFFFFFFE, 4_000_000, 0x00400000,
    };

    private static byte[] Mutate(byte[] source, Random random, out string description)
    {
        byte[] data = (byte[])source.Clone();
        int strategy = random.Next(8);
        switch (strategy)
        {
            case 0:
            {
                int pos = random.Next(data.Length);
                data[pos] ^= (byte)random.Next(1, 256);
                description = $"flip byte @{pos}";
                return data;
            }

            case 1:
            {
                int pos = random.Next(data.Length);
                int length = Math.Min(random.Next(1, 33), data.Length - pos);
                random.NextBytes(data.AsSpan(pos, length));
                description = $"randomize {length} bytes @{pos}";
                return data;
            }

            case 2:
            {
                int length = random.Next(0, data.Length);
                description = $"truncate to {length} bytes";
                return data[..length];
            }

            case 3:
            {
                int pos = random.Next(data.Length + 1);
                byte[] insert = new byte[random.Next(1, 17)];
                random.NextBytes(insert);
                description = $"insert {insert.Length} bytes @{pos}";
                return data[..pos].Concat(insert).Concat(data[pos..]).ToArray();
            }

            case 4:
            {
                int length = Math.Min(random.Next(4, 65), data.Length);
                int from = random.Next(data.Length - length + 1);
                int to = random.Next(data.Length + 1);
                description = $"duplicate {length} bytes from @{from} to @{to}";
                return data[..to].Concat(data[from..(from + length)]).Concat(data[to..]).ToArray();
            }

            case 5:
            {
                // Header-focused bit flip.
                int pos = random.Next(Math.Min(data.Length, 64));
                data[pos] ^= (byte)(1 << random.Next(8));
                description = $"flip bit in header @{pos}";
                return data;
            }

            case 6:
            {
                // Interesting 32-bit value somewhere in the first 160 bytes (dimensions, offsets, counts).
                int limit = Math.Max(1, Math.Min(data.Length, 160) - 3);
                int pos = random.Next(limit);
                uint value = InterestingValues[random.Next(InterestingValues.Length)];
                bool bigEndian = random.Next(2) == 0;
                for (int i = 0; i < 4 && pos + i < data.Length; i++)
                {
                    data[pos + i] = (byte)(bigEndian ? value >> (8 * (3 - i)) : value >> (8 * i));
                }

                description = $"write 0x{value:X8} ({(bigEndian ? "BE" : "LE")}) @{pos}";
                return data;
            }

            default:
            {
                // Interesting 16-bit value in the first 200 bytes.
                int limit = Math.Max(1, Math.Min(data.Length, 200) - 1);
                int pos = random.Next(limit);
                ushort value = (ushort)InterestingValues[random.Next(InterestingValues.Length)];
                data[pos] = (byte)(value >> 8);
                if (pos + 1 < data.Length)
                {
                    data[pos + 1] = (byte)value;
                }

                description = $"write 0x{value:X4} @{pos}";
                return data;
            }
        }
    }

    // ----- Guarded execution -----

    private static async Task<CallResult> RunGuarded(Action<byte[]> action, byte[] input)
    {
        Task<CallResult> task = Task.Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            Exception? ex = null;
            try
            {
                action(input);
            }
            catch (Exception caught)
            {
                ex = caught;
            }

            sw.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return new CallResult(ex, sw.Elapsed, allocated);
        });

        try
        {
            CallResult result = await task.WaitAsync(PerCallTimeout);
            return result;
        }
        catch (TimeoutException)
        {
            return new CallResult(null, PerCallTimeout, 0, TimedOut: true);
        }
    }

    private static string SaveFailingInput(string seedName, int mutation, byte[] data)
    {
        try
        {
            // The nightly workflow uploads ${{ runner.temp }}/EasyImageSharp-fuzz, which is not always the
            // same directory as Path.GetTempPath() on every runner OS.
            string root = Environment.GetEnvironmentVariable("RUNNER_TEMP") is { Length: > 0 } runnerTemp ? runnerTemp : Path.GetTempPath();
            string dir = Path.Combine(root, "EasyImageSharp-fuzz");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{seedName.Replace('/', '_')}-{mutation}.bin");
            File.WriteAllBytes(path, data);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "(could not save)";
        }
    }

    private sealed record CallResult(Exception? Exception, TimeSpan Elapsed, long AllocatedBytes, bool TimedOut = false)
    {
        public string? Failure
        {
            get
            {
                if (this.TimedOut)
                {
                    return $"did not finish within {PerCallTimeout.TotalSeconds:F0} s (potential infinite loop)";
                }

                if (this.AllocatedBytes > MaxAllocationPerCall)
                {
                    return $"allocated {this.AllocatedBytes / 1024.0 / 1024.0:F1} MB";
                }

                return this.Exception switch
                {
                    null or ImageFormatException or NotSupportedException => null,
                    _ => $"{this.Exception.GetType().Name}: {this.Exception.Message}{Environment.NewLine}{this.Exception.StackTrace}",
                };
            }
        }
    }

    private sealed class Outcomes
    {
        public int Success;
        public int FormatException;
        public int UnknownFormat;
        public int NotSupported;
        public int Failures;

        public void Record(CallResult result)
        {
            if (result.Failure is not null)
            {
                this.Failures++;
            }
            else if (result.Exception is null)
            {
                this.Success++;
            }
            else if (result.Exception is UnknownImageFormatException)
            {
                this.UnknownFormat++;
            }
            else if (result.Exception is ImageFormatException)
            {
                this.FormatException++;
            }
            else
            {
                this.NotSupported++;
            }
        }
    }
}

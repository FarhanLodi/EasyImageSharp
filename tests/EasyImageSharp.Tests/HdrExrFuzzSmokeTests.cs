using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Exr;
using EasyImageSharp.Formats.Hdr;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests;

/// <summary>
/// Fixed-seed mutation fuzzing of the Radiance HDR and OpenEXR decoders, in the style of
/// <see cref="FuzzSmokeTests"/>: the seeds are the small fixtures of <c>Fixtures/hdr/</c> and <c>Fixtures/exr/</c>
/// plus this library's own encoder output, and the only acceptable outcomes of <c>Image.Load</c> and
/// <c>Image.Identify</c> are success, <see cref="ImageFormatException"/> or <see cref="NotSupportedException"/>,
/// within the per-call time and allocation limits. <c>EASYIMAGESHARP_FUZZ_MUTATIONS</c> and
/// <c>EASYIMAGESHARP_FUZZ_SEED</c> deepen or vary the run exactly as they do for the main harness.
/// </summary>
public class HdrExrFuzzSmokeTests
{
    private const long MaxAllocationPerCall = 100L * 1024 * 1024;

    /// <summary>Fixtures above this many pixels decode too slowly to be mutated hundreds of times.</summary>
    private const int MaxSeedPixels = 70_000;

    private static readonly TimeSpan PerCallTimeout = TimeSpan.FromSeconds(30);

    private static readonly DecoderOptions FuzzOptions = new() { MaxPixels = 4_000_000 };

    private static readonly int Seed = long.TryParse(Environment.GetEnvironmentVariable("EASYIMAGESHARP_FUZZ_SEED"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long seed)
        ? unchecked((int)seed)
        : 4242;

    private static readonly int MutationsPerSeed = int.TryParse(Environment.GetEnvironmentVariable("EASYIMAGESHARP_FUZZ_MUTATIONS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count > 0
        ? count
        : 150;

    private static readonly uint[] InterestingValues =
    {
        0, 1, 2, 0x7F, 0x80, 0xFF, 0x100, 0x7FFF, 0x8000, 0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0xFFFFFFFE, 4_000_000, 0x00400000,
    };

    private static readonly (string Name, Action<byte[]> Action)[] Calls =
    {
        ("Load", bytes => Image.Load<Rgba32>(bytes, FuzzOptions).Dispose()),
        ("LoadVector", bytes => Image.Load<RgbaVector>(bytes, FuzzOptions).Dispose()),
        ("Identify", bytes => Image.Identify(bytes, FuzzOptions)),
    };

    private readonly ITestOutputHelper output;

    public HdrExrFuzzSmokeTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task MutatedInputs_NeverEscapeTheDecoderContract()
    {
        List<(string Name, byte[] Bytes)> seeds = CollectSeeds();
        Assert.True(seeds.Count >= 40, $"only {seeds.Count} seeds");

        var random = new Random(Seed);
        var failures = new List<string>();
        int calls = 0, successes = 0, rejected = 0;
        var stopwatch = Stopwatch.StartNew();
        TimeSpan slowest = TimeSpan.Zero;
        long largest = 0;
        foreach ((string name, byte[] bytes) in seeds)
        {
            for (int i = 0; i < MutationsPerSeed; i++)
            {
                byte[] mutated = Mutate(bytes, random, out string mutation);
                foreach ((string call, Action<byte[]> action) in Calls)
                {
                    (Exception? ex, TimeSpan elapsed, long allocated, bool timedOut) = await RunGuarded(action, mutated);
                    calls++;
                    slowest = elapsed > slowest ? elapsed : slowest;
                    largest = Math.Max(largest, allocated);
                    string? failure = timedOut ? "timed out"
                        : allocated > MaxAllocationPerCall ? $"allocated {allocated / 1048576.0:F1} MB"
                        : ex is null or ImageFormatException or NotSupportedException ? null
                        : $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}";
                    if (failure is not null)
                    {
                        failures.Add($"{name} #{i} ({mutation}) via {call}: {failure} [{Save(name, i, mutated)}]");
                    }
                    else if (ex is null)
                    {
                        successes++;
                    }
                    else
                    {
                        rejected++;
                    }
                }
            }
        }

        this.output.WriteLine($"Fuzzed {seeds.Count} seeds x {MutationsPerSeed} mutations x {Calls.Length} calls = {calls} calls in "
            + $"{stopwatch.Elapsed.TotalSeconds:F1} s (seed {Seed}): {successes} succeeded, {rejected} rejected, {failures.Count} failed; "
            + $"slowest {slowest.TotalMilliseconds:F0} ms, largest allocation {largest / 1048576.0:F1} MB.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
    }

    private static List<(string Name, byte[] Bytes)> CollectSeeds()
    {
        var seeds = new List<(string, byte[])>();
        foreach (string folder in new[] { "hdr", "exr" })
        {
            foreach (JsonElement entry in HdrExrFixtures.Manifest(folder))
            {
                bool small = !entry.TryGetProperty("width", out JsonElement w) || w.GetInt32() * entry.GetProperty("height").GetInt32() <= MaxSeedPixels;
                if (small)
                {
                    seeds.Add(($"{folder}/{entry.GetProperty("file").GetString()}", HdrExrFixtures.Bytes(folder, entry)));
                }
            }
        }

        using Image<RgbaVector> image = HdrTests.HdrImage(41, 19, seed: 8);
        seeds.Add(("hdr/encoder-rle", HdrTests.Encode(image, new HdrEncoder())));
        seeds.Add(("hdr/encoder-flat", HdrTests.Encode(image, new HdrEncoder { Compression = HdrCompression.None })));
        using Image<RgbaVector> special = ExrTests.SpecialValueImage(29, 37, seed: 8);
        foreach (ExrCompression compression in new[] { ExrCompression.None, ExrCompression.Rle, ExrCompression.Zips, ExrCompression.Zip, ExrCompression.Piz })
        {
            seeds.Add(($"exr/encoder-{compression}", HdrTests.Encode(special, new ExrEncoder { Compression = compression })));
        }

        using var smooth = new Image<RgbaVector>(40, 36);
        for (int y = 0; y < smooth.Height; y++)
        {
            for (int x = 0; x < smooth.Width; x++)
            {
                smooth[x, y] = new RgbaVector(x / 40f, y / 36f, 0.5f, 1f);
            }
        }

        seeds.Add(("exr/encoder-piz-smooth", HdrTests.Encode(smooth, new ExrEncoder { Compression = ExrCompression.Piz })));
        seeds.Add(("exr/encoder-zip-float", HdrTests.Encode(smooth, new ExrEncoder { Compression = ExrCompression.Zip, PixelType = ExrPixelType.Float })));
        return seeds;
    }

    private static byte[] Mutate(byte[] source, Random random, out string description)
    {
        byte[] data = (byte[])source.Clone();
        switch (random.Next(9))
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
                int pos = random.Next(Math.Min(data.Length, 64));
                data[pos] ^= (byte)(1 << random.Next(8));
                description = $"flip bit in header @{pos}";
                return data;
            }

            default:
            {
                // Interesting 32-bit little- or big-endian values where the dimensions, attribute sizes, data
                // windows and chunk offsets live; OpenEXR headers are longer than the main harness assumes.
                int limit = Math.Max(1, Math.Min(data.Length, 1024) - 3);
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
        }
    }

    private static async Task<(Exception? Exception, TimeSpan Elapsed, long Allocated, bool TimedOut)> RunGuarded(Action<byte[]> action, byte[] input)
    {
        Task<(Exception?, TimeSpan, long, bool)> task = Task.Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            Exception? ex = HdrExrFixtures.Catch(() => action(input));
            return (ex, watch.Elapsed, GC.GetAllocatedBytesForCurrentThread() - before, false);
        });

        try
        {
            return await task.WaitAsync(PerCallTimeout);
        }
        catch (TimeoutException)
        {
            return (null, PerCallTimeout, 0, true);
        }
    }

    private static string Save(string name, int mutation, byte[] data)
    {
        try
        {
            string root = Environment.GetEnvironmentVariable("RUNNER_TEMP") is { Length: > 0 } runnerTemp ? runnerTemp : Path.GetTempPath();
            string dir = Path.Combine(root, "EasyImageSharp-fuzz");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{name.Replace('/', '_')}-{mutation}.bin");
            File.WriteAllBytes(path, data);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "(could not save)";
        }
    }
}

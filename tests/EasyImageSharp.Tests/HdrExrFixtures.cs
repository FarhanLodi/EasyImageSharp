using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Shared access to the high dynamic range fixture folders, <c>Fixtures/hdr/</c> (written by
/// <c>gen_hdr.py</c> straight from the format description) and <c>Fixtures/exr/</c> (written or copied by
/// <c>exr/build_exr_fixtures.py</c> from the OpenEXR reference library). Ground truth is little-endian float32
/// RGBA (<c>.f32</c>) or, for the larger official images, the SHA-256 of that array with NaNs canonicalised.
/// </summary>
internal static class HdrExrFixtures
{
    private static readonly Dictionary<string, JsonElement[]> Cache = new();

    public static JsonElement[] Manifest(string folder)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(folder, out JsonElement[]? entries))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(FixturePath.Get($"{folder}/manifest.json")));
                entries = document.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
                Cache[folder] = entries;
            }

            return entries;
        }
    }

    public static JsonElement Entry(string folder, string name)
        => Manifest(folder).Single(e => e.GetProperty("name").GetString() == name);

    public static IEnumerable<object[]> Names(string folder, Func<JsonElement, bool> predicate)
        => Manifest(folder).Where(predicate).Select(e => new object[] { e.GetProperty("name").GetString()! });

    public static bool IsExpectedFailure(JsonElement entry) => entry.TryGetProperty("expect", out _);

    public static string? Text(JsonElement entry, string property)
        => entry.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static byte[] Bytes(string folder, JsonElement entry) => FixturePath.Read($"{folder}/{entry.GetProperty("file").GetString()}");

    public static float[] Truth(string folder, JsonElement entry)
        => MemoryMarshal.Cast<byte, float>(FixturePath.Read($"{folder}/{entry.GetProperty("f32").GetString()}")).ToArray();

    /// <summary>SHA-256 over the RGBA float32 array with every NaN replaced by 0x7FC00000, as the build script computes it.</summary>
    public static string CanonicalSha256(ReadOnlySpan<RgbaVector> pixels)
    {
        float[] values = MemoryMarshal.Cast<RgbaVector, float>(pixels).ToArray();
        uint[] bits = new uint[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            bits[i] = float.IsNaN(values[i]) ? 0x7FC00000u : BitConverter.SingleToUInt32Bits(values[i]);
        }

        return Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(bits.AsSpan()))).ToLowerInvariant();
    }

    /// <summary>
    /// Asserts that every component equals the ground truth bit for bit (any NaN matches any NaN), or within
    /// <paramref name="relativeTolerance"/> of the larger magnitude when one is given.
    /// </summary>
    public static void AssertMatches(string label, float[] expected, ReadOnlySpan<RgbaVector> actual, int width, double relativeTolerance = 0)
    {
        ReadOnlySpan<float> got = MemoryMarshal.Cast<RgbaVector, float>(actual);
        Assert.True(expected.Length == got.Length, $"{label}: decoded {got.Length / 4} pixels, ground truth has {expected.Length / 4}.");
        for (int i = 0; i < expected.Length; i++)
        {
            float want = expected[i];
            float have = got[i];
            bool same = (float.IsNaN(want) && float.IsNaN(have))
                || BitConverter.SingleToInt32Bits(want) == BitConverter.SingleToInt32Bits(have)
                || (relativeTolerance > 0 && Math.Abs(want - have) <= relativeTolerance * Math.Max(Math.Abs(want), Math.Abs(have)));
            if (!same)
            {
                int pixel = i / 4;
                Assert.Fail($"{label}: component {"RGBA"[i % 4]} of pixel ({pixel % width}, {pixel / width}) is {have:R}, expected {want:R}.");
            }
        }
    }

    public static Exception? Catch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}

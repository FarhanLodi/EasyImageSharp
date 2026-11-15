using System.Diagnostics;
using EasyImageSharp.Fonts;
using EasyImageSharp.Fonts.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests.Fonts;

/// <summary>
/// Fixed-seed mutation fuzzing of every test font container. Loading a mutated font may only succeed or throw
/// <see cref="InvalidFontException"/> or <see cref="NotSupportedException"/>; when it loads, laying out, measuring
/// and rendering text with it must not throw at all (malformed glyphs render empty) and must finish promptly.
/// </summary>
public class FontFuzzTests
{
    private const int MutationsPerFont = 150;
    private const string Sample = "Hamburgefonstiv AV To 0123 \u00E9\u20AC";

    private readonly ITestOutputHelper output;

    public FontFuzzTests(ITestOutputHelper output) => this.output = output;

    public static TheoryData<string> Seeds => new()
    {
        FontAssets.Regular, FontAssets.Cff, FontAssets.Cid, FontAssets.Collection, FontAssets.Woff, FontAssets.Woff2,
    };

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Mutated_fonts_fail_only_with_documented_exceptions(string file)
    {
        byte[] original = FontAssets.Read(file);

        // A stable per-file seed (string.GetHashCode is randomised per process).
        var random = new Random(file.Aggregate(20261104, (h, c) => unchecked((h * 31) + c)));
        int loaded = 0, rejected = 0;
        for (int i = 0; i < MutationsPerFont; i++)
        {
            byte[] mutated = Mutate(original, random, out string mutation);
            var timer = Stopwatch.StartNew();
            IReadOnlyList<FontFace> faces;
            try
            {
                faces = FontFace.LoadAll(mutated);
            }
            catch (Exception ex) when (ex is InvalidFontException or NotSupportedException)
            {
                rejected++;
                continue;
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"{file} mutation {i} ({mutation}) threw {ex.GetType().Name}: {ex.Message}");
            }

            loaded++;
            try
            {
                foreach (FontFace face in faces)
                {
                    Exercise(face);
                }
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"{file} mutation {i} ({mutation}) loaded but failed to render: {ex.GetType().Name}: {ex.Message}");
            }

            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), $"{file} mutation {i} ({mutation}) took {timer.Elapsed}");
        }

        this.output.WriteLine($"{file}: {loaded} mutations loaded and rendered, {rejected} rejected");
    }

    [Fact]
    public void Every_truncation_of_a_font_header_region_is_handled()
    {
        foreach (string file in new[] { FontAssets.Regular, FontAssets.Cff, FontAssets.Collection, FontAssets.Woff, FontAssets.Woff2 })
        {
            byte[] original = FontAssets.Read(file);
            for (int length = 0; length < Math.Min(original.Length, 600); length += 7)
            {
                try
                {
                    foreach (FontFace face in FontFace.LoadAll(original.AsSpan(0, length)))
                    {
                        Exercise(face);
                    }
                }
                catch (Exception ex) when (ex is InvalidFontException or NotSupportedException)
                {
                }
            }
        }
    }

    private static void Exercise(FontFace face)
    {
        _ = face.FamilyName;
        _ = face.HasKerning;
        float size = 14;
        var font = new Font(face, size);
        TextMeasurement m = TextMeasurer.Measure(Sample, font, new TextLayoutOptions { WrappingWidth = 60 });
        _ = m.InkBounds;
        int count = Math.Min(face.GlyphCount, 48);
        for (int g = 0; g < count; g++)
        {
            GlyphRasterizer.Render(face.GetOutline(g), size / face.UnitsPerEm, 0.25f, g % 7 == 0 ? 1.5f : 0f);
        }
    }

    private static byte[] Mutate(byte[] original, Random random, out string mutation)
    {
        byte[] data = (byte[])original.Clone();
        switch (random.Next(7))
        {
            case 0:
            {
                int flips = 1 + random.Next(8);
                for (int k = 0; k < flips; k++)
                {
                    data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                }

                mutation = $"{flips} bit flips";
                return data;
            }

            case 1:
            {
                int start = random.Next(data.Length);
                int length = Math.Min(data.Length - start, 1 + random.Next(64));
                random.NextBytes(data.AsSpan(start, length));
                mutation = $"random run at {start}+{length}";
                return data;
            }

            case 2:
            {
                int length = random.Next(data.Length);
                mutation = $"truncated to {length}";
                return data[..length];
            }

            case 3:
            {
                // Header-focused: table directory, record offsets and lengths, and the first table headers.
                int position = random.Next(Math.Min(data.Length, 400));
                data[position] = (byte)random.Next(256);
                mutation = $"header byte {position}";
                return data;
            }

            case 4:
            {
                uint[] interesting = [0, 1, 0x7F, 0x80, 0xFF, 0x7FFF, 0x8000, 0xFFFF, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF];
                int position = random.Next(Math.Max(1, data.Length - 4));
                uint value = interesting[random.Next(interesting.Length)];
                bool wide = random.Next(2) == 0;
                if (wide)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(position, 4), value);
                }
                else
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(position, 2), (ushort)value);
                }

                mutation = $"interesting {(wide ? 32 : 16)}-bit 0x{value:X} at {position}";
                return data;
            }

            case 5:
            {
                // Duplicate a chunk over another place (copies plausible structures into wrong positions).
                int from = random.Next(data.Length);
                int to = random.Next(data.Length);
                int length = Math.Min(Math.Min(data.Length - from, data.Length - to), 1 + random.Next(256));
                data.AsSpan(from, length).ToArray().CopyTo(data.AsSpan(to));
                mutation = $"copied {from}+{length} to {to}";
                return data;
            }

            default:
            {
                int position = random.Next(data.Length);
                int length = 1 + random.Next(32);
                byte[] inserted = new byte[length];
                random.NextBytes(inserted);
                mutation = $"inserted {length} bytes at {position}";
                return [.. data.AsSpan(0, position), .. inserted, .. data.AsSpan(position)];
            }
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests;

/// <summary>
/// Covers the TIFF features beyond the plain strip/LZW/Deflate baseline: CCITT Group 3 and Group 4
/// bilevel coding, JPEG-in-TIFF, planar and tiled layouts, the 32-bit sample formats and the YCbCr,
/// CIELab and Separated photometric interpretations, plus the matching encoder options.
/// </summary>
/// <remarks>
/// The fixtures under <c>Fixtures/tiffadv/</c> are written by Pillow/libtiff or assembled byte by byte by
/// <c>Fixtures/gen_tiffadv.py</c>; the ground truth in the accompanying <c>.rgba</c> dumps never comes from
/// this library.
/// </remarks>
public class TiffAdvancedTests
{
    private readonly ITestOutputHelper output;

    public TiffAdvancedTests(ITestOutputHelper output) => this.output = output;

    public static IEnumerable<object[]> Fixtures => AdvancedManifest.Load().Select(e => new object[] { e.Name });

    [Fact]
    public void Manifest_IsPresentAndNonEmpty()
    {
        Assert.True(FixturePath.Exists("tiffadv/manifest.json"), "Fixtures/tiffadv/manifest.json is missing; run Fixtures/generate.py.");
        Assert.NotEmpty(AdvancedManifest.Load());
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_DecodesToReference(string name)
    {
        AdvancedEntry entry = AdvancedManifest.Get(name);
        byte[] bytes = FixturePath.Read($"tiffadv/{entry.File}");

        ImageInfo info = Image.Identify(bytes);
        Assert.True(
            info.Width == entry.Width && info.Height == entry.Height && info.FrameCount == entry.Frames,
            $"{name}: Identify reported {info.Width}x{info.Height} x{info.FrameCount}, manifest says {entry.Width}x{entry.Height} x{entry.Frames}.");

        byte[] expected = FixturePath.Read($"tiffadv/{entry.Name}.rgba");
        using Image<Rgba32> image = Image.Load<Rgba32>(bytes);
        Assert.Equal(entry.Frames, image.Frames.Count);

        int offset = 0;
        for (int f = 0; f < entry.Frames; f++)
        {
            ImageFrame<Rgba32> frame = image.Frames[f];
            int count = frame.Width * frame.Height * 4;
            Assert.True(offset + count <= expected.Length, $"{name}: the .rgba dump is shorter than the manifest implies.");
            Compare(name, entry, frame, expected.AsSpan(offset, count));
            offset += count;
        }

        Assert.Equal(expected.Length, offset);
    }

    private void Compare(string name, AdvancedEntry entry, ImageFrame<Rgba32> frame, ReadOnlySpan<byte> expected)
    {
        double squaredError = 0;
        int worst = 0;
        int worstIndex = -1;
        Span<byte> channels = stackalloc byte[4];
        for (int y = 0; y < frame.Height; y++)
        {
            ReadOnlySpan<Rgba32> row = frame.GetRowSpan(y);
            for (int x = 0; x < frame.Width; x++)
            {
                int i = ((y * frame.Width) + x) * 4;
                Rgba32 got = row[x];
                channels[0] = got.R;
                channels[1] = got.G;
                channels[2] = got.B;
                channels[3] = got.A;
                for (int c = 0; c < 4; c++)
                {
                    int difference = Math.Abs(channels[c] - expected[i + c]);
                    squaredError += (double)difference * difference;
                    if (difference > worst)
                    {
                        worst = difference;
                        worstIndex = i;
                    }
                }
            }
        }

        switch (entry.Match)
        {
            case "psnr":
            {
                double mse = squaredError / (frame.Width * frame.Height * 4.0);
                double psnr = mse <= 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
                this.output.WriteLine($"{name}: PSNR {psnr:F1} dB (max channel delta {worst}).");
                Assert.True(psnr >= entry.Psnr, $"{name}: PSNR {psnr:F2} dB is below the required {entry.Psnr} dB. [{entry.Notes}]");
                break;
            }

            case "tolerance":
                Assert.True(
                    worst <= entry.Tolerance,
                    $"{name}: channel delta {worst} at pixel #{worstIndex / 4} exceeds the tolerance of {entry.Tolerance}. [{entry.Notes}]");
                break;

            default:
                Assert.True(
                    worst == 0,
                    $"{name}: first mismatch at pixel #{worstIndex / 4} (delta {worst}); the decode is not exact. [{entry.Notes}]");
                break;
        }
    }

    // ----- CCITT -----

    [Fact]
    public void CcittTables_ArePrefixFreeAndComplete()
    {
        foreach ((string[] terminating, string[] makeup, int[] lookup) in new[]
        {
            (TiffCcittTables.WhiteTerminating, TiffCcittTables.WhiteMakeup, TiffCcittTables.WhiteLookup),
            (TiffCcittTables.BlackTerminating, TiffCcittTables.BlackMakeup, TiffCcittTables.BlackLookup),
        })
        {
            var seen = new Dictionary<string, int>();
            var codes = new List<(string Code, int Run)>();
            for (int run = 0; run < terminating.Length; run++)
            {
                codes.Add((terminating[run], run));
            }

            for (int i = 0; i < makeup.Length; i++)
            {
                codes.Add((makeup[i], (i + 1) * 64));
            }

            for (int i = 0; i < TiffCcittTables.ExtendedMakeup.Length; i++)
            {
                codes.Add((TiffCcittTables.ExtendedMakeup[i], 1792 + (i * 64)));
            }

            foreach ((string code, int run) in codes)
            {
                Assert.True(code.Length <= TiffCcittTables.LookupBits, $"code {code} is wider than the lookup window.");
                Assert.False(seen.ContainsKey(code), $"duplicate code {code}.");
                seen[code] = run;

                // Prefix-freedom: no other code may be a prefix of this one.
                for (int length = 1; length < code.Length; length++)
                {
                    Assert.False(seen.ContainsKey(code[..length]), $"code {code} has the prefix {code[..length]}.");
                }

                // Every padding of the code must resolve back to the same run and length.
                int prefix = TiffCcittTables.ParseCode(code) << (TiffCcittTables.LookupBits - code.Length);
                for (int suffix = 0; suffix < 1 << (TiffCcittTables.LookupBits - code.Length); suffix++)
                {
                    int entry = lookup[prefix | suffix];
                    Assert.Equal(code.Length, entry & TiffCcittTables.LengthMask);
                    Assert.Equal(run, entry >> TiffCcittTables.LengthBits);
                }
            }

            // The all-zero window is the EOL prefix and must never resolve to a run.
            Assert.Equal(0, lookup[0]);
        }
    }

    [Theory]
    [InlineData(TiffCompression.Ccitt4)]
    [InlineData(TiffCompression.Ccitt3)]
    [InlineData(TiffCompression.CcittRle)]
    public void CcittEncoder_RoundTripsBilevelExactly(TiffCompression compression)
    {
        using Image<L8> source = BilevelPage(211, 97, seed: 5);
        byte[] encoded = Save(source, new TiffEncoder { Compression = compression });
        byte[] plain = Save(source, new TiffEncoder { Compression = TiffCompression.None, BitsPerPixel = TiffBitsPerPixel.Bit1 });

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        AssertSamePixels(source, decoded);

        this.output.WriteLine(
            $"{compression}: {encoded.Length} bytes vs {plain.Length} uncompressed bilevel "
            + $"({(double)plain.Length / encoded.Length:F2}x) and {(source.Width * source.Height) + 8} as 8-bit gray.");
        Assert.True(encoded.Length < plain.Length, $"{compression} output ({encoded.Length} B) is not smaller than uncompressed ({plain.Length} B).");
    }

    [Fact]
    public void CcittEncoder_PicksBilevelAndTagsTheFaxCompression()
    {
        using Image<L8> source = BilevelPage(64, 32, seed: 6);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(Save(source, new TiffEncoder { Compression = TiffCompression.Ccitt4 }));
        TiffFrameMetadata tiff = decoded.Frames.RootFrame.Metadata.GetTiffMetadata();
        Assert.Equal(TiffCompressionMethod.CcittGroup4Fax, tiff.Compression);
        Assert.Equal(TiffPhotometricInterpretation.WhiteIsZero, tiff.PhotometricInterpretation);
        Assert.Equal(new ushort[] { 1 }, tiff.BitsPerSample);
    }

    [Fact]
    public void CcittEncoder_ThresholdsGreyscaleToBilevel()
    {
        using var source = new Image<L8>(32, 8);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                source[x, y] = new L8((byte)(x * 8));
            }
        }

        using Image<Rgba32> decoded = Image.Load<Rgba32>(Save(source, new TiffEncoder { Compression = TiffCompression.Ccitt4 }));
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                byte expected = (byte)(Math.Min(x * 8, 255) >= 128 ? 255 : 0);
                Assert.Equal(expected, decoded[x, y].R);
            }
        }
    }

    [Fact]
    public void CcittDecoder_ToleratesTruncatedAndOverlongCodedData()
    {
        byte[] original = FixturePath.Read("tiffadv/ccitt_g4_64x24.tif");
        foreach (int keep in new[] { 1, 4, 12, 30 })
        {
            byte[] mangled = Truncate(original, keep);
            using Image<Rgba32> image = Image.Load<Rgba32>(mangled);
            Assert.Equal(64, image.Width);
            Assert.Equal(24, image.Height);
        }
    }

    // ----- Layout equivalence -----

    /// <summary>Every layout variant of the same RGB page must decode to exactly the chunky page's pixels.</summary>
    [Theory]
    [InlineData("layout_tiled_raw")]
    [InlineData("layout_tiled_deflate")]
    [InlineData("layout_tiled_wide")]
    [InlineData("layout_tiled_deflate_predictor")]
    [InlineData("layout_planar_raw")]
    [InlineData("layout_planar_strips")]
    [InlineData("layout_planar_deflate")]
    [InlineData("layout_planar_tiled")]
    [InlineData("layout_planar_rgb16_mm")]
    public void Layout_MatchesTheChunkyEquivalent(string name)
    {
        using Image<Rgba32> reference = Image.Load<Rgba32>(FixturePath.Read("tiffadv/layout_chunky_raw.tif"));
        using Image<Rgba32> variant = Image.Load<Rgba32>(FixturePath.Read($"tiffadv/{name}.tif"));
        Assert.Equal(reference.Width, variant.Width);
        Assert.Equal(reference.Height, variant.Height);
        for (int y = 0; y < reference.Height; y++)
        {
            for (int x = 0; x < reference.Width; x++)
            {
                Assert.True(reference[x, y].Equals(variant[x, y]), $"{name}: pixel ({x},{y}) differs from the chunky page.");
            }
        }
    }

    [Fact]
    public void PlanarPage_ReportsItsLayoutInTheFrameMetadata()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Read("tiffadv/layout_planar_tiled.tif"));
        TiffFrameMetadata tiff = image.Frames.RootFrame.Metadata.GetTiffMetadata();
        Assert.Equal(TiffPlanarConfiguration.Planar, tiff.PlanarConfiguration);
        Assert.True(tiff.Tiled);
    }

    // ----- Encoder options -----

    [Theory]
    [InlineData(TiffCompression.None)]
    [InlineData(TiffCompression.Deflate)]
    [InlineData(TiffCompression.Lzw)]
    [InlineData(TiffCompression.PackBits)]
    public void Encoder_RoundTripsColourExactly(TiffCompression compression)
    {
        using Image<Rgba32> source = ColourPage(29, 17);
        byte[] encoded = Save(source, new TiffEncoder { Compression = compression });
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        AssertSameRgba(source, decoded);
    }

    [Theory]
    [InlineData(TiffCompression.Lzw)]
    [InlineData(TiffCompression.Deflate)]
    public void Encoder_HorizontalDifferencingRoundTripsAndShrinksPhotographicPages(TiffCompression compression)
    {
        using Image<Rgb24> source = SmoothPage(64, 48);
        byte[] plain = Save(source, new TiffEncoder { Compression = compression });
        byte[] predicted = Save(source, new TiffEncoder { Compression = compression, Predictor = TiffPredictor.Horizontal });

        using Image<Rgba32> decoded = Image.Load<Rgba32>(predicted);
        Assert.Equal(TiffPredictor.Horizontal, decoded.Frames.RootFrame.Metadata.GetTiffMetadata().Predictor);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Rgb24 want = source[x, y];
                Rgba32 got = decoded[x, y];
                Assert.True(want.R == got.R && want.G == got.G && want.B == got.B, $"pixel ({x},{y}) differs.");
            }
        }

        this.output.WriteLine($"{compression}: {plain.Length} B plain, {predicted.Length} B with horizontal differencing.");
        Assert.True(predicted.Length < plain.Length, $"differencing grew the file: {predicted.Length} B vs {plain.Length} B.");
    }

    [Theory]
    [InlineData(TiffBitsPerPixel.Bit1, 1, 1)]
    [InlineData(TiffBitsPerPixel.Bit4, 4, 1)]
    [InlineData(TiffBitsPerPixel.Bit8, 8, 1)]
    [InlineData(TiffBitsPerPixel.Bit24, 8, 3)]
    [InlineData(TiffBitsPerPixel.Bit32, 8, 4)]
    public void Encoder_WritesTheRequestedDepth(TiffBitsPerPixel depth, int bitsPerSample, int samplesPerPixel)
    {
        using Image<Rgba32> source = ColourPage(23, 9);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(Save(source, new TiffEncoder { BitsPerPixel = depth }));
        TiffFrameMetadata tiff = decoded.Frames.RootFrame.Metadata.GetTiffMetadata();
        Assert.Equal(samplesPerPixel, tiff.SamplesPerPixel);
        Assert.Equal(Enumerable.Repeat((ushort)bitsPerSample, samplesPerPixel).ToArray(), tiff.BitsPerSample);
        Assert.Equal(source.Width, decoded.Width);
    }

    [Fact]
    public void Encoder_WhiteIsZeroInvertsTheStoredGreyLevels()
    {
        using var source = new Image<L8>(16, 4);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                source[x, y] = new L8((byte)(x * 17));
            }
        }

        byte[] encoded = Save(source, new TiffEncoder
        {
            Compression = TiffCompression.None,
            PhotometricInterpretation = TiffPhotometricInterpretation.WhiteIsZero,
        });

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        Assert.Equal(TiffPhotometricInterpretation.WhiteIsZero, decoded.Frames.RootFrame.Metadata.GetTiffMetadata().PhotometricInterpretation);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                // The stored sample is the complement, so the decode gives the original grey level back.
                Assert.Equal(source[x, y].PackedValue, decoded[x, y].R);
            }
        }
    }

    [Fact]
    public void Encoder_RejectsContradictoryOptions()
    {
        using Image<Rgba32> source = ColourPage(8, 8);
        Assert.Throws<NotSupportedException>(() => Save(source, new TiffEncoder
        {
            Compression = TiffCompression.Ccitt4,
            BitsPerPixel = TiffBitsPerPixel.Bit24,
        }));
        Assert.Throws<NotSupportedException>(() => Save(source, new TiffEncoder
        {
            Compression = TiffCompression.Lzw,
            BitsPerPixel = TiffBitsPerPixel.Bit1,
            Predictor = TiffPredictor.Horizontal,
        }));
        Assert.Throws<NotSupportedException>(() => Save(source, new TiffEncoder
        {
            Compression = TiffCompression.PackBits,
            Predictor = TiffPredictor.Horizontal,
        }));
        Assert.Throws<NotSupportedException>(() => Save(source, new TiffEncoder
        {
            BitsPerPixel = TiffBitsPerPixel.Bit8,
            PhotometricInterpretation = TiffPhotometricInterpretation.Rgb,
        }));
        Assert.Throws<NotSupportedException>(() => Save(source, new TiffEncoder
        {
            PhotometricInterpretation = TiffPhotometricInterpretation.YCbCr,
        }));
    }

    /// <summary>The defaults must keep producing exactly the bytes they produced before the options existed.</summary>
    [Fact]
    public void Encoder_DefaultsAreUnchangedByTheNewOptions()
    {
        using Image<Rgba32> source = ColourPage(19, 13);
        byte[] withDefaults = Save(source, new TiffEncoder());
        byte[] spelledOut = Save(source, new TiffEncoder
        {
            Compression = TiffCompression.Deflate,
            BitsPerPixel = TiffBitsPerPixel.Bit32,
            PhotometricInterpretation = TiffPhotometricInterpretation.Rgb,
            Predictor = TiffPredictor.None,
        });
        Assert.Equal(withDefaults, spelledOut);
    }

    // ----- Fuzzing -----

    /// <summary>
    /// Byte mutations of every fixture in this corpus must still leave the decoder inside its contract:
    /// success, an <see cref="ImageFormatException"/> or a <see cref="NotSupportedException"/>, and nothing else.
    /// </summary>
    [Fact]
    public void MutatedFixtures_StayInsideTheDecoderContract()
    {
        var random = new Random(20260822);
        var options = new DecoderOptions { MaxPixels = 1_000_000 };
        var failures = new List<string>();
        int success = 0;
        int rejected = 0;
        int unsupported = 0;

        foreach (AdvancedEntry entry in AdvancedManifest.Load())
        {
            byte[] original = FixturePath.Read($"tiffadv/{entry.File}");
            for (int i = 0; i < 120; i++)
            {
                byte[] mutated = Mutate(original, random, out string mutation);
                foreach (string call in new[] { "Load", "Identify" })
                {
                    try
                    {
                        if (call == "Load")
                        {
                            Image.Load<Rgba32>(mutated, options).Dispose();
                        }
                        else
                        {
                            Image.Identify(mutated, options);
                        }

                        success++;
                    }
                    catch (NotSupportedException)
                    {
                        unsupported++;
                    }
                    catch (ImageFormatException)
                    {
                        rejected++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{entry.Name} #{i} ({mutation}) via {call}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }

        this.output.WriteLine($"tiffadv fuzz: success={success} formatException={rejected} notSupported={unsupported} failures={failures.Count}");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(10)));
    }

    private static byte[] Mutate(byte[] source, Random random, out string description)
    {
        byte[] data = (byte[])source.Clone();
        switch (random.Next(5))
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
                int length = Math.Min(random.Next(1, 25), data.Length - pos);
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
                // An interesting 32-bit value inside the directory, where the offsets and counts live.
                int limit = Math.Max(1, Math.Min(data.Length, 320) - 4);
                int pos = random.Next(limit);
                uint[] interesting = { 0, 1, 0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF };
                BitConverter.TryWriteBytes(data.AsSpan(pos), interesting[random.Next(interesting.Length)]);
                description = $"write interesting dword @{pos}";
                return data;
            }

            default:
            {
                int pos = random.Next(data.Length + 1);
                byte[] insert = new byte[random.Next(1, 9)];
                random.NextBytes(insert);
                description = $"insert {insert.Length} bytes @{pos}";
                return data[..pos].Concat(insert).Concat(data[pos..]).ToArray();
            }
        }
    }

    private static Image<Rgba32> ColourPage(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32((byte)(x * 9), (byte)(y * 11), (byte)((x + y) * 5), (byte)(255 - (x * 3 % 256)));
            }
        }

        return image;
    }

    private static Image<Rgb24> SmoothPage(int width, int height)
    {
        var image = new Image<Rgb24>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgb24((byte)(x + y), (byte)((x * 2) + 30), (byte)(200 - y));
            }
        }

        return image;
    }

    private static void AssertSameRgba(Image<Rgba32> source, Image<Rgba32> decoded)
    {
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Assert.True(source[x, y].Equals(decoded[x, y]), $"pixel ({x},{y}) differs.");
            }
        }
    }

    /// <summary>Every scheme must reproduce the coded rows bit for bit, at widths that are not whole bytes too.</summary>
    /// <param name="schemeTag">2, 3 or 4: the TIFF compression tag naming the scheme.</param>
    /// <param name="width">The page width in pixels.</param>
    [Theory]
    [InlineData(4, 1)]
    [InlineData(4, 7)]
    [InlineData(4, 37)]
    [InlineData(4, 64)]
    [InlineData(4, 1730)]
    [InlineData(3, 37)]
    [InlineData(3, 1730)]
    [InlineData(2, 37)]
    [InlineData(2, 1730)]
    public void CcittCodec_RoundTripsRowsBitForBit(int schemeTag, int width)
    {
        const int Rows = 9;
        TiffCcittScheme scheme = SchemeOf(schemeTag);
        byte[] source = PackedBilevelRows(width, Rows, seed: width);
        byte[] coded = TiffCcitt.Encode(source, width, Rows, scheme);

        var decoded = new byte[source.Length];
        TiffCcitt.Decode(coded, decoded, width, Rows, Options(scheme));
        Assert.Equal(source, decoded);
    }

    private static TiffCcittScheme SchemeOf(int compressionTag) => compressionTag switch
    {
        2 => TiffCcittScheme.ModifiedHuffman,
        3 => TiffCcittScheme.Group3,
        _ => TiffCcittScheme.Group4,
    };

    /// <summary>
    /// A page whose declared geometry disagrees with the coded data - because the file lies, or because the
    /// stream was cut short - must be clamped and padded, never overrun or throw.
    /// </summary>
    [Fact]
    public void CcittCodec_ToleratesRowsThatEndEarlyOrRunPastTheWidth()
    {
        const int Width = 64;
        const int Rows = 8;
        byte[] source = PackedBilevelRows(Width, Rows, seed: 77);
        byte[] coded = TiffCcitt.Encode(source, Width, Rows, TiffCcittScheme.Group4);
        TiffCcittOptions options = Options(TiffCcittScheme.Group4);

        // Narrower than coded: every row's runs overrun the declared width and must be clamped.
        var narrow = new byte[((40 + 7) / 8) * Rows];
        TiffCcitt.Decode(coded, narrow, 40, Rows, options);

        // Wider than coded: every row ends before the declared width and the rest stays white.
        var wide = new byte[((96 + 7) / 8) * Rows];
        TiffCcitt.Decode(coded, wide, 96, Rows, options);

        // Fewer rows than coded: the extra coded rows are simply not read.
        var shallow = new byte[(Width / 8) * 3];
        TiffCcitt.Decode(coded, shallow, Width, 3, options);
        Assert.Equal(source.AsSpan(0, shallow.Length).ToArray(), shallow);

        // More rows than coded: the rows the data does cover are exact, the rest stay white.
        var deep = new byte[(Width / 8) * 20];
        TiffCcitt.Decode(coded, deep, Width, 20, options);
        Assert.Equal(source, deep.AsSpan(0, source.Length).ToArray());
        Assert.All(deep.Skip(source.Length), b => Assert.Equal(0, b));

        // Truncating the coded data at every length must leave the buffer usable.
        for (int keep = 0; keep <= coded.Length; keep++)
        {
            var partial = new byte[(Width / 8) * Rows];
            TiffCcitt.Decode(coded.AsSpan(0, keep), partial, Width, Rows, options);
        }
    }

    /// <summary>FillOrder 2 only reverses the bits inside each coded byte; the decoded rows are identical.</summary>
    [Fact]
    public void CcittCodec_FillOrder2ReadsTheSameRows()
    {
        const int Width = 53;
        const int Rows = 6;
        byte[] source = PackedBilevelRows(Width, Rows, seed: 12);
        byte[] coded = TiffCcitt.Encode(source, Width, Rows, TiffCcittScheme.Group4);
        byte[] reversed = coded.Select(ReverseBits).ToArray();

        var decoded = new byte[source.Length];
        TiffCcitt.Decode(reversed, decoded, Width, Rows, new TiffCcittOptions(TiffCcittScheme.Group4, false, false, LsbFirst: true));
        Assert.Equal(source, decoded);
    }

    private static byte ReverseBits(byte value)
    {
        int b = value;
        b = ((b & 0xF0) >> 4) | ((b & 0x0F) << 4);
        b = ((b & 0xCC) >> 2) | ((b & 0x33) << 2);
        b = ((b & 0xAA) >> 1) | ((b & 0x55) << 1);
        return (byte)b;
    }

    private static TiffCcittOptions Options(TiffCcittScheme scheme)
        => new(scheme, TwoDimensional: false, ByteAlign: scheme == TiffCcittScheme.ModifiedHuffman, LsbFirst: false);

    /// <summary>Bit-packed bilevel rows with document-like runs; the padding bits of a partial byte stay clear.</summary>
    private static byte[] PackedBilevelRows(int width, int rows, int seed)
    {
        var random = new Random(seed);
        int rowBytes = (width + 7) / 8;
        var packed = new byte[rowBytes * rows];
        for (int y = 0; y < rows; y++)
        {
            int x = 0;
            bool ink = false;
            while (x < width)
            {
                int run = Math.Min(width - x, 1 + random.Next(y == 0 ? 40 : 12));
                if (ink)
                {
                    for (int i = 0; i < run; i++)
                    {
                        packed[(y * rowBytes) + ((x + i) >> 3)] |= (byte)(0x80 >> ((x + i) & 7));
                    }
                }

                x += run;
                ink = !ink;
            }
        }

        return packed;
    }

    // ----- Support -----

    /// <summary>Rewrites the first strip of a single-strip TIFF so it keeps only its first bytes.</summary>
    private static byte[] Truncate(byte[] tiff, int keepBytes)
    {
        byte[] copy = (byte[])tiff.Clone();
        int ifd = BitConverter.ToInt32(copy, 4);
        int entries = BitConverter.ToUInt16(copy, ifd);
        for (int i = 0; i < entries; i++)
        {
            int entry = ifd + 2 + (i * 12);
            if (BitConverter.ToUInt16(copy, entry) == 279)
            {
                BitConverter.TryWriteBytes(copy.AsSpan(entry + 8), keepBytes);
            }
        }

        return copy;
    }

    /// <summary>A scan-like bilevel page: a white sheet with lines of word-shaped blobs and a little speckle.</summary>
    private static Image<L8> BilevelPage(int width, int height, int seed)
    {
        var random = new Random(seed);
        var image = new Image<L8>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new L8(255);
            }
        }

        for (int line = 2; line + 4 < height; line += 7)
        {
            int x = 3 + random.Next(4);
            while (x < width - 4)
            {
                int word = 2 + random.Next(9);
                for (int dy = 0; dy < 4 && line + dy < height; dy++)
                {
                    for (int dx = 0; dx < word && x + dx < width; dx++)
                    {
                        // A blank top-left corner and a blank bottom row give the blob a glyph-like outline.
                        bool ink = !(dy == 0 && dx < 1) && !(dy == 3 && dx >= word - 1);
                        if (ink)
                        {
                            image[x + dx, line + dy] = new L8(0);
                        }
                    }
                }

                x += word + 1 + random.Next(3);
            }
        }

        for (int i = 0; i < (width * height) / 1500; i++)
        {
            image[random.Next(width), random.Next(height)] = new L8(0);
        }

        return image;
    }

    private static byte[] Save<TPixel>(Image<TPixel> image, TiffEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    private static void AssertSamePixels(Image<L8> source, Image<Rgba32> decoded)
    {
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Assert.True(source[x, y].PackedValue == decoded[x, y].R, $"pixel ({x},{y}) differs.");
            }
        }
    }

    // ----- Manifest -----

    public sealed class AdvancedEntry
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
        public int Frames { get; set; }

        [JsonPropertyName("feature")]
        public string Feature { get; set; } = string.Empty;

        /// <summary>How the decode is compared with the ground truth: <c>exact</c>, <c>tolerance</c> or <c>psnr</c>.</summary>
        [JsonPropertyName("match")]
        public string Match { get; set; } = "exact";

        [JsonPropertyName("tolerance")]
        public int Tolerance { get; set; }

        [JsonPropertyName("psnr")]
        public double Psnr { get; set; }

        [JsonPropertyName("notes")]
        public string Notes { get; set; } = string.Empty;
    }

    internal static class AdvancedManifest
    {
        private static IReadOnlyList<AdvancedEntry>? cached;

        public static IReadOnlyList<AdvancedEntry> Load()
            => cached ??= JsonSerializer.Deserialize<List<AdvancedEntry>>(System.IO.File.ReadAllBytes(FixturePath.Get("tiffadv/manifest.json")))
                ?? new List<AdvancedEntry>();

        public static AdvancedEntry Get(string name)
            => Load().SingleOrDefault(e => e.Name == name)
                ?? throw new Xunit.Sdk.XunitException($"Fixture 'tiffadv/{name}' is not listed in manifest.json; run Fixtures/generate.py.");
    }
}

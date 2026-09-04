using System.Runtime.InteropServices;
using System.Text.Json;
using EasyImageSharp.Formats;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Animated PNG decoding: every fixture under <c>Fixtures/apng/</c> (see <c>EXPECTED.md</c> there) is decoded
/// and compared frame by frame with the accompanying <c>.rgba</c> dump, which holds the fully composited
/// canvas of each animation frame in display order. Those bytes come from the NumPy compositor in
/// <c>gen_apng.py</c>, written from the APNG specification rather than from this library - and deliberately
/// not from Pillow, whose <c>APNG_BLEND_OP_OVER</c> lerps alpha and so is wrong wherever a source pixel is
/// partly transparent. On the fixtures where that bug cannot bite the generator additionally asserted that
/// Pillow's own decode matches (the manifest's <c>pillow_verified</c> flag), so those files carry two
/// independent oracles. The malformed fixtures are the same well-formed animation with exactly one thing
/// wrong and must be rejected with the exception the manifest names.
/// </summary>
public class ApngTests
{
    private const string Folder = "apng";

    /// <summary>
    /// The colour of the IDAT still image in the two hidden-first-frame fixtures. No animation frame uses it,
    /// so a decoder that emitted the hidden default image as a frame is caught by looking for it.
    /// </summary>
    private static readonly Rgba32 HiddenStillColour = new(255, 0, 255, 255);

    public static IEnumerable<object[]> DecodableFixtures => ApngFixtures.Names(decodable: true);

    public static IEnumerable<object[]> MalformedFixtures => ApngFixtures.Names(decodable: false);

    [Fact]
    public void Manifest_IsPresentAndNonEmpty()
    {
        Assert.True(FixturePath.Exists($"{Folder}/manifest.json"), "Fixtures/apng/manifest.json is missing; run Fixtures/generate.py.");
        Assert.NotEmpty(ApngFixtures.All);
        Assert.Contains(ApngFixtures.All, e => e.Expect is null);
        Assert.Contains(ApngFixtures.All, e => e.Expect is not null);

        // Every decodable entry must bring its ground truth with it, or the suite would silently assert nothing.
        foreach (ApngFixture entry in ApngFixtures.All.Where(e => e.Expect is null))
        {
            Assert.True(FixturePath.Exists($"{Folder}/{entry.Name}.rgba"), $"Fixtures/apng/{entry.Name}.rgba is missing; run Fixtures/generate.py.");
        }
    }

    /// <summary>Each animation frame is the whole composited canvas and must match the reference compositor byte for byte.</summary>
    [Theory]
    [MemberData(nameof(DecodableFixtures))]
    public void Fixture_DecodesToReference(string name)
    {
        ApngFixture entry = ApngFixtures.Get(name);
        byte[] bytes = ApngFixtures.Bytes(name);
        Assert.Equal(ImageFormat.Png, Image.DetectFormat(bytes));

        ImageInfo info = Image.Identify(bytes);
        Assert.True(
            info.Width == entry.Width && info.Height == entry.Height,
            $"apng/{name}: Identify reported {info.Width}x{info.Height}, manifest says {entry.Width}x{entry.Height}.");
        Assert.True(info.FrameCount == entry.Frames, $"apng/{name}: Identify reported {info.FrameCount} frame(s), manifest says {entry.Frames}.");

        byte[] expected = ApngFixtures.ExpectedRgba(name);
        int stride = entry.Width * entry.Height * 4;
        Assert.True(
            expected.Length == stride * entry.Frames,
            $"apng/{name}: the .rgba dump holds {expected.Length} bytes, {stride * entry.Frames} expected for {entry.Frames} frame(s); run Fixtures/generate.py.");

        using Image<Rgba32> image = Image.Load<Rgba32>(bytes);
        Assert.True(image.Frames.Count == entry.Frames, $"apng/{name}: decoded {image.Frames.Count} frame(s), manifest says {entry.Frames}.");

        for (int f = 0; f < entry.Frames; f++)
        {
            ImageFrame<Rgba32> frame = image.Frames[f];
            Assert.True(
                frame.Width == entry.Width && frame.Height == entry.Height,
                $"apng/{name} frame {f}: decoded {frame.Width}x{frame.Height}, every frame must be the full {entry.Width}x{entry.Height} canvas.");
            AssertFrameMatches(entry, f, expected.AsSpan(f * stride, stride), frame);
        }
    }

    /// <summary>
    /// The fcTL fields survive onto every frame. This is what proves a zero delay denominator becomes /100
    /// (the specification's default) rather than a NaN-valued rational, and that the two enums are populated.
    /// </summary>
    [Theory]
    [MemberData(nameof(DecodableFixtures))]
    public void Fixture_FrameMetadataMatchesManifest(string name)
    {
        ApngFixture entry = ApngFixtures.Get(name);
        using Image<Rgba32> image = Image.Load<Rgba32>(ApngFixtures.Bytes(name));
        Assert.True(image.Frames.Count == entry.Frames, $"apng/{name}: decoded {image.Frames.Count} frame(s), manifest says {entry.Frames}.");

        for (int f = 0; f < entry.Frames; f++)
        {
            PngFrameMetadata frameMetadata = image.Frames[f].Metadata.GetPngMetadata();
            Rational delay = frameMetadata.FrameDelay;
            Assert.True(
                delay.Numerator == (uint)entry.Delays[f][0] && delay.Denominator == (uint)entry.Delays[f][1],
                $"apng/{name} frame {f}: delay {delay}, manifest says {entry.Delays[f][0]}/{entry.Delays[f][1]}. [{entry.Notes}]");
            Assert.False(
                double.IsNaN(delay.ToDouble()),
                $"apng/{name} frame {f}: the delay evaluates to NaN, so a zero fcTL delay_den was not normalised to 100.");
            Assert.True(
                frameMetadata.DisposalMethod == (PngDisposalMethod)entry.Disposals[f],
                $"apng/{name} frame {f}: disposal {frameMetadata.DisposalMethod}, manifest says dispose_op {entry.Disposals[f]}.");
            Assert.True(
                frameMetadata.BlendMethod == (PngBlendMethod)entry.Blends[f],
                $"apng/{name} frame {f}: blend {frameMetadata.BlendMethod}, manifest says blend_op {entry.Blends[f]}.");
        }
    }

    /// <summary>The acTL facts and the hidden-default-image determination survive onto the image metadata.</summary>
    [Theory]
    [MemberData(nameof(DecodableFixtures))]
    public void Fixture_ImageMetadataMatchesManifest(string name)
    {
        ApngFixture entry = ApngFixtures.Get(name);
        using Image<Rgba32> image = Image.Load<Rgba32>(ApngFixtures.Bytes(name));
        PngMetadata png = image.Metadata.GetPngMetadata();

        Assert.True(png.IsAnimated == entry.IsAnimated, $"apng/{name}: IsAnimated is {png.IsAnimated}, manifest says {entry.IsAnimated}.");
        Assert.True(
            png.RepeatCount == entry.RepeatCount,
            $"apng/{name}: RepeatCount is {png.RepeatCount}, manifest says {entry.RepeatCount} (0 means loop forever).");
        Assert.True(
            png.AnimateRootFrame == entry.AnimateRootFrame,
            $"apng/{name}: AnimateRootFrame is {png.AnimateRootFrame}, manifest says {entry.AnimateRootFrame}. [{entry.Notes}]");
    }

    /// <summary>
    /// Identify walks the chunk table without inflating anything, so it is a second implementation of the
    /// animation facts and must not drift from the decoder's.
    /// </summary>
    [Theory]
    [MemberData(nameof(DecodableFixtures))]
    public void Identify_AgreesWithDecode(string name)
    {
        byte[] bytes = ApngFixtures.Bytes(name);
        ImageInfo info = Image.Identify(bytes);
        using Image<Rgba32> image = Image.Load<Rgba32>(bytes);

        Assert.True(info.FrameCount == image.Frames.Count, $"apng/{name}: Identify reported {info.FrameCount} frame(s), the decoder produced {image.Frames.Count}.");
        Assert.Equal(image.Width, info.Width);
        Assert.Equal(image.Height, info.Height);

        PngMetadata identified = info.Metadata.GetPngMetadata();
        PngMetadata decoded = image.Metadata.GetPngMetadata();
        Assert.True(identified.IsAnimated == decoded.IsAnimated, $"apng/{name}: Identify says IsAnimated {identified.IsAnimated}, the decoder says {decoded.IsAnimated}.");
        Assert.True(identified.RepeatCount == decoded.RepeatCount, $"apng/{name}: Identify says RepeatCount {identified.RepeatCount}, the decoder says {decoded.RepeatCount}.");
        Assert.True(
            identified.AnimateRootFrame == decoded.AnimateRootFrame,
            $"apng/{name}: Identify says AnimateRootFrame {identified.AnimateRootFrame}, the decoder says {decoded.AnimateRootFrame}.");
    }

    /// <summary>
    /// Every malformed fixture is one well-formed animation with a single defect, and must be rejected with
    /// the exception the manifest names - never with a framework exception escaping the decoder.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedFixtures))]
    public void Malformed_IsRejected(string name)
    {
        ApngFixture entry = ApngFixtures.Get(name);
        byte[] bytes = ApngFixtures.Bytes(name);
        Assert.Equal(ImageFormat.Png, Image.DetectFormat(bytes));

        Exception ex = Assert.ThrowsAny<Exception>(() => Image.Load<Rgba32>(bytes));
        Assert.True(
            ex.GetType().Name == entry.Expect,
            $"apng/{name}: expected {entry.Expect} but got {ex.GetType().Name}: {ex.Message} [{entry.Notes}]");

        // Header inspection is deliberately lenient about animation chunks it cannot make sense of, but it
        // must still either succeed or fail through the documented contract.
        try
        {
            Image.Identify(bytes);
        }
        catch (Exception identifyEx) when (identifyEx is ImageFormatException or NotSupportedException)
        {
        }
    }

    /// <summary>
    /// Guards the corpus rather than the decoder: all three dispose_op values times both blend_op values must
    /// still reach a decoded frame, so a fixture regeneration cannot quietly drop a combination.
    /// </summary>
    [Fact]
    public void DisposalAndBlendCombinations_AreAllCovered()
    {
        var seen = new HashSet<(PngDisposalMethod Disposal, PngBlendMethod Blend)>();
        foreach (ApngFixture entry in ApngFixtures.All.Where(e => e.Expect is null))
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(ApngFixtures.Bytes(entry.Name));
            foreach (ImageFrame<Rgba32> frame in image.Frames)
            {
                PngFrameMetadata frameMetadata = frame.Metadata.GetPngMetadata();
                seen.Add((frameMetadata.DisposalMethod, frameMetadata.BlendMethod));
            }
        }

        PngDisposalMethod[] disposals = { PngDisposalMethod.None, PngDisposalMethod.RestoreToBackground, PngDisposalMethod.RestoreToPrevious };
        PngBlendMethod[] blends = { PngBlendMethod.Source, PngBlendMethod.Over };
        foreach (PngDisposalMethod disposal in disposals)
        {
            foreach (PngBlendMethod blend in blends)
            {
                Assert.True(
                    seen.Contains((disposal, blend)),
                    $"no decoded apng fixture frame uses {disposal} x {blend}; the corpus no longer covers the whole matrix. Run Fixtures/generate.py.");
            }
        }
    }

    /// <summary>Frames drawn at an offset: the four corners, both edges and 1x1 rectangles.</summary>
    [Theory]
    [InlineData("offsets_edges")]
    [InlineData("frame_1x1")]
    public void OffsetFrames_ComposeWhereTheirRectangleSays(string name)
    {
        ApngFixture entry = ApngFixtures.Get(name);
        Assert.True(entry.Frames > 1, $"apng/{name} is no longer a multi-frame fixture; run Fixtures/generate.py.");

        using Image<Rgba32> image = Image.Load<Rgba32>(ApngFixtures.Bytes(name));
        Assert.True(image.Frames.Count == entry.Frames, $"apng/{name}: decoded {image.Frames.Count} frame(s), manifest says {entry.Frames}.");

        byte[] expected = ApngFixtures.ExpectedRgba(name);
        int stride = entry.Width * entry.Height * 4;
        for (int f = 0; f < entry.Frames; f++)
        {
            AssertFrameMatches(entry, f, expected.AsSpan(f * stride, stride), image.Frames[f]);
        }

        // Each offset rectangle really does change the canvas, so a decoder that ignored the offsets and
        // redrew the same pixels every time could not pass the comparison above by accident.
        for (int f = 1; f < entry.Frames; f++)
        {
            ReadOnlySpan<byte> previous = expected.AsSpan((f - 1) * stride, stride);
            ReadOnlySpan<byte> current = expected.AsSpan(f * stride, stride);
            Assert.False(previous.SequenceEqual(current), $"apng/{name} frame {f}: the reference canvas did not change at all, so the fixture proves nothing.");
        }
    }

    /// <summary>A single-frame animation is still an animation: one frame, acTL honoured, fcTL delay carried.</summary>
    [Fact]
    public void SingleFrameAnimation_DecodesAsOneAnimatedFrame()
    {
        ApngFixture entry = ApngFixtures.Get("single_frame");
        Assert.Equal(1, entry.Frames);

        using Image<Rgba32> image = Image.Load<Rgba32>(ApngFixtures.Bytes("single_frame"));
        Assert.Single(image.Frames);
        Assert.Equal(1, Image.Identify(ApngFixtures.Bytes("single_frame")).FrameCount);

        PngMetadata png = image.Metadata.GetPngMetadata();
        Assert.True(png.IsAnimated, "apng/single_frame: a one-frame APNG still carries an acTL chunk.");
        Assert.True(png.AnimateRootFrame, "apng/single_frame: the IDAT image is introduced by an fcTL, so it is the animation's only frame.");

        PngFrameMetadata frameMetadata = image.Frames[0].Metadata.GetPngMetadata();
        Assert.Equal(new Rational((uint)entry.Delays[0][0], (uint)entry.Delays[0][1]), frameMetadata.FrameDelay);
        AssertFrameMatches(entry, 0, ApngFixtures.ExpectedRgba("single_frame"), image.Frames[0]);
    }

    /// <summary>
    /// When no fcTL precedes IDAT the IDAT image is a still fallback outside the animation. It must not be
    /// emitted as a frame: both fixtures use a solid magenta default image that no animation frame contains.
    /// </summary>
    [Theory]
    [InlineData("hidden_first_frame")]
    [InlineData("hidden_first_frame_single")]
    public void HiddenFirstFrame_DoesNotEmitTheStillImage(string name)
    {
        ApngFixture entry = ApngFixtures.Get(name);
        Assert.False(entry.AnimateRootFrame, $"apng/{name} is no longer a hidden-first-frame fixture; run Fixtures/generate.py.");

        using Image<Rgba32> image = Image.Load<Rgba32>(ApngFixtures.Bytes(name));
        Assert.True(image.Frames.Count == entry.Frames, $"apng/{name}: decoded {image.Frames.Count} frame(s), manifest says {entry.Frames}.");
        Assert.False(
            image.Metadata.GetPngMetadata().AnimateRootFrame,
            $"apng/{name}: the IDAT image sits outside the animation, so AnimateRootFrame must be false.");

        for (int f = 0; f < image.Frames.Count; f++)
        {
            Span<Rgba32> pixels = image.Frames[f].PixelSpan;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].Equals(HiddenStillColour))
                {
                    Assert.Fail(
                        $"apng/{name} frame {f}: pixel #{i} ({i % entry.Width},{i / entry.Width}) is the hidden default image's {HiddenStillColour}, so it leaked into the animation.");
                }
            }
        }
    }

    /// <summary>
    /// The frame cap truncates the decode to a prefix of the animation while Identify keeps reporting what
    /// the acTL chunk declares, so a caller can still see how much was left behind. Mirrors
    /// <see cref="DecoderLimitsTests.Tiff_MaxFrames_TruncatesDecodeButNotIdentify"/>.
    /// </summary>
    [Fact]
    public void MaxFrames_TruncatesDecodeButNotIdentify()
    {
        ApngFixture entry = ApngFixtures.Get("offsets_edges");
        byte[] bytes = ApngFixtures.Bytes("offsets_edges");
        Assert.True(entry.Frames > 3, $"apng/offsets_edges now has {entry.Frames} frame(s); the truncation test needs more than three.");

        Assert.Equal(entry.Frames, Image.Identify(bytes).FrameCount);

        using Image<Rgba32> limited = Image.Load<Rgba32>(bytes, new DecoderOptions { MaxFrames = 3 });
        Assert.Equal(3, limited.Frames.Count);

        // What survives must be the first three frames, not a resampling of the animation.
        byte[] expected = ApngFixtures.ExpectedRgba("offsets_edges");
        int stride = entry.Width * entry.Height * 4;
        for (int f = 0; f < limited.Frames.Count; f++)
        {
            AssertFrameMatches(entry, f, expected.AsSpan(f * stride, stride), limited.Frames[f]);
        }

        // Identify is deliberately unlimited, so it still reports the true count, and the default decode is
        // unaffected by the capped one.
        Assert.Equal(entry.Frames, Image.Identify(bytes).FrameCount);
        using Image<Rgba32> full = Image.Load<Rgba32>(bytes);
        Assert.Equal(entry.Frames, full.Frames.Count);
    }

    /// <summary>
    /// The regression guard on the still-image path: no fixture in the (non-animated) <c>png/</c> corpus may
    /// have acquired animation facts now that the same decoder walks both.
    /// </summary>
    [Fact]
    public void StillPngFixtures_AreUnaffected()
    {
        FixtureDecodeTests.FixtureEntry[] entries = FixtureDecodeTests.Manifest.Load("png");
        Assert.NotEmpty(entries);

        foreach (FixtureDecodeTests.FixtureEntry entry in entries)
        {
            byte[] bytes = FixturePath.Read($"png/{entry.File}");

            ImageInfo info = Image.Identify(bytes);
            PngMetadata identified = info.Metadata.GetPngMetadata();
            Assert.True(info.FrameCount == 1, $"png/{entry.Name}: Identify reported {info.FrameCount} frame(s) for a still image.");
            Assert.False(identified.IsAnimated, $"png/{entry.Name}: a file with no acTL chunk must not report IsAnimated.");
            Assert.True(identified.AnimateRootFrame, $"png/{entry.Name}: a still image's root frame is the image itself.");

            using Image<Rgba32> image = Image.Load<Rgba32>(bytes);
            PngMetadata decoded = image.Metadata.GetPngMetadata();
            Assert.True(image.Frames.Count == 1, $"png/{entry.Name}: decoded {image.Frames.Count} frame(s) for a still image.");
            Assert.False(decoded.IsAnimated, $"png/{entry.Name}: a file with no acTL chunk must not report IsAnimated.");
            Assert.True(decoded.AnimateRootFrame, $"png/{entry.Name}: a still image's root frame is the image itself.");
            Assert.Equal(1u, decoded.RepeatCount);
        }
    }

    /// <summary>
    /// The compositor works in <see cref="Rgba32"/> and each frame is converted once at the end, so every
    /// other pixel format must be exactly that conversion of the RGBA decode - frame count and per-frame
    /// metadata included.
    /// </summary>
    [Theory]
    [InlineData("dispose_none_blend_source")]
    [InlineData("dispose_previous_blend_over")]
    [InlineData("palette_animated")]
    [InlineData("gray16")]
    [InlineData("hidden_first_frame")]
    public void Fixture_DecodesIntoOtherPixelFormats(string name)
    {
        byte[] bytes = ApngFixtures.Bytes(name);
        using Image<Rgba32> reference = Image.Load<Rgba32>(bytes);

        AssertDecodesLikeReference<Rgb24>(name, bytes, reference);
        AssertDecodesLikeReference<Rgba64>(name, bytes, reference);
        AssertDecodesLikeReference<L8>(name, bytes, reference);
        AssertDecodesLikeReference<Bgra32>(name, bytes, reference);
        AssertDecodesLikeReference<La16>(name, bytes, reference);
    }

    /// <summary>The animation metadata is carried by two hand-written copy constructors, which a deep clone exercises.</summary>
    [Fact]
    public void Metadata_SurvivesDeepClone()
    {
        using Image<Rgba32> source = Image.Load<Rgba32>(ApngFixtures.Bytes("hidden_first_frame"));
        PngMetadata original = source.Metadata.GetPngMetadata();
        Assert.True(original.IsAnimated);
        Assert.Equal(2u, original.RepeatCount);
        Assert.False(original.AnimateRootFrame);

        using Image<Rgba32> clone = source.Clone();
        PngMetadata copied = clone.Metadata.GetPngMetadata();
        Assert.NotSame(original, copied);
        Assert.Equal(original.IsAnimated, copied.IsAnimated);
        Assert.Equal(original.RepeatCount, copied.RepeatCount);
        Assert.Equal(original.AnimateRootFrame, copied.AnimateRootFrame);

        Assert.Equal(source.Frames.Count, clone.Frames.Count);
        for (int f = 0; f < source.Frames.Count; f++)
        {
            PngFrameMetadata before = source.Frames[f].Metadata.GetPngMetadata();
            PngFrameMetadata after = clone.Frames[f].Metadata.GetPngMetadata();
            Assert.NotSame(before, after);
            Assert.Equal(before.FrameDelay, after.FrameDelay);
            Assert.Equal(before.DisposalMethod, after.DisposalMethod);
            Assert.Equal(before.BlendMethod, after.BlendMethod);
        }

        // The clone is a copy, not a view: writing to it must not reach back into the source.
        copied.RepeatCount = 99;
        copied.AnimateRootFrame = true;
        Assert.Equal(2u, original.RepeatCount);
        Assert.False(original.AnimateRootFrame);

        // The same three properties set by hand, which is how an author drives the encoder.
        using var authored = new Image<Rgba32>(4, 4);
        PngMetadata png = authored.Metadata.GetPngMetadata();
        png.IsAnimated = true;
        png.RepeatCount = 7;
        png.AnimateRootFrame = false;

        using Image<Rgba32> authoredClone = authored.Clone();
        PngMetadata authoredCopy = authoredClone.Metadata.GetPngMetadata();
        Assert.True(authoredCopy.IsAnimated);
        Assert.Equal(7u, authoredCopy.RepeatCount);
        Assert.False(authoredCopy.AnimateRootFrame);
    }

    /// <summary>Decodes the fixture into another pixel format and requires it to be the converted RGBA decode.</summary>
    /// <typeparam name="TPixel">The pixel format to decode into.</typeparam>
    /// <param name="name">The fixture name, for diagnostics.</param>
    /// <param name="bytes">The fixture's bytes.</param>
    /// <param name="reference">The same file decoded as <see cref="Rgba32"/>.</param>
    private static void AssertDecodesLikeReference<TPixel>(string name, byte[] bytes, Image<Rgba32> reference)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using Image<TPixel> image = Image.Load<TPixel>(bytes);
        Assert.True(
            image.Frames.Count == reference.Frames.Count,
            $"apng/{name}: decoding into {typeof(TPixel).Name} produced {image.Frames.Count} frame(s), the Rgba32 decode produced {reference.Frames.Count}.");
        Assert.Equal(reference.Width, image.Width);
        Assert.Equal(reference.Height, image.Height);

        for (int f = 0; f < image.Frames.Count; f++)
        {
            ReadOnlySpan<Rgba32> want = reference.Frames[f].PixelSpan;
            ReadOnlySpan<TPixel> got = image.Frames[f].PixelSpan;
            for (int i = 0; i < want.Length; i++)
            {
                TPixel expected = TPixel.FromRgba32(want[i]);
                if (!EqualityComparer<TPixel>.Default.Equals(got[i], expected))
                {
                    Assert.Fail($"apng/{name} frame {f}: pixel #{i} decoded into {typeof(TPixel).Name} is {got[i]}, converting the Rgba32 decode gives {expected}.");
                }
            }

            PngFrameMetadata wanted = reference.Frames[f].Metadata.GetPngMetadata();
            PngFrameMetadata actual = image.Frames[f].Metadata.GetPngMetadata();
            Assert.Equal(wanted.FrameDelay, actual.FrameDelay);
            Assert.Equal(wanted.DisposalMethod, actual.DisposalMethod);
            Assert.Equal(wanted.BlendMethod, actual.BlendMethod);
        }
    }

    /// <summary>
    /// Compares one composited frame with its block of the <c>.rgba</c> dump: exactly when the manifest
    /// records no tolerance, and otherwise allowing that many levels on any single channel.
    /// </summary>
    /// <param name="entry">The fixture being checked.</param>
    /// <param name="index">The frame's index, for diagnostics.</param>
    /// <param name="want">The frame's block of the ground-truth dump.</param>
    /// <param name="frame">The decoded frame.</param>
    private static void AssertFrameMatches(ApngFixture entry, int index, ReadOnlySpan<byte> want, ImageFrame<Rgba32> frame)
    {
        ReadOnlySpan<byte> got = MemoryMarshal.AsBytes(frame.PixelSpan);
        Assert.True(want.Length == got.Length, $"apng/{entry.Name} frame {index}: the frame holds {got.Length} bytes, the .rgba dump {want.Length}.");

        int mismatch = FirstMismatch(want, got, entry.Tolerance);
        if (mismatch < 0)
        {
            return;
        }

        int i = mismatch / 4;
        int x = i % frame.Width;
        int y = i / frame.Width;
        Rgba32 wantPixel = MemoryMarshal.Cast<byte, Rgba32>(want)[i];
        Assert.Fail(
            $"apng/{entry.Name} frame {index}: first mismatch at pixel #{i} ({x},{y}): expected {wantPixel}, decoded {frame[x, y]} (tolerance {entry.Tolerance}). [{entry.Notes}]");
    }

    /// <summary>Returns the index of the first byte differing by more than <paramref name="tolerance"/>, or -1 when none does.</summary>
    /// <param name="want">The expected bytes.</param>
    /// <param name="got">The decoded bytes, the same length.</param>
    /// <param name="tolerance">The largest accepted absolute difference per byte.</param>
    private static int FirstMismatch(ReadOnlySpan<byte> want, ReadOnlySpan<byte> got, int tolerance)
    {
        int i = want.CommonPrefixLength(got);
        if (tolerance == 0)
        {
            return i == want.Length ? -1 : i;
        }

        for (; i < want.Length; i++)
        {
            if (Math.Abs(want[i] - got[i]) > tolerance)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// One entry of the APNG fixture manifest. It carries the per-frame fcTL facts and the acTL facts that the
/// shared <see cref="FixtureDecodeTests.FixtureEntry"/> does not model, so the APNG suite reads its own.
/// </summary>
internal sealed record ApngFixture(
    string Name, string File, int Width, int Height, int Frames, uint RepeatCount, bool AnimateRootFrame,
    bool IsAnimated, int[][] Delays, int[] Disposals, int[] Blends, int Tolerance, bool PillowVerified,
    string Notes, string? Expect);

/// <summary>Reads <c>Fixtures/apng/manifest.json</c>, written by <c>Fixtures/gen_apng.py</c>.</summary>
internal static class ApngFixtures
{
    private static ApngFixture[]? cache;

    public static ApngFixture[] All
    {
        get
        {
            if (cache is null)
            {
                using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText(FixturePath.Get("apng/manifest.json")));
                cache = document.RootElement.EnumerateArray().Select(Read).ToArray();
            }

            return cache;
        }
    }

    /// <summary>The names of the fixtures that decode (<paramref name="decodable"/> true) or of those that must be rejected.</summary>
    /// <param name="decodable">True for the well-formed fixtures, false for the malformed ones.</param>
    public static IEnumerable<object[]> Names(bool decodable)
    {
        if (!FixturePath.Exists("apng/manifest.json"))
        {
            // Surface a clear failure through Manifest_IsPresentAndNonEmpty rather than an empty theory.
            yield return new object[] { "(manifest missing)" };
            yield break;
        }

        foreach (ApngFixture entry in All)
        {
            bool isDecodable = entry.Expect is null;
            if (isDecodable == decodable)
            {
                yield return new object[] { entry.Name };
            }
        }
    }

    public static ApngFixture Get(string name)
        => All.SingleOrDefault(e => e.Name == name)
           ?? throw new Xunit.Sdk.XunitException($"Fixture 'apng/{name}' is not listed in manifest.json; run Fixtures/generate.py.");

    public static byte[] Bytes(string name) => FixturePath.Read($"apng/{Get(name).File}");

    /// <summary>The composited canvas of every animation frame in display order, width * height * 4 bytes each.</summary>
    /// <param name="name">The fixture name.</param>
    public static byte[] ExpectedRgba(string name) => FixturePath.Read($"apng/{Get(name).Name}.rgba");

    private static ApngFixture Read(JsonElement element)
    {
        string? expect = element.TryGetProperty("expect", out JsonElement e) ? e.GetString() : null;
        return new ApngFixture(
            element.GetProperty("name").GetString()!,
            element.GetProperty("file").GetString()!,
            Int(element, "width"),
            Int(element, "height"),
            element.TryGetProperty("frames", out JsonElement frames) ? frames.GetInt32() : 1,
            element.TryGetProperty("repeat_count", out JsonElement plays) ? plays.GetUInt32() : 1u,
            !element.TryGetProperty("animate_root_frame", out JsonElement root) || root.GetBoolean(),
            element.TryGetProperty("is_animated", out JsonElement animated) && animated.GetBoolean(),
            Rows(element, "delays"),
            Values(element, "disposals"),
            Values(element, "blends"),
            Int(element, "tolerance"),
            element.TryGetProperty("pillow_verified", out JsonElement verified) && verified.GetBoolean(),
            element.GetProperty("notes").GetString() ?? string.Empty,
            expect);
    }

    private static int Int(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : 0;

    private static int[] Values(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
            ? value.EnumerateArray().Select(item => item.GetInt32()).ToArray()
            : Array.Empty<int>();

    private static int[][] Rows(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
            ? value.EnumerateArray().Select(row => row.EnumerateArray().Select(item => item.GetInt32()).ToArray()).ToArray()
            : Array.Empty<int[]>();
}

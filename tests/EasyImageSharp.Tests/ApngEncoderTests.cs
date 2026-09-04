using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The APNG side of the PNG encoder: which chunks it emits and in what order, the single sequence number
/// series shared by fcTL and fdAT, the acTL counts, the per-frame delay, disposal and blend fields, the
/// sub-rectangle diff - and, above all, that a single still frame is written exactly as it always was.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here trusts <see cref="PngDecoder"/> alone. The chunk stream is walked by a parser written in
/// this file that verifies every CRC itself (the decoder deliberately does not, so a corrupt CRC would go
/// unnoticed everywhere else), the still IDAT is compared against filtered scanlines rebuilt from the
/// source pixels, and the fixture round-trips are compared against the committed <c>.rgba</c> ground truth
/// under <c>Fixtures/apng/</c>, which was produced by hand-assembled chunk writers and Pillow rather than
/// by this library.
/// </para>
/// <para>
/// <see cref="RoundTripPreservesTheCommittedFramePixels"/> also writes every re-encoded fixture to
/// <c>apng-roundtrip/</c> next to the test binaries so that
/// <c>python Fixtures/gen_apng.py --verify &lt;that directory&gt;</c> can decode the encoder's own output with
/// Pillow and compare it against the same ground truth. That was run against this revision and reported
/// <c>0 mismatch(es)</c>. Interlaced output is never written there: Pillow 11.3 raises
/// <c>TypeError</c> on an Adam7-interlaced APNG frame, so it is not an oracle for that shape - see
/// <see cref="InterlacedAnimationsRoundTripButAreNotCrossCheckedWithPillow"/>.
/// </para>
/// </remarks>
public class ApngEncoderTests
{
    /// <summary>Where the re-encoded fixtures are dropped for <c>gen_apng.py --verify</c>.</summary>
    private static readonly string RoundTripDirectory = Path.Combine(AppContext.BaseDirectory, "apng-roundtrip");

    /// <summary>Adam7 pass geometry: (x start, y start, x step, y step). The still scanline check rebuilds these itself.</summary>
    private static readonly (int X0, int Y0, int Dx, int Dy)[] Adam7Passes =
    {
        (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2),
    };

    private static readonly (int X0, int Y0, int Dx, int Dy)[] SinglePass = { (0, 0, 1, 1) };

    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static readonly Lazy<ApngManifestEntry[]> ManifestEntries = new(LoadManifest);

    /// <summary>The fixtures a decoder is expected to accept, which are the ones that can be re-encoded.</summary>
    public static TheoryData<string> WellFormedFixtures()
    {
        var data = new TheoryData<string>();
        foreach (ApngManifestEntry entry in Manifest())
        {
            if (entry.Expect is null)
            {
                data.Add(entry.Name);
            }
        }

        if (data.Count == 0)
        {
            data.Add("(manifest missing)");
        }

        return data;
    }

    /// <summary>Every colour type and bit depth a still PNG may use, interlaced and not.</summary>
    public static TheoryData<PngColorType, PngBitDepth, PngInterlaceMethod> StillConfigurations()
    {
        (PngColorType Type, PngBitDepth[] Depths)[] combinations =
        {
            (PngColorType.Grayscale, new[] { PngBitDepth.Bit1, PngBitDepth.Bit2, PngBitDepth.Bit4, PngBitDepth.Bit8, PngBitDepth.Bit16 }),
            (PngColorType.Rgb, new[] { PngBitDepth.Bit8, PngBitDepth.Bit16 }),
            (PngColorType.Palette, new[] { PngBitDepth.Bit1, PngBitDepth.Bit2, PngBitDepth.Bit4, PngBitDepth.Bit8 }),
            (PngColorType.GrayscaleWithAlpha, new[] { PngBitDepth.Bit8, PngBitDepth.Bit16 }),
            (PngColorType.RgbWithAlpha, new[] { PngBitDepth.Bit8, PngBitDepth.Bit16 }),
        };

        var data = new TheoryData<PngColorType, PngBitDepth, PngInterlaceMethod>();
        foreach ((PngColorType type, PngBitDepth[] depths) in combinations)
        {
            foreach (PngBitDepth depth in depths)
            {
                data.Add(type, depth, PngInterlaceMethod.None);
                data.Add(type, depth, PngInterlaceMethod.Adam7);
            }
        }

        return data;
    }

    // ----- Chunk stream shape -----

    [Fact]
    public void TheFixtureManifestIsPresentAndDescribesTheCommittedFiles()
    {
        ApngManifestEntry[] entries = Manifest();
        Assert.NotEmpty(entries);
        Assert.Contains(entries, entry => entry.Expect is null && entry.Frames > 1);
        foreach (ApngManifestEntry entry in entries)
        {
            Assert.True(FixturePath.Exists("apng/" + entry.File), $"fixture {entry.File} is missing; run Fixtures/gen_apng.py.");
        }
    }

    [Fact]
    public void AnAnimationEmitsTheChunksInTheOrderTheFormatRequires()
    {
        using Image<Rgba32> source = MovingSquare(64, 64, 4);

        byte[] encoded = Encode(source, new PngEncoder { RepeatCount = 7 });

        List<Chunk> chunks = ReadChunks(encoded);
        Assert.Equal(
            new[] { "IHDR", "acTL", "fcTL", "IDAT", "fcTL", "fdAT", "fcTL", "fdAT", "fcTL", "fdAT", "IEND" },
            Skeleton(chunks));

        // The animation control has to sit after IHDR and before the first IDAT for the file to be an APNG.
        int actl = chunks.FindIndex(chunk => chunk.Name == "acTL");
        Assert.Equal(1, chunks.Count(chunk => chunk.Name == "acTL"));
        Assert.True(actl > chunks.FindIndex(chunk => chunk.Name == "IHDR"), "acTL was written before IHDR.");
        Assert.True(actl < chunks.FindIndex(chunk => chunk.Name == "IDAT"), "acTL was written after the first IDAT.");

        (uint frames, uint plays) = ReadAnimationControl(chunks);
        Assert.Equal(4u, frames);
        Assert.Equal((uint)source.Frames.Count, frames);
        Assert.Equal(7u, plays);

        Assert.Equal(4, chunks.Count(chunk => chunk.Name == "fcTL"));
        Assert.Equal(3, chunks.Count(chunk => chunk.Name == "fdAT"));
        Assert.Equal(1, chunks.Count(chunk => chunk.Name == "IDAT"));

        // One counter, shared, strictly increasing, no gaps: 0..6 for four frames whose first one is IDAT.
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5, 6 }, SequenceNumbers(chunks));

        // The first frame is the IDAT image, so its rectangle has to be the whole canvas at the origin.
        FrameControl first = FrameControls(chunks)[0];
        Assert.Equal(0, first.XOffset);
        Assert.Equal(0, first.YOffset);
        Assert.Equal(64, first.Width);
        Assert.Equal(64, first.Height);

        Assert.Equal(4, Image.Identify(encoded).FrameCount);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        AssertFramesEqual(source, decoded, "a four frame animation");
    }

    [Fact]
    public void AHiddenRootFrameKeepsTheStillImageOutsideTheAnimation()
    {
        using Image<Rgba32> source = MovingSquare(32, 32, 3);
        source.Metadata.GetPngMetadata().AnimateRootFrame = false;

        byte[] encoded = Encode(source, new PngEncoder());

        List<Chunk> chunks = ReadChunks(encoded);
        Assert.Equal(
            new[] { "IHDR", "acTL", "IDAT", "fcTL", "fdAT", "fcTL", "fdAT", "fcTL", "fdAT", "IEND" },
            Skeleton(chunks));

        // Every frame is an fdAT now, because IDAT is a still fallback rather than the animation's frame 0.
        Assert.Equal(3, chunks.Count(chunk => chunk.Name == "fdAT"));
        Assert.Equal(3, chunks.Count(chunk => chunk.Name == "fcTL"));
        Assert.Equal(3u, ReadAnimationControl(chunks).Frames);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, SequenceNumbers(chunks));

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        Assert.Equal(3, decoded.Frames.Count);
        Assert.False(decoded.Metadata.GetPngMetadata().AnimateRootFrame);
        AssertFramesEqual(source, decoded, "a hidden root frame animation");
    }

    [Theory]
    [MemberData(nameof(WellFormedFixtures))]
    public void SequenceNumbersFormOneUnbrokenSeriesForEveryFixture(string name)
    {
        if (name == "(manifest missing)")
        {
            return;
        }

        ApngManifestEntry entry = Entry(name);
        using Image<Rgba32> source = Image.Load<Rgba32>(FixturePath.Read("apng/" + entry.File));

        byte[] encoded = Encode(source, new PngEncoder());

        List<Chunk> chunks = ReadChunks(encoded);
        int frames = source.Frames.Count;
        bool animateRoot = source.Metadata.GetPngMetadata().AnimateRootFrame;

        Assert.Equal((uint)frames, ReadAnimationControl(chunks).Frames);
        Assert.Equal(frames, chunks.Count(chunk => chunk.Name == "fcTL"));
        Assert.Equal(animateRoot ? frames - 1 : frames, chunks.Count(chunk => chunk.Name == "fdAT"));
        Assert.Equal(1, chunks.Count(chunk => chunk.Name == "IDAT"));

        uint[] sequences = SequenceNumbers(chunks);
        Assert.Equal(chunks.Count(chunk => chunk.Name is "fcTL" or "fdAT"), sequences.Length);
        for (uint i = 0; i < sequences.Length; i++)
        {
            Assert.Equal(i, sequences[i]);
        }
    }

    [Fact]
    public void TheAnimationControlPrefersTheOptionThenTheMetadataThenLoopingForever()
    {
        using Image<Rgba32> source = MovingSquare(16, 16, 2);

        // No PNG metadata at all: the file says "loop forever", which is what num_plays 0 means.
        Assert.Equal(0u, ReadAnimationControl(ReadChunks(Encode(source, new PngEncoder()))).Plays);

        source.Metadata.GetPngMetadata().RepeatCount = 5;
        Assert.Equal(5u, ReadAnimationControl(ReadChunks(Encode(source, new PngEncoder()))).Plays);

        // The explicit option wins over the metadata, including when it is zero.
        Assert.Equal(9u, ReadAnimationControl(ReadChunks(Encode(source, new PngEncoder { RepeatCount = 9 }))).Plays);
        Assert.Equal(0u, ReadAnimationControl(ReadChunks(Encode(source, new PngEncoder { RepeatCount = 0 }))).Plays);

        using Image<Rgba32> decoded = Image.Load<Rgba32>(Encode(source, new PngEncoder { RepeatCount = 9 }));
        Assert.Equal(9u, decoded.Metadata.GetPngMetadata().RepeatCount);
        Assert.True(decoded.Metadata.GetPngMetadata().IsAnimated);
    }

    [Fact]
    public void ASingleFrameMarkedAnimatedBecomesAOneFrameAnimation()
    {
        using var source = new Image<Rgba32>(12, 9, new Rgba32(80, 120, 160, 255));
        source.Metadata.GetPngMetadata().IsAnimated = true;

        byte[] encoded = Encode(source, new PngEncoder());

        List<Chunk> chunks = ReadChunks(encoded);
        Assert.Equal(new[] { "IHDR", "acTL", "fcTL", "IDAT", "IEND" }, Skeleton(chunks));
        Assert.Equal(1u, ReadAnimationControl(chunks).Frames);
        Assert.DoesNotContain(chunks, chunk => chunk.Name == "fdAT");
        Assert.Equal(new uint[] { 0 }, SequenceNumbers(chunks));

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        Assert.Single(decoded.Frames);
        Assert.True(decoded.Metadata.GetPngMetadata().IsAnimated);
        AssertFramesEqual(source, decoded, "a single frame animation");
    }

    // ----- Delays -----

    [Fact]
    public void AFrameDelayThatFitsTheChunkIsWrittenVerbatim()
    {
        using Image<Rgba32> source = MovingSquare(16, 16, 3);
        Rational[] delays = { new(1, 24), new(1001, 30000), new(3, 50) };
        for (int i = 0; i < delays.Length; i++)
        {
            source.Frames[i].Metadata.GetPngMetadata().FrameDelay = delays[i];
        }

        byte[] encoded = Encode(source, new PngEncoder());

        List<FrameControl> controls = FrameControls(ReadChunks(encoded));
        Assert.Equal(3, controls.Count);
        for (int i = 0; i < delays.Length; i++)
        {
            Assert.Equal((ushort)delays[i].Numerator, controls[i].DelayNumerator);
            Assert.Equal((ushort)delays[i].Denominator, controls[i].DelayDenominator);
        }

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        for (int i = 0; i < delays.Length; i++)
        {
            Assert.Equal(delays[i], decoded.Frames[i].Metadata.GetPngMetadata().FrameDelay);
        }
    }

    [Fact]
    public void AFrameDelayTooLargeForTheChunkIsApproximatedInMilliseconds()
    {
        using Image<Rgba32> source = MovingSquare(16, 16, 2);

        // 70000/1000000 s is 70 ms, but neither half fits in the fcTL's two 16-bit fields.
        source.Frames[1].Metadata.GetPngMetadata().FrameDelay = new Rational(70000, 1000000);

        List<FrameControl> controls = FrameControls(ReadChunks(Encode(source, new PngEncoder())));
        Assert.Equal(70, controls[1].DelayNumerator);
        Assert.Equal(1000, controls[1].DelayDenominator);
    }

    [Fact]
    public void TheFrameDelayOptionReplacesEveryFramesOwnDelay()
    {
        using Image<Rgba32> source = MovingSquare(16, 16, 3);
        source.Frames[1].Metadata.GetPngMetadata().FrameDelay = new Rational(1, 24);

        byte[] encoded = Encode(source, new PngEncoder { FrameDelay = 25 });

        foreach (FrameControl control in FrameControls(ReadChunks(encoded)))
        {
            Assert.Equal(25, control.DelayNumerator);
            Assert.Equal(1000, control.DelayDenominator);
        }

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        foreach (ImageFrame<Rgba32> frame in decoded.Frames)
        {
            Assert.Equal(new Rational(25, 1000), frame.Metadata.GetPngMetadata().FrameDelay);
        }
    }

    [Fact]
    public void AFrameWithoutADelayGetsTheHundredMillisecondDefault()
    {
        using Image<Rgba32> source = MovingSquare(16, 16, 3);

        // Frame 1 carries PNG metadata whose delay is the 0/100 default; frames 0 and 2 carry none at all.
        // Both land on the same fallback, and so - today - does a deliberate zero delay: see
        // ADeliberateZeroDelayIsCurrentlyRewrittenToTheDefault.
        _ = source.Frames[1].Metadata.GetPngMetadata();

        foreach (FrameControl control in FrameControls(ReadChunks(Encode(source, new PngEncoder()))))
        {
            Assert.Equal(100, control.DelayNumerator);
            Assert.Equal(1000, control.DelayDenominator);
        }
    }

    [Fact]
    public void ADeliberateZeroDelayIsCurrentlyRewrittenToTheDefault()
    {
        // APNG allows delay_num 0, meaning "show this frame as fast as the viewer can", and the
        // delay_exotic fixture carries one. The encoder's delay resolution treats a zero numerator as
        // "no delay was set" and falls back to 100 ms, so a decoded 0/1 does not survive a re-encode.
        // This pins that behaviour rather than endorsing it; when the encoder learns to write a zero
        // delay verbatim this test is the one that has to change.
        using Image<Rgba32> source = MovingSquare(16, 16, 2);
        source.Frames[1].Metadata.GetPngMetadata().FrameDelay = new Rational(0, 1);

        FrameControl control = FrameControls(ReadChunks(Encode(source, new PngEncoder())))[1];
        Assert.Equal(100, control.DelayNumerator);
        Assert.Equal(1000, control.DelayDenominator);
    }

    // ----- Disposal and blending -----

    [Fact]
    public void DisposalOperationsAreWrittenAndSurviveARoundTrip()
    {
        PngDisposalMethod[] disposals =
        {
            PngDisposalMethod.None,
            PngDisposalMethod.RestoreToBackground,
            PngDisposalMethod.RestoreToPrevious,
            PngDisposalMethod.None,
        };

        using Image<Rgba32> source = MovingSquare(32, 32, 4);
        for (int i = 0; i < disposals.Length; i++)
        {
            source.Frames[i].Metadata.GetPngMetadata().DisposalMethod = disposals[i];
        }

        byte[] encoded = Encode(source, new PngEncoder());

        List<FrameControl> controls = FrameControls(ReadChunks(encoded));
        for (int i = 0; i < disposals.Length; i++)
        {
            Assert.Equal((byte)disposals[i], controls[i].Dispose);
        }

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        AssertFramesEqual(source, decoded, "an animation with mixed disposal");
        for (int i = 0; i < disposals.Length; i++)
        {
            Assert.Equal(disposals[i], decoded.Frames[i].Metadata.GetPngMetadata().DisposalMethod);
        }
    }

    [Fact]
    public void RestoreToPreviousRoundTripsAgainstAnExpectationComputedByHand()
    {
        // Three 8x8 stamps land on a plain background, each one undone before the next: what a viewer must
        // show for frame i is the background plus stamp i, and nothing of the stamps before it. The frames
        // are built that way here, so the composited output the decoder returns has to match them exactly.
        const int Size = 24;
        var background = new Rgba32(30, 30, 30, 255);
        Image<Rgba32>? source = null;
        for (int i = 0; i < 3; i++)
        {
            var frame = new Image<Rgba32>(Size, Size, background);
            for (int y = 4 + (i * 2); y < 12 + (i * 2); y++)
            {
                for (int x = 2 + (i * 6); x < 10 + (i * 6); x++)
                {
                    frame[x, y] = new Rgba32((byte)(200 - (i * 40)), 90, (byte)(40 + (i * 60)), 255);
                }
            }

            if (source is null)
            {
                source = frame;
            }
            else
            {
                source.Frames.AddFrame(frame.Frames.RootFrame.PixelSpan);
                frame.Dispose();
            }
        }

        using Image<Rgba32> animation = source!;
        foreach (ImageFrame<Rgba32> frame in animation.Frames)
        {
            frame.Metadata.GetPngMetadata().DisposalMethod = PngDisposalMethod.RestoreToPrevious;
        }

        byte[] encoded = Encode(animation, new PngEncoder());

        Assert.All(FrameControls(ReadChunks(encoded)), control => Assert.Equal(2, control.Dispose));
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        Assert.Equal(3, decoded.Frames.Count);
        for (int i = 0; i < 3; i++)
        {
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    bool stamped = y >= 4 + (i * 2) && y < 12 + (i * 2) && x >= 2 + (i * 6) && x < 10 + (i * 6);
                    Rgba32 want = stamped
                        ? new Rgba32((byte)(200 - (i * 40)), 90, (byte)(40 + (i * 60)), 255)
                        : background;
                    Assert.Equal(want, decoded.Frames[i][x, y]);
                }
            }
        }
    }

    [Fact]
    public void AFrameThatDemandsSourceBlendingIsNeverWrittenAsBlended()
    {
        using Image<Rgba32> source = ScatteredChanges(48, 48, 3);
        foreach (ImageFrame<Rgba32> frame in source.Frames)
        {
            frame.Metadata.GetPngMetadata().BlendMethod = PngBlendMethod.Source;
        }

        byte[] encoded = Encode(source, new PngEncoder());

        Assert.All(FrameControls(ReadChunks(encoded)), control => Assert.Equal(0, control.Blend));
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        AssertFramesEqual(source, decoded, "an animation pinned to SOURCE blending");
        Assert.All(decoded.Frames, frame => Assert.Equal(PngBlendMethod.Source, frame.Metadata.GetPngMetadata().BlendMethod));
    }

    [Fact]
    public void ScatteredChangesAreSentAsABlendedFrameWhenThatIsSmaller()
    {
        // Changes sprinkled over the whole canvas cannot be boxed into a small rectangle, but they can be
        // sent as a mostly transparent rectangle blended over what is already there.
        using Image<Rgba32> source = ScatteredChanges(64, 64, 3);

        byte[] encoded = Encode(source, new PngEncoder());

        List<FrameControl> controls = FrameControls(ReadChunks(encoded));
        Assert.Equal(0, controls[0].Blend);
        Assert.Contains(controls.Skip(1), control => control.Blend == 1);

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        AssertFramesEqual(source, decoded, "a blended animation");
    }

    // ----- Sub-rectangles -----

    [Fact]
    public void OnlyTheRectangleThatChangedIsWritten()
    {
        // A fixed 8x8 block at 20,12 changes colour every frame and nothing else moves.
        using Image<Rgba32> source = RecolouredBlock(64, 64, 4, 20, 12, 8);

        byte[] encoded = Encode(source, new PngEncoder());

        List<FrameControl> controls = FrameControls(ReadChunks(encoded));
        Assert.Equal(4, controls.Count);
        Assert.Equal(new FrameControl(0, 64, 64, 0, 0, 100, 1000, 0, 0), controls[0]);
        foreach (FrameControl control in controls.Skip(1))
        {
            Assert.Equal(20, control.XOffset);
            Assert.Equal(12, control.YOffset);
            Assert.Equal(8, control.Width);
            Assert.Equal(8, control.Height);
        }

        // The same canvas scrolled by a pixel each frame changes everywhere, so no rectangle can shrink it.
        using Image<Rgba32> everywhere = ScrollingPattern(64, 64, 4);
        byte[] whole = Encode(everywhere, new PngEncoder());
        Assert.All(FrameControls(ReadChunks(whole)), control => Assert.Equal(64, control.Width));
        Assert.True(
            encoded.Length * 4 < whole.Length,
            $"a sub-rectangle animation took {encoded.Length} bytes against {whole.Length} for one that changes everywhere.");
    }

    [Fact]
    public void AnUnchangedFrameCollapsesToASinglePixel()
    {
        using var source = new Image<Rgba32>(32, 32, new Rgba32(9, 90, 190, 255));
        source.Frames.AddFrame(source.Frames.RootFrame.PixelSpan);
        source.Frames.AddFrame(source.Frames.RootFrame.PixelSpan);

        byte[] encoded = Encode(source, new PngEncoder());

        List<FrameControl> controls = FrameControls(ReadChunks(encoded));
        Assert.Equal(3, controls.Count);
        Assert.Equal(32, controls[0].Width);
        foreach (FrameControl control in controls.Skip(1))
        {
            Assert.Equal(1, control.Width);
            Assert.Equal(1, control.Height);
            Assert.Equal(0, control.XOffset);
            Assert.Equal(0, control.YOffset);
        }

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        AssertFramesEqual(source, decoded, "three identical frames");
    }

    // ----- The still-image guard -----

    [Theory]
    [MemberData(nameof(StillConfigurations))]
    public void ASingleFrameFileCarriesNoAnimationChunks(PngColorType colorType, PngBitDepth bitDepth, PngInterlaceMethod interlace)
    {
        using Image<Rgba32> source = Photo(19, 13);

        byte[] encoded = Encode(source, new PngEncoder { ColorType = colorType, BitDepth = bitDepth, InterlaceMethod = interlace });

        List<string> names = ReadChunks(encoded).ConvertAll(chunk => chunk.Name);
        Assert.DoesNotContain("acTL", names);
        Assert.DoesNotContain("fcTL", names);
        Assert.DoesNotContain("fdAT", names);
        Assert.Equal("IHDR", names[0]);
        Assert.Equal("IEND", names[^1]);
        Assert.Equal(1, names.Count(name => name == "IDAT"));
        Assert.Equal(
            colorType == PngColorType.Palette ? new[] { "IHDR", "PLTE", "IDAT", "IEND" } : new[] { "IHDR", "IDAT", "IEND" },
            Skeleton(ReadChunks(encoded)));

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        Assert.Single(decoded.Frames);
        Assert.False(decoded.Metadata.GetPngMetadata().IsAnimated);
    }

    [Theory]
    [MemberData(nameof(StillConfigurations))]
    public void ASingleFrameFileIsByteIdenticalWhateverTheAnimationOptionsSay(
        PngColorType colorType, PngBitDepth bitDepth, PngInterlaceMethod interlace)
    {
        // This is the guard that protects every existing still-PNG test: the animation options must not be
        // able to reach a single-frame file at all, so the two encoders below have to agree byte for byte.
        using Image<Rgba32> source = Photo(19, 13);
        var plain = new PngEncoder { ColorType = colorType, BitDepth = bitDepth, InterlaceMethod = interlace };
        var animated = new PngEncoder
        {
            ColorType = colorType,
            BitDepth = bitDepth,
            InterlaceMethod = interlace,
            FrameDelay = 33,
            RepeatCount = 12,
        };

        Assert.Equal(Encode(source, plain), Encode(source, animated));
    }

    [Theory]
    [InlineData(PngColorType.Rgb, PngBitDepth.Bit8, PngInterlaceMethod.None)]
    [InlineData(PngColorType.Rgb, PngBitDepth.Bit16, PngInterlaceMethod.None)]
    [InlineData(PngColorType.RgbWithAlpha, PngBitDepth.Bit8, PngInterlaceMethod.None)]
    [InlineData(PngColorType.RgbWithAlpha, PngBitDepth.Bit16, PngInterlaceMethod.None)]
    [InlineData(PngColorType.Rgb, PngBitDepth.Bit8, PngInterlaceMethod.Adam7)]
    [InlineData(PngColorType.Rgb, PngBitDepth.Bit16, PngInterlaceMethod.Adam7)]
    [InlineData(PngColorType.RgbWithAlpha, PngBitDepth.Bit8, PngInterlaceMethod.Adam7)]
    [InlineData(PngColorType.RgbWithAlpha, PngBitDepth.Bit16, PngInterlaceMethod.Adam7)]
    public void ASingleFrameIdatHoldsExactlyTheFilteredScanlinesAndNothingElse(
        PngColorType colorType, PngBitDepth bitDepth, PngInterlaceMethod interlace)
    {
        // Byte-pinning the whole file would pin the deflate implementation too, and .NET 8 and .NET 10 do
        // not produce the same compressed bytes. The inflated IDAT is this library's own output, though, so
        // rebuilding it here from the source pixels pins the still pipeline exactly and portably.
        using Image<Rgba32> source = Photo(19, 13);
        var encoder = new PngEncoder
        {
            ColorType = colorType,
            BitDepth = bitDepth,
            InterlaceMethod = interlace,
            FilterMethod = PngFilterMethod.None,
        };

        byte[] encoded = Encode(source, encoder);

        byte[] expected = ExpectedScanlines(source, colorType, bitDepth, interlace);
        Assert.Equal(expected, Inflate(Concatenate(ReadChunks(encoded), "IDAT")));
    }

    [Fact]
    public void ExportingTheFirstFrameIsTheDocumentedOptOutFromWritingAnAnimation()
    {
        using Image<Rgba32> animation = MovingSquare(24, 24, 3);
        using Image<Rgba32> standalone = MovingSquare(24, 24, 1);

        using Image<Rgba32> exported = animation.Frames.ExportFrame(0);

        byte[] fromExport = Encode(exported, new PngEncoder());
        Assert.Equal(Encode(standalone, new PngEncoder()), fromExport);
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, Skeleton(ReadChunks(fromExport)));
    }

    [Fact]
    public void SaveAsPngOnAMultiFrameImageWritesAnApngRatherThanDroppingFrames()
    {
        using Image<Rgba32> source = MovingSquare(32, 24, 3);

        using var buffer = new MemoryStream();
        source.SaveAsPng(buffer);
        byte[] encoded = buffer.ToArray();

        List<Chunk> chunks = ReadChunks(encoded);
        Assert.Equal(1, chunks.Count(chunk => chunk.Name == "acTL"));
        Assert.Equal(3u, ReadAnimationControl(chunks).Frames);
        Assert.Equal(3, Image.Identify(encoded).FrameCount);

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        AssertFramesEqual(source, decoded, "an image saved with SaveAsPng");
    }

    // ----- The documented limitations -----

    [Theory]
    [InlineData(PngColorType.Palette, null)]
    [InlineData(PngColorType.Palette, PngBitDepth.Bit1)]
    [InlineData(PngColorType.Palette, PngBitDepth.Bit4)]
    [InlineData(PngColorType.Palette, PngBitDepth.Bit8)]
    [InlineData(PngColorType.Grayscale, PngBitDepth.Bit1)]
    [InlineData(PngColorType.Grayscale, PngBitDepth.Bit2)]
    [InlineData(PngColorType.Grayscale, PngBitDepth.Bit4)]
    public void AnimatedPaletteAndSubByteGrayscaleOutputIsRefused(PngColorType colorType, PngBitDepth? bitDepth)
    {
        using Image<Rgba32> animation = MovingSquare(16, 16, 3);
        using var single = new Image<Rgba32>(16, 16, new Rgba32(120, 60, 30, 255));
        single.Metadata.GetPngMetadata().IsAnimated = true;

        var encoder = new PngEncoder { ColorType = colorType, BitDepth = bitDepth };
        Assert.Throws<NotSupportedException>(() => Encode(animation, encoder));
        Assert.Throws<NotSupportedException>(() => Encode(single, encoder));

        // The same options on a still image are perfectly legal, which is what makes the refusal specific.
        using var still = new Image<Rgba32>(16, 16, new Rgba32(120, 60, 30, 255));
        Assert.NotEmpty(Encode(still, encoder));
    }

    [Theory]
    [InlineData(PngColorType.Rgb, PngBitDepth.Bit8, 2)]
    [InlineData(PngColorType.Rgb, PngBitDepth.Bit16, 2)]
    [InlineData(PngColorType.RgbWithAlpha, PngBitDepth.Bit8, 6)]
    [InlineData(PngColorType.RgbWithAlpha, PngBitDepth.Bit16, 6)]
    [InlineData(PngColorType.Grayscale, PngBitDepth.Bit8, 0)]
    [InlineData(PngColorType.Grayscale, PngBitDepth.Bit16, 0)]
    [InlineData(PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit8, 4)]
    [InlineData(PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit16, 4)]
    public void AnimatedOutputCoversTruecolourAndGrayscaleAtEightAndSixteenBits(
        PngColorType colorType, PngBitDepth bitDepth, int expectedIhdrColorType)
    {
        // Grayscale output goes through a luminance conversion, so a grayscale source is the only one that
        // can be required to come back unchanged for every colour type in the table.
        using Image<Rgba32> source = GrayAnimation(24, 18, 3);

        byte[] encoded = Encode(source, new PngEncoder { ColorType = colorType, BitDepth = bitDepth });

        byte[] header = ReadChunks(encoded)[0].Payload;
        Assert.Equal(expectedIhdrColorType, header[9]);
        Assert.Equal((int)bitDepth, header[8]);
        Assert.Equal(3u, ReadAnimationControl(ReadChunks(encoded)).Frames);

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        AssertFramesEqual(source, decoded, $"a {colorType}/{bitDepth} animation");
    }

    [Fact]
    public void AMultiFrameTruecolourImageGainsAnAlphaChannel()
    {
        // Sub-rectangle frames leave the pixels they do not touch transparent, which needs a colour type
        // that can express that, so an animation defaults to the alpha-bearing sibling of the still default.
        using Image<Rgba32> rgba = MovingSquare(16, 16, 3);
        using Image<Rgb24> source = rgba.CloneAs<Rgb24>();

        byte[] encoded = Encode(source, new PngEncoder());

        Assert.Equal(6, ReadChunks(encoded)[0].Payload[9]);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        Assert.Equal(3, decoded.Frames.Count);
    }

    [Fact]
    public void InterlacedAnimationsRoundTripButAreNotCrossCheckedWithPillow()
    {
        // Pillow 11.3 raises TypeError when it seeks past frame 0 of an Adam7-interlaced APNG, so this file
        // is deliberately not written into apng-roundtrip/: gen_apng.py --verify would report a failure that
        // says nothing about the encoder. This library's own decoder is the only oracle for the shape, and
        // the fixture corpus proves that decoder against hand-assembled interlaced APNGs independently.
        using Image<Rgba32> source = MovingSquare(24, 24, 3);

        byte[] encoded = Encode(source, new PngEncoder { InterlaceMethod = PngInterlaceMethod.Adam7 });

        List<Chunk> chunks = ReadChunks(encoded);
        Assert.Equal(1, chunks[0].Payload[12]);
        Assert.Equal(
            new[] { "IHDR", "acTL", "fcTL", "IDAT", "fcTL", "fdAT", "fcTL", "fdAT", "IEND" },
            Skeleton(chunks));
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4 }, SequenceNumbers(chunks));

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        AssertFramesEqual(source, decoded, "an interlaced animation");
        Assert.True(decoded.Metadata.GetPngMetadata().Interlaced);
    }

    // ----- Fixture round-trips -----

    [Theory]
    [MemberData(nameof(WellFormedFixtures))]
    public void RoundTripPreservesTheCommittedFramePixels(string name)
    {
        if (name == "(manifest missing)")
        {
            return;
        }

        ApngManifestEntry entry = Entry(name);
        using Image<Rgba32> source = Image.Load<Rgba32>(FixturePath.Read("apng/" + entry.File));
        Assert.Equal(entry.Frames, source.Frames.Count);

        byte[] encoded = Encode(source, new PngEncoder());
        Dump(entry.Name, encoded);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);

        // The ground truth is the committed per-frame RGBA dump, not this library's decode of its own file.
        byte[] truth = FixturePath.Read("apng/" + entry.Name + ".rgba");
        Assert.Equal(entry.Frames * entry.Width * entry.Height * 4, truth.Length);
        Assert.Equal(entry.Frames, decoded.Frames.Count);
        Assert.Equal(entry.Width, decoded.Width);
        Assert.Equal(entry.Height, decoded.Height);

        int offset = 0;
        for (int frame = 0; frame < entry.Frames; frame++)
        {
            for (int y = 0; y < entry.Height; y++)
            {
                for (int x = 0; x < entry.Width; x++)
                {
                    var want = new Rgba32(truth[offset], truth[offset + 1], truth[offset + 2], truth[offset + 3]);
                    Rgba32 got = decoded.Frames[frame][x, y];
                    if (!want.Equals(got))
                    {
                        Assert.Fail($"{entry.Name} frame {frame} pixel {x},{y} is {got} but the fixture says {want}.");
                    }

                    offset += 4;
                }
            }
        }

        PngMetadata metadata = decoded.Metadata.GetPngMetadata();
        Assert.True(metadata.IsAnimated);
        Assert.Equal(entry.RepeatCount, metadata.RepeatCount);
        Assert.Equal(entry.AnimateRootFrame, metadata.AnimateRootFrame);

        for (int frame = 0; frame < entry.Frames; frame++)
        {
            PngFrameMetadata before = source.Frames[frame].Metadata.GetPngMetadata();
            PngFrameMetadata after = decoded.Frames[frame].Metadata.GetPngMetadata();
            Assert.Equal(before.DisposalMethod, after.DisposalMethod);
            if (before.FrameDelay.Numerator != 0)
            {
                // A zero delay is the one field that does not survive; see
                // ADeliberateZeroDelayIsCurrentlyRewrittenToTheDefault.
                Assert.Equal(before.FrameDelay, after.FrameDelay);
            }
        }
    }

    // ----- Helpers -----

    /// <summary>One PNG chunk as the in-test walker sees it; the CRC has already been checked.</summary>
    private readonly record struct Chunk(string Name, byte[] Payload);

    /// <summary>The fields of one fcTL chunk, read back from the encoder's own bytes.</summary>
    private readonly record struct FrameControl(
        uint Sequence, int Width, int Height, int XOffset, int YOffset,
        ushort DelayNumerator, ushort DelayDenominator, byte Dispose, byte Blend);

    /// <summary>
    /// Walks a PNG file into its chunks, checking the signature, that the chunks tile the file exactly, and
    /// that every CRC is right. The decoder never verifies a CRC, so nothing else in the suite would notice
    /// the encoder emitting a bad one.
    /// </summary>
    private static List<Chunk> ReadChunks(byte[] file)
    {
        Assert.True(file.Length > Signature.Length, "the encoder produced a file too short to be a PNG.");
        Assert.Equal(Signature, file.Take(Signature.Length));

        var chunks = new List<Chunk>();
        int offset = Signature.Length;
        while (offset < file.Length)
        {
            Assert.True(offset + 12 <= file.Length, $"a chunk header at {offset} runs past the end of the file.");
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(offset));
            Assert.InRange(length, 0, file.Length - offset - 12);

            string name = Encoding.ASCII.GetString(file, offset + 4, 4);
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset + 8 + length));
            uint computed = Crc32(file.AsSpan(offset + 4, length + 4));
            Assert.True(stored == computed, $"chunk {name} at {offset} carries CRC {stored:X8}, but its bytes hash to {computed:X8}.");

            chunks.Add(new Chunk(name, file.AsSpan(offset + 8, length).ToArray()));
            offset += 12 + length;
        }

        Assert.Equal(file.Length, offset);
        Assert.NotEmpty(chunks);
        Assert.Equal("IEND", chunks[^1].Name);
        Assert.Equal(1, chunks.Count(chunk => chunk.Name == "IEND"));
        return chunks;
    }

    /// <summary>The PNG CRC-32 (reflected, polynomial 0xEDB88320), implemented here so the check is independent.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>The chunk names that decide the file's structure, with ancillary metadata (pHYs, tEXt, ...) dropped.</summary>
    private static string[] Skeleton(List<Chunk> chunks)
        => chunks.Where(chunk => chunk.Name is "IHDR" or "PLTE" or "IDAT" or "IEND" or "acTL" or "fcTL" or "fdAT")
            .Select(chunk => chunk.Name)
            .ToArray();

    private static (uint Frames, uint Plays) ReadAnimationControl(List<Chunk> chunks)
    {
        Chunk actl = Assert.Single(chunks, chunk => chunk.Name == "acTL");
        Assert.Equal(8, actl.Payload.Length);
        return (BinaryPrimitives.ReadUInt32BigEndian(actl.Payload), BinaryPrimitives.ReadUInt32BigEndian(actl.Payload.AsSpan(4)));
    }

    private static List<FrameControl> FrameControls(List<Chunk> chunks)
    {
        var controls = new List<FrameControl>();
        foreach (Chunk chunk in chunks)
        {
            if (chunk.Name != "fcTL")
            {
                continue;
            }

            Assert.Equal(26, chunk.Payload.Length);
            ReadOnlySpan<byte> payload = chunk.Payload;
            controls.Add(new FrameControl(
                BinaryPrimitives.ReadUInt32BigEndian(payload),
                (int)BinaryPrimitives.ReadUInt32BigEndian(payload[4..]),
                (int)BinaryPrimitives.ReadUInt32BigEndian(payload[8..]),
                (int)BinaryPrimitives.ReadUInt32BigEndian(payload[12..]),
                (int)BinaryPrimitives.ReadUInt32BigEndian(payload[16..]),
                BinaryPrimitives.ReadUInt16BigEndian(payload[20..]),
                BinaryPrimitives.ReadUInt16BigEndian(payload[22..]),
                payload[24],
                payload[25]));
        }

        return controls;
    }

    /// <summary>Every sequence number in file order: the fcTL chunks and the fdAT chunks share one series.</summary>
    private static uint[] SequenceNumbers(List<Chunk> chunks)
    {
        var numbers = new List<uint>();
        foreach (Chunk chunk in chunks)
        {
            if (chunk.Name is "fcTL" or "fdAT")
            {
                Assert.True(chunk.Payload.Length >= 4, $"{chunk.Name} is too short to hold a sequence number.");
                numbers.Add(BinaryPrimitives.ReadUInt32BigEndian(chunk.Payload));
            }
        }

        return numbers.ToArray();
    }

    private static byte[] Concatenate(List<Chunk> chunks, string name)
    {
        using var buffer = new MemoryStream();
        foreach (Chunk chunk in chunks)
        {
            if (chunk.Name == name)
            {
                buffer.Write(chunk.Payload);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// Rebuilds the filtered scanline stream a still truecolour image must deflate into: a zero filter byte
    /// per row followed by the raw samples, once per Adam7 pass when the file is interlaced.
    /// </summary>
    private static byte[] ExpectedScanlines(
        Image<Rgba32> image, PngColorType colorType, PngBitDepth bitDepth, PngInterlaceMethod interlace)
    {
        bool wide = bitDepth == PngBitDepth.Bit16;
        bool alpha = colorType == PngColorType.RgbWithAlpha;
        using var buffer = new MemoryStream();
        foreach ((int x0, int y0, int dx, int dy) in interlace == PngInterlaceMethod.Adam7 ? Adam7Passes : SinglePass)
        {
            int passWidth = x0 < image.Width ? ((image.Width - x0 + dx - 1) / dx) : 0;
            int passHeight = y0 < image.Height ? ((image.Height - y0 + dy - 1) / dy) : 0;
            if (passWidth == 0 || passHeight == 0)
            {
                continue;
            }

            for (int row = 0; row < passHeight; row++)
            {
                buffer.WriteByte(0);
                int y = y0 + (row * dy);
                for (int column = 0; column < passWidth; column++)
                {
                    Rgba32 pixel = image[x0 + (column * dx), y];
                    WriteSample(buffer, pixel.R, wide);
                    WriteSample(buffer, pixel.G, wide);
                    WriteSample(buffer, pixel.B, wide);
                    if (alpha)
                    {
                        WriteSample(buffer, pixel.A, wide);
                    }
                }
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Writes one sample; 16-bit output repeats the byte, which is the exact 8-to-16-bit widening v * 257.</summary>
    private static void WriteSample(Stream stream, byte value, bool wide)
    {
        stream.WriteByte(value);
        if (wide)
        {
            stream.WriteByte(value);
        }
    }

    private static byte[] Encode<TPixel>(Image<TPixel> image, PngEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var buffer = new MemoryStream();
        image.Save(buffer, encoder);
        return buffer.ToArray();
    }

    private static void AssertFramesEqual<TPixel>(Image<TPixel> expected, Image<Rgba32> actual, string what)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Frames.Count, actual.Frames.Count);
        for (int frame = 0; frame < expected.Frames.Count; frame++)
        {
            for (int y = 0; y < expected.Height; y++)
            {
                for (int x = 0; x < expected.Width; x++)
                {
                    Rgba32 want = expected.Frames[frame][x, y].ToRgba32();
                    Rgba32 got = actual.Frames[frame][x, y];
                    if (!want.Equals(got))
                    {
                        Assert.Fail($"{what}: frame {frame} pixel {x},{y} is {got} but should be {want}.");
                    }
                }
            }
        }
    }

    /// <summary>A canvas with a static background and one 8x8 square that moves right between frames.</summary>
    private static Image<Rgba32> MovingSquare(int width, int height, int frames)
    {
        Image<Rgba32>? owner = null;
        for (int i = 0; i < frames; i++)
        {
            var frame = new Image<Rgba32>(width, height, new Rgba32(20, 40, 60, 255));
            for (int y = 4; y < Math.Min(height, 12); y++)
            {
                for (int x = 4 + (i * 4); x < Math.Min(width, 12 + (i * 4)); x++)
                {
                    frame[x, y] = new Rgba32(240, (byte)(20 + (i * 30)), 10, 255);
                }
            }

            owner = Append(owner, frame);
        }

        return owner!;
    }

    /// <summary>A canvas where one fixed block changes colour every frame, so the changed rectangle is exactly that block.</summary>
    private static Image<Rgba32> RecolouredBlock(int width, int height, int frames, int left, int top, int size)
    {
        Image<Rgba32>? owner = null;
        for (int i = 0; i < frames; i++)
        {
            var frame = new Image<Rgba32>(width, height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    frame[x, y] = new Rgba32((byte)(30 + (x & 7)), (byte)(90 + (y & 7)), 160, 255);
                }
            }

            for (int y = top; y < top + size; y++)
            {
                for (int x = left; x < left + size; x++)
                {
                    frame[x, y] = new Rgba32(250, (byte)(10 + (i * 60)), 20, 255);
                }
            }

            owner = Append(owner, frame);
        }

        return owner!;
    }

    /// <summary>A detailed pattern scrolled by one pixel per frame, so every pixel differs from the frame before it.</summary>
    private static Image<Rgba32> ScrollingPattern(int width, int height, int frames)
    {
        Image<Rgba32>? owner = null;
        for (int i = 0; i < frames; i++)
        {
            var frame = new Image<Rgba32>(width, height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int u = x + i;
                    frame[x, y] = new Rgba32((byte)((u * 13) % 256), (byte)((y * 29) % 256), (byte)(((u * y) + 7) % 256), 255);
                }
            }

            owner = Append(owner, frame);
        }

        return owner!;
    }

    /// <summary>Frames whose differences are sprinkled over the whole canvas rather than confined to a box.</summary>
    private static Image<Rgba32> ScatteredChanges(int width, int height, int frames)
    {
        Image<Rgba32>? owner = null;
        for (int i = 0; i < frames; i++)
        {
            var frame = new Image<Rgba32>(width, height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool touched = i > 0 && (Scramble(x, y, i) & 31) == 0;
                    frame[x, y] = touched
                        ? new Rgba32(255, (byte)(20 * i), 0, 255)
                        : new Rgba32((byte)(30 + (x & 7)), (byte)(90 + (y & 7)), 160, 255);
                }
            }

            owner = Append(owner, frame);
        }

        return owner!;
    }

    /// <summary>A grayscale animation: every channel carries the same value, so no colour type in the table loses anything.</summary>
    private static Image<Rgba32> GrayAnimation(int width, int height, int frames)
    {
        Image<Rgba32>? owner = null;
        for (int i = 0; i < frames; i++)
        {
            var frame = new Image<Rgba32>(width, height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte level = (byte)(((x * 5) + (y * 3)) % 240);
                    if (y >= 4 && y < 10 && x >= 3 + (i * 5) && x < 9 + (i * 5))
                    {
                        level = (byte)(240 + i);
                    }

                    frame[x, y] = new Rgba32(level, level, level, 255);
                }
            }

            owner = Append(owner, frame);
        }

        return owner!;
    }

    /// <summary>A detailed still source: many colours and a varying alpha, so no colour type encodes it trivially.</summary>
    private static Image<Rgba32> Photo(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32(
                    (byte)((x * 13) % 256), (byte)((y * 29) % 256), (byte)(((x * y) + 7) % 256), (byte)(x % 2 == 0 ? 255 : 64));
            }
        }

        return image;
    }

    private static Image<Rgba32> Append(Image<Rgba32>? owner, Image<Rgba32> frame)
    {
        if (owner is null)
        {
            return frame;
        }

        owner.Frames.AddFrame(frame.Frames.RootFrame.PixelSpan);
        frame.Dispose();
        return owner;
    }

    private static int Scramble(int x, int y, int seed)
    {
        int value = (x * 73856093) ^ (y * 19349663) ^ (seed * 83492791);
        value ^= value >> 13;
        return value & int.MaxValue;
    }

    /// <summary>
    /// Writes one re-encoded fixture next to the test binaries so <c>gen_apng.py --verify</c> can decode it
    /// with Pillow. It is a development aid: a locked or read-only output directory must not fail a test.
    /// </summary>
    private static void Dump(string name, byte[] encoded)
    {
        try
        {
            Directory.CreateDirectory(RoundTripDirectory);
            File.WriteAllBytes(Path.Combine(RoundTripDirectory, name + ".png"), encoded);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ApngManifestEntry[] Manifest() => ManifestEntries.Value;

    private static ApngManifestEntry[] LoadManifest()
        => FixturePath.Exists("apng/manifest.json")
            ? JsonSerializer.Deserialize<ApngManifestEntry[]>(FixturePath.Read("apng/manifest.json")) ?? Array.Empty<ApngManifestEntry>()
            : Array.Empty<ApngManifestEntry>();

    private static ApngManifestEntry Entry(string name)
        => Manifest().FirstOrDefault(entry => entry.Name == name)
            ?? throw new InvalidOperationException($"The APNG manifest has no entry called '{name}'.");

    /// <summary>One entry of <c>Fixtures/apng/manifest.json</c>; only the fields this suite asserts on are read.</summary>
    private sealed class ApngManifestEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("frames")]
        public int Frames { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("repeat_count")]
        public uint RepeatCount { get; set; }

        [JsonPropertyName("animate_root_frame")]
        public bool AnimateRootFrame { get; set; }

        /// <summary>The exception a decoder must raise for this fixture, or <see langword="null"/> when it is well formed.</summary>
        [JsonPropertyName("expect")]
        public string? Expect { get; set; }
    }
}

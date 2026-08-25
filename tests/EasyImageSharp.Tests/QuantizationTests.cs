using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Processing.Dithering;
using EasyImageSharp.Processing.Quantization;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The quantizers (Wu, octree, fixed palette, web safe), the palette lookup and the <c>Quantize()</c>
/// operation. Every image is synthetic and deterministic, so the expectations are derived rather than
/// recorded: a palette that already holds every colour of an image must reproduce it exactly, palettes never
/// exceed their budget, and repeating a quantization must produce identical bytes.
/// </summary>
public class QuantizationTests
{
    // ----- Lossless reproduction of small-palette images -----

    public static TheoryData<string, int> SmallPaletteCases()
    {
        var data = new TheoryData<string, int>();
        foreach (string quantizer in new[] { "Wu", "Octree" })
        {
            foreach (int colors in new[] { 2, 7, 16, 64, 255, 256 })
            {
                data.Add(quantizer, colors);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SmallPaletteCases))]
    public void ImagesWithinTheBudgetAreQuantizedLosslessly(string quantizerName, int colorCount)
    {
        using Image<Rgba32> image = DistinctColors(colorCount, 32);
        IQuantizer quantizer = Create(quantizerName, new QuantizerOptions());

        using Image<Rgba32> result = image.Clone(ctx => ctx.Quantize(quantizer));

        AssertPixelsEqual(image, result);
    }

    [Theory]
    [InlineData("Wu")]
    [InlineData("Octree")]
    public void LosslessReproductionHoldsWithDitheringDisabled(string quantizerName)
    {
        using Image<Rgba32> image = DistinctColors(200, 25);
        IQuantizer quantizer = Create(quantizerName, new QuantizerOptions { Dither = null });

        using Image<Rgba32> result = image.Clone(ctx => ctx.Quantize(quantizer));

        AssertPixelsEqual(image, result);
    }

    [Theory]
    [InlineData("Wu")]
    [InlineData("Octree")]
    public void IndicesResolveThroughThePaletteToTheOriginalColours(string quantizerName)
    {
        using Image<Rgba32> image = DistinctColors(96, 16);
        IQuantizer<Rgba32> worker = Create(quantizerName, new QuantizerOptions()).CreatePixelSpecificQuantizer<Rgba32>();

        IndexedImageFrame<Rgba32> indexed = worker.QuantizeFrame(image.Frames.RootFrame);

        Assert.Equal(image.Width, indexed.Width);
        Assert.Equal(image.Height, indexed.Height);
        ReadOnlyMemory<Rgba32> palette = indexed.Palette;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                byte index = indexed.GetRowSpan(y)[x];
                Assert.True(index < palette.Length);
                Assert.Equal(image[x, y], palette.Span[index]);
            }
        }
    }

    // ----- Palette budgets -----

    [Theory]
    [InlineData("Wu", 2)]
    [InlineData("Wu", 3)]
    [InlineData("Wu", 16)]
    [InlineData("Wu", 100)]
    [InlineData("Wu", 256)]
    [InlineData("Octree", 2)]
    [InlineData("Octree", 3)]
    [InlineData("Octree", 16)]
    [InlineData("Octree", 100)]
    [InlineData("Octree", 256)]
    public void ThePaletteNeverExceedsMaxColors(string quantizerName, int maxColors)
    {
        using Image<Rgba32> image = Photo(48, 40);
        IQuantizer quantizer = Create(quantizerName, new QuantizerOptions { MaxColors = maxColors });

        IndexedImageFrame<Rgba32> indexed = quantizer.CreatePixelSpecificQuantizer<Rgba32>()
            .QuantizeFrame(image.Frames.RootFrame);

        Assert.InRange(indexed.Palette.Length, 1, maxColors);
        Assert.All(AllIndices(indexed), index => Assert.True(index < indexed.Palette.Length));
    }

    [Theory]
    [InlineData("Wu", 4)]
    [InlineData("Wu", 64)]
    [InlineData("Octree", 4)]
    [InlineData("Octree", 64)]
    public void ThePaletteStillFitsWhenTransparencyTakesAnEntry(string quantizerName, int maxColors)
    {
        using Image<Rgba32> image = PhotoWithTransparentBand(48, 40);
        IQuantizer quantizer = Create(quantizerName, new QuantizerOptions { MaxColors = maxColors });

        IndexedImageFrame<Rgba32> indexed = quantizer.CreatePixelSpecificQuantizer<Rgba32>()
            .QuantizeFrame(image.Frames.RootFrame);

        Assert.InRange(indexed.Palette.Length, 1, maxColors);
    }

    // ----- Transparency -----

    [Theory]
    [InlineData("Wu")]
    [InlineData("Octree")]
    public void TransparentPixelsShareASingleFullyTransparentEntry(string quantizerName)
    {
        using Image<Rgba32> image = PhotoWithTransparentBand(32, 24);
        IQuantizer quantizer = Create(quantizerName, new QuantizerOptions());

        IndexedImageFrame<Rgba32> indexed = quantizer.CreatePixelSpecificQuantizer<Rgba32>()
            .QuantizeFrame(image.Frames.RootFrame);

        Rgba32[] palette = indexed.Palette.ToArray();
        int transparentEntries = 0;
        int transparentIndex = -1;
        for (int i = 0; i < palette.Length; i++)
        {
            if (palette[i].A == 0)
            {
                transparentEntries++;
                transparentIndex = i;
            }
        }

        Assert.Equal(1, transparentEntries);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                bool transparent = image[x, y].A == 0;
                Assert.Equal(transparent, indexed.GetRowSpan(y)[x] == transparentIndex);
            }
        }
    }

    [Fact]
    public void PixelsBelowTheTransparencyThresholdBecomeTransparent()
    {
        using var image = new Image<Rgba32>(4, 1);
        image[0, 0] = new Rgba32(200, 10, 10, 255);
        image[1, 0] = new Rgba32(200, 10, 10, 63);  // Below the 64/255 default cutoff.
        image[2, 0] = new Rgba32(200, 10, 10, 64);  // At the cutoff: opaque.
        image[3, 0] = new Rgba32(200, 10, 10, 0);

        IndexedImageFrame<Rgba32> indexed = KnownQuantizers.Wu
            .CreatePixelSpecificQuantizer<Rgba32>()
            .QuantizeFrame(image.Frames.RootFrame);

        Rgba32[] palette = indexed.Palette.ToArray();
        byte[] row = indexed.GetRowSpan(0).ToArray();
        Assert.Equal(0, palette[row[1]].A);
        Assert.Equal(0, palette[row[3]].A);
        Assert.Equal(row[1], row[3]);
        Assert.NotEqual(0, palette[row[0]].A);
        Assert.NotEqual(0, palette[row[2]].A);
    }

    [Fact]
    public void TheTransparencyThresholdIsConfigurable()
    {
        using var image = new Image<Rgba32>(2, 1);
        image[0, 0] = new Rgba32(10, 20, 30, 200);
        image[1, 0] = new Rgba32(10, 20, 30, 255);

        var quantizer = new WuQuantizer(new QuantizerOptions { TransparencyThreshold = 1f, Dither = null });
        IndexedImageFrame<Rgba32> indexed = quantizer.CreatePixelSpecificQuantizer<Rgba32>()
            .QuantizeFrame(image.Frames.RootFrame);

        // With a threshold of 1 every pixel below fully opaque counts as transparent.
        Assert.Equal(0, indexed.Palette.Span[indexed.GetRowSpan(0)[0]].A);
    }

    [Fact]
    public void FullyTransparentImagesStillProduceAUsablePalette()
    {
        using var image = new Image<Rgba32>(5, 4);

        IndexedImageFrame<Rgba32> indexed = KnownQuantizers.Wu
            .CreatePixelSpecificQuantizer<Rgba32>()
            .QuantizeFrame(image.Frames.RootFrame);

        Assert.Equal(1, indexed.Palette.Length);
        Assert.Equal(0, indexed.Palette.Span[0].A);
        Assert.All(AllIndices(indexed), index => Assert.Equal(0, index));
    }

    // ----- Determinism -----

    [Theory]
    [InlineData("Wu")]
    [InlineData("Octree")]
    [InlineData("WebSafe")]
    public void QuantizationIsDeterministic(string quantizerName)
    {
        using Image<Rgba32> image = Photo(64, 64);

        (byte[] first, Rgba32[] firstPalette) = QuantizeToBytes(image, Create(quantizerName, new QuantizerOptions()));
        (byte[] second, Rgba32[] secondPalette) = QuantizeToBytes(image, Create(quantizerName, new QuantizerOptions()));

        Assert.Equal(firstPalette, secondPalette);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("Wu")]
    [InlineData("Octree")]
    public void QuantizationIsDeterministicForImagesLargeEnoughToRunInParallel(string quantizerName)
    {
        using Image<Rgba32> image = Photo(256, 256);

        (byte[] first, Rgba32[] firstPalette) = QuantizeToBytes(image, Create(quantizerName, new QuantizerOptions()));
        (byte[] second, Rgba32[] secondPalette) = QuantizeToBytes(image, Create(quantizerName, new QuantizerOptions()));

        Assert.Equal(firstPalette, secondPalette);
        Assert.Equal(first, second);
    }

    // ----- The web-safe palette -----

    [Fact]
    public void TheWebSafePaletteIsThe216ColourCube()
    {
        Color[] palette = WebSafePaletteQuantizer.Palette.ToArray();

        Assert.Equal(216, palette.Length);
        var seen = new HashSet<Rgba32>();
        foreach (Color color in palette)
        {
            Rgba32 rgba = color.ToRgba32();
            Assert.True(seen.Add(rgba));
            Assert.Equal(255, rgba.A);
            Assert.Equal(0, rgba.R % 0x33);
            Assert.Equal(0, rgba.G % 0x33);
            Assert.Equal(0, rgba.B % 0x33);
        }

        // Red-major ordering: index = 36r + 6g + b.
        Assert.Equal(new Rgba32(0, 0, 0), palette[0].ToRgba32());
        Assert.Equal(new Rgba32(0, 0, 0x33), palette[1].ToRgba32());
        Assert.Equal(new Rgba32(0, 0x33, 0), palette[6].ToRgba32());
        Assert.Equal(new Rgba32(0x33, 0, 0), palette[36].ToRgba32());
        Assert.Equal(new Rgba32(255, 255, 255), palette[215].ToRgba32());
    }

    [Fact]
    public void WebSafeColoursSurviveWebSafeQuantizationUnchanged()
    {
        Color[] palette = WebSafePaletteQuantizer.Palette.ToArray();
        using var image = new Image<Rgba32>(palette.Length, 1);
        for (int i = 0; i < palette.Length; i++)
        {
            image[i, 0] = palette[i].ToRgba32();
        }

        using Image<Rgba32> result = image.Clone(ctx => ctx.Quantize(KnownQuantizers.WebSafe));

        AssertPixelsEqual(image, result);
    }

    [Fact]
    public void WebSafeQuantizationSnapsToTheNearestCubeCorner()
    {
        using var image = new Image<Rgba32>(3, 1);
        image[0, 0] = new Rgba32(0x30, 0x02, 0xFD);
        image[1, 0] = new Rgba32(0x7F, 0x80, 0x81);
        image[2, 0] = new Rgba32(255, 255, 255);

        var quantizer = new WebSafePaletteQuantizer(new QuantizerOptions { Dither = null });
        using Image<Rgba32> result = image.Clone(ctx => ctx.Quantize(quantizer));

        Assert.Equal(new Rgba32(0x33, 0x00, 0xFF), result[0, 0]);
        Assert.Equal(new Rgba32(0x66, 0x99, 0x99), result[1, 0]);
        Assert.Equal(new Rgba32(0xFF, 0xFF, 0xFF), result[2, 0]);
    }

    // ----- Fixed palettes -----

    [Fact]
    public void APaletteQuantizerUsesTheSuppliedPaletteVerbatim()
    {
        Color[] palette = { Color.Black, Color.Red, Color.Lime, Color.Blue };
        var quantizer = new PaletteQuantizer(palette, new QuantizerOptions { Dither = null });

        IQuantizer<Rgba32> worker = quantizer.CreatePixelSpecificQuantizer<Rgba32>();

        Assert.Equal(4, worker.Palette.Length);
        Assert.Equal(new Rgba32(0, 0, 0), worker.Palette.Span[0]);
        Assert.Equal(new Rgba32(255, 0, 0), worker.Palette.Span[1]);
        Assert.Equal(new Rgba32(0, 255, 0), worker.Palette.Span[2]);
        Assert.Equal(new Rgba32(0, 0, 255), worker.Palette.Span[3]);
        Assert.Equal(4, quantizer.Palette.Length);
    }

    [Fact]
    public void AFixedPaletteMapsEveryColourToItsNearestEntry()
    {
        Color[] palette = { Color.Black, Color.White };
        var quantizer = new PaletteQuantizer(palette, new QuantizerOptions { Dither = null });
        using var image = new Image<Rgba32>(4, 1);
        image[0, 0] = new Rgba32(10, 10, 10);
        image[1, 0] = new Rgba32(250, 250, 250);
        image[2, 0] = new Rgba32(100, 100, 100);
        image[3, 0] = new Rgba32(200, 200, 200);

        using Image<Rgba32> result = image.Clone(ctx => ctx.Quantize(quantizer));

        Assert.Equal(new Rgba32(0, 0, 0), result[0, 0]);
        Assert.Equal(new Rgba32(255, 255, 255), result[1, 0]);
        Assert.Equal(new Rgba32(0, 0, 0), result[2, 0]);
        Assert.Equal(new Rgba32(255, 255, 255), result[3, 0]);
    }

    [Fact]
    public void AFixedPaletteIgnoresMaxColors()
    {
        Color[] palette = Enumerable.Range(0, 40).Select(i => new Color((byte)(i * 6), 0, 0)).ToArray();
        var quantizer = new PaletteQuantizer(palette, new QuantizerOptions { MaxColors = 4 });

        Assert.Equal(40, quantizer.CreatePixelSpecificQuantizer<Rgba32>().Palette.Length);
    }

    [Fact]
    public void AFixedPaletteWithATransparentEntryReceivesTransparentPixels()
    {
        Color[] palette = { Color.Transparent, Color.Red, Color.Blue };
        var quantizer = new PaletteQuantizer(palette, new QuantizerOptions { Dither = null });
        using var image = new Image<Rgba32>(2, 1);
        image[0, 0] = new Rgba32(250, 5, 5, 255);
        image[1, 0] = new Rgba32(250, 5, 5, 0);

        IndexedImageFrame<Rgba32> indexed = quantizer.CreatePixelSpecificQuantizer<Rgba32>()
            .QuantizeFrame(image.Frames.RootFrame);

        Assert.Equal(1, indexed.GetRowSpan(0)[0]);
        Assert.Equal(0, indexed.GetRowSpan(0)[1]);
    }

    [Fact]
    public void PaletteQuantizerRejectsEmptyAndOversizedPalettes()
    {
        Assert.Throws<ArgumentException>(() => new PaletteQuantizer(Array.Empty<Color>()));
        Assert.Throws<ArgumentException>(() => new PaletteQuantizer(new Color[257]));
        Assert.Throws<ArgumentNullException>(() => new PaletteQuantizer(new[] { Color.Red }, null!));
    }

    // ----- Colour matching modes -----

    [Fact]
    public void ExactMatchingFindsTheTrueNearestEntry()
    {
        Color[] palette = { new(0, 0, 0), new(8, 8, 8), new(255, 255, 255) };
        var quantizer = new PaletteQuantizer(
            palette, new QuantizerOptions { Dither = null, ColorMatchingMode = ColorMatchingMode.Exact });
        using var image = new Image<Rgba32>(1, 1);
        image[0, 0] = new Rgba32(7, 7, 7);

        using Image<Rgba32> result = image.Clone(ctx => ctx.Quantize(quantizer));

        Assert.Equal(new Rgba32(8, 8, 8), result[0, 0]);
    }

    [Theory]
    [InlineData(ColorMatchingMode.Exact)]
    [InlineData(ColorMatchingMode.Coarse)]
    public void BothMatchingModesProduceValidIndicesAndStayClose(ColorMatchingMode mode)
    {
        using Image<Rgba32> image = Photo(40, 30);
        var quantizer = new WuQuantizer(new QuantizerOptions { Dither = null, ColorMatchingMode = mode });

        IndexedImageFrame<Rgba32> indexed = quantizer.CreatePixelSpecificQuantizer<Rgba32>()
            .QuantizeFrame(image.Frames.RootFrame);

        Assert.All(AllIndices(indexed), index => Assert.True(index < indexed.Palette.Length));
        Assert.True(MeanAbsoluteError(image, indexed) < 8, "A 256-entry palette should keep the error small.");
    }

    // ----- GetQuantizedColor -----

    [Fact]
    public void GetQuantizedColorReturnsTheIndexAndTheMatchingPaletteEntry()
    {
        Color[] palette = { Color.Black, Color.White, Color.Red };
        var quantizer = new PaletteQuantizer(palette, new QuantizerOptions { Dither = null });
        IQuantizer<Rgba32> worker = quantizer.CreatePixelSpecificQuantizer<Rgba32>();

        byte index = worker.GetQuantizedColor(new Rgba32(240, 10, 10), out Rgba32 match);

        Assert.Equal(2, index);
        Assert.Equal(new Rgba32(255, 0, 0), match);
        Assert.Equal(match, worker.Palette.Span[index]);
    }

    [Fact]
    public void GetQuantizedColorFailsBeforeAPaletteExists()
    {
        IQuantizer<Rgba32> worker = KnownQuantizers.Wu.CreatePixelSpecificQuantizer<Rgba32>();

        Assert.Throws<InvalidOperationException>(() => worker.GetQuantizedColor(new Rgba32(1, 2, 3), out _));
    }

    // ----- Shared palettes across frames -----

    [Fact]
    public void AddPaletteColorsBuildsOnePaletteForSeveralFrames()
    {
        using var red = new Image<Rgba32>(8, 8, new Rgba32(255, 0, 0));
        using var green = new Image<Rgba32>(8, 8, new Rgba32(0, 255, 0));
        IQuantizer<Rgba32> worker = new WuQuantizer(new QuantizerOptions { Dither = null })
            .CreatePixelSpecificQuantizer<Rgba32>();

        worker.AddPaletteColors(red.Frames.RootFrame);
        worker.AddPaletteColors(green.Frames.RootFrame);

        Assert.Equal(2, worker.Palette.Length);
        IndexedImageFrame<Rgba32> redIndexed = worker.QuantizeFrame(red.Frames.RootFrame);
        IndexedImageFrame<Rgba32> greenIndexed = worker.QuantizeFrame(green.Frames.RootFrame);
        Assert.Equal(new Rgba32(255, 0, 0), redIndexed.Palette.Span[redIndexed.GetRowSpan(0)[0]]);
        Assert.Equal(new Rgba32(0, 255, 0), greenIndexed.Palette.Span[greenIndexed.GetRowSpan(0)[0]]);
    }

    [Fact]
    public void QuantizingARegionOnlyReadsThatRegion()
    {
        using var image = new Image<Rgba32>(8, 8, new Rgba32(10, 20, 30));
        for (int x = 4; x < 8; x++)
        {
            image[x, 0] = new Rgba32(200, 100, 50);
        }

        IQuantizer<Rgba32> worker = new WuQuantizer(new QuantizerOptions { Dither = null })
            .CreatePixelSpecificQuantizer<Rgba32>();
        IndexedImageFrame<Rgba32> indexed = worker.QuantizeFrame(image.Frames.RootFrame, new Rectangle(0, 1, 4, 4));

        Assert.Equal(4, indexed.Width);
        Assert.Equal(4, indexed.Height);
        Assert.Equal(1, indexed.Palette.Length);
        Assert.Equal(new Rgba32(10, 20, 30), indexed.Palette.Span[0]);
    }

    [Theory]
    [InlineData(-1, 0, 4, 4)]
    [InlineData(0, -1, 4, 4)]
    [InlineData(0, 0, 0, 4)]
    [InlineData(0, 0, 4, 0)]
    [InlineData(5, 0, 4, 4)]
    [InlineData(0, 5, 4, 4)]
    public void RegionsOutsideTheFrameAreRejected(int x, int y, int width, int height)
    {
        using var image = new Image<Rgba32>(8, 8);
        IQuantizer<Rgba32> worker = KnownQuantizers.Wu.CreatePixelSpecificQuantizer<Rgba32>();
        var bounds = new Rectangle(x, y, width, height);

        Assert.Throws<ArgumentOutOfRangeException>(() => worker.QuantizeFrame(image.Frames.RootFrame, bounds));
        Assert.Throws<ArgumentOutOfRangeException>(() => worker.AddPaletteColors(image.Frames.RootFrame, bounds));
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        IQuantizer<Rgba32> worker = KnownQuantizers.Wu.CreatePixelSpecificQuantizer<Rgba32>();

        Assert.Throws<ArgumentNullException>(() => worker.QuantizeFrame(null!));
        Assert.Throws<ArgumentNullException>(() => worker.AddPaletteColors(null!));
        Assert.Throws<ArgumentNullException>(() => new WuQuantizer(null!));
        Assert.Throws<ArgumentNullException>(() => new OctreeQuantizer(null!));
        Assert.Throws<ArgumentNullException>(() => new WebSafePaletteQuantizer(null!));
        Assert.Throws<ArgumentNullException>(() => KnownQuantizers.Wu.CreatePixelSpecificQuantizer<Rgba32>(null!));

        using var image = new Image<Rgba32>(2, 2);
        Assert.Throws<ArgumentNullException>(() => image.Mutate(ctx => ctx.Quantize(null!)));
    }

    // ----- Options -----

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(257)]
    public void MaxColorsIsRangeChecked(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuantizerOptions { MaxColors = value });
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    [InlineData(float.NaN)]
    public void DitherScaleAndTransparencyThresholdAreRangeChecked(float value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuantizerOptions { DitherScale = value });
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuantizerOptions { TransparencyThreshold = value });
    }

    [Fact]
    public void OptionsHaveTheDocumentedDefaults()
    {
        var options = new QuantizerOptions();

        Assert.Equal(256, options.MaxColors);
        Assert.Same(KnownDitherings.FloydSteinberg, options.Dither);
        Assert.Equal(1f, options.DitherScale);
        Assert.Equal(ColorMatchingMode.Exact, options.ColorMatchingMode);
        Assert.Equal(QuantizerOptions.DefaultTransparencyThreshold, options.TransparencyThreshold);
        Assert.Equal(256, KnownQuantizers.Wu.Options.MaxColors);
        Assert.Equal(256, KnownQuantizers.Octree.Options.MaxColors);
        Assert.Equal(256, KnownQuantizers.WebSafe.Options.MaxColors);
    }

    [Fact]
    public void QuantizersCanBeCreatedWithPerCallOptions()
    {
        IQuantizer<Rgba32> worker = KnownQuantizers.Wu.CreatePixelSpecificQuantizer<Rgba32>(
            new QuantizerOptions { MaxColors = 8, Dither = null });

        using Image<Rgba32> image = Photo(32, 32);
        IndexedImageFrame<Rgba32> indexed = worker.QuantizeFrame(image.Frames.RootFrame);

        Assert.Equal(8, worker.Options.MaxColors);
        Assert.InRange(indexed.Palette.Length, 1, 8);
        Assert.Equal(256, KnownQuantizers.Wu.Options.MaxColors); // The shared instance is untouched.
    }

    [Fact]
    public void DitherScaleZeroBehavesLikeNoDither()
    {
        using Image<Rgba32> image = Photo(32, 32);

        using Image<Rgba32> scaled = image.Clone(ctx => ctx.Quantize(
            new WuQuantizer(new QuantizerOptions { MaxColors = 8, DitherScale = 0f })));
        using Image<Rgba32> undithered = image.Clone(ctx => ctx.Quantize(
            new WuQuantizer(new QuantizerOptions { MaxColors = 8, Dither = null })));

        AssertPixelsEqual(scaled, undithered);
    }

    // ----- The Quantize operation -----

    [Fact]
    public void QuantizeReplacesPixelsWithPaletteColoursOnEveryFrame()
    {
        using var image = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50));
        ImageFrame<Rgba32> second = image.Frames.CreateFrame(8, 8);
        for (int y = 0; y < 8; y++)
        {
            second.GetRowSpan(y).Fill(new Rgba32(10, 240, 90));
        }

        image.Mutate(ctx => ctx.Quantize(new WuQuantizer(new QuantizerOptions { MaxColors = 4, Dither = null })));

        Assert.Equal(new Rgba32(200, 100, 50), image.Frames[0][0, 0]);
        Assert.Equal(new Rgba32(10, 240, 90), image.Frames[1][0, 0]);
    }

    [Fact]
    public void TheDefaultQuantizeOverloadUsesWu()
    {
        using Image<Rgba32> image = Photo(32, 32);

        using Image<Rgba32> viaDefault = image.Clone(ctx => ctx.Quantize());
        using Image<Rgba32> viaWu = image.Clone(ctx => ctx.Quantize(KnownQuantizers.Wu));

        AssertPixelsEqual(viaDefault, viaWu);
    }

    [Fact]
    public void QuantizeKeepsThePixelFormatAndSize()
    {
        using Image<Rgb24> image = TestImages.Gradient(24, 24);

        image.Mutate(ctx => ctx.Quantize(new WuQuantizer(new QuantizerOptions { MaxColors = 16 })));

        Assert.Equal(24, image.Width);
        Assert.Equal(24, image.Height);
    }

    [Fact]
    public void QuantizingReducesTheDistinctColourCountToTheBudget()
    {
        using Image<Rgba32> image = Photo(64, 64);
        Assert.True(DistinctColorCount(image) > 256);

        using Image<Rgba32> result = image.Clone(ctx => ctx.Quantize(
            new WuQuantizer(new QuantizerOptions { MaxColors = 32 })));

        Assert.InRange(DistinctColorCount(result), 1, 32);
    }

    // ----- IndexedImageFrame -----

    [Fact]
    public void IndexedFrameRowAccessIsBoundsChecked()
    {
        using Image<Rgba32> image = Photo(4, 3);
        IndexedImageFrame<Rgba32> indexed = KnownQuantizers.Wu.CreatePixelSpecificQuantizer<Rgba32>()
            .QuantizeFrame(image.Frames.RootFrame);

        Assert.Equal(4, indexed.GetRowSpan(0).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => indexed.GetRowSpan(-1).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => indexed.GetRowSpan(3).Length);
    }

    // ----- Helpers -----

    private static IQuantizer Create(string name, QuantizerOptions options) => name switch
    {
        "Wu" => new WuQuantizer(options),
        "Octree" => new OctreeQuantizer(options),
        "WebSafe" => new WebSafePaletteQuantizer(options),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown quantizer."),
    };

    /// <summary>An image using exactly <paramref name="colorCount"/> well-separated colours, each repeated.</summary>
    private static Image<Rgba32> DistinctColors(int colorCount, int width)
    {
        int height = Math.Max(1, ((colorCount * 3) + width - 1) / width);
        var image = new Image<Rgba32>(width, height);
        var colors = new Rgba32[colorCount];
        for (int i = 0; i < colorCount; i++)
        {
            // Spread the colours over the cube; the multipliers are odd, so no two indices collide below 256.
            colors[i] = new Rgba32((byte)((i * 37) % 256), (byte)((i * 91) % 256), (byte)((i * 151) % 256));
        }

        Assert.Equal(colorCount, colors.Distinct().Count());
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = colors[((y * width) + x) % colorCount];
            }
        }

        return image;
    }

    /// <summary>A smooth image with far more than 256 distinct colours.</summary>
    private static Image<Rgba32> Photo(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32(
                    (byte)((x * 255) / Math.Max(1, width - 1)),
                    (byte)((y * 255) / Math.Max(1, height - 1)),
                    (byte)(((x + y) * 255) / Math.Max(1, width + height - 2)));
            }
        }

        return image;
    }

    private static Image<Rgba32> PhotoWithTransparentBand(int width, int height)
    {
        Image<Rgba32> image = Photo(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width / 4; x++)
            {
                image[x, y] = default;
            }
        }

        return image;
    }

    private static (byte[] Indices, Rgba32[] Palette) QuantizeToBytes(Image<Rgba32> image, IQuantizer quantizer)
    {
        IndexedImageFrame<Rgba32> indexed = quantizer.CreatePixelSpecificQuantizer<Rgba32>()
            .QuantizeFrame(image.Frames.RootFrame);
        var indices = new byte[indexed.Width * indexed.Height];
        for (int y = 0; y < indexed.Height; y++)
        {
            indexed.GetRowSpan(y).CopyTo(indices.AsSpan(y * indexed.Width));
        }

        return (indices, indexed.Palette.ToArray());
    }

    private static List<byte> AllIndices<TPixel>(IndexedImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var indices = new List<byte>(frame.Width * frame.Height);
        for (int y = 0; y < frame.Height; y++)
        {
            indices.AddRange(frame.GetRowSpan(y).ToArray());
        }

        return indices;
    }

    private static double MeanAbsoluteError(Image<Rgba32> source, IndexedImageFrame<Rgba32> indexed)
    {
        Rgba32[] palette = indexed.Palette.ToArray();
        long total = 0;
        for (int y = 0; y < indexed.Height; y++)
        {
            for (int x = 0; x < indexed.Width; x++)
            {
                Rgba32 a = source[x, y];
                Rgba32 b = palette[indexed.GetRowSpan(y)[x]];
                total += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            }
        }

        return total / (indexed.Width * indexed.Height * 3.0);
    }

    private static int DistinctColorCount(Image<Rgba32> image)
    {
        var seen = new HashSet<Rgba32>();
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                seen.Add(image[x, y]);
            }
        }

        return seen.Count;
    }

    private static void AssertPixelsEqual(Image<Rgba32> expected, Image<Rgba32> actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                if (expected[x, y] != actual[x, y])
                {
                    Assert.Fail($"Pixel ({x}, {y}): expected {expected[x, y]}, got {actual[x, y]}.");
                }
            }
        }
    }
}

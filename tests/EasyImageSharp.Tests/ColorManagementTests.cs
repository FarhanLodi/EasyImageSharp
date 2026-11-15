using System.Numerics;
using EasyImageSharp.ColorSpaces;
using EasyImageSharp.Formats;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests;

/// <summary>
/// Conversion from embedded ICC profiles to sRGB, at load time and as a processing operation. Accuracy is checked
/// against LittleCMS (through Pillow's ImageCms) on the Fixtures/colormanagement test card; see gen_colormanagement.py
/// for how the profiles and references are produced.
/// </summary>
public class ColorManagementTests
{
    /// <summary>
    /// The documented accuracy of the managed engine against LittleCMS on 8-bit output: no channel more than one code
    /// value apart, CIE76 delta E at most 1.5 on any pixel and at most 0.05 on average over the card. Both sides round
    /// to 8 bits from slightly different float pipelines (single precision here; LittleCMS's own adaptation of its
    /// built-in sRGB), so a few percent of pixels land one code value apart. The worst measured pixel (Adobe RGB
    /// 94,178,220) sits on two rounding boundaries at once - 179.506 and 222.489 in float64 with this library's
    /// constants - and comes out 0,180,222 here and 0,179,223 in LittleCMS, delta E 1.30. Measured maxima: Display P3
    /// 0.88, Adobe RGB 1.30, ProPhoto 0.81; means 0.012, 0.017, 0.008.
    /// </summary>
    private const double MaxDeltaE = 1.5;
    private const double MaxMeanDeltaE = 0.05;

    /// <summary>No channel of any pixel may differ from LittleCMS by more than one 8-bit code value.</summary>
    private const int MaxCodeValueStep = 1;

    private static readonly DecoderOptions ConvertOptions = new() { ColorProfileHandling = ColorProfileHandling.ConvertToSrgb };

    private static readonly DecoderOptions StrictOptions = new()
    {
        ColorProfileHandling = ColorProfileHandling.ConvertToSrgb,
        UnsupportedColorProfileHandling = UnsupportedColorProfileHandling.Throw,
    };

    private readonly ITestOutputHelper output;

    public ColorManagementTests(ITestOutputHelper output) => this.output = output;

    [Theory]
    [InlineData("display_p3_v4.icc", "display_p3_to_srgb.png")]
    [InlineData("adobe_rgb_v2.icc", "adobe_rgb_to_srgb.png")]
    [InlineData("prophoto_v4.icc", "prophoto_to_srgb.png")]
    public void ConvertToSrgb_MatchesLittleCmsWithinDeltaE(string profileFile, string referenceFile)
    {
        using Image<Rgba32> card = LoadCard();
        card.Metadata.IccProfile = new IccProfile(FixturePath.Read($"colormanagement/{profileFile}"));
        card.Mutate(ctx => ctx.ConvertToSrgb());

        using Image<Rgba32> reference = Image.Load<Rgba32>(FixturePath.Read($"colormanagement/{referenceFile}"));
        this.AssertWithinDeltaE(reference, card, referenceFile);
        Assert.Null(card.Metadata.IccProfile);
        Assert.True(card.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? record));
        Assert.Equal(ColorProfileConversionStatus.Converted, record.Status);
        Assert.NotNull(record.SourceProfileDescription);
        Assert.Null(record.Detail);
    }

    [Theory]
    [InlineData("display_p3_tagged.png", "display_p3_to_srgb.png")]
    [InlineData("adobe_rgb_tagged.png", "adobe_rgb_to_srgb.png")]
    [InlineData("prophoto_tagged.png", "prophoto_to_srgb.png")]
    [InlineData("display_p3_tagged.tif", "display_p3_to_srgb.png")]
    public void LoadWithConvertToSrgb_EqualsTheProcessingOperationAndRemovesTheProfile(string taggedFile, string referenceFile)
    {
        byte[] tagged = FixturePath.Read($"colormanagement/{taggedFile}");
        using Image<Rgba32> loaded = Image.Load<Rgba32>(tagged, ConvertOptions);
        using Image<Rgba32> processed = Image.Load<Rgba32>(tagged);
        Assert.NotNull(processed.Metadata.IccProfile);
        processed.Mutate(ctx => ctx.ConvertToSrgb());

        Assert.Equal(Pixels(processed), Pixels(loaded));
        Assert.Null(loaded.Metadata.IccProfile);
        Assert.Null(loaded.Frames.RootFrame.Metadata.IccProfile);
        Assert.True(loaded.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? record));
        Assert.Equal(ColorProfileConversionStatus.Converted, record.Status);
        Assert.True(loaded.Frames.RootFrame.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? frameRecord));
        Assert.Equal(ColorProfileConversionStatus.Converted, frameRecord.Status);

        using Image<Rgba32> reference = Image.Load<Rgba32>(FixturePath.Read($"colormanagement/{referenceFile}"));
        this.AssertWithinDeltaE(reference, loaded, taggedFile);
    }

    [Fact]
    public async Task LoadAsync_AppliesTheSameConversion()
    {
        byte[] tagged = FixturePath.Read("colormanagement/display_p3_tagged.png");
        using Image<Rgba32> sync = Image.Load<Rgba32>(tagged, ConvertOptions);
        using var stream = new MemoryStream(tagged);
        using Image<Rgba32> async = await Image.LoadAsync<Rgba32>(stream, ConvertOptions);
        Assert.Equal(Pixels(sync), Pixels(async));
        Assert.Null(async.Metadata.IccProfile);
    }

    [Fact]
    public void DefaultOptions_LeavePixelsAndProfileUntouched()
    {
        byte[] tagged = FixturePath.Read("colormanagement/display_p3_tagged.png");
        using Image<Rgba32> card = LoadCard();
        using Image<Rgba32> loaded = Image.Load<Rgba32>(tagged);
        using Image<Rgba32> explicitPreserve = Image.Load<Rgba32>(tagged, new DecoderOptions { ColorProfileHandling = ColorProfileHandling.Preserve });

        Assert.Equal(ColorProfileHandling.Preserve, DecoderOptions.Default.ColorProfileHandling);
        Assert.Equal(UnsupportedColorProfileHandling.Keep, DecoderOptions.Default.UnsupportedColorProfileHandling);
        Assert.Equal(Pixels(card), Pixels(loaded));
        Assert.Equal(Pixels(card), Pixels(explicitPreserve));
        Assert.Equal(FixturePath.Read("colormanagement/display_p3_v4.icc"), loaded.Metadata.IccProfile!.ToByteArray());
        Assert.False(loaded.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? _));
    }

    [Fact]
    public void UntaggedImage_IsReportedAsNoProfileAndUnchanged()
    {
        byte[] untagged = FixturePath.Read("colormanagement/src.png");
        using Image<Rgba32> plain = Image.Load<Rgba32>(untagged);
        using Image<Rgba32> converted = Image.Load<Rgba32>(untagged, StrictOptions);
        Assert.Equal(Pixels(plain), Pixels(converted));
        Assert.True(converted.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? record));
        Assert.Equal(ColorProfileConversionStatus.NoProfile, record.Status);
    }

    [Theory]
    [InlineData("srgb_v2_table.icc")]
    [InlineData("srgb_v4_para.icc")]
    [InlineData("srgb_v2_with_lut.icc")]
    public void SrgbProfiles_AreRecognisedAndDroppedWithoutTouchingPixels(string profileFile)
    {
        using Image<Rgba32> card = LoadCard();
        byte[] before = Pixels(card);
        card.Metadata.IccProfile = new IccProfile(FixturePath.Read($"colormanagement/{profileFile}"));
        card.Mutate(ctx => ctx.ConvertToSrgb(UnsupportedColorProfileHandling.Throw));

        Assert.Equal(before, Pixels(card));
        Assert.Null(card.Metadata.IccProfile);
        Assert.True(card.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? record));
        Assert.Equal(ColorProfileConversionStatus.AlreadySrgb, record.Status);
    }

    [Fact]
    public void SrgbTableProfile_ReallyIsWithinHalfACodeValueOfLittleCms()
    {
        // The sRGB-equivalence shortcut is only honest if converting through the profile would not move pixels:
        // LittleCMS's own conversion of the table profile to its built-in sRGB stays within one code value.
        using Image<Rgba32> card = LoadCard();
        using Image<Rgba32> reference = Image.Load<Rgba32>(FixturePath.Read("colormanagement/srgb_table_to_srgb.png"));
        byte[] expected = Pixels(reference), actual = Pixels(card);
        int worst = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(expected[i] - actual[i]));
        }

        Assert.InRange(worst, 0, 1);
        Assert.True(IccMatrixProfile.Create(new IccProfile(FixturePath.Read("colormanagement/srgb_v2_table.icc"))).IsSrgbEquivalent);
        Assert.False(IccMatrixProfile.Create(new IccProfile(FixturePath.Read("colormanagement/display_p3_v4.icc"))).IsSrgbEquivalent);
    }

    [Theory]
    [InlineData("gray_tagged.png")]
    [InlineData("p3_lut_tagged.png")]
    public void UnsupportedProfiles_AreKeptAndReportedByDefault_AndThrowWhenStrict(string taggedFile)
    {
        byte[] tagged = FixturePath.Read($"colormanagement/{taggedFile}");
        using Image<Rgba32> card = LoadCard();
        using Image<Rgba32> loaded = Image.Load<Rgba32>(tagged, ConvertOptions);

        Assert.Equal(Pixels(card), Pixels(loaded));
        Assert.NotNull(loaded.Metadata.IccProfile);
        Assert.True(loaded.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? record));
        Assert.Equal(ColorProfileConversionStatus.Unsupported, record.Status);
        Assert.False(string.IsNullOrEmpty(record.Detail));
        Assert.NotNull(record.SourceProfileDescription);

        Assert.Throws<NotSupportedException>(() => Image.Load<Rgba32>(tagged, StrictOptions));
        using Image<Rgba32> processed = Image.Load<Rgba32>(tagged);
        Assert.Throws<NotSupportedException>(() => processed.Mutate(ctx => ctx.ConvertToSrgb(UnsupportedColorProfileHandling.Throw)));
        Assert.Equal(Pixels(card), Pixels(processed));
        Assert.NotNull(processed.Metadata.IccProfile);
    }

    [Fact]
    public void CmykAndMalformedProfiles_FollowThePolicy()
    {
        byte[] cmyk = FixturePath.Read("colormanagement/display_p3_v4.icc");
        "CMYK"u8.CopyTo(cmyk.AsSpan(16));
        using Image<Rgba32> card = LoadCard();
        card.Metadata.IccProfile = new IccProfile(cmyk);
        card.Mutate(ctx => ctx.ConvertToSrgb());
        Assert.True(card.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? record));
        Assert.Equal(ColorProfileConversionStatus.Unsupported, record.Status);
        Assert.StartsWith("Unsupported: ", record.ToString(), StringComparison.Ordinal);
        Assert.Equal("NoProfile", new ColorProfileConversionMetadata().ToString());
        Assert.NotNull(card.Metadata.IccProfile);

        using Image<Rgba32> garbage = LoadCard();
        byte[] before = Pixels(garbage);
        garbage.Metadata.IccProfile = new IccProfile(new byte[] { 1, 2, 3, 4, 5 });
        garbage.Mutate(ctx => ctx.ConvertToSrgb());
        Assert.True(garbage.Metadata.TryGetFormatMetadata(out record));
        Assert.Equal(ColorProfileConversionStatus.Invalid, record.Status);
        Assert.Equal(before, Pixels(garbage));
        Assert.Throws<InvalidImageContentException>(() => garbage.Mutate(ctx => ctx.ConvertToSrgb(UnsupportedColorProfileHandling.Throw)));
    }

    [Fact]
    public void MalformedEmbeddedProfile_WithStrictLoad_KeepsTheDecoderExceptionContract()
    {
        // Overwrite the colorant tag type of the embedded profile: the PNG still decodes, the profile is malformed.
        byte[] tagged = FixturePath.Read("colormanagement/display_p3_tagged.png");
        using Image<Rgba32> image = Image.Load<Rgba32>(tagged);
        byte[] profile = image.Metadata.IccProfile!.ToByteArray();
        int rXyz = FindTag(profile, "rXYZ");
        "junk"u8.CopyTo(profile.AsSpan(rXyz));
        image.Metadata.IccProfile = new IccProfile(profile);
        using var encoded = new MemoryStream();
        image.SaveAsPng(encoded);

        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(encoded.ToArray(), StrictOptions));
        using Image<Rgba32> lenient = Image.Load<Rgba32>(encoded.ToArray(), ConvertOptions);
        Assert.True(lenient.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? record));
        Assert.Equal(ColorProfileConversionStatus.Invalid, record.Status);
    }

    [Fact]
    public void EightBitTablePath_IsBitExactWithThePerPixelPublicConversion()
    {
        IccMatrixProfile profile = IccMatrixProfile.Create(new IccProfile(FixturePath.Read("colormanagement/display_p3_v4.icc")));
        using Image<Rgba32> card = LoadCard();
        var expected = new Rgba32[card.Width * card.Height];
        card.CopyPixelDataTo(expected);
        for (int i = 0; i < expected.Length; i++)
        {
            Vector4 v = expected[i].ToScaledVector4();
            expected[i] = Rgba32.FromScaledVector4(new Vector4(profile.ToSrgb(new Vector3(v.X, v.Y, v.Z)), v.W));
        }

        card.Mutate(ctx => ctx.ConvertToSrgb(profile));
        var actual = new Rgba32[expected.Length];
        card.CopyPixelDataTo(actual);
        Assert.Equal(expected, actual);

        // Every 8-bit layout takes the same table path and agrees with Rgba32.
        using Image<Bgra32> bgra = LoadCard().CloneAs<Bgra32>();
        bgra.Mutate(ctx => ctx.ConvertToSrgb(profile));
        using Image<Rgb24> rgb = LoadCard().CloneAs<Rgb24>();
        rgb.Mutate(ctx => ctx.ConvertToSrgb(profile));
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.Equal(actual[i], bgra[i % card.Width, i / card.Width].ToRgba32());
            Assert.Equal(actual[i], rgb[i % card.Width, i / card.Width].ToRgba32());
        }
    }

    [Fact]
    public void WidePixelFormats_KeepPrecisionAndFloatKeepsOutOfGamutValues()
    {
        byte[] tagged = FixturePath.Read("colormanagement/display_p3_tagged.png");
        IccMatrixProfile profile = IccMatrixProfile.Create(new IccProfile(FixturePath.Read("colormanagement/display_p3_v4.icc")));
        using Image<RgbaVector> rawVector = Image.Load<RgbaVector>(tagged);
        using Image<RgbaVector> vector = Image.Load<RgbaVector>(tagged, ConvertOptions);
        using Image<Rgba64> rawWide = Image.Load<Rgba64>(tagged);
        using Image<Rgba64> wide = Image.Load<Rgba64>(tagged, ConvertOptions);
        using Image<Rgba32> narrow = Image.Load<Rgba32>(tagged, ConvertOptions);
        using Image<Rgba32> card = LoadCard();

        for (int y = 0; y < card.Height; y++)
        {
            for (int x = 0; x < card.Width; x++)
            {
                // Each format converts exactly what it decoded, through the public per-pixel conversion.
                Vector4 source = rawVector[x, y].ToScaledVector4();
                Vector3 exact = profile.ToSrgb(new Vector3(source.X, source.Y, source.Z));
                RgbaVector v = vector[x, y];
                Assert.Equal(exact, new Vector3(v.R, v.G, v.B));

                Vector4 wideSource = rawWide[x, y].ToScaledVector4();
                Vector3 wideExact = profile.ToSrgb(new Vector3(wideSource.X, wideSource.Y, wideSource.Z));
                Assert.Equal(Rgba64.FromScaledVector4(new Vector4(wideExact, wideSource.W)), wide[x, y]);

                // 16 bits keep the float result to the nearest 1/65535, far finer than 8-bit rounding would.
                Vector4 w = wide[x, y].ToScaledVector4();
                Vector3 clipped = Vector3.Clamp(exact, Vector3.Zero, Vector3.One);
                Assert.InRange(MathF.Abs(w.X - clipped.X), 0, 0.6f / 65535);
                Assert.InRange(MathF.Abs(w.Y - clipped.Y), 0, 0.6f / 65535);
                Assert.InRange(MathF.Abs(w.Z - clipped.Z), 0, 0.6f / 65535);

                Vector4 narrowSource = card[x, y].ToScaledVector4();
                Vector3 narrowExact = profile.ToSrgb(new Vector3(narrowSource.X, narrowSource.Y, narrowSource.Z));
                Assert.Equal(Rgba32.FromScaledVector4(new Vector4(narrowExact, 1)), narrow[x, y]);
            }
        }

        // Pure P3 green lies outside sRGB: the float result keeps the negative red and blue that sRGB cannot show.
        int greenColumn = 8;
        Assert.Equal(new Rgba32(0, 255, 0, 255), card[greenColumn, 4]);
        RgbaVector green = vector[greenColumn, 4];
        Assert.True(green.R < 0 && green.B < 0, $"Expected out-of-gamut components, got {green.R}, {green.G}, {green.B}.");
        Assert.True(green.G > 1, $"Expected green above 1, got {green.G}.");
    }

    [Fact]
    public void AlphaIsCopiedUnchanged_AndNaNComponentsDoNotThrow()
    {
        IccMatrixProfile profile = IccMatrixProfile.Create(new IccProfile(FixturePath.Read("colormanagement/display_p3_v4.icc")));
        using var rgba = new Image<Rgba32>(4, 1);
        rgba[0, 0] = new Rgba32(200, 30, 90, 0);
        rgba[1, 0] = new Rgba32(200, 30, 90, 1);
        rgba[2, 0] = new Rgba32(200, 30, 90, 128);
        rgba[3, 0] = new Rgba32(200, 30, 90, 255);
        rgba.Mutate(ctx => ctx.ConvertToSrgb(profile));
        Assert.Equal(new byte[] { 0, 1, 128, 255 }, Enumerable.Range(0, 4).Select(x => rgba[x, 0].A));
        Assert.Single(Enumerable.Range(0, 4).Select(x => rgba[x, 0] with { A = 0 }).Distinct());

        using var vector = new Image<RgbaVector>(1, 1, new RgbaVector(float.NaN, 0.5f, 0.5f, 0.25f));
        vector.Mutate(ctx => ctx.ConvertToSrgb(profile));
        Vector3 expected = profile.ToSrgb(new Vector3(0, 0.5f, 0.5f));
        Assert.Equal(expected, new Vector3(vector[0, 0].R, vector[0, 0].G, vector[0, 0].B));
        Assert.Equal(0.25f, vector[0, 0].A);
    }

    [Fact]
    public void GrayPixelFormats_AreConvertedAsNeutrals()
    {
        byte[] tagged = FixturePath.Read("colormanagement/adobe_rgb_tagged.png");
        IccMatrixProfile profile = IccMatrixProfile.Create(new IccProfile(FixturePath.Read("colormanagement/adobe_rgb_v2.icc")));
        using Image<L8> plain = Image.Load<L8>(tagged);
        using Image<L8> converted = Image.Load<L8>(tagged, ConvertOptions);
        Assert.True(converted.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? record));
        Assert.Equal(ColorProfileConversionStatus.Converted, record.Status);
        for (int x = 0; x < plain.Width; x++)
        {
            float l = plain[x, 3].PackedValue / 255f;
            Assert.Equal(L8.FromScaledVector4(new Vector4(profile.ToSrgb(new Vector3(l)), 1)), converted[x, 3]);
        }
    }

    [Fact]
    public void MultipageTiff_ConvertsEachPageFromItsOwnProfile()
    {
        byte[] tiff = FixturePath.Read("colormanagement/multipage_tagged.tif");
        using Image<Rgba32> image = Image.Load<Rgba32>(tiff, ConvertOptions);
        Assert.Equal(3, image.Frames.Count);
        ColorProfileConversionStatus[] expected =
            [ColorProfileConversionStatus.Converted, ColorProfileConversionStatus.Converted, ColorProfileConversionStatus.AlreadySrgb];
        for (int i = 0; i < 3; i++)
        {
            Assert.True(image.Frames[i].Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? record));
            Assert.Equal(expected[i], record.Status);
            Assert.Null(image.Frames[i].Metadata.IccProfile);
        }

        Assert.Null(image.Metadata.IccProfile);
        Assert.True(image.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? summary));
        Assert.Equal(ColorProfileConversionStatus.Converted, summary.Status);

        using Image<Rgba32> p3 = Image.Load<Rgba32>(FixturePath.Read("colormanagement/display_p3_to_srgb.png"));
        using Image<Rgba32> adobe = Image.Load<Rgba32>(FixturePath.Read("colormanagement/adobe_rgb_to_srgb.png"));
        using Image<Rgba32> card = LoadCard();
        this.AssertWithinDeltaE(p3, image.Frames.CloneFrame(0), "page 0");
        this.AssertWithinDeltaE(adobe, image.Frames.CloneFrame(1), "page 1");
        Assert.Equal(Pixels(card), Pixels(image.Frames.CloneFrame(2)));

        // Lazy pages convert identically, page by page.
        using var stream = new MemoryStream(tiff);
        int index = 0;
        foreach (Image<Rgba32> page in Image.LoadTiffPages<Rgba32>(stream, ConvertOptions))
        {
            using (page)
            {
                Assert.Equal(Pixels(image.Frames.CloneFrame(index)), Pixels(page));
                Assert.Null(page.Metadata.IccProfile);
                Assert.True(page.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? record));
                Assert.Equal(expected[index], record.Status);
            }

            index++;
        }

        Assert.Equal(3, index);
    }

    [Fact]
    public void MixedFrames_KeepOnlyTheProfilesThatStillDescribeUnconvertedPixels()
    {
        IccProfile p3 = new(FixturePath.Read("colormanagement/display_p3_v4.icc"));
        IccProfile gray = new(FixturePath.Read("colormanagement/gray_gamma22_v4.icc"));
        using Image<Rgba32> image = LoadCard();
        var pixels = new Rgba32[image.Width * image.Height];
        image.CopyPixelDataTo(pixels);
        image.Frames.AddFrame(pixels);
        image.Frames.AddFrame(pixels);
        image.Metadata.IccProfile = p3;
        image.Frames[0].Metadata.IccProfile = p3;
        image.Frames[1].Metadata.IccProfile = gray;

        // Frame 2 has no profile of its own and relies on the image-level one.
        image.Mutate(ctx => ctx.ConvertToSrgb());
        Assert.Null(image.Frames[0].Metadata.IccProfile);
        Assert.Same(gray, image.Frames[1].Metadata.IccProfile);
        Assert.Null(image.Frames[2].Metadata.IccProfile);
        Assert.Null(image.Metadata.IccProfile);
        Assert.True(image.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? summary));
        Assert.Equal(ColorProfileConversionStatus.Unsupported, summary.Status);
        Assert.Equal(Pixels(image.Frames.CloneFrame(0)), Pixels(image.Frames.CloneFrame(2)));
        Assert.Equal(pixels, ToArray(image.Frames.CloneFrame(1)));

        // When the root frame is the unconverted one, the image-level profile it relies on stays.
        using Image<Rgba32> second = LoadCard();
        second.Frames.AddFrame(pixels);
        second.Metadata.IccProfile = gray;
        second.Frames[0].Metadata.IccProfile = gray;
        second.Frames[1].Metadata.IccProfile = p3;
        second.Mutate(ctx => ctx.ConvertToSrgb());
        Assert.Same(gray, second.Metadata.IccProfile);
        Assert.Null(second.Frames[1].Metadata.IccProfile);
    }

    [Fact]
    public void Clone_LeavesTheSourceUntouched_AndTheOperationIsIdempotent()
    {
        using Image<Rgba32> source = Image.Load<Rgba32>(FixturePath.Read("colormanagement/display_p3_tagged.png"));
        byte[] before = Pixels(source);
        using Image<Rgba32> converted = source.Clone(ctx => ctx.ConvertToSrgb());
        Assert.Equal(before, Pixels(source));
        Assert.NotNull(source.Metadata.IccProfile);
        Assert.False(source.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? _));
        Assert.NotEqual(before, Pixels(converted));

        byte[] once = Pixels(converted);
        converted.Mutate(ctx => ctx.ConvertToSrgb());
        Assert.Equal(once, Pixels(converted));
        Assert.True(converted.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? record));
        Assert.Equal(ColorProfileConversionStatus.NoProfile, record.Status);
    }

    [Fact]
    public void ExplicitSourceProfile_ConvertsUntaggedPixelsAndClearsEmbeddedProfiles()
    {
        byte[] p3Bytes = FixturePath.Read("colormanagement/display_p3_v4.icc");
        using Image<Rgba32> tagged = Image.Load<Rgba32>(FixturePath.Read("colormanagement/display_p3_tagged.png"), ConvertOptions);
        using Image<Rgba32> untagged = LoadCard();
        untagged.Metadata.IccProfile = new IccProfile(FixturePath.Read("colormanagement/gray_gamma22_v4.icc"));
        untagged.Mutate(ctx => ctx.ConvertToSrgb(IccMatrixProfile.Create(new IccProfile(p3Bytes))));
        Assert.Equal(Pixels(tagged), Pixels(untagged));
        Assert.Null(untagged.Metadata.IccProfile);
        Assert.True(untagged.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? record));
        Assert.Equal(ColorProfileConversionStatus.Converted, record.Status);
        Assert.Throws<ArgumentNullException>(() => untagged.Mutate(ctx => ctx.ConvertToSrgb((IccMatrixProfile)null!)));
    }

    [Fact]
    public void Conversion_IsIndependentOfTheDegreeOfParallelism()
    {
        IccMatrixProfile profile = IccMatrixProfile.Create(new IccProfile(FixturePath.Read("colormanagement/prophoto_v4.icc")));
        using Image<Rgba32> card = LoadCard();
        using var large = new Image<Rgba64>(card.Width * 8, card.Height * 8);
        for (int y = 0; y < large.Height; y++)
        {
            for (int x = 0; x < large.Width; x++)
            {
                Rgba32 c = card[x % card.Width, y % card.Height];
                large[x, y] = new Rgba64((ushort)((c.R * 257) ^ x), (ushort)((c.G * 257) ^ y), (ushort)(c.B * 257), ushort.MaxValue);
            }
        }

        using Image<Rgba64> serial = large.Clone(
            new ImageExecutionOptions { Configuration = new Configuration { MaxDegreeOfParallelism = 1 } },
            ctx => ctx.ConvertToSrgb(profile));
        using Image<Rgba64> parallel = large.Clone(
            new ImageExecutionOptions { Configuration = new Configuration { MaxDegreeOfParallelism = 4, MinimumPixelsPerTask = 1024 } },
            ctx => ctx.ConvertToSrgb(profile));
        var a = new Rgba64[large.Width * large.Height];
        var b = new Rgba64[a.Length];
        serial.CopyPixelDataTo(a);
        parallel.CopyPixelDataTo(b);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Cancellation_IsHonoured()
    {
        // Cancelled between operations: the conversion's own checkpoints stop it before any pixel is written.
        using var during = new CancellationTokenSource();
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Read("colormanagement/display_p3_tagged.png"));
        byte[] before = Pixels(image);
        Assert.ThrowsAny<OperationCanceledException>(() => image.Mutate(
            new ImageExecutionOptions { CancellationToken = during.Token },
            ctx =>
            {
                during.Cancel();
                ctx.ConvertToSrgb();
            }));
        Assert.Equal(before, Pixels(image));
        Assert.NotNull(image.Metadata.IccProfile);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Image.Load<Rgba32>(
            FixturePath.Read("colormanagement/display_p3_tagged.png"),
            new DecoderOptions
            {
                ColorProfileHandling = ColorProfileHandling.ConvertToSrgb,
                ExecutionOptions = new ImageExecutionOptions { CancellationToken = cancelled.Token },
            }));
    }

    [Fact]
    public void UndefinedPolicyValues_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecoderOptions { ColorProfileHandling = (ColorProfileHandling)7 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DecoderOptions { UnsupportedColorProfileHandling = (UnsupportedColorProfileHandling)(-1) });
        using Image<Rgba32> image = LoadCard();
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(ctx => ctx.ConvertToSrgb((UnsupportedColorProfileHandling)9)));
    }

    [Fact]
    public void ConversionRecord_IsDeepCopiedByClone()
    {
        using Image<Rgba32> loaded = Image.Load<Rgba32>(FixturePath.Read("colormanagement/gray_tagged.png"), ConvertOptions);
        using Image<Rgba64> copy = loaded.CloneAs<Rgba64>();
        Assert.True(loaded.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? original));
        Assert.True(copy.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? copied));
        Assert.NotSame(original, copied);
        Assert.Equal(original.Status, copied.Status);
        Assert.Equal(original.Detail, copied.Detail);
        Assert.Equal(original.SourceProfileDescription, copied.SourceProfileDescription);
    }

    private static Image<Rgba32> LoadCard() => Image.Load<Rgba32>(FixturePath.Read("colormanagement/src.png"));

    private static byte[] Pixels(Image<Rgba32> image)
    {
        var bytes = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(bytes);
        return bytes;
    }

    private static Rgba32[] ToArray(Image<Rgba32> image)
    {
        var pixels = new Rgba32[image.Width * image.Height];
        image.CopyPixelDataTo(pixels);
        return pixels;
    }

    private static int FindTag(byte[] profile, string signature)
    {
        int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(128));
        for (int i = 0; i < count; i++)
        {
            int entry = 132 + (12 * i);
            if (System.Text.Encoding.ASCII.GetString(profile, entry, 4) == signature)
            {
                return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(entry + 4));
            }
        }

        throw new InvalidOperationException($"Tag {signature} not found.");
    }

    private void AssertWithinDeltaE(Image<Rgba32> expected, Image<Rgba32> actual, string label)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        double worst = 0, total = 0;
        int differing = 0, largestStep = 0, count = expected.Width * expected.Height;
        (Rgba32 Expected, Rgba32 Actual) worstPair = default;
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgba32 e = expected[x, y], a = actual[x, y];
                double deltaE = Cie76(e, a);
                if (deltaE > worst)
                {
                    worst = deltaE;
                    worstPair = (e, a);
                }

                total += deltaE;
                differing += e == a ? 0 : 1;
                largestStep = Math.Max(largestStep, Math.Max(Math.Abs(e.R - a.R), Math.Max(Math.Abs(e.G - a.G), Math.Abs(e.B - a.B))));
            }
        }

        double mean = total / count;
        Rgba32 we = worstPair.Expected, wa = worstPair.Actual;
        this.output.WriteLine(FormattableString.Invariant($"{label}: max dE76 {worst:F4} (LittleCMS {we.R},{we.G},{we.B} vs {wa.R},{wa.G},{wa.B})")
            + FormattableString.Invariant($", mean dE76 {mean:F5}, {differing}/{count} pixels differ, by at most {largestStep} code value(s)"));
        Assert.True(largestStep <= MaxCodeValueStep, $"{label}: a channel differs from LittleCMS by {largestStep} code values.");
        Assert.True(worst <= MaxDeltaE, FormattableString.Invariant($"{label}: max delta E {worst:F3} exceeds {MaxDeltaE}."));
        Assert.True(mean <= MaxMeanDeltaE, FormattableString.Invariant($"{label}: mean delta E {mean:F4} exceeds {MaxMeanDeltaE}."));
    }

    /// <summary>CIE76 delta E between two 8-bit sRGB colours, through D65 XYZ and CIELAB in double precision.</summary>
    /// <remarks>Written out here rather than borrowed from the library, so the metric is independent of the code under test.</remarks>
    private static double Cie76(Rgba32 first, Rgba32 second)
    {
        (double l1, double a1, double b1) = Lab(first);
        (double l2, double a2, double b2) = Lab(second);
        return Math.Sqrt(((l1 - l2) * (l1 - l2)) + ((a1 - a2) * (a1 - a2)) + ((b1 - b2) * (b1 - b2)));
    }

    private static (double L, double A, double B) Lab(Rgba32 c)
    {
        static double Linear(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : ((24389.0 / 27 * t) + 16) / 116;

        double r = Linear(c.R), g = Linear(c.G), b = Linear(c.B);
        double x = (0.4124564 * r) + (0.3575761 * g) + (0.1804375 * b);
        double y = (0.2126729 * r) + (0.7151522 * g) + (0.0721750 * b);
        double z = (0.0193339 * r) + (0.1191920 * g) + (0.9503041 * b);
        double fx = F(x / 0.95047), fy = F(y), fz = F(z / 1.08883);
        return ((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }
}

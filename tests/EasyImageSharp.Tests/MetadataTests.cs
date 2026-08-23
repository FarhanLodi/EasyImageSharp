using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Bmp;
using EasyImageSharp.Formats.Gif;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The metadata containers themselves (<see cref="ImageMetadata"/>, <see cref="ImageFrameMetadata"/> and the
/// per-format facts) plus the decoder/encoder paths that fill and write them. Fixtures come from
/// <c>Fixtures/metadata/</c> (see <c>EXPECTED.md</c> there); EXIF parsing itself is covered by
/// <see cref="ExifTests"/>.
/// </summary>
public class MetadataTests
{
    private const double DpiTolerance = 0.01;

    // ----- Defaults and resolution -----

    [Fact]
    public void NewMetadataUsesNinetySixDpiAndNoProfiles()
    {
        var metadata = new ImageMetadata();

        Assert.Equal(96, metadata.HorizontalResolution);
        Assert.Equal(96, metadata.VerticalResolution);
        Assert.Equal(PixelResolutionUnit.PixelsPerInch, metadata.ResolutionUnits);
        Assert.Null(metadata.ExifProfile);
        Assert.Null(metadata.IccProfile);
        Assert.Null(metadata.XmpProfile);
        Assert.Null(metadata.DecodedImageFormat);
    }

    [Fact]
    public void ImagesCreatedInMemoryHaveDefaultMetadata()
    {
        using var image = new Image<Rgba32>(4, 4);

        Assert.Equal(96, image.Metadata.HorizontalResolution);
        Assert.Null(image.Metadata.DecodedImageFormat);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ResolutionRejectsNonPositiveValues(double value)
    {
        var metadata = new ImageMetadata();

        Assert.Throws<ArgumentOutOfRangeException>(() => metadata.HorizontalResolution = value);
        Assert.Throws<ArgumentOutOfRangeException>(() => metadata.VerticalResolution = value);
    }

    [Fact]
    public void SetResolutionAssignsBothAxesAndTheUnit()
    {
        var metadata = new ImageMetadata();

        metadata.SetResolution(300, 150, PixelResolutionUnit.PixelsPerCentimeter);

        Assert.Equal(300, metadata.HorizontalResolution);
        Assert.Equal(150, metadata.VerticalResolution);
        Assert.Equal(PixelResolutionUnit.PixelsPerCentimeter, metadata.ResolutionUnits);
    }

    [Fact]
    public void ResolutionConvertsBetweenUnits()
    {
        var metadata = new ImageMetadata();
        metadata.SetResolution(254, 254, PixelResolutionUnit.PixelsPerInch);

        Assert.Equal(254, metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerInch), 6);
        Assert.Equal(100, metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerCentimeter), 6);
        Assert.Equal(10000, metadata.GetVerticalResolution(PixelResolutionUnit.PixelsPerMeter), 3);
    }

    [Fact]
    public void AspectRatioResolutionPassesThroughEveryConversion()
    {
        var metadata = new ImageMetadata();
        metadata.SetResolution(4, 3, PixelResolutionUnit.AspectRatio);

        Assert.Equal(4, metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerInch));
        Assert.Equal(3, metadata.GetVerticalResolution(PixelResolutionUnit.PixelsPerMeter));
    }

    // ----- Format-specific containers -----

    [Fact]
    public void FormatMetadataIsCreatedOnFirstAccessAndCached()
    {
        var metadata = new ImageMetadata();

        Assert.False(metadata.TryGetFormatMetadata(out PngMetadata? missing));
        Assert.Null(missing);

        PngMetadata png = metadata.GetPngMetadata();
        png.Gamma = 0.5f;

        Assert.Same(png, metadata.GetPngMetadata());
        Assert.True(metadata.TryGetFormatMetadata(out PngMetadata? found));
        Assert.Same(png, found);
    }

    [Fact]
    public void SetFormatMetadataReplacesTheStoredContainer()
    {
        var metadata = new ImageMetadata();
        metadata.GetJpegMetadata().Quality = 10;

        var replacement = new JpegMetadata { Quality = 90 };
        metadata.SetFormatMetadata(replacement);

        Assert.Same(replacement, metadata.GetJpegMetadata());
        Assert.Equal(90, metadata.GetJpegMetadata().Quality);
        Assert.Throws<ArgumentNullException>(() => metadata.SetFormatMetadata<JpegMetadata>(null!));
    }

    [Fact]
    public void EveryFormatContainerIsReachableFromImageMetadata()
    {
        var metadata = new ImageMetadata();

        Assert.NotNull(metadata.GetJpegMetadata());
        Assert.NotNull(metadata.GetPngMetadata());
        Assert.NotNull(metadata.GetTiffMetadata());
        Assert.NotNull(metadata.GetBmpMetadata());
        Assert.NotNull(metadata.GetGifMetadata());

        var frame = new ImageFrameMetadata();
        Assert.NotNull(frame.GetGifMetadata());
        Assert.NotNull(frame.GetTiffMetadata());
        Assert.NotNull(frame.GetPngMetadata());
    }

    // ----- Deep cloning -----

    [Fact]
    public void DeepCloneCopiesResolutionProfilesAndFormatMetadata()
    {
        ImageMetadata source = BuildFullMetadata();

        ImageMetadata clone = source.DeepClone();

        Assert.Equal(source.HorizontalResolution, clone.HorizontalResolution);
        Assert.Equal(source.VerticalResolution, clone.VerticalResolution);
        Assert.Equal(source.ResolutionUnits, clone.ResolutionUnits);
        Assert.Equal(source.DecodedImageFormat, clone.DecodedImageFormat);
        Assert.NotSame(source.ExifProfile, clone.ExifProfile);
        Assert.NotSame(source.IccProfile, clone.IccProfile);
        Assert.NotSame(source.XmpProfile, clone.XmpProfile);
        Assert.Equal(source.IccProfile!.ToByteArray(), clone.IccProfile!.ToByteArray());
        Assert.Equal(source.XmpProfile!.ToByteArray(), clone.XmpProfile!.ToByteArray());
        Assert.Equal("Test Camera", clone.ExifProfile!.GetValue(ExifTag.Model)!.Value);
        Assert.NotSame(source.GetPngMetadata(), clone.GetPngMetadata());
        Assert.Equal(0.5f, clone.GetPngMetadata().Gamma);
    }

    [Fact]
    public void MutatingACloneNeverAffectsTheOriginal()
    {
        ImageMetadata source = BuildFullMetadata();

        ImageMetadata clone = source.DeepClone();
        clone.HorizontalResolution = 1;
        clone.ExifProfile!.SetValue(ExifTag.Model, "Changed");
        clone.GetPngMetadata().Gamma = 9f;
        clone.GetPngMetadata().TextData.Add(new PngTextData("Extra", "value"));
        clone.XmpProfile = null;

        Assert.Equal(300, source.HorizontalResolution);
        Assert.Equal("Test Camera", source.ExifProfile!.GetValue(ExifTag.Model)!.Value);
        Assert.Equal(0.5f, source.GetPngMetadata().Gamma);
        Assert.Single(source.GetPngMetadata().TextData);
        Assert.NotNull(source.XmpProfile);
    }

    [Fact]
    public void FrameMetadataDeepCloneIsIndependent()
    {
        var source = new ImageFrameMetadata { XmpProfile = new XmpProfile("<x/>") };
        GifFrameMetadata gif = source.GetGifMetadata();
        gif.FrameDelay = 42;
        gif.DisposalMethod = GifDisposalMethod.RestoreToPrevious;
        gif.HasTransparency = true;
        gif.TransparencyIndex = 7;
        source.GetTiffMetadata().BitsPerSample = new ushort[] { 8, 8, 8 };

        ImageFrameMetadata clone = source.DeepClone();
        clone.GetGifMetadata().FrameDelay = 1;
        clone.GetTiffMetadata().BitsPerSample![0] = 16;

        Assert.Equal(42, source.GetGifMetadata().FrameDelay);
        Assert.Equal(GifDisposalMethod.RestoreToPrevious, clone.GetGifMetadata().DisposalMethod);
        Assert.True(clone.GetGifMetadata().HasTransparency);
        Assert.Equal((byte)7, clone.GetGifMetadata().TransparencyIndex);
        Assert.Equal(new ushort[] { 8, 8, 8 }, source.GetTiffMetadata().BitsPerSample);
        Assert.NotSame(source.XmpProfile, clone.XmpProfile);
    }

    [Fact]
    public void ImageCloneDeepClonesMetadata()
    {
        using Image<Rgba32> image = LoadFixture("metadata/exif_pillow.jpg");

        using Image<Rgba32> clone = image.Clone();
        clone.Metadata.HorizontalResolution = 7;
        clone.Metadata.ExifProfile!.SetValue(ExifTag.Make, "Other");

        Assert.NotSame(image.Metadata, clone.Metadata);
        Assert.Equal(300, image.Metadata.HorizontalResolution, DpiTolerance);
        Assert.Equal("EasyImageSharp", image.Metadata.ExifProfile!.GetValue(ExifTag.Make)!.Value);
    }

    [Fact]
    public void ImageCloneAsDeepClonesMetadata()
    {
        using Image<Rgba32> image = LoadFixture("metadata/exif_pillow.jpg");

        using Image<Rgb24> clone = image.CloneAs<Rgb24>();
        clone.Metadata.ExifProfile!.RemoveValue(ExifTag.Make);

        Assert.Equal(ImageFormat.Jpeg, clone.Metadata.DecodedImageFormat);
        Assert.True(image.Metadata.ExifProfile!.Contains(ExifTag.Make));
    }

    [Fact]
    public void CloneWithOperationDeepClonesMetadataAndMutatePreservesIt()
    {
        using Image<Rgba32> image = LoadFixture("metadata/exif_pillow.jpg");

        using Image<Rgba32> resized = image.Clone(ctx => ctx.Resize(8, 8));
        resized.Metadata.VerticalResolution = 5;

        Assert.Equal(300, image.Metadata.VerticalResolution, DpiTolerance);
        Assert.NotNull(resized.Metadata.ExifProfile);

        ImageMetadata before = image.Metadata;
        image.Mutate(ctx => ctx.Grayscale());
        Assert.Same(before, image.Metadata);
        Assert.NotNull(image.Metadata.ExifProfile);
    }

    // ----- DecodedImageFormat -----

    [Theory]
    [InlineData("metadata/dpi_300.jpg", "JPEG")]
    [InlineData("metadata/dpi_150x100.png", "PNG")]
    [InlineData("metadata/dpi_200.tif", "TIFF")]
    [InlineData("metadata/dpi_96x120.bmp", "BMP")]
    [InlineData("metadata/gif_meta.gif", "GIF")]
    public void DecodersRecordTheFormatTheyDecodedFrom(string fixture, string formatName)
    {
        using Image<Rgba32> image = LoadFixture(fixture);

        Assert.NotNull(image.Metadata.DecodedImageFormat);
        Assert.Equal(formatName, image.Metadata.DecodedImageFormat!.Name);
        Assert.Equal(formatName, Image.Identify(FixturePath.Read(fixture)).Metadata.DecodedImageFormat!.Name);
    }

    // ----- Resolution read from files -----

    [Fact]
    public void JpegJfifDensityBecomesDpi()
    {
        using Image<Rgba32> image = LoadFixture("metadata/dpi_300.jpg");

        Assert.Equal(PixelResolutionUnit.PixelsPerInch, image.Metadata.ResolutionUnits);
        Assert.Equal(300, image.Metadata.HorizontalResolution, DpiTolerance);
        Assert.Equal(300, image.Metadata.VerticalResolution, DpiTolerance);
    }

    [Fact]
    public void PngPhysBecomesPixelsPerMeter()
    {
        using Image<Rgba32> image = LoadFixture("metadata/dpi_150x100.png");

        Assert.Equal(PixelResolutionUnit.PixelsPerMeter, image.Metadata.ResolutionUnits);
        Assert.Equal(5906, image.Metadata.HorizontalResolution);
        Assert.Equal(3937, image.Metadata.VerticalResolution);
        Assert.Equal(150, image.Metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerInch), 0.02);
        Assert.Equal(100, image.Metadata.GetVerticalResolution(PixelResolutionUnit.PixelsPerInch), 0.02);
    }

    [Fact]
    public void PngWithoutPhysKeepsTheDefaultResolution()
    {
        using Image<Rgba32> image = LoadFixture("metadata/dpi_none.png");

        Assert.Equal(96, image.Metadata.HorizontalResolution);
        Assert.Equal(PixelResolutionUnit.PixelsPerInch, image.Metadata.ResolutionUnits);
    }

    [Fact]
    public void TiffResolutionTagsBecomeDpi()
    {
        using Image<Rgba32> image = LoadFixture("metadata/dpi_200.tif");

        Assert.Equal(200, image.Metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerInch), DpiTolerance);
        Assert.Equal(200, image.Metadata.GetVerticalResolution(PixelResolutionUnit.PixelsPerInch), DpiTolerance);
    }

    [Fact]
    public void BmpResolutionComesFromPixelsPerMetre()
    {
        using Image<Rgba32> image = LoadFixture("metadata/dpi_96x120.bmp");

        Assert.Equal(PixelResolutionUnit.PixelsPerMeter, image.Metadata.ResolutionUnits);
        Assert.Equal(3780, image.Metadata.HorizontalResolution);
        Assert.Equal(4724, image.Metadata.VerticalResolution);
        Assert.Equal(96, image.Metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerInch), 0.02);
        Assert.Equal(120, image.Metadata.GetVerticalResolution(PixelResolutionUnit.PixelsPerInch), 0.02);
    }

    // ----- Resolution round trips through the encoders -----

    [Fact]
    public void JpegEncoderRoundTripsDpi()
    {
        // The JFIF density fields are 16-bit integers, so only whole densities survive a JPEG round trip.
        using var image = new Image<Rgba32>(8, 8);
        image.Metadata.SetResolution(150, 220, PixelResolutionUnit.PixelsPerInch);

        using Image<Rgba32> decoded = ReEncode(image, new JpegEncoder());

        Assert.Equal(PixelResolutionUnit.PixelsPerInch, decoded.Metadata.ResolutionUnits);
        Assert.Equal(150, decoded.Metadata.HorizontalResolution, DpiTolerance);
        Assert.Equal(220, decoded.Metadata.VerticalResolution, DpiTolerance);
    }

    [Fact]
    public void PngEncoderRoundTripsDpiWithinTolerance()
    {
        using var image = new Image<Rgba32>(8, 8);
        image.Metadata.SetResolution(300, 72, PixelResolutionUnit.PixelsPerInch);

        using Image<Rgba32> decoded = ReEncode(image, new PngEncoder());

        Assert.Equal(300, decoded.Metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerInch), DpiTolerance);
        Assert.Equal(72, decoded.Metadata.GetVerticalResolution(PixelResolutionUnit.PixelsPerInch), DpiTolerance);
    }

    [Fact]
    public void PngEncoderRoundTripsAspectRatioResolution()
    {
        using var image = new Image<Rgba32>(8, 8);
        image.Metadata.SetResolution(4, 3, PixelResolutionUnit.AspectRatio);

        using Image<Rgba32> decoded = ReEncode(image, new PngEncoder());

        Assert.Equal(PixelResolutionUnit.AspectRatio, decoded.Metadata.ResolutionUnits);
        Assert.Equal(4, decoded.Metadata.HorizontalResolution);
        Assert.Equal(3, decoded.Metadata.VerticalResolution);
    }

    [Fact]
    public void TiffEncoderRoundTripsDpi()
    {
        using var image = new Image<Rgba32>(8, 8);
        image.Metadata.SetResolution(199.5, 400, PixelResolutionUnit.PixelsPerInch);

        using Image<Rgba32> decoded = ReEncode(image, new TiffEncoder());

        Assert.Equal(199.5, decoded.Metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerInch), DpiTolerance);
        Assert.Equal(400, decoded.Metadata.GetVerticalResolution(PixelResolutionUnit.PixelsPerInch), DpiTolerance);
    }

    [Fact]
    public void BmpEncoderRoundTripsResolutionThroughPixelsPerMetre()
    {
        using var image = new Image<Rgba32>(8, 8);
        image.Metadata.SetResolution(96, 120, PixelResolutionUnit.PixelsPerInch);

        using Image<Rgba32> decoded = ReEncode(image, new BmpEncoder());

        Assert.Equal(PixelResolutionUnit.PixelsPerMeter, decoded.Metadata.ResolutionUnits);
        Assert.Equal(3780, decoded.Metadata.HorizontalResolution);
        Assert.Equal(4724, decoded.Metadata.VerticalResolution);
    }

    // ----- ICC and XMP -----

    [Theory]
    [InlineData("metadata/icc.jpg")]
    [InlineData("metadata/icc.png")]
    [InlineData("metadata/icc.tif")]
    public void IccProfileBytesSurviveDecodingExactly(string fixture)
    {
        byte[] expected = FixturePath.Read("metadata/icc_profile.bin");

        using Image<Rgba32> image = LoadFixture(fixture);

        Assert.NotNull(image.Metadata.IccProfile);
        Assert.Equal(expected, image.Metadata.IccProfile!.ToByteArray());
        Assert.Equal(expected.Length, image.Metadata.IccProfile.Length);
    }

    [Fact]
    public void IccHeaderFieldsAreParsed()
    {
        using Image<Rgba32> image = LoadFixture("metadata/icc.png");

        IccProfileHeader header = image.Metadata.IccProfile!.Header;
        Assert.True(header.IsValid);
        Assert.Equal("RGB ", header.ColorSpace);
        Assert.Equal("XYZ ", header.ConnectionSpace);
        Assert.Equal("mntr", header.ProfileClass);
        Assert.Equal(new Version(2, 1, 0), header.Version);
        Assert.Equal("EasyImageSharp Test Profile", header.Description);
        Assert.Equal(new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc), header.CreationDate);
    }

    [Fact]
    public void IccProfileHeaderDegradesOnGarbageBytes()
    {
        var profile = new IccProfile(new byte[] { 1, 2, 3 });

        Assert.False(profile.Header.IsValid);
        Assert.Equal(3, profile.Length);
        Assert.Equal(string.Empty, profile.Header.ColorSpace);
        Assert.Null(profile.Header.Description);
    }

    [Theory]
    [InlineData("metadata/xmp.jpg")]
    [InlineData("metadata/xmp.png")]
    [InlineData("metadata/xmp.tif")]
    public void XmpPacketBytesSurviveDecodingExactly(string fixture)
    {
        byte[] expected = FixturePath.Read("metadata/xmp_packet.xml");

        using Image<Rgba32> image = LoadFixture(fixture);

        Assert.NotNull(image.Metadata.XmpProfile);
        Assert.Equal(expected, image.Metadata.XmpProfile!.ToByteArray());
        Assert.Contains("EasyImageSharp metadata fixture", image.Metadata.XmpProfile.ToXml(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("JPEG")]
    [InlineData("PNG")]
    [InlineData("TIFF")]
    public void IccAndXmpRoundTripThroughTheEncoders(string format)
    {
        byte[] icc = FixturePath.Read("metadata/icc_profile.bin");
        byte[] xmp = FixturePath.Read("metadata/xmp_packet.xml");
        using var image = new Image<Rgba32>(8, 8);
        image.Metadata.IccProfile = new IccProfile(icc);
        image.Metadata.XmpProfile = new XmpProfile(xmp);

        using Image<Rgba32> decoded = ReEncode(image, EncoderFor(format));

        Assert.Equal(icc, decoded.Metadata.IccProfile!.ToByteArray());
        Assert.Equal(xmp, decoded.Metadata.XmpProfile!.ToByteArray());
    }

    [Fact]
    public void XmpProfileConvertsBetweenTextAndBytes()
    {
        var fromText = new XmpProfile("<x:xmpmeta/>");
        var withBom = new XmpProfile(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'<', (byte)'a', (byte)'/', (byte)'>' });

        Assert.Equal("<x:xmpmeta/>", fromText.ToXml());
        Assert.Equal("<a/>", withBom.ToXml());
        Assert.Equal(12, fromText.Length);
        Assert.Throws<ArgumentNullException>(() => new XmpProfile((string)null!));
        Assert.Throws<ArgumentNullException>(() => new XmpProfile((byte[])null!));
    }

    // ----- Per-format facts -----

    [Fact]
    public void JpegMetadataCarriesQualityColorTypeAndComments()
    {
        using Image<Rgba32> image = LoadFixture("metadata/comment_q75.jpg");

        JpegMetadata jpeg = image.Metadata.GetJpegMetadata();
        Assert.Equal(75, jpeg.Quality);
        Assert.False(jpeg.Progressive);
        Assert.Equal(JpegColorType.YCbCr, jpeg.ColorType);
        Assert.Equal(new[] { "First comment" }, jpeg.Comments);
    }

    [Fact]
    public void JpegMetadataFlagsProgressiveFiles()
    {
        using Image<Rgba32> image = LoadFixture("metadata/q50_progressive.jpg");

        JpegMetadata jpeg = image.Metadata.GetJpegMetadata();
        Assert.True(jpeg.Progressive);
        Assert.Equal(50, jpeg.Quality);
    }

    [Fact]
    public void JpegCommentsAreWrittenBackByTheEncoder()
    {
        using var image = new Image<Rgba32>(8, 8);
        image.Metadata.GetJpegMetadata().Comments.Add("Round trip");

        using Image<Rgba32> decoded = ReEncode(image, new JpegEncoder());

        Assert.Equal(new[] { "Round trip" }, decoded.Metadata.GetJpegMetadata().Comments);
    }

    [Fact]
    public void PngMetadataCarriesHeaderFactsGammaAndText()
    {
        using Image<Rgba32> image = LoadFixture("metadata/text.png");

        PngMetadata png = image.Metadata.GetPngMetadata();
        Assert.Equal(PngColorType.Rgb, png.ColorType);
        Assert.Equal(PngBitDepth.Bit8, png.BitDepth);
        Assert.False(png.Interlaced);
        Assert.Equal(0.45455f, png.Gamma!.Value, 0.00001);

        Assert.Equal("Metadata fixture", TextValue(png, "Title"));
        Assert.Equal("EasyImageSharp", TextValue(png, "Author"));
        Assert.Equal(new string('z', 300), TextValue(png, "Description"));

        PngTextData comment = png.TextData.Single(t => t.Keyword == "Comment");
        Assert.Equal("Grüße 日本", comment.Value);
        Assert.Equal("de", comment.LanguageTag);
        Assert.Equal("Kommentar", comment.TranslatedKeyword);
    }

    [Fact]
    public void PngTextAndGammaRoundTripThroughTheEncoder()
    {
        using var image = new Image<Rgba32>(8, 8);
        PngMetadata png = image.Metadata.GetPngMetadata();
        png.Gamma = 0.45455f;
        png.TextData.Add(new PngTextData("Title", "Latin-1 title"));
        png.TextData.Add(new PngTextData("Long", new string('q', 2000)));
        png.TextData.Add(new PngTextData("Comment", "Grüße 日本", "de", "Kommentar"));

        using Image<Rgba32> decoded = ReEncode(image, new PngEncoder());

        PngMetadata result = decoded.Metadata.GetPngMetadata();
        Assert.Equal(0.45455f, result.Gamma!.Value, 0.00001);
        Assert.Equal("Latin-1 title", TextValue(result, "Title"));
        Assert.Equal(new string('q', 2000), TextValue(result, "Long"));
        PngTextData comment = result.TextData.Single(t => t.Keyword == "Comment");
        Assert.Equal("Grüße 日本", comment.Value);
        Assert.Equal("de", comment.LanguageTag);
        Assert.Equal("Kommentar", comment.TranslatedKeyword);
    }

    [Fact]
    public void PngTextDataValidatesItsKeyword()
    {
        Assert.Throws<ArgumentNullException>(() => new PngTextData(null!, "v"));
        Assert.Throws<ArgumentException>(() => new PngTextData(string.Empty, "v"));
        Assert.Throws<ArgumentException>(() => new PngTextData(new string('k', 80), "v"));

        var text = new PngTextData("Title", "v");
        Assert.Equal(text, new PngTextData("Title", "v"));
        Assert.True(text == new PngTextData("Title", "v"));
        Assert.True(text != new PngTextData("Title", "w"));
        Assert.Equal("Title: v", text.ToString());
    }

    [Fact]
    public void BmpMetadataCarriesTheDecodedBitDepth()
    {
        using Image<Rgba32> image = LoadFixture("metadata/dpi_96x120.bmp");

        Assert.Equal(BmpBitsPerPixel.Pixel24, image.Metadata.GetBmpMetadata().BitsPerPixel);
        Assert.Equal(24, Image.Identify(FixturePath.Read("metadata/dpi_96x120.bmp")).PixelType.BitsPerPixel);
    }

    [Fact]
    public void TiffMetadataCarriesByteOrderAndPerPageFacts()
    {
        using Image<Rgba32> image = LoadFixture("metadata/multipage_meta.tif");

        Assert.Equal(ByteOrder.BigEndian, image.Metadata.GetTiffMetadata().ByteOrder);
        Assert.Equal(2, image.Frames.Count);

        TiffFrameMetadata page1 = image.Frames[0].Metadata.GetTiffMetadata();
        Assert.Equal(new ushort[] { 8 }, page1.BitsPerSample);
        Assert.Equal((ushort)1, page1.SamplesPerPixel);
        Assert.Equal(TiffCompressionMethod.None, page1.Compression);
        Assert.Equal(TiffPhotometricInterpretation.BlackIsZero, page1.PhotometricInterpretation);
        Assert.Equal(TiffPlanarConfiguration.Chunky, page1.PlanarConfiguration);
        Assert.Equal(TiffPredictor.None, page1.Predictor);
        Assert.False(page1.Tiled);
        Assert.Equal(8u, page1.RowsPerStrip);

        Assert.Equal(12, image.Frames[0].Width);
        Assert.Equal(9, image.Frames[1].Width);
        Assert.Equal("Page 1", image.Frames[0].Metadata.ExifProfile!.GetValue(ExifTag.ImageDescription)!.Value);
        Assert.Equal("Page 2", image.Frames[1].Metadata.ExifProfile!.GetValue(ExifTag.ImageDescription)!.Value);
    }

    [Fact]
    public void TiffPagesCarryTheirOwnResolution()
    {
        using Image<Rgba32> image = LoadFixture("metadata/multipage_meta.tif");

        Assert.Equal(100, image.Metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerInch), DpiTolerance);
        Rational page2 = image.Frames[1].Metadata.ExifProfile!.GetValue(ExifTag.XResolution)!.Value;
        Assert.Equal(250, page2.ToDouble(), DpiTolerance);
    }

    [Fact]
    public void GifMetadataCarriesLoopCountTableSizeAndComments()
    {
        using Image<Rgba32> image = LoadFixture("metadata/gif_meta.gif");

        GifMetadata gif = image.Metadata.GetGifMetadata();
        Assert.Equal(3, gif.RepeatCount);
        Assert.Equal(4, gif.GlobalColorTableLength);
        Assert.Equal(new[] { "EasyImageSharp GIF metadata fixture" }, gif.Comments);
    }

    [Fact]
    public void GifFrameMetadataCarriesDelayDisposalAndTransparency()
    {
        using Image<Rgba32> image = LoadFixture("metadata/gif_meta.gif");

        Assert.Equal(3, image.Frames.Count);
        int[] delays = image.Frames.Select(f => f.Metadata.GetGifMetadata().FrameDelay).ToArray();
        GifDisposalMethod[] disposals = image.Frames.Select(f => f.Metadata.GetGifMetadata().DisposalMethod).ToArray();

        Assert.Equal(new[] { 10, 20, 30 }, delays);
        Assert.Equal(
            new[] { GifDisposalMethod.RestoreToBackground, GifDisposalMethod.NotDispose, GifDisposalMethod.RestoreToPrevious },
            disposals);
        Assert.All(image.Frames, f => Assert.True(f.Metadata.GetGifMetadata().HasTransparency));

        // Pillow rebuilds the palette when it writes the file, so the encoded transparent index is 1 and the
        // second and third frames carry their own four-entry local colour table.
        Assert.All(image.Frames, f => Assert.Equal((byte)1, f.Metadata.GetGifMetadata().TransparencyIndex));
        Assert.Equal(new[] { 0, 4, 4 }, image.Frames.Select(f => f.Metadata.GetGifMetadata().LocalColorTableLength));
    }

    // ----- Identify -----

    [Theory]
    [InlineData("metadata/exif_pillow.jpg")]
    [InlineData("metadata/exif_pillow.png")]
    [InlineData("metadata/exif_pillow.tif")]
    public void IdentifyExposesMetadataWithoutDecodingPixels(string fixture)
    {
        ImageInfo info = Image.Identify(FixturePath.Read(fixture));

        Assert.Equal(16, info.Width);
        Assert.Equal(16, info.Height);
        Assert.NotNull(info.Metadata.ExifProfile);
        Assert.Equal("EasyImageSharp", info.Metadata.ExifProfile!.GetValue(ExifTag.Make)!.Value);
    }

    [Fact]
    public void IdentifyExposesResolutionAndFormatFacts()
    {
        ImageInfo jpeg = Image.Identify(FixturePath.Read("metadata/dpi_300.jpg"));
        ImageInfo png = Image.Identify(FixturePath.Read("metadata/text.png"));
        ImageInfo gif = Image.Identify(FixturePath.Read("metadata/gif_meta.gif"));

        Assert.Equal(300, jpeg.Metadata.HorizontalResolution, DpiTolerance);
        Assert.Equal(PngColorType.Rgb, png.Metadata.GetPngMetadata().ColorType);
        Assert.Equal(0.45455f, png.Metadata.GetPngMetadata().Gamma!.Value, 0.00001);
        Assert.Equal(3, gif.Metadata.GetGifMetadata().RepeatCount);
        Assert.Equal(3, gif.FrameCount);
    }

    [Fact]
    public void IdentifyExposesIccAndXmp()
    {
        ImageInfo icc = Image.Identify(FixturePath.Read("metadata/icc.jpg"));
        ImageInfo xmp = Image.Identify(FixturePath.Read("metadata/xmp.png"));

        Assert.Equal(FixturePath.Read("metadata/icc_profile.bin"), icc.Metadata.IccProfile!.ToByteArray());
        Assert.Equal(FixturePath.Read("metadata/xmp_packet.xml"), xmp.Metadata.XmpProfile!.ToByteArray());
    }

    // ----- Helpers -----

    private static ImageMetadata BuildFullMetadata()
    {
        var metadata = new ImageMetadata();
        metadata.SetResolution(300, 200, PixelResolutionUnit.PixelsPerInch);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Model, "Test Camera");
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        metadata.ExifProfile = exif;
        metadata.IccProfile = new IccProfile(FixturePath.Read("metadata/icc_profile.bin"));
        metadata.XmpProfile = new XmpProfile("<x:xmpmeta/>");
        metadata.GetPngMetadata().Gamma = 0.5f;
        metadata.GetPngMetadata().TextData.Add(new PngTextData("Title", "Original"));
        metadata.DecodedImageFormat = ImageFormat.Png;
        return metadata;
    }

    private static string TextValue(PngMetadata png, string keyword)
        => png.TextData.Single(t => t.Keyword == keyword).Value;

    internal static IImageEncoder EncoderFor(string format) => format switch
    {
        "JPEG" => new JpegEncoder(),
        "PNG" => new PngEncoder(),
        "TIFF" => new TiffEncoder(),
        "BMP" => new BmpEncoder(),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown encoder."),
    };

    internal static Image<Rgba32> LoadFixture(string relativePath)
        => Image.Load<Rgba32>(FixturePath.Read(relativePath));

    internal static Image<Rgba32> ReEncode<TPixel>(Image<TPixel> image, IImageEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var buffer = new MemoryStream();
        image.Save(buffer, encoder);
        return Image.Load<Rgba32>(buffer.ToArray());
    }
}

using System.Runtime.InteropServices;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Iptc;
using EasyImageSharp.Metadata.Xmp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Writing BigTIFF (version 43) and multi-strip pages with <see cref="TiffEncoder"/>, on small images: the container
/// facts are checked with <see cref="TiffTestReader"/>, a reader written from the TIFF 6.0 and BigTIFF
/// specifications, and the pixels by decoding with the BigTIFF decoder and the seekable region reader, both of which
/// are validated against Pillow- and tifffile-written fixtures elsewhere.
/// </summary>
public class BigTiffEncoderTests
{
    public static IEnumerable<object[]> Arms => new[]
    {
        new object[] { TiffCompression.None, null!, null!, TiffPredictor.None },
        new object[] { TiffCompression.Deflate, null!, 5, TiffPredictor.None },
        new object[] { TiffCompression.Lzw, null!, 4, TiffPredictor.Horizontal },
        new object[] { TiffCompression.PackBits, null!, 7, TiffPredictor.None },
        new object[] { TiffCompression.Deflate, TiffBitsPerPixel.Bit24, 1, TiffPredictor.Horizontal },
        new object[] { TiffCompression.Lzw, TiffBitsPerPixel.Bit8, 10, TiffPredictor.None },
        new object[] { TiffCompression.None, TiffBitsPerPixel.Bit4, 6, TiffPredictor.None },
        new object[] { TiffCompression.Ccitt4, null!, 8, TiffPredictor.None },
        new object[] { TiffCompression.Ccitt3, null!, 3, TiffPredictor.None },
        new object[] { TiffCompression.CcittRle, null!, 23, TiffPredictor.None },
    };

    [Fact]
    public void BigTiff_WritesTheVersion43Header()
    {
        using Image<Rgba32> image = Pattern(9, 7, 1);
        byte[] file = Encode(image, new TiffEncoder { Format = TiffFormatType.BigTiff });

        Assert.Equal((byte)'I', file[0]);
        Assert.Equal((byte)'I', file[1]);
        Assert.Equal(43, TiffTestReader.U16(file, 2, false));
        Assert.Equal(8, TiffTestReader.U16(file, 4, false)); // Bytes per offset.
        Assert.Equal(0, TiffTestReader.U16(file, 6, false)); // Reserved.
        ulong first = TiffTestReader.U64(file, 8, false);
        Assert.True(first >= 16 && first % 2 == 0, $"First directory at {first}.");
        using Image<Rgba32> decoded = Image.Load<Rgba32>(file);
        Assert.True(decoded.Metadata.GetTiffMetadata().BigTiff);
    }

    [Theory]
    [MemberData(nameof(Arms))]
    public void BigTiff_StripsRoundTripThroughTheDecoder(TiffCompression compression, TiffBitsPerPixel? depth, int? rowsPerStrip, TiffPredictor predictor)
    {
        using Image<Rgba32> image = Pattern(37, 23, 2);
        var big = new TiffEncoder { Format = TiffFormatType.BigTiff, Compression = compression, BitsPerPixel = depth, RowsPerStrip = rowsPerStrip, Predictor = predictor };
        var classic = new TiffEncoder { Compression = compression, BitsPerPixel = depth, Predictor = predictor };
        byte[] file = Encode(image, big);

        Dictionary<int, TiffTestReader.Field> ifd = TiffTestReader.FirstDirectory(file);
        int rows = Math.Min(rowsPerStrip ?? 23, 23);
        int strips = (23 + rows - 1) / rows;
        Assert.Equal(16, ifd[273].Type); // StripOffsets: LONG8.
        Assert.Equal(16, ifd[279].Type); // StripByteCounts: LONG8.
        Assert.Equal(strips, ifd[273].Count);
        Assert.Equal(strips, ifd[279].Count);
        Assert.Equal((ulong)rows, ifd[278].Integers(false)[0]);
        ulong[] offsets = ifd[273].Integers(false);
        ulong[] counts = ifd[279].Integers(false);
        for (int s = 0; s < strips; s++)
        {
            Assert.True(offsets[s] % 2 == 0 && offsets[s] + counts[s] <= (ulong)file.Length, $"Strip {s} at {offsets[s]}+{counts[s]}.");
        }

        // The pixels are those of the single-strip classic encode of the same image, decoded the same way.
        using Image<Rgba32> fromBig = Image.Load<Rgba32>(file);
        using Image<Rgba32> fromClassic = Image.Load<Rgba32>(Encode(image, classic));
        AssertSamePixels(fromClassic, fromBig, $"{compression}/{depth}/{rowsPerStrip}");
        if (depth is null && compression is not (TiffCompression.Ccitt3 or TiffCompression.Ccitt4 or TiffCompression.CcittRle))
        {
            AssertSamePixels(image, fromBig, "lossless RGBA");
        }
    }

    [Fact]
    public void Classic_MultiStripPagesUseLongTables()
    {
        using Image<Rgba32> image = Pattern(20, 11, 3);
        byte[] file = Encode(image, new TiffEncoder { RowsPerStrip = 3 });
        Assert.Equal(42, TiffTestReader.U16(file, 2, false));
        Dictionary<int, TiffTestReader.Field> ifd = TiffTestReader.FirstDirectory(file);
        Assert.Equal(4, ifd[273].Type);
        Assert.Equal(4, ifd[279].Count);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(file);
        AssertSamePixels(image, decoded, "classic strips");
        Assert.Equal((uint?)3, decoded.Frames.RootFrame.Metadata.GetTiffMetadata().RowsPerStrip);
    }

    [Fact]
    public void RowsPerStrip_IsValidated_AndCappedAtThePageHeight()
    {
        using Image<Rgba32> image = Pattern(5, 4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode(image, new TiffEncoder { RowsPerStrip = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode(image, new TiffEncoder { Format = (TiffFormatType)7 }));

        Dictionary<int, TiffTestReader.Field> ifd = TiffTestReader.FirstDirectory(Encode(image, new TiffEncoder { RowsPerStrip = 1000 }));
        Assert.Equal(1, ifd[273].Count);
        Assert.Equal(4ul, ifd[278].Integers(false)[0]);
    }

    [Fact]
    public void BigTiff_MultiPageChainsThroughEightByteNextPointers()
    {
        using Image<Rgba32> image = Pattern(16, 9, 5);
        using Image<Rgba32> second = Pattern(21, 13, 6);
        using Image<Rgba32> third = Pattern(16, 9, 7);
        image.Frames.AddFrame(second.Frames.RootFrame);
        image.Frames.AddFrame(third.Frames.RootFrame);
        var pageExif = new ExifProfile();
        pageExif.SetValue(ExifTag.Software, "page two");
        image.Frames[1].Metadata.ExifProfile = pageExif;
        image.Frames[2].Metadata.IptcProfile = new IptcProfile();
        image.Frames[2].Metadata.IptcProfile!.SetValue(IptcTag.Headline, "third page");

        byte[] file = Encode(image, new TiffEncoder { Format = TiffFormatType.BigTiff, RowsPerStrip = 4 });
        List<Dictionary<int, TiffTestReader.Field>> pages = TiffTestReader.Directories(file);
        Assert.Equal(3, pages.Count);
        Assert.Equal(21ul, pages[1][256].Integers(false)[0]);

        using Image<Rgba32> decoded = Image.Load<Rgba32>(file);
        Assert.Equal(3, decoded.Frames.Count);
        for (int f = 0; f < 3; f++)
        {
            AssertSamePixels(image.Frames[f], decoded.Frames[f], $"page {f}");
        }

        Assert.Equal("page two", decoded.Frames[1].Metadata.ExifProfile!.GetValue(ExifTag.Software)!.Value);
        Assert.Equal("third page", decoded.Frames[2].Metadata.IptcProfile!.GetValue(IptcTag.Headline));
        Assert.Null(decoded.Frames[1].Metadata.IptcProfile);

        int lazy = 0;
        foreach (Image<Rgba32> page in new TiffDecoder().DecodePages<Rgba32>(file))
        {
            using (page)
            {
                AssertSamePixels(image.Frames[lazy], page.Frames.RootFrame, $"lazy page {lazy}");
                lazy++;
            }
        }

        Assert.Equal(3, lazy);
    }

    [Fact]
    public void BigTiff_SubDirectoriesAreIfd8_AndEveryProfileSurvives()
    {
        using Image<Rgba32> image = Pattern(12, 10, 8);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Maker");
        exif.SetValue(ExifTag.Artist, "A long enough artist name to sit outside the value field");
        exif.SetValue(ExifTag.ExposureTime, new Rational(1, 250));
        exif.SetValue(ExifTag.DateTimeOriginal, "2026:11:04 10:11:12");
        exif.SetValue(ExifTag.GPSLatitudeRef, "S");
        exif.SetValue(ExifTag.GPSAltitude, new Rational(5, 2));
        exif.SetValue(ExifTag.InteroperabilityIndex, "R98");
        image.Metadata.ExifProfile = exif;
        var icc = new byte[257];
        new Random(1).NextBytes(icc);
        image.Metadata.IccProfile = new IccProfile(icc);
        image.Metadata.XmpProfile = new XmpProfile("<x:xmpmeta xmlns:x='adobe:ns:meta/'/>");
        image.Metadata.IptcProfile = new IptcProfile();
        image.Metadata.IptcProfile.SetValue(IptcTag.Caption, "Caption in a BigTIFF — UTF-8");
        image.Metadata.SetResolution(300, 300, PixelResolutionUnit.PixelsPerInch);

        byte[] file = Encode(image, new TiffEncoder { Format = TiffFormatType.BigTiff });
        Dictionary<int, TiffTestReader.Field> ifd = TiffTestReader.FirstDirectory(file);
        Assert.Equal(18, ifd[0x8769].Type); // Exif pointer: IFD8.
        Assert.Equal(18, ifd[0x8825].Type); // GPS pointer: IFD8.
        long exifAt = (long)ifd[0x8769].Integers(false)[0];
        Dictionary<int, TiffTestReader.Field> exifIfd = TiffTestReader.ReadDirectory(file, exifAt, false, true, out long next);
        Assert.Equal(0, next);
        Assert.Equal(18, exifIfd[0xA005].Type); // Interop pointer: IFD8.
        Assert.Equal(icc, ifd[34675].Data);
        Assert.Equal(4, ifd[33723].Type);

        using Image<Rgba32> decoded = Image.Load<Rgba32>(file);
        ExifProfile got = decoded.Metadata.ExifProfile!;
        Assert.Equal("Maker", got.GetValue(ExifTag.Make)!.Value);
        Assert.Equal("A long enough artist name to sit outside the value field", got.GetValue(ExifTag.Artist)!.Value);
        Assert.Equal(new Rational(1, 250), got.GetValue(ExifTag.ExposureTime)!.Value);
        Assert.Equal("2026:11:04 10:11:12", got.GetValue(ExifTag.DateTimeOriginal)!.Value);
        Assert.Equal("S", got.GetValue(ExifTag.GPSLatitudeRef)!.Value);
        Assert.Equal(new Rational(5, 2), got.GetValue(ExifTag.GPSAltitude)!.Value);
        Assert.Equal("R98", got.GetValue(ExifTag.InteroperabilityIndex)!.Value);
        Assert.Equal(icc, decoded.Metadata.IccProfile!.ToByteArray());
        Assert.Equal(image.Metadata.XmpProfile.ToXml(), decoded.Metadata.XmpProfile!.ToXml());
        Assert.Equal("Caption in a BigTIFF — UTF-8", decoded.Metadata.IptcProfile!.GetValue(IptcTag.Caption));
        Assert.Equal(300, decoded.Metadata.HorizontalResolution, 6);
    }

    [Theory]
    [InlineData(TiffCompression.None, 3)]
    [InlineData(TiffCompression.Deflate, 4)]
    [InlineData(TiffCompression.Lzw, 5)]
    [InlineData(TiffCompression.PackBits, 2)]
    public void BigTiff_RegionReaderReadsTheStrips(TiffCompression compression, int rowsPerStrip)
    {
        using Image<Rgba32> image = Pattern(31, 19, 9);
        byte[] file = Encode(image, new TiffEncoder { Format = TiffFormatType.BigTiff, Compression = compression, RowsPerStrip = rowsPerStrip });
        using var stream = new MemoryStream(file);
        using TiffRegionReader reader = Image.OpenTiffRegionReader(stream);
        Assert.True(reader.IsBigTiff);
        Assert.False(reader.IsTiled);
        Assert.Equal(31, reader.Width);
        Assert.Equal(19, reader.Height);

        foreach (TiffRegion region in new[] { new TiffRegion(0, 0, 31, 19), new TiffRegion(3, 5, 11, 7), new TiffRegion(30, 18, 1, 1) })
        {
            using Image<Rgba32> got = reader.ReadRegion<Rgba32>(region);
            for (int y = 0; y < region.Height; y++)
            {
                for (int x = 0; x < region.Width; x++)
                {
                    Assert.Equal(image[(int)region.X + x, (int)region.Y + y], got[x, y]);
                }
            }
        }
    }

    [Fact]
    public void Auto_StaysClassicWhileTheFileFits_AndSwitchesToBigTiffBeyond()
    {
        using Image<Rgba32> image = Pattern(40, 30, 10);
        byte[] auto = Encode(image, new TiffEncoder());
        Assert.Equal(42, TiffTestReader.U16(auto, 2, false));

        // Lower the classic ceiling so the switch can be exercised without a 4 GiB file.
        byte[] switched = Encode(image, new TiffEncoder { ClassicSizeLimit = auto.Length - 1 });
        Assert.Equal(43, TiffTestReader.U16(switched, 2, false));
        using Image<Rgba32> decoded = Image.Load<Rgba32>(switched);
        AssertSamePixels(image, decoded, "auto-switched BigTIFF");

        byte[] fits = Encode(image, new TiffEncoder { ClassicSizeLimit = auto.Length });
        Assert.Equal(auto, fits);
    }

    [Fact]
    public void Classic_RefusesAFileBeyondItsOffsets()
    {
        using Image<Rgba32> image = Pattern(40, 30, 11);
        byte[] auto = Encode(image, new TiffEncoder());
        var ex = Assert.Throws<NotSupportedException>(
            () => Encode(image, new TiffEncoder { Format = TiffFormatType.Classic, ClassicSizeLimit = auto.Length - 1 }));
        Assert.Contains("BigTiff", ex.Message);
        Assert.Equal(auto, Encode(image, new TiffEncoder { Format = TiffFormatType.Classic }));
    }

    /// <summary>A decoded BigTIFF's flag does not steer the encoder: Auto still writes classic for a small file.</summary>
    [Fact]
    public void Auto_IgnoresTheDecodedBigTiffFlag()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Read("tiff/bigtiff_le_rgb.tif"));
        Assert.True(image.Metadata.GetTiffMetadata().BigTiff);
        Assert.Equal(42, TiffTestReader.U16(Encode(image, new TiffEncoder()), 2, false));
        byte[] big = Encode(image, new TiffEncoder { Format = TiffFormatType.BigTiff });
        using Image<Rgba32> again = Image.Load<Rgba32>(big);
        AssertSamePixels(image, again, "BigTIFF fixture re-encoded as BigTIFF");
    }

    private static Image<Rgba32> Pattern(int width, int height, int seed)
    {
        var random = new Random(seed);
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32((byte)random.Next(256), (byte)(x * 9), (byte)(y * 11), (byte)(x % 4 == 0 ? 255 : random.Next(256)));
            }
        }

        return image;
    }

    private static byte[] Encode(Image image, TiffEncoder encoder)
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    private static void AssertSamePixels(Image<Rgba32> expected, Image<Rgba32> actual, string what)
        => AssertSamePixels(expected.Frames.RootFrame, actual.Frames.RootFrame, what);

    private static void AssertSamePixels(ImageFrame<Rgba32> expected, ImageFrame<Rgba32> actual, string what)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.True(
            MemoryMarshal.AsBytes(expected.PixelSpan).SequenceEqual(MemoryMarshal.AsBytes(actual.PixelSpan)),
            $"{what}: pixels differ.");
    }
}

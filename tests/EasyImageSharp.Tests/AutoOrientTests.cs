using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// <c>AutoOrient()</c> against the eight EXIF orientation fixtures in <c>Fixtures/metadata/</c>. Each
/// <c>orient_N.jpg</c> is the same 64x48 test card (8x6 flat 8x8 blocks with a unique gray level per block,
/// saved at quality 100 so every conforming decoder reproduces it bit-exactly) tagged with orientation N;
/// <c>orient_N.rgba</c> is Pillow's <c>ImageOps.exif_transpose</c> result for that file, so the assertions
/// below are exact comparisons against an independent implementation.
/// </summary>
public class AutoOrientTests
{
    private const int CardWidth = 64;
    private const int CardHeight = 48;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void OrientedPixelsMatchPillowExactly(int orientation)
    {
        using Image<Rgba32> image = MetadataTests.LoadFixture($"metadata/orient_{orientation}.jpg");
        Assert.Equal(CardWidth, image.Width);
        Assert.Equal(CardHeight, image.Height);

        image.Mutate(ctx => ctx.AutoOrient());

        (int width, int height) = orientation >= 5 ? (CardHeight, CardWidth) : (CardWidth, CardHeight);
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        AssertMatchesReference(image, $"metadata/orient_{orientation}.rgba");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void EveryFixtureStoresTheSameCardBeforeItIsOriented(int orientation)
    {
        // All eight files hold identical pixels and differ only in their Orientation tag, so decoding one
        // without orienting it must reproduce the card exactly. (Flat 8x8 blocks at quality 100 are DC-only,
        // which is what makes a JPEG comparison bit-exact.)
        using Image<Rgba32> image = MetadataTests.LoadFixture($"metadata/orient_{orientation}.jpg");

        AssertMatchesReference(image, "metadata/card.rgba");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void TheOrientationTagIsResetToOne(int orientation)
    {
        using Image<Rgba32> image = MetadataTests.LoadFixture($"metadata/orient_{orientation}.jpg");
        Assert.Equal((ushort)orientation, image.Metadata.ExifProfile!.GetValue(ExifTag.Orientation)!.Value);

        image.Mutate(ctx => ctx.AutoOrient());

        Assert.Equal((ushort)1, image.Metadata.ExifProfile!.GetValue(ExifTag.Orientation)!.Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void AutoOrientIsIdempotent(int orientation)
    {
        using Image<Rgba32> once = MetadataTests.LoadFixture($"metadata/orient_{orientation}.jpg");
        once.Mutate(ctx => ctx.AutoOrient());
        byte[] first = Snapshot(once);

        once.Mutate(ctx => ctx.AutoOrient());

        Assert.Equal(first, Snapshot(once));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void TheExifPixelDimensionsAreSwappedForTheTransposingOrientations(int orientation)
    {
        using Image<Rgba32> image = MetadataTests.LoadFixture($"metadata/orient_{orientation}.jpg");
        ExifProfile profile = image.Metadata.ExifProfile!;
        Assert.Equal((uint)CardWidth, profile.GetValue(ExifTag.PixelXDimension)!.Value);
        Assert.Equal((uint)CardHeight, profile.GetValue(ExifTag.PixelYDimension)!.Value);

        image.Mutate(ctx => ctx.AutoOrient());

        Assert.Equal((uint)CardHeight, profile.GetValue(ExifTag.PixelXDimension)!.Value);
        Assert.Equal((uint)CardWidth, profile.GetValue(ExifTag.PixelYDimension)!.Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheExifPixelDimensionsAreLeftAloneForTheOtherOrientations(int orientation)
    {
        using Image<Rgba32> image = MetadataTests.LoadFixture($"metadata/orient_{orientation}.jpg");

        image.Mutate(ctx => ctx.AutoOrient());

        ExifProfile profile = image.Metadata.ExifProfile!;
        Assert.Equal((uint)CardWidth, profile.GetValue(ExifTag.PixelXDimension)!.Value);
        Assert.Equal((uint)CardHeight, profile.GetValue(ExifTag.PixelYDimension)!.Value);
    }

    [Fact]
    public void OrientationOneLeavesThePixelsUntouched()
    {
        using Image<Rgba32> image = MetadataTests.LoadFixture("metadata/orient_1.jpg");
        byte[] before = Snapshot(image);

        image.Mutate(ctx => ctx.AutoOrient());

        Assert.Equal(before, Snapshot(image));
    }

    [Fact]
    public void ImagesWithoutAnExifProfileAreLeftAlone()
    {
        using Image<Rgb24> image = TestImages.Gradient(9, 7);
        byte[] before = Snapshot(image);

        image.Mutate(ctx => ctx.AutoOrient());

        Assert.Null(image.Metadata.ExifProfile);
        Assert.Equal(9, image.Width);
        Assert.Equal(before, Snapshot(image));
    }

    [Fact]
    public void ImagesWithoutAnOrientationTagAreLeftAlone()
    {
        using Image<Rgb24> image = TestImages.Gradient(9, 7);
        var profile = new ExifProfile();
        profile.SetValue(ExifTag.Make, "Maker");
        image.Metadata.ExifProfile = profile;
        byte[] before = Snapshot(image);

        image.Mutate(ctx => ctx.AutoOrient());

        Assert.False(profile.Contains(ExifTag.Orientation));
        Assert.Equal(before, Snapshot(image));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(65535)]
    public void OutOfRangeOrientationValuesAreLeftUntouched(int value)
    {
        using Image<Rgb24> image = TestImages.Gradient(9, 7);
        var profile = new ExifProfile();
        profile.SetValue(ExifTag.Orientation, (ushort)value);
        image.Metadata.ExifProfile = profile;
        byte[] before = Snapshot(image);

        image.Mutate(ctx => ctx.AutoOrient());

        Assert.Equal(9, image.Width);
        Assert.Equal(before, Snapshot(image));
        Assert.Equal((ushort)value, profile.GetValue(ExifTag.Orientation)!.Value);
    }

    [Fact]
    public void EveryFrameOfAMultiFrameImageIsOriented()
    {
        using var image = new Image<Rgba32>(4, 2);
        image.Frames.CreateFrame(4, 2);
        for (int i = 0; i < image.Frames.Count; i++)
        {
            image.Frames[i][0, 0] = new Rgba32((byte)(10 + i), 20, 30);
        }

        var profile = new ExifProfile();
        profile.SetValue(ExifTag.Orientation, (ushort)6); // 90 degrees clockwise.
        image.Metadata.ExifProfile = profile;

        image.Mutate(ctx => ctx.AutoOrient());

        Assert.Equal(2, image.Width);
        Assert.Equal(4, image.Height);
        Assert.All(image.Frames, frame =>
        {
            Assert.Equal(2, frame.Width);
            Assert.Equal(4, frame.Height);
        });

        // The pixel that was top-left is top-right after a clockwise quarter turn.
        Assert.Equal(new Rgba32(10, 20, 30), image.Frames[0][1, 0]);
        Assert.Equal(new Rgba32(11, 20, 30), image.Frames[1][1, 0]);
    }

    [Fact]
    public void AnOrientedImageSurvivesAnEncodeAndDecodeAsUpright()
    {
        using Image<Rgba32> image = MetadataTests.LoadFixture("metadata/orient_6.jpg");
        image.Mutate(ctx => ctx.AutoOrient());

        using Image<Rgba32> decoded = MetadataTests.ReEncode(image, new JpegEncoder { Quality = 100 });

        Assert.Equal(CardHeight, decoded.Width);
        Assert.Equal(CardWidth, decoded.Height);
        Assert.Equal((ushort)1, decoded.Metadata.ExifProfile!.GetValue(ExifTag.Orientation)!.Value);
    }

    [Fact]
    public void CloneWithAutoOrientLeavesTheSourceUntouched()
    {
        using Image<Rgba32> image = MetadataTests.LoadFixture("metadata/orient_8.jpg");

        using Image<Rgba32> oriented = image.Clone(ctx => ctx.AutoOrient());

        Assert.Equal(CardWidth, image.Width);
        Assert.Equal((ushort)8, image.Metadata.ExifProfile!.GetValue(ExifTag.Orientation)!.Value);
        Assert.Equal(CardHeight, oriented.Width);
        Assert.Equal((ushort)1, oriented.Metadata.ExifProfile!.GetValue(ExifTag.Orientation)!.Value);
    }

    // ----- Helpers -----

    private static void AssertMatchesReference(Image<Rgba32> image, string referencePath)
    {
        byte[] expected = FixturePath.Read(referencePath);
        Assert.Equal(image.Width * image.Height * 4, expected.Length);

        byte[] actual = Snapshot(image);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                int pixel = i / 4;
                Assert.Fail(
                    $"Pixel ({pixel % image.Width}, {pixel / image.Width}) channel {i % 4} of {referencePath}: "
                    + $"expected {expected[i]}, got {actual[i]}.");
            }
        }
    }

    private static byte[] Snapshot<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var buffer = new byte[image.Frames.Count * image.Width * image.Height * 4];
        var row = new Rgba32[image.Width];
        int offset = 0;
        foreach (ImageFrame<TPixel> frame in image.Frames)
        {
            for (int y = 0; y < frame.Height; y++)
            {
                PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y), row);
                foreach (Rgba32 pixel in row)
                {
                    buffer[offset++] = pixel.R;
                    buffer[offset++] = pixel.G;
                    buffer[offset++] = pixel.B;
                    buffer[offset++] = pixel.A;
                }
            }
        }

        return buffer;
    }
}

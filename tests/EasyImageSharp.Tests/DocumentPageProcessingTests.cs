using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

public sealed class DocumentPageProcessingTests
{
    [Theory]
    [InlineData(nameof(IImageProcessingContext.AutoRotateDocumentPages))]
    [InlineData(nameof(IImageProcessingContext.AutoCropPages))]
    public void NewPageOperations_HaveDefaultBodiesForExistingContextImplementations(string methodName)
    {
        // Existing consumers implementing the large public interface must not gain new abstract obligations.
        System.Reflection.MethodInfo method = typeof(IImageProcessingContext).GetMethod(methodName)!;
        Assert.NotNull(method);
        Assert.False(method.IsAbstract);
        Assert.NotNull(method.GetMethodBody());
    }

    [Fact]
    public void AutoRotateDocumentPages_CorrectsMixedOrientationsIndependently()
    {
        using Image<L8> pages = DocumentFixtures.LoadGray("text_page_rot90.png");
        using Image<L8> second = DocumentFixtures.LoadGray("text_page_rot180.png");
        using Image<L8> blank = DocumentPages.Blank(80, 60);
        pages.Frames.AddFrame(second.Frames.RootFrame);
        pages.Frames.AddFrame(blank.Frames.RootFrame);

        using Image<L8> corrected = pages.Clone(ctx => ctx.AutoRotateDocumentPages());

        for (int index = 0; index < 2; index++)
        {
            using Image<L8> page = corrected.Frames.CloneFrame(index);
            Assert.Equal(500, page.Width);
            Assert.Equal(700, page.Height);
            Assert.Equal(RotateMode.None, page.DetectOrientation().Rotation);
        }

        AssertFrameEqual(blank.Frames.RootFrame, corrected.Frames[2]);
        Assert.Equal(700, pages.Width);
        Assert.Equal(500, pages.Height);
        AssertFrameEqual(second.Frames.RootFrame, pages.Frames[1]);
    }

    [Fact]
    public void AutoRotateDocument_KeepsRootDrivenBehavior()
    {
        using Image<L8> pages = DocumentFixtures.LoadGray("text_page_rot90.png");
        using Image<L8> second = DocumentFixtures.LoadGray("text_page_rot180.png");
        pages.Frames.AddFrame(second.Frames.RootFrame);
        RotateMode rootRotation = pages.DetectOrientation().Rotation;
        using Image<L8> expected = second.Clone(ctx => ctx.Rotate((float)(int)rootRotation));

        pages.Mutate(ctx => ctx.AutoRotateDocument());

        AssertFrameEqual(expected.Frames.RootFrame, pages.Frames[1]);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void AutoRotateDocument_RejectsInvalidConfidenceBeforeMutation(float confidence)
    {
        using Image<L8> page = DocumentFixtures.LoadGray("text_page_rot90.png");
        byte[] before = DocumentFixtures.Plane(page);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Mutate(ctx => ctx.AutoRotateDocument(confidence)));
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Mutate(ctx => ctx.AutoRotateDocumentPages(confidence)));
        Assert.Equal(before, DocumentFixtures.Plane(page));
    }

    [Fact]
    public void AutoCropPages_HandlesDifferentPageGeometryAndUndetectableRoot()
    {
        using Image<L8> pages = DocumentPages.Blank(80, 60);
        using Image<L8> photo = DocumentFixtures.LoadGray("perspective_page.png");
        using Image<L8> rotatedPhoto = photo.Clone(ctx => ctx.Rotate(90));
        using Image<L8> expected = photo.Clone(ctx => ctx.AutoCropPage());
        using Image<L8> expectedRotated = rotatedPhoto.Clone(ctx => ctx.AutoCropPage());
        Assert.True(expected.Width < photo.Width);
        Assert.True(expectedRotated.Width < rotatedPhoto.Width);
        pages.Frames.AddFrame(photo.Frames.RootFrame);
        pages.Frames.AddFrame(rotatedPhoto.Frames.RootFrame);

        using Image<L8> cropped = pages.Clone(ctx => ctx.AutoCropPages());

        AssertFrameEqual(pages.Frames.RootFrame, cropped.Frames.RootFrame);
        AssertFrameEqual(expected.Frames.RootFrame, cropped.Frames[1]);
        AssertFrameEqual(expectedRotated.Frames.RootFrame, cropped.Frames[2]);
        Assert.NotEqual(cropped.Frames[1].Width, cropped.Frames[2].Width);
        AssertFrameEqual(photo.Frames.RootFrame, pages.Frames[1]);
        // A skipped root still shares its buffer until a later in-place operation requests ownership.
        cropped.Mutate(ctx => ctx.Invert());
        AssertFrameEqual(photo.Frames.RootFrame, pages.Frames[1]);
        Assert.Equal(255, pages[0, 0].PackedValue);
    }

    [Fact]
    public void AutoCropPage_UndetectableRootLeavesAllPagesUnchanged()
    {
        using Image<L8> pages = DocumentPages.Blank(80, 60);
        using Image<L8> photo = DocumentFixtures.LoadGray("perspective_page.png");
        pages.Frames.AddFrame(photo.Frames.RootFrame);

        pages.Mutate(ctx => ctx.AutoCropPage());

        Assert.Equal(80, pages.Width);
        Assert.Equal(60, pages.Height);
        AssertFrameEqual(photo.Frames.RootFrame, pages.Frames[1]);
    }

    private static void AssertFrameEqual(ImageFrame<L8> expected, ImageFrame<L8> actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.GetRowSpan(y).SequenceEqual(actual.GetRowSpan(y)), $"Pixels differ in row {y}.");
        }
    }
}

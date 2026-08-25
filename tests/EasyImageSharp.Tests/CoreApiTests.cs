using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Tensors;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The image API that surrounds the pixels: wrapping caller memory, copying pixel data in and out,
/// frame list editing, encoding to base64 or a named format, and the disposal contract.
/// </summary>
public class CoreApiTests
{
    // ----- WrapMemory -----

    [Fact]
    public void WrapMemory_SharesTheCallersBufferInBothDirections()
    {
        var buffer = new Rgba32[4 * 3];
        buffer[0] = new Rgba32(1, 2, 3, 4);

        using Image<Rgba32> image = Image<Rgba32>.WrapMemory(buffer.AsMemory(), 4, 3);

        Assert.Equal(4, image.Width);
        Assert.Equal(3, image.Height);
        Assert.Equal(new Rgba32(1, 2, 3, 4), image[0, 0]);

        // Writing through the image is visible in the caller's array...
        image[3, 2] = new Rgba32(9, 8, 7, 6);
        Assert.Equal(new Rgba32(9, 8, 7, 6), buffer[^1]);

        // ...and writing the array is visible through the image.
        buffer[1] = new Rgba32(5, 5, 5, 5);
        Assert.Equal(new Rgba32(5, 5, 5, 5), image[1, 0]);
    }

    [Fact]
    public void WrapMemory_OverBytes_ReinterpretsWithoutCopying()
    {
        // Two BGRA pixels: blue and semi-transparent red.
        byte[] bytes = { 255, 0, 0, 255, 0, 0, 255, 128 };

        using Image<Bgra32> image = Image<Bgra32>.WrapMemory(bytes.AsMemory(), 2, 1);

        Assert.Equal(new Rgba32(0, 0, 255, 255), image[0, 0].ToRgba32());
        Assert.Equal(new Rgba32(255, 0, 0, 128), image[1, 0].ToRgba32());

        image[0, 0] = new Bgra32(1, 2, 3, 4);
        Assert.Equal(new byte[] { 3, 2, 1, 4 }, bytes[..4]);
    }

    [Fact]
    public void WrapMemory_IgnoresSurplusCapacity()
    {
        var buffer = new L8[100];
        using Image<L8> image = Image<L8>.WrapMemory(buffer.AsMemory(), 3, 2);

        Assert.Equal(3, image.Width);
        Assert.Equal(2, image.Height);
        Assert.True(image.DangerousTryGetSinglePixelMemory(out Memory<L8> memory));
        Assert.Equal(6, memory.Length);
    }

    [Fact]
    public void WrapMemory_RejectsBuffersThatAreTooSmall()
    {
        var pixels = new Rgba32[5];
        var bytes = new byte[5];

        Assert.Throws<ArgumentException>(() => Image<Rgba32>.WrapMemory(pixels.AsMemory(), 3, 2));
        Assert.Throws<ArgumentException>(() => Image<Rgba32>.WrapMemory(bytes.AsMemory(), 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Image<Rgba32>.WrapMemory(pixels.AsMemory(), 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Image<Rgba32>.WrapMemory(pixels.AsMemory(), 1, -1));
    }

    [Fact]
    public void WrapMemory_DisposingTheImageLeavesTheCallersBufferIntact()
    {
        var buffer = new Rgba32[4];
        Array.Fill(buffer, new Rgba32(7, 7, 7, 7));

        var image = Image<Rgba32>.WrapMemory(buffer.AsMemory(), 2, 2);
        image.Dispose();

        Assert.All(buffer, p => Assert.Equal(new Rgba32(7, 7, 7, 7), p));
    }

    [Fact]
    public void WrapMemory_ProducesAnImageThatEncodesLikeAnyOther()
    {
        var buffer = new Rgba32[8 * 8];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = new Rgba32((byte)i, (byte)(255 - i), 128, 255);
        }

        using Image<Rgba32> wrapped = Image<Rgba32>.WrapMemory(buffer.AsMemory(), 8, 8);
        using var stream = new MemoryStream();
        wrapped.SaveAsPng(stream);

        stream.Position = 0;
        using Image<Rgba32> reloaded = Image.Load<Rgba32>(stream);
        Assert.Equal(buffer[10], reloaded[2, 1]);
    }

    // ----- Single pixel memory and pixel data copies -----

    [Fact]
    public void DangerousTryGetSinglePixelMemory_ExposesTheLiveBuffer()
    {
        using var image = new Image<Rgb24>(4, 2);

        Assert.True(image.DangerousTryGetSinglePixelMemory(out Memory<Rgb24> memory));
        Assert.Equal(8, memory.Length);

        memory.Span[5] = new Rgb24(1, 2, 3);
        Assert.Equal(new Rgb24(1, 2, 3), image[1, 1]);
    }

    [Fact]
    public void CopyPixelDataTo_CopiesPixelsAndBytes()
    {
        using Image<Rgb24> image = TestImages.Gradient(4, 3);

        var pixels = new Rgb24[12];
        image.CopyPixelDataTo(pixels);
        Assert.Equal(image[3, 2], pixels[11]);

        var bytes = new byte[36];
        image.CopyPixelDataTo(bytes);
        Assert.Equal(image[0, 0].R, bytes[0]);
        Assert.Equal(image[0, 0].G, bytes[1]);
        Assert.Equal(image[0, 0].B, bytes[2]);
        Assert.Equal(image[3, 2].B, bytes[35]);

        Assert.Equal(bytes, image.GetPixelBytes());
    }

    [Fact]
    public void CopyPixelDataTo_RejectsBuffersThatAreTooSmall()
    {
        using var image = new Image<Rgb24>(4, 3);

        Assert.Throws<ArgumentException>(() => image.CopyPixelDataTo(new Rgb24[11]));
        Assert.Throws<ArgumentException>(() => image.CopyPixelDataTo(new byte[35]));
    }

    [Fact]
    public void CopyPixelDataTo_WithStride_LeavesRowPaddingUntouched()
    {
        using var image = new Image<Rgb24>(2, 2);
        image[0, 0] = new Rgb24(1, 1, 1);
        image[1, 0] = new Rgb24(2, 2, 2);
        image[0, 1] = new Rgb24(3, 3, 3);
        image[1, 1] = new Rgb24(4, 4, 4);

        // Rows of 6 bytes padded out to 8, the classic 4-byte aligned bitmap stride.
        var destination = new byte[16];
        Array.Fill(destination, (byte)0xEE);
        image.CopyPixelDataTo(destination, 8);

        Assert.Equal(new byte[] { 1, 1, 1, 2, 2, 2, 0xEE, 0xEE }, destination[..8]);
        Assert.Equal(new byte[] { 3, 3, 3, 4, 4, 4, 0xEE, 0xEE }, destination[8..]);
    }

    [Fact]
    public void CopyPixelDataTo_WithStride_ValidatesStrideAndCapacity()
    {
        using var image = new Image<Rgb24>(2, 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => image.CopyPixelDataTo(new byte[16], 5));
        Assert.Throws<ArgumentException>(() => image.CopyPixelDataTo(new byte[13], 8));
    }

    // ----- LoadPixelData with a stride -----

    [Fact]
    public void LoadPixelData_WithStride_DropsTheRowPadding()
    {
        byte[] padded =
        {
            1, 1, 1, 2, 2, 2, 0xEE, 0xEE,
            3, 3, 3, 4, 4, 4, 0xEE, 0xEE,
        };

        using Image<Rgb24> image = Image.LoadPixelData<Rgb24>(padded.AsMemory(), 2, 2, 8);

        Assert.Equal(new Rgb24(1, 1, 1), image[0, 0]);
        Assert.Equal(new Rgb24(2, 2, 2), image[1, 0]);
        Assert.Equal(new Rgb24(3, 3, 3), image[0, 1]);
        Assert.Equal(new Rgb24(4, 4, 4), image[1, 1]);
    }

    [Fact]
    public void LoadPixelData_WithStride_AcceptsABufferWithoutTrailingPadding()
    {
        // The last row does not need its padding present.
        byte[] data = { 1, 1, 1, 1, 0xEE, 0xEE, 2, 2, 2, 2 };

        using Image<Rgba32> image = Image.LoadPixelData(data.AsMemory(), 1, 2, 6);

        Assert.Equal(new Rgba32(1, 1, 1, 1), image[0, 0]);
        Assert.Equal(new Rgba32(2, 2, 2, 2), image[0, 1]);
    }

    [Fact]
    public void LoadPixelData_WithStride_ValidatesItsArguments()
    {
        var data = new byte[64];

        Assert.Throws<ArgumentOutOfRangeException>(() => Image.LoadPixelData<Rgb24>(data.AsMemory(), 4, 2, 11));
        Assert.Throws<ArgumentException>(() => Image.LoadPixelData<Rgb24>(data.AsMemory(), 4, 6, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => Image.LoadPixelData<Rgb24>(data.AsMemory(), 0, 2, 12));
    }

    [Fact]
    public void LoadPixelData_WithStride_RoundTripsWithCopyPixelDataTo()
    {
        using Image<Rgba32> original = TestImages.AlphaGradient(5, 4);
        const int Stride = (5 * 4) + 8;

        var buffer = new byte[Stride * 4];
        original.CopyPixelDataTo(buffer, Stride);
        using Image<Rgba32> restored = Image.LoadPixelData<Rgba32>(buffer.AsMemory(), 5, 4, Stride);

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                Assert.Equal(original[x, y], restored[x, y]);
            }
        }
    }

    // ----- Base64 and format-based saving -----

    [Fact]
    public void ToBase64String_RoundTripsThroughTheDecoder()
    {
        using Image<Rgb24> image = TestImages.Gradient(6, 4);

        string base64 = image.ToBase64String(new PngEncoder());
        byte[] decoded = Convert.FromBase64String(base64);

        Assert.Equal(ImageFormat.Png, Image.DetectFormat(decoded));
        using Image<Rgb24> reloaded = Image.Load<Rgb24>(decoded);
        Assert.Equal(0, TestImages.AveragePixelDifference(image, reloaded));
    }

    [Fact]
    public void ToBase64String_AcceptsAFormatInsteadOfAnEncoder()
    {
        using Image<Rgb24> image = TestImages.Gradient(4, 4);

        Assert.Equal(image.ToBase64String(new PngEncoder()), image.ToBase64String(ImageFormat.Png));
        Assert.Throws<ArgumentNullException>(() => image.ToBase64String((IImageEncoder)null!));
    }

    [Fact]
    public void Save_TakesAnImageFormat()
    {
        using Image<Rgb24> image = TestImages.Gradient(8, 8);
        using var stream = new MemoryStream();

        image.Save(stream, ImageFormat.Png);

        stream.Position = 0;
        Assert.Equal(ImageFormat.Png, Image.DetectFormat(stream.ToArray()));
        using Image<Rgb24> reloaded = Image.Load<Rgb24>(stream);
        Assert.Equal(0, TestImages.AveragePixelDifference(image, reloaded));
    }

    [Fact]
    public async Task SaveAsync_TakesAnImageFormat()
    {
        using Image<Rgb24> image = TestImages.Gradient(8, 8);
        using var stream = new MemoryStream();

        await image.SaveAsync(stream, ImageFormat.Bmp);

        Assert.Equal(ImageFormat.Bmp, Image.DetectFormat(stream.ToArray()));
    }

    [Fact]
    public void Save_WithAFormatThatCannotBeEncoded_Throws()
    {
        using Image<Rgb24> image = TestImages.Gradient(4, 4);
        using var stream = new MemoryStream();

        ImageFormat? readOnlyFormat = ImageFormat.All.FirstOrDefault(f => !f.CanEncode);
        if (readOnlyFormat is not null)
        {
            Assert.Throws<NotSupportedException>(() => image.Save(stream, readOnlyFormat));
        }

        Assert.Throws<ArgumentNullException>(() => image.Save(stream, (ImageFormat)null!));
    }

    // ----- Multi-image ProcessPixelRows -----

    [Fact]
    public void ProcessPixelRows_OverTwoImages_GivesBothAccessors()
    {
        using Image<Rgb24> source = TestImages.Gradient(6, 4);
        using var destination = new Image<L8>(6, 4);

        source.ProcessPixelRows(destination, (from, to) =>
        {
            Assert.Equal(6, from.Width);
            Assert.Equal(4, to.Height);
            for (int y = 0; y < from.Height; y++)
            {
                Span<Rgb24> input = from.GetRowSpan(y);
                Span<L8> output = to.GetRowSpan(y);
                for (int x = 0; x < input.Length; x++)
                {
                    output[x] = L8.FromRgba32(input[x].ToRgba32());
                }
            }
        });

        Assert.Equal(L8.FromRgba32(source[3, 2].ToRgba32()), destination[3, 2]);
    }

    [Fact]
    public void ProcessPixelRows_OverThreeImages_GivesAllThreeAccessors()
    {
        using var a = new Image<L8>(4, 2, new L8(10));
        using var b = new Image<L8>(4, 2, new L8(30));
        using var sum = new Image<L8>(4, 2);

        a.ProcessPixelRows(b, sum, (first, second, third) =>
        {
            for (int y = 0; y < first.Height; y++)
            {
                Span<L8> left = first.GetRowSpan(y);
                Span<L8> right = second.GetRowSpan(y);
                Span<L8> output = third.GetRowSpan(y);
                for (int x = 0; x < left.Length; x++)
                {
                    output[x] = new L8((byte)((left[x].PackedValue + right[x].PackedValue) / 2));
                }
            }
        });

        Assert.Equal(new L8(20), sum[0, 0]);
        Assert.Equal(new L8(20), sum[3, 1]);
    }

    [Fact]
    public void ProcessPixelRows_RejectsNullArguments()
    {
        using var image = new Image<L8>(2, 2);
        using var other = new Image<L8>(2, 2);

        Assert.Throws<ArgumentNullException>(() => image.ProcessPixelRows((PixelAccessorAction<L8>)null!));
        Assert.Throws<ArgumentNullException>(() => image.ProcessPixelRows((Image<L8>)null!, (a, b) => { }));
        Assert.Throws<ArgumentNullException>(() => image.ProcessPixelRows(other, (PixelAccessorAction<L8, L8>)null!));
        Assert.Throws<ArgumentNullException>(() => image.ProcessPixelRows(other, (Image<L8>)null!, (a, b, c) => { }));
    }

    // ----- Frame collection -----

    [Fact]
    public void InsertFrame_PutsACopyAtTheGivenIndex()
    {
        using var image = new Image<L8>(2, 2, new L8(1));
        using var other = new Image<L8>(2, 2, new L8(2));

        ImageFrame<L8> inserted = image.Frames.InsertFrame(0, other.Frames.RootFrame);

        Assert.Equal(2, image.Frames.Count);
        Assert.Same(inserted, image.Frames.RootFrame);
        Assert.Equal(new L8(2), image[0, 0]);
        Assert.Equal(new L8(1), image.Frames[1][0, 0]);

        // The insert took a copy: the source image is untouched by later writes.
        inserted[0, 0] = new L8(9);
        Assert.Equal(new L8(2), other[0, 0]);
    }

    [Fact]
    public void InsertFrame_AtCount_Appends()
    {
        using var image = new Image<L8>(2, 2, new L8(1));
        using var other = new Image<L8>(2, 2, new L8(2));

        image.Frames.InsertFrame(image.Frames.Count, other.Frames.RootFrame);

        Assert.Equal(2, image.Frames.Count);
        Assert.Equal(new L8(2), image.Frames[1][0, 0]);
    }

    [Fact]
    public void InsertFrame_ValidatesItsArguments()
    {
        using var image = new Image<L8>(2, 2);

        Assert.Throws<ArgumentNullException>(() => image.Frames.InsertFrame(0, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Frames.InsertFrame(2, image.Frames.RootFrame));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Frames.InsertFrame(-1, image.Frames.RootFrame));
    }

    [Fact]
    public void MoveFrame_ReordersWithoutCopying()
    {
        using var image = new Image<L8>(2, 2, new L8(1));
        image.Frames.AddFrame(new Image<L8>(2, 2, new L8(2)).Frames.RootFrame);
        image.Frames.AddFrame(new Image<L8>(2, 2, new L8(3)).Frames.RootFrame);

        ImageFrame<L8> last = image.Frames[2];
        image.Frames.MoveFrame(2, 0);

        Assert.Same(last, image.Frames.RootFrame);
        Assert.Equal(new L8(3), image.Frames[0][0, 0]);
        Assert.Equal(new L8(1), image.Frames[1][0, 0]);
        Assert.Equal(new L8(2), image.Frames[2][0, 0]);
    }

    [Fact]
    public void MoveFrame_ToItsOwnIndex_IsANoOp()
    {
        using var image = new Image<L8>(2, 2, new L8(1));
        image.Frames.AddFrame(new Image<L8>(2, 2, new L8(2)).Frames.RootFrame);

        ImageFrame<L8> root = image.Frames.RootFrame;
        image.Frames.MoveFrame(0, 0);

        Assert.Same(root, image.Frames.RootFrame);
        Assert.Equal(2, image.Frames.Count);
    }

    [Fact]
    public void MoveFrame_ValidatesBothIndexes()
    {
        using var image = new Image<L8>(2, 2);
        image.Frames.CreateFrame(2, 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => image.Frames.MoveFrame(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Frames.MoveFrame(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Frames.MoveFrame(-1, 0));
    }

    [Fact]
    public void AddFrame_FromPixels_UsesTheRootFrameSize()
    {
        using var image = new Image<L8>(3, 2);
        var pixels = new L8[6];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new L8((byte)(i + 1));
        }

        ImageFrame<L8> frame = image.Frames.AddFrame(pixels);

        Assert.Equal(2, image.Frames.Count);
        Assert.Equal(3, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(new L8(1), frame[0, 0]);
        Assert.Equal(new L8(6), frame[2, 1]);

        // The frame took a copy of the caller's buffer.
        pixels[0] = new L8(99);
        Assert.Equal(new L8(1), frame[0, 0]);
    }

    [Fact]
    public void AddFrame_FromPixels_RejectsBuffersThatAreTooSmall()
    {
        using var image = new Image<L8>(3, 2);
        Assert.Throws<ArgumentException>(() => image.Frames.AddFrame(new L8[5]));
    }

    [Fact]
    public void RemoveFrame_HasDocumentedBoundsBehaviour()
    {
        using var image = new Image<L8>(2, 2, new L8(1));

        // The last-frame rule is checked before the index.
        Assert.Throws<InvalidOperationException>(() => image.Frames.RemoveFrame(0));
        Assert.Throws<InvalidOperationException>(() => image.Frames.RemoveFrame(7));

        image.Frames.AddFrame(new Image<L8>(2, 2, new L8(2)).Frames.RootFrame);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Frames.RemoveFrame(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Frames.RemoveFrame(-1));

        // Removing the root frame promotes the next one.
        image.Frames.RemoveFrame(0);
        Assert.Single(image.Frames);
        Assert.Equal(new L8(2), image[0, 0]);
    }

    [Fact]
    public void Frames_EnumerationSeesTheLiveCollection()
    {
        using var image = new Image<L8>(2, 2);
        image.Frames.CreateFrame(2, 2);

        Assert.Equal(2, image.Frames.Count());
        Assert.Equal(image.Frames.ToArray(), image.Frames.ToArray());

        // Structural changes during enumeration invalidate it.
        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (ImageFrame<L8> frame in image.Frames)
            {
                image.Frames.CreateFrame(2, 2);
            }
        });
    }

    [Fact]
    public void Frames_WritingPixelsDuringEnumerationIsFine()
    {
        using var image = new Image<L8>(2, 2);
        image.Frames.CreateFrame(2, 2);

        foreach (ImageFrame<L8> frame in image.Frames)
        {
            frame[0, 0] = new L8(5);
        }

        Assert.Equal(new L8(5), image[0, 0]);
        Assert.Equal(new L8(5), image.Frames[1][0, 0]);
    }

    // ----- Disposal contract -----

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var image = new Image<Rgb24>(2, 2);
        image.Dispose();
        image.Dispose();

        Assert.Throws<ObjectDisposedException>(() => image.Clone());
    }

    [Fact]
    public void Dispose_MakesEveryPixelEntryPointThrow()
    {
        var image = new Image<Rgb24>(4, 4);
        var second = new Image<Rgb24>(4, 4);
        ImageFrame<Rgb24> frame = image.Frames.RootFrame;
        image.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = image[0, 0]; });
        Assert.Throws<ObjectDisposedException>(() => image[0, 0] = default);
        Assert.Throws<ObjectDisposedException>(() => { _ = frame[0, 0]; });
        Assert.Throws<ObjectDisposedException>(() => frame[0, 0] = default);
        Assert.Throws<ObjectDisposedException>(() => frame.GetRowSpan(0));
        Assert.Throws<ObjectDisposedException>(() => image.ProcessPixelRows(_ => { }));
        Assert.Throws<ObjectDisposedException>(() => frame.ProcessPixelRows(_ => { }));
        Assert.Throws<ObjectDisposedException>(() => image.ProcessPixelRows(second, (a, b) => { }));
        Assert.Throws<ObjectDisposedException>(() => second.ProcessPixelRows(image, (a, b) => { }));
        Assert.Throws<ObjectDisposedException>(() => image.ProcessPixelRows(second, second, (a, b, c) => { }));
        Assert.Throws<ObjectDisposedException>(() => image.CopyPixelDataTo(new Rgb24[16]));
        Assert.Throws<ObjectDisposedException>(() => image.CopyPixelDataTo(new byte[48]));
        Assert.Throws<ObjectDisposedException>(() => image.CopyPixelDataTo(new byte[64], 16));
        Assert.Throws<ObjectDisposedException>(() => image.GetPixelBytes());
        Assert.Throws<ObjectDisposedException>(() => image.DangerousTryGetSinglePixelMemory(out _));
        Assert.Throws<ObjectDisposedException>(() => frame.DangerousTryGetSinglePixelMemory(out _));
        Assert.Throws<ObjectDisposedException>(() => image.Clone());
        Assert.Throws<ObjectDisposedException>(() => image.CloneAs<Rgba32>());

        second.Dispose();
    }

    [Fact]
    public void Dispose_MakesEverySaveEntryPointThrow()
    {
        var image = new Image<Rgb24>(4, 4);
        image.Dispose();
        using var stream = new MemoryStream();

        Assert.Throws<ObjectDisposedException>(() => image.Save(stream, new PngEncoder()));
        Assert.Throws<ObjectDisposedException>(() => image.Save(stream, ImageFormat.Png));
        Assert.Throws<ObjectDisposedException>(() => image.SaveAsPng(stream));
        Assert.Throws<ObjectDisposedException>(() => image.ToBase64String(new PngEncoder()));
        Assert.Throws<ObjectDisposedException>(() => image.ToBase64String(ImageFormat.Png));
    }

    [Fact]
    public void Dispose_MakesTensorExtensionsThrow()
    {
        var image = new Image<Rgb24>(4, 4);
        image.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = image.ToChwTensor(); });
        Assert.Throws<ObjectDisposedException>(() => { _ = image.ToHwcTensor(); });
        Assert.Throws<ObjectDisposedException>(() => { _ = image.ToGrayscaleTensor(); });
    }

    [Fact]
    public void Dispose_MakesFrameEditingThrow()
    {
        var image = new Image<Rgb24>(4, 4);
        var donor = new Image<Rgb24>(4, 4);
        image.Dispose();

        Assert.Throws<ObjectDisposedException>(() => image.Frames.AddFrame(donor.Frames.RootFrame));
        Assert.Throws<ObjectDisposedException>(() => image.Frames.AddFrame(new Rgb24[16]));
        Assert.Throws<ObjectDisposedException>(() => image.Frames.InsertFrame(0, donor.Frames.RootFrame));
        Assert.Throws<ObjectDisposedException>(() => image.Frames.CreateFrame(2, 2));
        Assert.Throws<ObjectDisposedException>(() => image.Frames.CloneFrame(0));
        Assert.Throws<ObjectDisposedException>(() => image.Frames.MoveFrame(0, 0));
        Assert.Throws<ObjectDisposedException>(() => image.Frames.RemoveFrame(0));
        Assert.Throws<ObjectDisposedException>(() => image.Frames.ExportFrame(0));

        // A frame taken out of an image before it was disposed keeps working.
        donor.Dispose();
    }

    [Fact]
    public void Dispose_DoesNotReachFramesExportedEarlier()
    {
        var image = new Image<L8>(2, 2, new L8(4));
        image.Frames.CreateFrame(2, 2);

        using Image<L8> exported = image.Frames.ExportFrame(0);
        image.Dispose();

        Assert.Equal(new L8(4), exported[0, 0]);
    }

    [Fact]
    public void DisposedImage_StillReportsItsSize()
    {
        var image = new Image<Rgb24>(6, 3);
        image.Dispose();

        // Size is metadata, not pixel access, and stays readable for logging.
        Assert.Equal(6, image.Width);
        Assert.Equal(3, image.Height);
        Assert.Equal(new Size(6, 3), image.Size);
    }

    // ----- Concurrency -----

    [Fact]
    public void DistinctImages_CanBeEncodedAndDecodedInParallel()
    {
        byte[][] encoded = new byte[8][];
        Parallel.For(0, 8, i =>
        {
            using Image<Rgb24> image = TestImages.Gradient(16 + i, 16);
            using var stream = new MemoryStream();
            image.SaveAsPng(stream);
            encoded[i] = stream.ToArray();
        });

        Parallel.For(0, 8, i =>
        {
            using Image<Rgb24> decoded = Image.Load<Rgb24>(encoded[i]);
            Assert.Equal(16 + i, decoded.Width);
        });
    }
}

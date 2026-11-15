using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Bmp;
using EasyImageSharp.Formats.Gif;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Formats.Qoi;
using EasyImageSharp.Formats.Tga;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.Formats.Webp;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Streaming;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests;

/// <summary>
/// <see cref="Image.Transcode(Stream, Stream, TranscodeOptions?, CancellationToken)"/> end to end. The oracle is the
/// in-memory path the transcoder replaces - <c>Image.Load</c>, <c>Mutate(AutoOrient().Resize())</c>, <c>Save</c> -
/// whose own output is checked against independent references elsewhere in the suite.
/// </summary>
public class TranscodeTests
{
    private readonly ITestOutputHelper output;

    public TranscodeTests(ITestOutputHelper output) => this.output = output;

    public static TheoryData<string> ResizeCases()
    {
        var data = new TheoryData<string>();
        foreach (string name in Cases.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    private static readonly Dictionary<string, ResizeOptions?> Cases = new()
    {
        ["none"] = null,
        ["stretch-bicubic"] = new ResizeOptions { Size = new Size(41, 23) },
        ["max-lanczos3"] = new ResizeOptions { Size = new Size(40, 40), Mode = ResizeMode.Max, Sampler = KnownResamplers.Lanczos3 },
        ["min-triangle"] = new ResizeOptions { Size = new Size(30, 30), Mode = ResizeMode.Min, Sampler = KnownResamplers.Triangle },
        ["pad-topleft"] = new ResizeOptions { Size = new Size(50, 50), Mode = ResizeMode.Pad, Position = AnchorPositionMode.TopLeft, PadColor = Color.Red },
        ["pad-center"] = new ResizeOptions { Size = new Size(51, 49), Mode = ResizeMode.Pad, PadColor = Color.Blue },
        ["boxpad-fits"] = new ResizeOptions { Size = new Size(200, 150), Mode = ResizeMode.BoxPad, PadColor = Color.Green },
        ["boxpad-shrinks"] = new ResizeOptions { Size = new Size(30, 30), Mode = ResizeMode.BoxPad },
        ["crop-right"] = new ResizeOptions { Size = new Size(30, 30), Mode = ResizeMode.Crop, Position = AnchorPositionMode.Right },
        ["crop-centre"] = new ResizeOptions { Size = new Size(25, 35), Mode = ResizeMode.Crop, CenterCoordinates = new PointF(0.8f, 0.2f) },
        ["manual-clipped"] = new ResizeOptions
        {
            Size = new Size(60, 40), Mode = ResizeMode.Manual, TargetRectangle = new Rectangle(-10, 15, 50, 40), PadColor = Color.White,
        },
        ["manual-outside"] = new ResizeOptions { Size = new Size(20, 20), Mode = ResizeMode.Manual, TargetRectangle = new Rectangle(30, 30, 5, 5) },
        ["width-only-nearest"] = new ResizeOptions { Size = new Size(33, 0), Sampler = KnownResamplers.NearestNeighbor },
        ["enlarge-compand"] = new ResizeOptions { Size = new Size(150, 0), Compand = true, Sampler = KnownResamplers.MitchellNetravali },
        ["straight-alpha"] = new ResizeOptions { Size = new Size(37, 29), PremultiplyAlpha = false, Sampler = KnownResamplers.Spline },
    };

    [Theory]
    [MemberData(nameof(ResizeCases))]
    public void PngToPng_StreamsBothEnds_AndWritesExactlyWhatLoadMutateSaveWrites(string name)
    {
        byte[] input = SourcePng(97, 61, seed: 1);
        var options = new TranscodeOptions { Resize = Cases[name] };
        byte[] expected = Reference(input, options, new PngEncoder());

        foreach (Stream source in new Stream[] { new MemoryStream(input), new ForwardOnlyStream(input, maxChunk: 333) })
        {
            var destination = new RecordingOutputStream();
            TranscodeResult result = Image.Transcode(source, destination, options);
            Assert.True(result.StreamedDecode);
            Assert.True(result.StreamedEncode);
            Assert.Same(ImageFormat.Png, result.SourceFormat);
            Assert.Equal(new Size(97, 61), result.SourceSize);
            byte[] actual = destination.ToArray();
            StreamingTestSupport.AssertSamePngModuloIdatSplit(expected, actual);
            using Image<Rgba32> decoded = Image.Load<Rgba32>(actual);
            Assert.Equal(result.OutputSize, decoded.Size);
        }
    }

    [Fact]
    public async Task Async_ReadsAndWritesAsynchronously_AndWritesWhatTheSyncPathWrites()
    {
        byte[] input = SourcePng(600, 400, seed: 2);
        var options = new TranscodeOptions { Resize = new ResizeOptions { Size = new Size(120, 0), Sampler = KnownResamplers.Lanczos3 } };
        var syncOutput = new MemoryStream();
        Image.Transcode(new MemoryStream(input), syncOutput, options);

        var source = new ForwardOnlyStream(input, maxChunk: 4000);
        var destination = new RecordingOutputStream();
        TranscodeResult result = await Image.TranscodeAsync(source, destination, options);
        Assert.True(result.StreamedDecode && result.StreamedEncode);
        Assert.Equal(syncOutput.ToArray(), destination.ToArray());
        Assert.Equal(0, source.SyncReads);
        Assert.True(source.AsyncReads > 0);
        Assert.Equal(0, destination.SyncWrites);
        Assert.True(destination.AsyncWrites > 0);
    }

    public static TheoryData<string> FallbackSources() => new()
    {
        "png/rgb8_adam7.png",
        "png/pal4_adam7.png",
        "apng/loop_forever.png",
        "apng/hidden_first_frame.png",
        "bmp/pal4.bmp",
        "gif/animated_3frames.gif",
        "tiff/bigtiff_be_rgb.tif",
        "webp/alph_lossless_f2.webp",
    };

    [Theory]
    [MemberData(nameof(FallbackSources))]
    public void SourcesWithoutARowDecoder_AreDecodedInFull_WithTheSameResult(string fixture)
    {
        byte[] input = FixturePath.Read(fixture);
        foreach (ResizeOptions? resize in new[] { null, new ResizeOptions { Size = new Size(17, 13), Mode = ResizeMode.Pad } })
        {
            var options = new TranscodeOptions { Resize = resize, Encoder = new PngEncoder() };
            var destination = new MemoryStream();
            TranscodeResult result = Image.Transcode(new ForwardOnlyStream(input, 1000), destination, options);
            Assert.False(result.StreamedDecode);
            Assert.True(result.StreamedEncode);
            StreamingTestSupport.AssertSamePngModuloIdatSplit(Reference(input, options, new PngEncoder()), destination.ToArray());
        }
    }

    public static TheoryData<string> FallbackEncoders() => new()
    {
        "bmp", "qoi", "tga", "tiff", "gif", "webp", "png-palette", "png-adam7", "png-gray1",
    };

    [Theory]
    [InlineData("jpeg/baseline_420.jpg")]
    public void JpegSource_IsStreamedRowByRow_AndMatchesTheBufferedPath(string fixture)
    {
        byte[] input = FixturePath.Read(fixture);

        // Without a resize the streamed decode is the full-size IDCT: byte-identical to the buffered path.
        var exact = new TranscodeOptions { Encoder = new PngEncoder() };
        var destination = new MemoryStream();
        TranscodeResult result = Image.Transcode(new ForwardOnlyStream(input, 1000), destination, exact);
        Assert.True(result.StreamedDecode);
        Assert.True(result.StreamedEncode);
        StreamingTestSupport.AssertSamePngModuloIdatSplit(Reference(input, exact, new PngEncoder()), destination.ToArray());

        // A shrinking resize may decode through the reduced IDCT (see ReducedDecode), which is close to, but by
        // design not bit-identical with, a full decode followed by the same resize.
        var reduced = new TranscodeOptions { Resize = new ResizeOptions { Size = new Size(17, 13), Mode = ResizeMode.Pad }, Encoder = new PngEncoder() };
        destination = new MemoryStream();
        result = Image.Transcode(new ForwardOnlyStream(input, 1000), destination, reduced);
        Assert.True(result.StreamedDecode);
        using Image<Rgba32> expected = Image.Load<Rgba32>(Reference(input, reduced, new PngEncoder()));
        using Image<Rgba32> actual = Image.Load<Rgba32>(destination.ToArray());
        Assert.Equal(expected.Size, actual.Size);
        double totalError = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgba32 e = expected[x, y], a = actual[x, y];
                totalError += Math.Abs(e.R - a.R) + Math.Abs(e.G - a.G) + Math.Abs(e.B - a.B) + Math.Abs(e.A - a.A);
            }
        }

        double meanError = totalError / (expected.Width * expected.Height * 4.0);
        this.output.WriteLine(FormattableString.Invariant($"Reduced-decode mean absolute channel error: {meanError:F3}"));
        Assert.True(meanError < 4.0, $"Mean absolute channel error {meanError:F3} is too large.");
    }

    [Fact]
    public void JpegEncoder_IsStreamedRowByRow_AndWritesTheSameBytes()
    {
        var encoder = new JpegEncoder();
        byte[] input = SourcePng(80, 50, seed: 3);
        var options = new TranscodeOptions { Resize = new ResizeOptions { Size = new Size(40, 40), Mode = ResizeMode.Crop }, Encoder = encoder };
        var destination = new MemoryStream();
        TranscodeResult result = Image.Transcode(new MemoryStream(input), destination, options);
        Assert.True(result.StreamedDecode);
        Assert.True(result.StreamedEncode);
        Assert.Equal(Reference(input, options, encoder), destination.ToArray());
    }

    [Theory]
    [MemberData(nameof(FallbackEncoders))]
    public void EncodersWithoutARowEncoder_GetTheCollectedImage_AndWriteTheSameBytes(string name)
    {
        IImageEncoder encoder = name switch
        {
            "bmp" => new BmpEncoder(),
            "qoi" => new QoiEncoder(),
            "tga" => new TgaEncoder(),
            "tiff" => new TiffEncoder(),
            "gif" => new GifEncoder(),
            "jpeg" => new JpegEncoder(),
            "webp" => new WebpEncoder(),
            "png-palette" => new PngEncoder { ColorType = PngColorType.Palette },
            "png-adam7" => new PngEncoder { InterlaceMethod = PngInterlaceMethod.Adam7 },
            _ => new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit1 },
        };

        byte[] input = SourcePng(80, 50, seed: 3);
        var options = new TranscodeOptions { Resize = new ResizeOptions { Size = new Size(40, 40), Mode = ResizeMode.Crop }, Encoder = encoder };
        var destination = new MemoryStream();
        TranscodeResult result = Image.Transcode(new MemoryStream(input), destination, options);
        Assert.True(result.StreamedDecode);
        Assert.False(result.StreamedEncode);
        Assert.Equal(Reference(input, options, encoder), destination.ToArray());
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
    public void ExifOrientation_IsAppliedToTheOutput_AsAutoOrientAppliesIt(int orientation)
    {
        byte[] jpeg = FixturePath.Read($"metadata/orient_{orientation}.jpg");
        AssertOrientedLikeReference(jpeg, orientation, streamedDecode: true);

        using Image<Rgba32> image = StreamingTestSupport.Noise<Rgba32>(45, 28, seed: orientation);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)orientation);
        image.Metadata.ExifProfile.SetValue(ExifTag.PixelXDimension, 45u);
        image.Metadata.ExifProfile.SetValue(ExifTag.PixelYDimension, 28u);
        AssertOrientedLikeReference(StreamingTestSupport.Encode(image, new PngEncoder()), orientation, streamedDecode: true);
    }

    [Fact]
    public void AutoOrientOff_KeepsTheStoredPixelsAndTheTag()
    {
        byte[] jpeg = FixturePath.Read("metadata/orient_6.jpg");
        var destination = new MemoryStream();
        TranscodeResult result = Image.Transcode(new MemoryStream(jpeg), destination, new TranscodeOptions { AutoOrient = false, Format = ImageFormat.Png });
        Assert.Equal(new Size(64, 48), result.OutputSize);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(destination.ToArray());
        Assert.Equal((ushort)6, decoded.Metadata.ExifProfile!.GetValue(ExifTag.Orientation)!.Value);
    }

    [Fact]
    public void MetadataPolicy_PreservesKeepsTheColourProfileOrStrips()
    {
        using Image<Rgba32> image = StreamingTestSupport.Noise<Rgba32>(30, 20, seed: 4);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Software, "camera firmware");
        image.Metadata.IccProfile = new IccProfile(FixturePath.Read("metadata/icc_profile.bin"));
        image.Metadata.XmpProfile = new XmpProfile("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray());
        image.Metadata.SetResolution(300, 300, PixelResolutionUnit.PixelsPerInch);
        image.Metadata.GetPngMetadata().TextData.Add(new PngTextData("Author", "someone"));
        byte[] input = StreamingTestSupport.Encode(image, new PngEncoder());

        using (Image<Rgba32> preserved = TranscodeAndLoad(input, TranscodeMetadata.Preserve))
        {
            Assert.Equal("camera firmware", preserved.Metadata.ExifProfile!.GetValue(ExifTag.Software)!.Value);
            Assert.NotNull(preserved.Metadata.IccProfile);
            Assert.NotNull(preserved.Metadata.XmpProfile);
            Assert.Equal(300, preserved.Metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerInch), 1);
            Assert.Equal("someone", Assert.Single(preserved.Metadata.GetPngMetadata().TextData).Value);
        }

        using (Image<Rgba32> colour = TranscodeAndLoad(input, TranscodeMetadata.KeepColorProfile))
        {
            Assert.Null(colour.Metadata.ExifProfile);
            Assert.Equal(image.Metadata.IccProfile.ToByteArray(), colour.Metadata.IccProfile!.ToByteArray());
            Assert.Null(colour.Metadata.XmpProfile);
            Assert.Empty(colour.Metadata.GetPngMetadata().TextData);
        }

        using (Image<Rgba32> stripped = TranscodeAndLoad(input, TranscodeMetadata.Strip))
        {
            Assert.Null(stripped.Metadata.ExifProfile);
            Assert.Null(stripped.Metadata.IccProfile);
            Assert.Null(stripped.Metadata.XmpProfile);
            Assert.Empty(stripped.Metadata.GetPngMetadata().TextData);
        }
    }

    [Fact]
    public void ASourceOverMaxSourcePixels_IsRejectedBeforeAnythingIsWritten()
    {
        byte[] input = SourcePng(300, 300, seed: 5);
        var destination = new MemoryStream();
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Transcode(
            new ForwardOnlyStream(input), destination, new TranscodeOptions { MaxSourcePixels = 89_999 }));
        Assert.Equal(0, destination.Length);

        // The default is DecoderOptions.MaxTotalPixels.
        var tight = new DecoderOptions { MaxTotalPixels = 89_999 };
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Transcode(
            new MemoryStream(input), destination, new TranscodeOptions { DecoderOptions = tight }));
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public void AStreamedSourceLargerThanMaxPixels_IsAccepted_ButAMaterialisedOneIsNot()
    {
        byte[] input = SourcePng(300, 300, seed: 6);
        var limits = new DecoderOptions { MaxPixels = 10_000 };
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(input, limits));

        var thumbnail = new TranscodeOptions { DecoderOptions = limits, Resize = new ResizeOptions { Size = new Size(64, 64) } };
        var destination = new MemoryStream();
        TranscodeResult result = Image.Transcode(new MemoryStream(input), destination, thumbnail);
        Assert.True(result.StreamedDecode);
        Assert.Equal(new Size(64, 64), result.OutputSize);

        // An interlaced source has to be decoded whole, so MaxPixels applies to it after all.
        using Image<Rgba32> image = StreamingTestSupport.Noise<Rgba32>(300, 300, seed: 7);
        byte[] interlaced = StreamingTestSupport.Encode(image, new PngEncoder { InterlaceMethod = PngInterlaceMethod.Adam7 });
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Transcode(new MemoryStream(interlaced), new MemoryStream(), thumbnail));

        // And so does an output that is collected for an encoder that needs the whole image.
        var bmp = new TranscodeOptions { DecoderOptions = limits, Encoder = new BmpEncoder() };
        destination = new MemoryStream();
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Transcode(new MemoryStream(input), destination, bmp));
        Assert.Equal(0, destination.Length);

        // Or one that is turned upright.
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        byte[] rotated = StreamingTestSupport.Encode(image, new PngEncoder());
        Assert.Throws<ImageSizeLimitExceededException>(
            () => Image.Transcode(new MemoryStream(rotated), new MemoryStream(), new TranscodeOptions { DecoderOptions = limits }));
    }

    [Fact]
    public void ASourceTooWideForTheWorkingSetBudget_IsRejectedBeforeAnyRow()
    {
        // 400 000 x 1 pixels is within every pixel limit, but its rows alone need more than the 16 MiB budget that
        // MaxPixels = 1 000 000 implies (four bytes a pixel would be 4 MB, so the 16 MiB floor applies).
        byte[] input = StreamingTestSupport.AssemblePng(400_000, 1, 8, 6, new byte[1 + (400_000 * 4)]);
        var options = new TranscodeOptions
        {
            DecoderOptions = new DecoderOptions { MaxPixels = 1_000_000 },
            Resize = new ResizeOptions { Size = new Size(100, 1) },
        };
        var destination = new MemoryStream();
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Transcode(new MemoryStream(input), destination, options));
        Assert.Equal(0, destination.Length);
        Assert.Equal(16L << 20, TranscodePipeline<Rgba32>.WorkingSetBudget(options.DecoderOptions));
    }

    [Fact]
    public async Task MalformedInput_KeepsTheExceptionContract()
    {
        byte[] input = SourcePng(64, 64, seed: 8);
        Assert.Throws<InvalidImageContentException>(
            () => Image.Transcode(new MemoryStream(input.AsSpan(0, input.Length / 2).ToArray()), new MemoryStream()));
        Assert.Throws<InvalidImageContentException>(
            () => Image.Transcode(new ForwardOnlyStream(input.AsSpan(0, input.Length / 2).ToArray()), new MemoryStream()));
        Assert.Throws<UnknownImageFormatException>(() => Image.Transcode(new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8, 9]), new MemoryStream()));
        Assert.Throws<UnknownImageFormatException>(() => Image.Transcode(new MemoryStream(), new MemoryStream()));
        await Assert.ThrowsAsync<UnknownImageFormatException>(() => Image.TranscodeAsync(new ForwardOnlyStream([]), new MemoryStream()));

        byte[] bmp = FixturePath.Read("bmp/pal4.bmp");
        Assert.Throws<InvalidImageContentException>(() => Image.Transcode(new MemoryStream(bmp.AsSpan(0, 60).ToArray()), new MemoryStream()));

        var random = new Random(9);
        for (int i = 0; i < 200; i++)
        {
            byte[] mutated = (byte[])input.Clone();
            mutated[random.Next(8, Math.Min(120, mutated.Length))] ^= (byte)(1 << random.Next(8));
            mutated[random.Next(8, mutated.Length)] ^= (byte)(1 << random.Next(8));
            try
            {
                Image.Transcode(
                    new ForwardOnlyStream(mutated, 501),
                    new MemoryStream(),
                    new TranscodeOptions { DecoderOptions = new DecoderOptions { MaxPixels = 1 << 20 }, Resize = new ResizeOptions { Size = new Size(9, 9) } });
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
            {
            }
        }
    }

    [Fact]
    public async Task Cancellation_IsHonouredBeforeAndDuringTheTranscode()
    {
        byte[] input = SourcePng(200, 200, seed: 10);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => Image.Transcode(new MemoryStream(input), new MemoryStream(), null, cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Image.TranscodeAsync(new MemoryStream(input), new MemoryStream(), null, cancelled.Token));
        }

        using (var midway = new CancellationTokenSource())
        {
            var source = new ForwardOnlyStream(input, maxChunk: 512) { CancelAfterSource = midway, CancelAfter = input.Length / 2 };
            Assert.ThrowsAny<OperationCanceledException>(() => Image.Transcode(source, new MemoryStream(), null, midway.Token));
        }

        using (var viaOptions = new CancellationTokenSource())
        {
            var source = new ForwardOnlyStream(input, maxChunk: 512) { CancelAfterSource = viaOptions, CancelAfter = input.Length / 2 };
            var options = new TranscodeOptions
            {
                DecoderOptions = new DecoderOptions { ExecutionOptions = new ImageExecutionOptions { CancellationToken = viaOptions.Token } },
            };
            Assert.ThrowsAny<OperationCanceledException>(() => Image.Transcode(source, new MemoryStream(), options));
        }
    }

    [Fact]
    public async Task Thumbnail_FitsTheBox_NeverEnlarges_AndKeepsOnlyTheColourProfile()
    {
        using Image<Rgba32> image = StreamingTestSupport.Noise<Rgba32>(300, 200, seed: 11);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Software, "camera");
        image.Metadata.IccProfile = new IccProfile(FixturePath.Read("metadata/icc_profile.bin"));
        byte[] input = StreamingTestSupport.Encode(image, new PngEncoder());

        var destination = new MemoryStream();
        TranscodeResult result = Image.Thumbnail(new MemoryStream(input), destination, 100, 100);
        Assert.Equal(new Size(100, 67), result.OutputSize);
        using (Image<Rgba32> thumbnail = Image.Load<Rgba32>(destination.ToArray()))
        {
            Assert.Null(thumbnail.Metadata.ExifProfile);
            Assert.NotNull(thumbnail.Metadata.IccProfile);
            using Image<Rgba32> expected = Image.Load<Rgba32>(input);
            expected.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(100, 100), Mode = ResizeMode.Max }));
            Assert.Equal(StreamingTestSupport.Pixels(expected), StreamingTestSupport.Pixels(thumbnail));
        }

        byte[] small = SourcePng(20, 10, seed: 12);
        destination = new MemoryStream();
        Assert.Equal(new Size(20, 10), (await Image.ThumbnailAsync(new MemoryStream(small), destination, 100, 100)).OutputSize);

        Assert.Throws<ArgumentOutOfRangeException>(() => Image.Thumbnail(new MemoryStream(small), new MemoryStream(), 0, 10));
    }

    [Fact]
    public void TheOutputFormat_DefaultsToTheSourcesWhenItCanBeEncoded()
    {
        byte[] bmp = FixturePath.Read("bmp/pal4.bmp");
        var destination = new MemoryStream();
        Image.Transcode(new MemoryStream(bmp), destination);
        Assert.Same(ImageFormat.Bmp, Image.DetectFormat(destination.ToArray()));

        destination = new MemoryStream();
        Image.Transcode(new MemoryStream(bmp), destination, new TranscodeOptions { Format = ImageFormat.Qoi });
        Assert.Same(ImageFormat.Qoi, Image.DetectFormat(destination.ToArray()));

        // A custom format that claims the data wins over the built-in PNG decoder, as in Image.Load; without an
        // encoder of its own the output falls back to PNG.
        byte[] png = SourcePng(16, 16, seed: 13);
        int decodes = 0;
        ImageFormat custom = ImageFormat.Create(
            "CUSTOMPNG", "image/x-custom", ["cpng"], static data => data.Length >= 4 && data[1] == (byte)'P',
            () =>
            {
                decodes++;
                return new PngDecoder();
            });
        destination = new MemoryStream();
        TranscodeResult result = Image.Transcode(
            new MemoryStream(png), destination, new TranscodeOptions { DecoderOptions = new DecoderOptions { AdditionalFormats = [custom] } });
        Assert.Same(custom, result.SourceFormat);
        Assert.False(result.StreamedDecode);
        Assert.Equal(1, decodes);
        Assert.Same(ImageFormat.Png, Image.DetectFormat(destination.ToArray()));
    }

    [Fact]
    public void InvalidArguments_AreRejected()
    {
        byte[] png = SourcePng(4, 4, seed: 14);
        Assert.Throws<ArgumentNullException>(() => Image.Transcode(null!, new MemoryStream()));
        Assert.Throws<ArgumentNullException>(() => Image.Transcode(new MemoryStream(png), null!));
        Assert.Throws<ArgumentException>(() => Image.Transcode(new MemoryStream(png), new MemoryStream([], writable: false)));
        Assert.Throws<ArgumentNullException>(
            () => Image.Transcode(new MemoryStream(png), new MemoryStream(), new TranscodeOptions { Resize = new ResizeOptions { Sampler = null! } }));
        Assert.Throws<ArgumentException>(
            () => Image.Transcode(new MemoryStream(png), new MemoryStream(), new TranscodeOptions { Resize = new ResizeOptions { Size = new Size(0, 0) } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranscodeOptions { MaxSourcePixels = 0 });
        Assert.Throws<ArgumentNullException>(() => new TranscodeOptions { DecoderOptions = null! });
    }

    [Fact]
    public void ShrinkOnly_LeavesSourcesThatFitAlone()
    {
        byte[] input = SourcePng(40, 30, seed: 15);
        var options = new TranscodeOptions
        {
            ShrinkOnly = true,
            Resize = new ResizeOptions { Size = new Size(40, 100), Mode = ResizeMode.Pad, PadColor = Color.Red },
        };
        Assert.Equal(new Size(40, 30), Image.Transcode(new MemoryStream(input), new MemoryStream(), options).OutputSize);

        var wider = new TranscodeOptions { ShrinkOnly = true, Resize = new ResizeOptions { Size = new Size(20, 0) } };
        Assert.Equal(new Size(20, 15), Image.Transcode(new MemoryStream(input), new MemoryStream(), wider).OutputSize);
    }

    [Fact]
    public void ThumbnailingALargePng_AllocatesAFractionOfLoadResizeSave()
    {
        const int Size = 2000;
        byte[] input = StreamingTestSupport.AssemblePng(Size, Size, 8, 6, StreamingTestSupport.RgbaScanlines(Size, Size, seed: 16), idatChunkBytes: 1 << 16);
        var resize = new ResizeOptions { Size = new Size(256, 256), Mode = ResizeMode.Max };
        var options = new TranscodeOptions { Resize = resize };

        long streamed = 0;
        long buffered = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Image.Transcode(new MemoryStream(input), Stream.Null, options);
            streamed = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            using (Image<Rgba32> image = Image.Load<Rgba32>(new MemoryStream(input)))
            {
                image.Mutate(ctx => ctx.Resize(resize));
                image.Save(Stream.Null, new PngEncoder());
            }

            buffered = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        this.output.WriteLine(FormattableString.Invariant($"{Size}x{Size} PNG -> 256x256: streamed {streamed:N0} bytes, Load+Resize+Save {buffered:N0} bytes (this thread)."));

        // The decoded frame alone is 16 000 000 bytes.
        Assert.True(streamed < 3_000_000, $"Streaming allocated {streamed:N0} bytes.");
        Assert.True(buffered > 16_000_000, $"The buffered path allocated only {buffered:N0} bytes.");
    }

    private static Image<Rgba32> TranscodeAndLoad(byte[] input, TranscodeMetadata policy)
    {
        var destination = new MemoryStream();
        Image.Transcode(new MemoryStream(input), destination, new TranscodeOptions { Metadata = policy, Resize = new ResizeOptions { Size = new Size(10, 10) } });
        return Image.Load<Rgba32>(destination.ToArray());
    }

    private static void AssertOrientedLikeReference(byte[] input, int orientation, bool streamedDecode)
    {
        foreach (ResizeOptions? resize in new[] { null, new ResizeOptions { Size = new Size(30, 30), Mode = ResizeMode.Max }, new ResizeOptions { Size = new Size(20, 30), Mode = ResizeMode.Crop } })
        {
            var options = new TranscodeOptions { Resize = resize, Encoder = new PngEncoder() };
            var destination = new MemoryStream();
            TranscodeResult result = Image.Transcode(new MemoryStream(input), destination, options);
            Assert.Equal(streamedDecode, result.StreamedDecode);
            byte[] expectedBytes = Reference(input, options, new PngEncoder());
            using Image<Rgba32> expected = Image.Load<Rgba32>(expectedBytes);
            using Image<Rgba32> actual = Image.Load<Rgba32>(destination.ToArray());
            Assert.Equal(expected.Size, actual.Size);
            Assert.Equal(expected.Size, result.OutputSize);

            // The tag is reset (and for 5-8 the EXIF pixel dimensions swapped) exactly as AutoOrient does.
            Assert.Equal(expected.Metadata.ExifProfile?.ToByteArray(), actual.Metadata.ExifProfile?.ToByteArray());
            if (resize is null || orientation == 1)
            {
                Assert.Equal(StreamingTestSupport.Pixels(expected), StreamingTestSupport.Pixels(actual));
                continue;
            }

            // Resizing before turning upright only reorders the floating-point additions.
            Rgba32[] left = StreamingTestSupport.Pixels(expected);
            Rgba32[] right = StreamingTestSupport.Pixels(actual);
            for (int i = 0; i < left.Length; i++)
            {
                Assert.InRange(right[i].R - left[i].R, -1, 1);
                Assert.InRange(right[i].G - left[i].G, -1, 1);
                Assert.InRange(right[i].B - left[i].B, -1, 1);
                Assert.InRange(right[i].A - left[i].A, -1, 1);
            }
        }
    }

    /// <summary>A PNG with alpha, an EXIF block, an ICC profile and a text chunk, as an input for the transcoder.</summary>
    private static byte[] SourcePng(int width, int height, int seed)
    {
        using Image<Rgba32> image = StreamingTestSupport.Noise<Rgba32>(width, height, seed);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Software, "source");
        image.Metadata.IccProfile = new IccProfile(FixturePath.Read("metadata/icc_profile.bin"));
        image.Metadata.GetPngMetadata().TextData.Add(new PngTextData("Title", "source image"));
        return StreamingTestSupport.Encode(image, new PngEncoder { CompressionLevel = System.IO.Compression.CompressionLevel.Fastest });
    }

    /// <summary>
    /// What the in-memory path writes for the same request: the root frame decoded with the same limits, oriented,
    /// resized, given the metadata the policy keeps, and saved with the same encoder.
    /// </summary>
    private static byte[] Reference(byte[] input, TranscodeOptions options, IImageEncoder encoder)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(input, new DecoderOptions { MaxFrames = 1 });
        image.Mutate(ctx =>
        {
            if (options.AutoOrient)
            {
                ctx.AutoOrient();
            }

            if (options.Resize is { } resize)
            {
                ctx.Resize(resize);
            }
        });

        // A multi-frame source keeps only its root frame; the output is one still image.
        if (image.Metadata.TryGetFormatMetadata(out PngMetadata? png))
        {
            png.IsAnimated = false;
        }

        return StreamingTestSupport.Encode(image, encoder);
    }
}

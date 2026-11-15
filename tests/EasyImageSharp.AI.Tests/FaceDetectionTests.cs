using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.AI.Tests;

/// <summary>
/// Face detection and redaction against <c>face_grid_stub.onnx</c>, a graph with the YuNet output contract whose
/// stride-32 cells are "faces" scored by brightness: a bright 32x32 cell at grid (c, r) decodes to a 64x64 box centred
/// on the cell with landmarks at fixed fractions of it. That pins the decoding, the coordinate mapping through padding,
/// downscaling and tiles, the suppression and the redaction regions exactly.
/// </summary>
public class FaceDetectionTests
{
    private static ImageAiSession Session()
    {
        var options = new ImageAiOptions { Offline = true };
        options.ModelPathOverrides["face-detection"] = TestModels.Get("face_grid_stub.onnx");
        return new ImageAiSession(options);
    }

    /// <summary>A black image with white (or <paramref name="value"/>-gray) 32x32 cells at the given grid positions.</summary>
    private static Image<Rgb24> Cells(int width, int height, params (int Col, int Row, byte Value)[] cells)
    {
        var image = new Image<Rgb24>(width, height, new Rgb24(0, 0, 0));
        foreach ((int col, int row, byte value) in cells)
        {
            for (int y = row * 32; y < (row + 1) * 32 && y < height; y++)
            {
                image.Frames.RootFrame.GetRowSpan(y).Slice(col * 32, Math.Min(32, width - (col * 32))).Fill(new Rgb24(value, value, value));
            }
        }

        return image;
    }

    private static void AssertBox(RectangleF expected, RectangleF actual, float tolerance = 0.01f)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
        Assert.InRange(actual.Width, expected.Width - tolerance, expected.Width + tolerance);
        Assert.InRange(actual.Height, expected.Height - tolerance, expected.Height + tolerance);
    }

    // ----- Decoding -----

    [Fact]
    public async Task DetectFaces_DecodesBoxLandmarksAndScore()
    {
        using ImageAiSession ai = Session();
        using Image<Rgb24> image = Cells(160, 128, (2, 1, 255));

        IReadOnlyList<DetectedFace> faces = await image.DetectFacesAsync(ai);

        DetectedFace face = Assert.Single(faces);
        Assert.Equal(1f, face.Confidence, 4);

        // Cell (2,1): centre (80, 48), 64x64 box.
        AssertBox(new RectangleF(48, 16, 64, 64), face.Bounds);
        Assert.Equal(64f + (0.3f * 32f), face.Landmarks.RightEye.X, 3);
        Assert.Equal(32f + (0.4f * 32f), face.Landmarks.RightEye.Y, 3);
        Assert.Equal(64f + (0.7f * 32f), face.Landmarks.LeftEye.X, 3);
        Assert.Equal(64f + (0.5f * 32f), face.Landmarks.NoseTip.X, 3);
        Assert.Equal(32f + (0.55f * 32f), face.Landmarks.NoseTip.Y, 3);
        Assert.Equal(64f + (0.35f * 32f), face.Landmarks.RightMouthCorner.X, 3);
        Assert.Equal(32f + (0.75f * 32f), face.Landmarks.LeftMouthCorner.Y, 3);
    }

    [Fact]
    public void DetectFaces_PaddingToMultiplesOf32DoesNotShiftCoordinates_AndBoxesAreClipped()
    {
        using ImageAiSession ai = Session();

        // 100x70 pads to 128x96; cell (0,0) decodes to (-16,-16,64,64), clipped to the image.
        using Image<Rgb24> image = Cells(100, 70, (0, 0, 255));

        DetectedFace face = Assert.Single(image.DetectFaces(ai));
        AssertBox(new RectangleF(0, 0, 48, 48), face.Bounds);
        Assert.Equal(0.3f * 32f, face.Landmarks.RightEye.X, 3);
    }

    [Fact]
    public void DetectFaces_ScoreThresholdFiltersDimCells()
    {
        using ImageAiSession ai = Session();
        using Image<Rgb24> image = Cells(256, 64, (0, 0, 255), (4, 0, 166), (6, 0, 102));

        // Scores 1.0, 0.65, 0.4: the default threshold 0.6 keeps two (their boxes do not overlap).
        IReadOnlyList<DetectedFace> faces = image.DetectFaces(ai);
        Assert.Equal(2, faces.Count);
        Assert.True(faces[0].Confidence > faces[1].Confidence);

        Assert.Equal(3, image.DetectFaces(ai, new FaceDetectionOptions { ScoreThreshold = 0.3f }).Count);
        Assert.Single(image.DetectFaces(ai, new FaceDetectionOptions { ScoreThreshold = 0.9f }));
    }

    [Fact]
    public void DetectFaces_NonMaximumSuppressionKeepsTheStrongerOfTwoOverlappingBoxes()
    {
        using ImageAiSession ai = Session();

        // Neighbouring cells give 64x64 boxes 32 px apart: IoU 1/3.
        using Image<Rgb24> image = Cells(128, 64, (1, 0, 200), (2, 0, 255));

        DetectedFace kept = Assert.Single(image.DetectFaces(ai));
        Assert.Equal(1f, kept.Confidence, 4);
        AssertBox(new RectangleF(48, 0, 64, 48), kept.Bounds);

        Assert.Equal(2, image.DetectFaces(ai, new FaceDetectionOptions { NmsThreshold = 0.5f }).Count);
    }

    [Fact]
    public void DetectFaces_LargeImagesAreDownscaledAndMappedBack()
    {
        using ImageAiSession ai = Session();

        // 1280x960 downscales by exactly 2 to 640x480: the 64x64 block at (128,128) becomes cell (2,2).
        using var image = new Image<Rgb24>(1280, 960, new Rgb24(0, 0, 0));
        for (int y = 128; y < 192; y++)
        {
            image.Frames.RootFrame.GetRowSpan(y).Slice(128, 64).Fill(new Rgb24(255, 255, 255));
        }

        DetectedFace face = Assert.Single(image.DetectFaces(ai));
        AssertBox(new RectangleF(96, 96, 128, 128), face.Bounds, tolerance: 0.05f);
        Assert.Equal(2f * ((2 + 0.5f) * 32f), face.Landmarks.NoseTip.X, 2);
    }

    [Fact]
    public void DetectFaces_TilingFindsSmallFacesThatTheDownscaledPassMisses()
    {
        using ImageAiSession ai = Session();
        using Image<Rgb24> image = Cells(1280, 960, (20, 15, 255));

        // Downscaled by 2, the 32 px face fills a quarter of a cell: score 0.25, below the threshold.
        Assert.Empty(image.DetectFaces(ai));

        // Full-resolution tiles (starts 0/480/640 by 0/320) see it whole in several tiles; suppression merges them,
        // and the copy cut by the inner edge of the tile at x = 640 is dropped.
        var tiled = new FaceDetectionOptions { TileSize = 640, TileOverlap = 160 };
        DetectedFace face = Assert.Single(image.DetectFaces(ai, tiled));
        AssertBox(new RectangleF(624, 464, 64, 64), face.Bounds);
        Assert.Equal(1f, face.Confidence, 4);
    }

    [Fact]
    public void TileStarts_CoverTheImageWithEqualTiles()
    {
        Assert.Equal([0], FaceDetector.TileStarts(500, 640, 160));
        Assert.Equal([0, 480, 640], FaceDetector.TileStarts(1280, 640, 160));
        Assert.Equal([0, 320], FaceDetector.TileStarts(960, 640, 160));
        Assert.Equal([0, 1], FaceDetector.TileStarts(641, 640, 160));
    }

    [Fact]
    public void DetectFaces_AnEmptyImageHasNoFaces_AndEveryPixelFormatWorks()
    {
        using ImageAiSession ai = Session();
        using var black = new Image<Rgba32>(37, 23);
        Assert.Empty(black.DetectFaces(ai));

        using Image<Rgb24> rgb = Cells(96, 96, (1, 1, 255));
        using Image<L8> gray = rgb.CloneAs<L8>();
        using Image<Bgra32> bgra = rgb.CloneAs<Bgra32>();
        AssertBox(Assert.Single(rgb.DetectFaces(ai)).Bounds, Assert.Single(gray.DetectFaces(ai)).Bounds);
        AssertBox(Assert.Single(rgb.DetectFaces(ai)).Bounds, Assert.Single(bgra.DetectFaces(ai)).Bounds);
    }

    [Fact]
    public void DetectFaces_RejectsAModelWithoutTheYuNetOutputs()
    {
        var options = new ImageAiOptions { Offline = true };
        options.ModelPathOverrides["face-detection"] = TestModels.IdentityRgb;
        using var ai = new ImageAiSession(options);
        using var image = new Image<Rgb24>(64, 64);

        Assert.Throws<ModelContractException>(() => image.DetectFaces(ai));
    }

    [Fact]
    public void Options_AreValidated()
    {
        using ImageAiSession ai = Session();
        using var image = new Image<Rgb24>(64, 64);

        Assert.Throws<ArgumentOutOfRangeException>(() => image.DetectFaces(ai, new FaceDetectionOptions { ScoreThreshold = 1.5f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.DetectFaces(ai, new FaceDetectionOptions { NmsThreshold = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.DetectFaces(ai, new FaceDetectionOptions { MaxInputSize = 16 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.DetectFaces(ai, new FaceDetectionOptions { TileSize = 32 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.DetectFaces(ai, new FaceDetectionOptions { TileSize = 256, TileOverlap = 256 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.RedactFaces(ai, new FaceRedactionOptions { Padding = -1f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.RedactFaces(ai, new FaceRedactionOptions { PixelSize = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.RedactFaces(ai, new FaceRedactionOptions { Style = (FaceRedactionStyle)7 }));
        Assert.Throws<ArgumentNullException>(() => image.RedactFaces(ai, new FaceRedactionOptions { Detection = null! }));
    }

    [Fact]
    public async Task DetectFaces_HonoursCancellation()
    {
        using ImageAiSession ai = Session();
        using Image<Rgb24> image = Cells(96, 96, (1, 1, 255));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => image.DetectFacesAsync(ai, cancellationToken: cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => image.DetectFaces(ai, new FaceDetectionOptions(), cts.Token));
    }

    // ----- Redaction -----

    [Fact]
    public async Task RedactFaces_FillCoversThePaddedBoxAndNothingElse()
    {
        using ImageAiSession ai = Session();
        using Image<Rgb24> image = Cells(160, 128, (2, 1, 255));
        image.Mutate(ctx => ctx.Fill(Color.FromRgb(10, 200, 30), new Rectangle(0, 100, 160, 28)));
        using Image<Rgb24> before = image.Clone();

        var options = new FaceRedactionOptions { Style = FaceRedactionStyle.Fill, FillColor = Color.FromRgb(255, 0, 0), Padding = 0.25f };
        IReadOnlyList<DetectedFace> faces = await image.RedactFacesAsync(ai, options);

        Assert.Single(faces);

        // Box (48,16,64,64) padded by 16 on each side: (32,0)-(128,96) after clipping.
        Rectangle region = FaceRedactor.Region(faces[0].Bounds, 0.25f, image.Width, image.Height);
        Assert.Equal(new Rectangle(32, 0, 96, 96), region);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Rgb24 expected = region.Contains(x, y) ? new Rgb24(255, 0, 0) : before[x, y];
                Assert.Equal(expected, image[x, y]);
            }
        }
    }

    [Theory]
    [InlineData(FaceRedactionStyle.Blur)]
    [InlineData(FaceRedactionStyle.Pixelate)]
    public void RedactFaces_BlurAndPixelateChangeOnlyTheFaceRegion(FaceRedactionStyle style)
    {
        using ImageAiSession ai = Session();
        using Image<Rgb24> image = TestImages.Noise<Rgb24>(192, 128);
        for (int y = 32; y < 64; y++)
        {
            image.Frames.RootFrame.GetRowSpan(y).Slice(64, 32).Fill(new Rgb24(255, 255, 255));
        }

        // Noise has mean brightness ~0.5 in every other cell, below the 0.9 threshold used here.
        var options = new FaceRedactionOptions { Style = style, Padding = 0f, Detection = new FaceDetectionOptions { ScoreThreshold = 0.9f } };
        using Image<Rgb24> before = image.Clone();
        IReadOnlyList<DetectedFace> faces = image.RedactFaces(ai, options);

        Rectangle region = FaceRedactor.Region(Assert.Single(faces).Bounds, 0f, image.Width, image.Height);
        Assert.Equal(new Rectangle(48, 16, 64, 64), region);
        int changedInside = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (region.Contains(x, y))
                {
                    changedInside += image[x, y].Equals(before[x, y]) ? 0 : 1;
                }
                else
                {
                    Assert.Equal(before[x, y], image[x, y]);
                }
            }
        }

        Assert.True(changedInside > region.Width * region.Height / 2, $"Only {changedInside} pixels of the face region changed.");
    }

    [Fact]
    public void RedactFaces_EveryFrameIsDetectedOnItsOwn()
    {
        using ImageAiSession ai = Session();
        // Each frame has one face cell and one dim (score 0.47) cell where the other frame has its face.
        using Image<Rgb24> image = Cells(128, 96, (0, 0, 255), (3, 2, 120));
        using (Image<Rgb24> second = Cells(128, 96, (3, 2, 255), (0, 0, 120)))
        {
            image.Frames.AddFrame(second.Frames.RootFrame);
        }

        var options = new FaceRedactionOptions { Style = FaceRedactionStyle.Fill, Padding = 0f };
        IReadOnlyList<DetectedFace> rootFaces = image.RedactFaces(ai, options);

        AssertBox(new RectangleF(0, 0, 48, 48), Assert.Single(rootFaces).Bounds);
        Assert.Equal(new Rgb24(0, 0, 0), image.Frames[0][10, 10]);
        Assert.Equal(new Rgb24(120, 120, 120), image.Frames[0][110, 80]);
        Assert.Equal(new Rgb24(0, 0, 0), image.Frames[1][110, 80]);
        Assert.Equal(new Rgb24(120, 120, 120), image.Frames[1][10, 10]);
    }

    [Fact]
    public void RedactFaces_WorksInsideAProcessingPipeline()
    {
        using ImageAiSession ai = Session();
        using Image<Rgba32> image = Cells(160, 128, (2, 1, 255)).CloneAs<Rgba32>();

        image.Mutate(ctx => ctx.RedactFaces(ai, new FaceRedactionOptions { Style = FaceRedactionStyle.Fill, Padding = 0f }));

        Assert.Equal(new Rgba32(0, 0, 0, 255), image[80, 48]);
        Assert.Equal(new Rgba32(0, 0, 0, 255), image[5, 5]);
    }

    [Fact]
    public void RedactFaceRegions_RedactsGivenRegionsWithoutADetector()
    {
        using var image = new Image<Rgb24>(64, 64, new Rgb24(255, 255, 255));
        var face = new DetectedFace(new RectangleF(10.5f, 10.5f, 20f, 20f), 1f, default);

        image.RedactFaceRegions([face], new FaceRedactionOptions { Style = FaceRedactionStyle.Fill, Padding = 0f });

        Assert.Equal(new Rgb24(0, 0, 0), image[10, 10]);
        Assert.Equal(new Rgb24(0, 0, 0), image[30, 30]);
        Assert.Equal(new Rgb24(255, 255, 255), image[31, 31]);
        Assert.Equal(new Rgb24(255, 255, 255), image[9, 9]);
        Assert.Throws<ArgumentNullException>(() => image.RedactFaceRegions(new DetectedFace[] { null! }));
    }

    [Fact]
    public void Region_RoundsOutwardsAndClips()
    {
        Assert.Equal(new Rectangle(0, 0, 8, 11), FaceRedactor.Region(new RectangleF(-5f, -2f, 10f, 10f), 0.25f, 100, 100));
        Assert.Equal(Rectangle.Empty, FaceRedactor.Region(new RectangleF(200f, 200f, 10f, 10f), 0f, 100, 100));
        Assert.Equal(new Rectangle(1, 2, 4, 5), FaceRedactor.Region(new RectangleF(1.2f, 2.9f, 3.1f, 4f), 0f, 100, 100));
    }

    [Fact]
    public void Registry_DescribesTheFaceDetector()
    {
        ModelDescriptor descriptor = ModelRegistry.FaceDetection;
        Assert.Same(descriptor, ModelRegistry.Find("face-detection"));
        Assert.Contains(descriptor, ModelRegistry.All);
        Assert.True(descriptor.IsPublished);
        Assert.Equal("MIT", descriptor.License);
        Assert.Equal(ImageModelTask.FaceDetection, descriptor.Task);
        Assert.Equal(ModelRegistry.GetChecksum(descriptor.FileName), descriptor.Sha256);
        Assert.Same(TensorNormalization.Byte, descriptor.Normalization);
    }
}

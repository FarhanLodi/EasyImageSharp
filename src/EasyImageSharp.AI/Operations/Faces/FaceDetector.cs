using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>
/// YuNet face detection over an already-loaded <see cref="InferenceSession"/>, following the upstream
/// <c>FaceDetectorYN</c> post-processing (and <c>tools/ModelExport/yunet_reference.py</c>, its executable reference):
/// BGR 0-255 input zero-padded at the bottom / right to a multiple of 32; per stride s in {8, 16, 32} the outputs
/// <c>cls_s</c>, <c>obj_s</c> <c>[1,N,1]</c>, <c>bbox_s</c> <c>[1,N,4]</c> and <c>kps_s</c> <c>[1,N,10]</c> over the
/// row-major anchor grid; score = sqrt(clamp(cls) * clamp(obj)); box centre (col + dx, row + dy) * s and size
/// exp(dw | dh) * s; landmark k at (col + kx, row + ky) * s; greedy non-maximum suppression.
/// </summary>
internal static class FaceDetector
{
    /// <summary>The network input name of the YuNet export.</summary>
    public const string InputName = "input";

    private const int Divisor = 32;

    /// <summary>Pixels (at tile scale) within which a detection counts as touching an inner tile edge.</summary>
    private const float TileEdgeMargin = 4f;

    private static readonly int[] Strides = [8, 16, 32];

    private static readonly float[] ByteMean = [0f];

    private static readonly float[] ByteStd = [1f / 255f];

    /// <summary>The 12 output names in the order cls, obj, bbox, kps for strides 8, 16, 32.</summary>
    private static readonly string[] OutputNames =
    [
        "cls_8", "cls_16", "cls_32",
        "obj_8", "obj_16", "obj_32",
        "bbox_8", "bbox_16", "bbox_32",
        "kps_8", "kps_16", "kps_32",
    ];

    /// <summary>Detects faces on the root frame; results are in source pixels, sorted by descending confidence.</summary>
    public static IReadOnlyList<DetectedFace> Detect<TPixel>(
        InferenceSession session, Image<TPixel> image, FaceDetectionOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        options.Validate();
        string inputName = OrtRunner.ResolveInputName(session, InputName);
        foreach (string name in OutputNames)
        {
            if (!session.OutputMetadata.ContainsKey(name))
            {
                throw new ModelContractException(
                    $"The face detector has no output '{name}'; a YuNet export with outputs {string.Join(", ", OutputNames)} is required.");
            }
        }

        var candidates = new List<Candidate>();
        int width = image.Width;
        int height = image.Height;
        int longest = Math.Max(width, height);

        // Whole-image pass, downscaled to MaxInputSize when larger.
        if (longest <= options.MaxInputSize)
        {
            Image<TPixel> single = FrameUtil.FrameAsImage(image, 0, out bool isCopy);
            try
            {
                RunPass(session, inputName, single, 1f, 1f, 0, 0, default, options, candidates, cancellationToken);
            }
            finally
            {
                if (isCopy)
                {
                    single.Dispose();
                }
            }
        }
        else
        {
            double scale = (double)options.MaxInputSize / longest;
            int scaledWidth = Math.Clamp((int)Math.Round(width * scale), 1, options.MaxInputSize);
            int scaledHeight = Math.Clamp((int)Math.Round(height * scale), 1, options.MaxInputSize);
            using Image<TPixel> scaled = FrameUtil.ResizeCopy(image, scaledWidth, scaledHeight, KnownResamplers.Triangle);
            RunPass(session, inputName, scaled, (float)scaledWidth / width, (float)scaledHeight / height, 0, 0, default, options, candidates, cancellationToken);

            if (options.TileSize > 0)
            {
                RunTiles(session, inputName, image, options, candidates, cancellationToken);
            }
        }

        return Suppress(candidates, options, width, height);
    }

    /// <summary>Scans the root frame at full resolution in overlapping, equally sized tiles.</summary>
    private static void RunTiles<TPixel>(
        InferenceSession session, string inputName, Image<TPixel> image, FaceDetectionOptions options, List<Candidate> candidates, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ImageFrame<TPixel> source = image.Frames.RootFrame;
        int tile = options.TileSize;
        foreach (int y in TileStarts(image.Height, tile, options.TileOverlap))
        {
            foreach (int x in TileStarts(image.Width, tile, options.TileOverlap))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rect = new Rectangle(x, y, Math.Min(tile, image.Width - x), Math.Min(tile, image.Height - y));
                using Image<TPixel> crop = FrameUtil.CropCopy(source, rect);
                var inner = new InnerEdges(x > 0, y > 0, rect.Right < image.Width, rect.Bottom < image.Height);
                RunPass(session, inputName, crop, 1f, 1f, x, y, inner, options, candidates, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Start offsets of tiles of <paramref name="tile"/> pixels covering <paramref name="size"/> with at least
    /// <paramref name="overlap"/> pixels shared by neighbours; the last tile is aligned to the far edge so every tile
    /// has the same size (one tensor shape for the whole pass).
    /// </summary>
    internal static List<int> TileStarts(int size, int tile, int overlap)
    {
        var starts = new List<int>();
        if (size <= tile)
        {
            starts.Add(0);
            return starts;
        }

        int step = tile - overlap;
        for (int start = 0; ; start += step)
        {
            if (start + tile >= size)
            {
                starts.Add(size - tile);
                break;
            }

            starts.Add(start);
        }

        return starts;
    }

    /// <summary>
    /// One network pass over <paramref name="pass"/>; candidates are mapped back to source pixels through the per-axis
    /// scale and the tile offset. Candidates touching an inner tile edge are dropped (the neighbouring tile sees them whole).
    /// </summary>
    private static void RunPass<TPixel>(
        InferenceSession session,
        string inputName,
        Image<TPixel> pass,
        float scaleX,
        float scaleY,
        int offsetX,
        int offsetY,
        InnerEdges inner,
        FaceDetectionOptions options,
        List<Candidate> candidates,
        CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = pass.Width;
        int height = pass.Height;
        int padWidth = checked(((width - 1) / Divisor + 1) * Divisor);
        int padHeight = checked(((height - 1) / Divisor + 1) * Divisor);
        int plane = checked(padWidth * padHeight);

        // BGR, raw 0-255, written into a zeroed padded buffer: the bottom / right padding stays black.
        float[] input = new float[checked(plane * 3)];
        pass.WriteTensor(
            input,
            new TensorWriteOptions { Channels = TensorChannels.Bgr, RowStride = padWidth, PlaneStride = plane },
            ByteMean,
            ByteStd);

        TensorResult[] outputs = OrtRunner.RunCancelable(session, inputName, input, [1, 3, padHeight, padWidth], OutputNames, cancellationToken);
        for (int s = 0; s < Strides.Length; s++)
        {
            int stride = Strides[s];
            int cols = padWidth / stride;
            int rows = padHeight / stride;
            int anchors = cols * rows;
            float[] cls = Check(outputs[s], anchors, 1);
            float[] obj = Check(outputs[3 + s], anchors, 1);
            float[] bbox = Check(outputs[6 + s], anchors, 4);
            float[] kps = Check(outputs[9 + s], anchors, 10);

            for (int index = 0; index < anchors; index++)
            {
                float score = MathF.Sqrt(Math.Clamp(cls[index], 0f, 1f) * Math.Clamp(obj[index], 0f, 1f));
                if (!(score >= options.ScoreThreshold))
                {
                    continue;
                }

                int row = index / cols;
                int col = index - (row * cols);
                float centerX = (col + bbox[(index * 4) + 0]) * stride;
                float centerY = (row + bbox[(index * 4) + 1]) * stride;
                float boxWidth = MathF.Exp(bbox[(index * 4) + 2]) * stride;
                float boxHeight = MathF.Exp(bbox[(index * 4) + 3]) * stride;
                float left = centerX - (boxWidth / 2f);
                float top = centerY - (boxHeight / 2f);
                if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(boxWidth) || !float.IsFinite(boxHeight))
                {
                    continue;
                }

                if ((inner.Left && left < TileEdgeMargin) || (inner.Top && top < TileEdgeMargin)
                    || (inner.Right && left + boxWidth > width - TileEdgeMargin) || (inner.Bottom && top + boxHeight > height - TileEdgeMargin))
                {
                    continue;
                }

                var points = new PointF[5];
                for (int n = 0; n < 5; n++)
                {
                    float px = (kps[(index * 10) + (2 * n)] + col) * stride;
                    float py = (kps[(index * 10) + (2 * n) + 1] + row) * stride;
                    points[n] = new PointF((px / scaleX) + offsetX, (py / scaleY) + offsetY);
                }

                candidates.Add(new Candidate(
                    (left / scaleX) + offsetX,
                    (top / scaleY) + offsetY,
                    boxWidth / scaleX,
                    boxHeight / scaleY,
                    score,
                    new FaceLandmarks(points[0], points[1], points[2], points[3], points[4])));
            }
        }
    }

    private static float[] Check(TensorResult output, int anchors, int width)
    {
        if (output.Data.Length != (long)anchors * width)
        {
            throw new ModelContractException(
                $"Face detector output has shape [{string.Join(",", output.Shape)}]; expected [1,{anchors},{width}] for the padded input.");
        }

        return output.Data;
    }

    /// <summary>
    /// Greedy non-maximum suppression by descending score (stable for equal scores), keeping at most
    /// <see cref="FaceDetectionOptions.MaxCandidates"/> candidates; boxes are clipped to the image afterwards.
    /// </summary>
    private static IReadOnlyList<DetectedFace> Suppress(List<Candidate> candidates, FaceDetectionOptions options, int width, int height)
    {
        Candidate[] ordered = candidates.OrderByDescending(c => c.Score).Take(options.MaxCandidates).ToArray();
        var kept = new List<Candidate>();
        foreach (Candidate candidate in ordered)
        {
            bool suppressed = false;
            foreach (Candidate winner in kept)
            {
                if (Iou(candidate, winner) > options.NmsThreshold)
                {
                    suppressed = true;
                    break;
                }
            }

            if (!suppressed)
            {
                kept.Add(candidate);
            }
        }

        var image = new RectangleF(0, 0, width, height);
        var faces = new List<DetectedFace>(kept.Count);
        foreach (Candidate c in kept)
        {
            RectangleF bounds = RectangleF.Intersect(new RectangleF(c.X, c.Y, c.Width, c.Height), image);
            if (bounds.Width > 0f && bounds.Height > 0f)
            {
                faces.Add(new DetectedFace(bounds, c.Score, c.Landmarks));
            }
        }

        return faces;
    }

    internal static float Iou(Candidate a, Candidate b)
    {
        float interWidth = MathF.Min(a.X + a.Width, b.X + b.Width) - MathF.Max(a.X, b.X);
        float interHeight = MathF.Min(a.Y + a.Height, b.Y + b.Height) - MathF.Max(a.Y, b.Y);
        if (interWidth <= 0f || interHeight <= 0f)
        {
            return 0f;
        }

        float intersection = interWidth * interHeight;
        float union = (a.Width * a.Height) + (b.Width * b.Height) - intersection;
        return union <= 0f ? 0f : intersection / union;
    }

    /// <summary>Which sides of a tile border another tile (detections touching them are dropped).</summary>
    private readonly record struct InnerEdges(bool Left, bool Top, bool Right, bool Bottom);

    /// <summary>A detection before suppression, in source pixels.</summary>
    internal readonly record struct Candidate(float X, float Y, float Width, float Height, float Score, FaceLandmarks Landmarks);
}

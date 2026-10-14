using System.Diagnostics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

public static partial class DocumentProcessingExtensions
{
    /// <summary>Clones and processes independent document pages using the selected profile. Caller owns the disposable result.</summary>
    /// <remarks>NeuralOcr and Screenshot preserve pixels by default. ScannedDocument enables orientation, deskew and adaptive binarization; PhoneDocument enables orientation, perspective and conditional background normalization. Geometry detectors are heuristic and can abstain. This pipeline does not dewarp curved pages. Cancellation is checked while collecting diagnostics and between processing stages; individual legacy processing stages may finish before cancellation is observed.</remarks>
    public static DocumentPreprocessingResult<TPixel> PreprocessDocument<TPixel>(this Image<TPixel> image,
        DocumentPreprocessingOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        image.EnsureNotDisposed();
        options ??= new DocumentPreprocessingOptions();
        options.Validate();
        using var execution = OperationExecution.Enter(options.ExecutionOptions, cancellationToken);
        cancellationToken = execution.CancellationToken;
        // Snapshot settings before processing so all pages use the same choices.
        DocumentPreprocessingProfile profile = options.Profile;
        float minimumConfidence = options.MinimumOrientationConfidence;
        float maximumSkew = options.MaximumSkewAngle;
        float minimumSkew = options.MinimumSkewAngle;
        int sampleDimension = options.MaximumSampleDimension;
        bool scanned = profile == DocumentPreprocessingProfile.ScannedDocument;
        bool phone = profile == DocumentPreprocessingProfile.PhoneDocument;
        bool orient = options.AutoOrient ?? (scanned || phone);
        bool perspective = options.CorrectPerspective ?? phone;
        bool deskew = options.Deskew ?? scanned;
        bool normalize = options.NormalizeBackground ?? phone;
        bool binarize = options.Binarize ?? scanned;
        bool forceNormalize = options.NormalizeBackground == true;
        cancellationToken.ThrowIfCancellationRequested();
        Image<TPixel> output = image.Clone();
        try
        {
            var pages = new List<DocumentPagePreprocessingResult>(output.Frames.Count);
            for (int index = 0; index < output.Frames.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImageFrame<TPixel> frame = output.Frames[index];
                var coordinates = new DocumentCoordinateMap();
                var stages = new List<DocumentPreprocessingStage>();
                long started = Stopwatch.GetTimestamp();
                DocumentAnalysis analysis = Analyze(frame, sampleDimension, cancellationToken);
                stages.Add(new DocumentPreprocessingStage("Analyze", true, "Sampled original page diagnostics.", null, Stopwatch.GetElapsedTime(started)));

                RunStage("Orient", orient, () =>
                {
                    if (analysis.IsLikelyBlank)
                    {
                        return (false, "Nearly uniform sampled page; orientation is uncertain.", null);
                    }

                    OrientationEstimate estimate = OrientationOps.Estimate(frame);
                    if (estimate.Confidence < minimumConfidence || estimate.Rotation == RotateMode.None)
                    {
                        return (false, estimate.Confidence < minimumConfidence ? "Orientation confidence below threshold." : "No quarter-turn correction indicated.", (double?)estimate.Confidence);
                    }

                    Size before = new(frame.Width, frame.Height);
                    ImageFrame<TPixel> rotated = estimate.Rotation switch
                    {
                        RotateMode.Rotate90 => FrameOps.Rotate90(frame),
                        RotateMode.Rotate180 => FrameOps.Rotate180(frame),
                        RotateMode.Rotate270 => FrameOps.Rotate270(frame),
                        _ => frame,
                    };
                    Replace(frame, rotated);
                    coordinates.AddRotation((int)estimate.Rotation, before, new Size(frame.Width, frame.Height));
                    return (true, "Applied heuristic quarter-turn estimate.", (double?)estimate.Confidence);
                });

                RunStage("Perspective", perspective, () =>
                {
                    if (analysis.IsLikelyBlank)
                    {
                        return (false, "Nearly uniform sampled page; page outline is uncertain.", null);
                    }

                    PointF[]? quad = PageDetection.Detect(frame);
                    if (quad is null)
                    {
                        return (false, "No convincing page outline found.", null);
                    }

                    Size size = PerspectiveWarp.NaturalSize(quad);
                    PointF[] rectangle = [new(0, 0), new(size.Width, 0), new(size.Width, size.Height), new(0, size.Height)];
                    double[] forward;
                    double[] inverse;
                    try
                    {
                        forward = PerspectiveWarp.SolveHomography(quad, rectangle);
                        inverse = PerspectiveWarp.SolveHomography(rectangle, quad);
                    }
                    catch (ArgumentException)
                    {
                        return (false, "Detected quadrilateral is degenerate; perspective left unchanged.", null);
                    }

                    if (forward.Any(value => !double.IsFinite(value)) || inverse.Any(value => !double.IsFinite(value)))
                    {
                        return (false, "Detected quadrilateral has no finite mapping; perspective left unchanged.", null);
                    }

                    Replace(frame, PerspectiveWarp.Warp(frame, quad, size.Width, size.Height));
                    coordinates.Add(forward, inverse);
                    return (true, "Rectified accepted page outline; detector does not provide a calibrated confidence score.", null);
                });

                RunStage("Deskew", deskew, () =>
                {
                    if (analysis.IsLikelyBlank)
                    {
                        return (false, "Nearly uniform sampled page; skew is uncertain.", null);
                    }

                    SkewOps.SkewEstimate estimate = SkewOps.Estimate(frame, maximumSkew);
                    if (estimate.PointCount == 0 || estimate.Strength <= 1.02)
                    {
                        return (false, "Insufficient text edges or weak projection peak.", null);
                    }

                    if (Math.Abs(estimate.AngleDegrees) < minimumSkew)
                    {
                        return (false, "Skew is below the minimum correction angle.", null);
                    }

                    float angle = (float)-estimate.AngleDegrees;
                    Size before = new(frame.Width, frame.Height);
                    Replace(frame, FrameOps.RotateArbitrary(frame, angle, Rgba32.White));
                    coordinates.AddRotation(angle, before, new Size(frame.Width, frame.Height));
                    return (true, "Corrected skew supported by text-edge projection peak.", null);
                });

                RunStage("NormalizeBackground", normalize, () =>
                {
                    DocumentAnalysis current = Analyze(frame, sampleDimension, cancellationToken);
                    if (!forceNormalize && (current.IsLikelyBlank || current.BackgroundSpread <= 40.0 / 255))
                    {
                        return (false, "Sampled background does not show sufficient illumination variation.", null);
                    }

                    IlluminationOps.BackgroundNormalize(frame, 0);
                    return (true, forceNormalize ? "Explicitly requested background normalization." : "Normalized uneven sampled background.", null);
                });

                RunStage("Binarize", binarize, () =>
                {
                    var binarizeOptions = new BinarizeOptions();
                    byte[] plane = DocumentPlanes.Luminance(frame);
                    BinarizeMethod method = LocalThresholdOps.ChooseMethod(plane, frame.Width, frame.Height, binarizeOptions, out _);
                    if (method == BinarizeMethod.Otsu)
                    {
                        FilterOps.OtsuThreshold(frame);
                    }
                    else
                    {
                        FilterOps.SauvolaThreshold(frame, binarizeOptions.WindowSize, binarizeOptions.KFor(method));
                    }

                    return (true, $"Selected {method} from histogram separation and background variation; output is binary luminance.", null);
                });

                pages.Add(new DocumentPagePreprocessingResult(index, analysis, new Size(frame.Width, frame.Height), coordinates, stages.AsReadOnly()));

                void RunStage(string name, bool enabled, Func<(bool Applied, string Reason, double? Confidence)> action)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long stamp = Stopwatch.GetTimestamp();
                    (bool applied, string reason, double? confidence) = enabled
                        ? action()
                        : (false, "Disabled by profile or explicit option.", null);
                    cancellationToken.ThrowIfCancellationRequested();
                    stages.Add(new DocumentPreprocessingStage(name, applied, reason, confidence, Stopwatch.GetElapsedTime(stamp)));
                }
            }

            return new DocumentPreprocessingResult<TPixel>(output, pages.AsReadOnly());
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void Replace<TPixel>(ImageFrame<TPixel> destination, ImageFrame<TPixel> result)
        where TPixel : unmanaged, IPixel<TPixel>
        => destination.ReplaceBuffer(result.PixelArray, result.Width, result.Height);
}

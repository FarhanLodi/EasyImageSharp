using System.Diagnostics;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>Document profiles with optional model-based orientation and dewarping.</summary>
public static class DocumentAiProcessingExtensions
{
    /// <summary>Processes independent pages into an owned output; source pixels and metadata are unchanged. Requested model failures propagate.</summary>
    /// <remarks>AI orientation precedes dewarp, followed by classical profile stages. Image-only dewarp exports make both coordinate directions unavailable. Grid exports supply processed-to-source mappings; source-to-processed is unavailable because arbitrary grids need not be invertible.</remarks>
    public static async Task<DocumentPreprocessingResult<TPixel>> PreprocessDocumentAIAsync<TPixel>(this Image<TPixel> image,
        ImageAiSession ai, DocumentAiPreprocessingOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ai);
        image.EnsureNotDisposed();
        options ??= new DocumentAiPreprocessingOptions();
        ArgumentNullException.ThrowIfNull(options.Preprocessing);
        ArgumentNullException.ThrowIfNull(options.DewarpContract);
        bool orient = options.AutoOrient, dewarp = options.Dewarp;
        float confidence = options.MinimumOrientationConfidence;
        if (!(confidence >= 0 && confidence <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Orientation confidence must be finite and in [0,1].");
        }

        DocumentDewarpContract contract = options.DewarpContract;
        contract.Validate();
        DocumentPreprocessingOptions requested = options.Preprocessing;
        var classical = new DocumentPreprocessingOptions
        {
            Profile = requested.Profile,
            ExecutionOptions = requested.ExecutionOptions,
            AutoOrient = orient ? false : requested.AutoOrient,
            CorrectPerspective = requested.CorrectPerspective,
            Deskew = requested.Deskew,
            NormalizeBackground = requested.NormalizeBackground,
            Binarize = requested.Binarize,
            MinimumOrientationConfidence = requested.MinimumOrientationConfidence,
            MaximumSkewAngle = requested.MaximumSkewAngle,
            MinimumSkewAngle = requested.MinimumSkewAngle,
            MaximumSampleDimension = requested.MaximumSampleDimension,
        };
        classical.Validate();
        using var execution = OperationExecution.Enter(classical.ExecutionOptions, cancellationToken);
        cancellationToken = execution.CancellationToken;

        InferenceSession? orientation = orient ? await ai.GetSessionAsync(ModelRegistry.DocumentOrientation, cancellationToken).ConfigureAwait(false) : null;
        InferenceSession? unwarp = dewarp ? await ai.GetSessionAsync(ModelRegistry.DocumentDewarp, cancellationToken).ConfigureAwait(false) : null;
        return await Task.Run(() => Process(image, orientation, unwarp, confidence, contract, classical, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Dewarps in place using an explicit export contract, for custom RGB, fixed-size or normalized-grid overrides.</summary>
    public static async Task DewarpDocumentWithContractAsync<TPixel>(this Image<TPixel> image, ImageAiSession ai,
        DocumentDewarpContract contract, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(contract);
        image.EnsureNotDisposed();
        contract.Validate();
        using var execution = OperationExecution.Enter(cancellationToken: cancellationToken);
        cancellationToken = execution.CancellationToken;
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.DocumentDewarp, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => AiOperations.ForEachFrame(image, page => DocumentDewarpRunner.Run(session, page, contract, cancellationToken).Image), cancellationToken).ConfigureAwait(false);
    }

    private static DocumentPreprocessingResult<TPixel> Process<TPixel>(Image<TPixel> source, InferenceSession? orientation,
        InferenceSession? unwarp, float confidence, DocumentDewarpContract contract, DocumentPreprocessingOptions classical,
        CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Image<TPixel> output = source.Clone();
        try
        {
            var pages = new List<DocumentPagePreprocessingResult>();
            for (int index = 0; index < source.Frames.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using Image<TPixel> page = source.Frames.CloneFrame(index);
                long stamp = Stopwatch.GetTimestamp();
                DocumentAnalysis analysis = page.AnalyzeDocument(classical.MaximumSampleDimension, cancellationToken)[0];
                var stages = new List<DocumentPreprocessingStage>
                {
                    new("AnalyzeOriginal", true, "Sampled original page before AI geometry.", null, Stopwatch.GetElapsedTime(stamp)),
                };
                var orientationMap = new DocumentCoordinateMap();
                if (orientation is not null)
                {
                    stamp = Stopwatch.GetTimestamp();
                    OrientationResult estimate = AiOperations.DetectOrientation(orientation, page, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    bool apply = estimate.Confidence >= confidence && estimate.Correction != RotateMode.None;
                    if (apply)
                    {
                        Size before = new(page.Width, page.Height);
                        page.Mutate(ctx => ctx.Rotate(estimate.Correction));
                        orientationMap.AddRotation((int)estimate.Correction, before, new Size(page.Width, page.Height));
                    }

                    stages.Add(new DocumentPreprocessingStage("AiOrient", apply,
                        apply ? "Applied model quarter-turn correction." : estimate.Confidence < confidence ? "Model orientation score below threshold." : "Model predicts upright page.",
                        estimate.Confidence, Stopwatch.GetElapsedTime(stamp)));
                }
                else
                {
                    stages.Add(new DocumentPreprocessingStage("AiOrient", false, "AI orientation disabled.", null, TimeSpan.Zero));
                }

                IDocumentCoordinateMap geometry = orientationMap;
                Image<TPixel>? dewarped = null;
                try
                {
                    if (unwarp is not null)
                    {
                        stamp = Stopwatch.GetTimestamp();
                        (Image<TPixel> warped, IDocumentCoordinateMap map) = DocumentDewarpRunner.Run(unwarp, page, contract, cancellationToken);
                        dewarped = warped;
                        geometry = new CompositeDocumentMap(orientationMap, map);
                        string reason = contract.OutputKind == DocumentDewarpOutputKind.RectifiedImage
                            ? "Applied rectified-image model; geometric correspondence is unavailable."
                            : "Resampled original full-resolution pixels using backward normalized grid; forward correspondence is unavailable.";
                        if (!contract.FixedInputSize.IsEmpty && contract.OutputKind == DocumentDewarpOutputKind.RectifiedImage)
                        {
                            reason += " Explicit fixed input size limits recovered image detail.";
                        }

                        stages.Add(new DocumentPreprocessingStage("AiDewarp", true, reason, null, Stopwatch.GetElapsedTime(stamp)));
                    }
                    else
                    {
                        stages.Add(new DocumentPreprocessingStage("AiDewarp", false, "AI dewarp disabled.", null, TimeSpan.Zero));
                    }

                    using DocumentPreprocessingResult<TPixel> processed = (dewarped ?? page).PreprocessDocument(classical, cancellationToken);
                    DocumentPagePreprocessingResult report = processed.Pages[0];
                    stages.AddRange(report.Stages);
                    ImageFrame<TPixel> frame = processed.Image.Frames.RootFrame;
                    output.Frames[index].ReplaceBuffer(frame.PixelArray, frame.Width, frame.Height);
                    pages.Add(new DocumentPagePreprocessingResult(index, analysis, report.OutputSize,
                        new CompositeDocumentMap(geometry, report.Coordinates), stages.AsReadOnly()));
                }
                finally
                {
                    dewarped?.Dispose();
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
}

internal sealed class CompositeDocumentMap(IDocumentCoordinateMap first, IDocumentCoordinateMap second) : IDocumentCoordinateMap
{
    public bool TryMapToProcessed(PointF source, out PointF processed)
    {
        processed = default;
        return first.TryMapToProcessed(source, out PointF intermediate) && second.TryMapToProcessed(intermediate, out processed);
    }

    public bool TryMapToSource(PointF processed, out PointF source)
    {
        source = default;
        return second.TryMapToSource(processed, out PointF intermediate) && first.TryMapToSource(intermediate, out source);
    }
}

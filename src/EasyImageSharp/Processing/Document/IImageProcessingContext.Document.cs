namespace EasyImageSharp.Processing;

/// <summary>Document-imaging operations (thresholds, morphology, cleanup, geometry, layout) for OCR pipelines.</summary>
public partial interface IImageProcessingContext
{
    // ----- Local thresholds (luminance at or above the local threshold becomes white) -----

    /// <summary>Niblack local threshold <c>T = m + k s</c> over a <paramref name="windowSize"/> window (typical k = -0.2).</summary>
    IImageProcessingContext NiblackThreshold(int windowSize, float k);

    /// <summary>
    /// Wolf-Jolion local threshold <c>T = (1 - k) m + k M + k (s / R)(m - M)</c> where M is the darkest grey level of
    /// the image and R the largest local standard deviation (typical k = 0.5).
    /// </summary>
    IImageProcessingContext WolfJolionThreshold(int windowSize, float k);

    /// <summary>
    /// Phansalkar local threshold <c>T = m (1 + p e^(-q m) + k (s / R - 1))</c> on intensities normalised to 0-1
    /// with R = 0.5; suited to low-contrast strokes (typical k = 0.25, p = 2, q = 10).
    /// </summary>
    IImageProcessingContext PhansalkarThreshold(int windowSize, float k, float p, float q);

    /// <summary>NICK local threshold <c>T = m + k sqrt((sum(p^2) - m^2) / NP)</c> (typical k = -0.1).</summary>
    IImageProcessingContext NickThreshold(int windowSize, float k);

    /// <summary>Binarises with the algorithm chosen by <paramref name="options"/> (Otsu or Sauvola automatically by default).</summary>
    IImageProcessingContext Binarize(BinarizeOptions options);

    // ----- Morphology (grey-level; colour images are converted to luminance first) -----

    /// <summary>Local minimum over the element (shrinks bright regions, grows dark ink).</summary>
    IImageProcessingContext Erode(StructuringElement element);

    /// <summary>Local maximum over the element (grows bright regions, thins dark ink).</summary>
    IImageProcessingContext Dilate(StructuringElement element);

    /// <summary>Erosion followed by dilation: removes small bright details.</summary>
    IImageProcessingContext Open(StructuringElement element);

    /// <summary>Dilation followed by erosion: removes small dark details.</summary>
    IImageProcessingContext Close(StructuringElement element);

    /// <summary>Source minus its opening: keeps small bright details.</summary>
    IImageProcessingContext TopHat(StructuringElement element);

    /// <summary>Closing minus the source: keeps small dark details.</summary>
    IImageProcessingContext BlackHat(StructuringElement element);

    /// <summary>Removes isolated dark specks and white pinholes of at most <paramref name="maxArea"/> pixels.</summary>
    IImageProcessingContext Despeckle(int maxArea);

    /// <summary>Zhang-Suen thinning of the dark (ink) pixels to one-pixel-wide skeletons; the result is binary.</summary>
    IImageProcessingContext Thin();

    // ----- Connected components (dark pixels are the objects) -----

    /// <summary>Paints 8-connected ink components with fewer than <paramref name="minArea"/> pixels white.</summary>
    IImageProcessingContext RemoveSmallObjects(int minArea);

    /// <summary>Keeps only the largest 8-connected ink component, painting all others white.</summary>
    IImageProcessingContext KeepLargestComponent();

    /// <summary>Fills background regions that do not reach the image border (holes inside ink) with black.</summary>
    IImageProcessingContext FillHoles();

    // ----- Illumination and tone -----

    /// <summary>
    /// Divides out the paper background (grey-level closing plus box blur of the given radius; 0 = automatic) and
    /// rescales it to white; corrects uneven illumination before thresholding.
    /// </summary>
    IImageProcessingContext BackgroundNormalize(int backgroundRadius);

    /// <summary>Removes wide soft shadows by dividing out a large-radius background estimate.</summary>
    IImageProcessingContext RemoveShadows();

    /// <summary>Linear stretch mapping the luminance percentiles to black and white (clipping outside).</summary>
    IImageProcessingContext ContrastStretch(float lowPercentile, float highPercentile);

    /// <summary>Stretches each channel independently between its 0.5th and 99.5th percentiles.</summary>
    IImageProcessingContext AutoLevels();

    /// <summary>Gamma correction <c>out = 255 (in / 255)^(1 / gamma)</c>; values above 1 brighten midtones.</summary>
    IImageProcessingContext Gamma(float gamma);

    /// <summary>Stretches the actual luminance range to 0-255 without clipping.</summary>
    IImageProcessingContext Normalize();

    // ----- Geometry -----

    /// <summary>
    /// Estimates the clockwise skew of the text (degrees) within +/-<paramref name="maxAngleDegrees"/> using the
    /// Hough estimator on the root frame; 0 when no text structure is found. Correct it with <c>Rotate(-skew)</c>
    /// or <see cref="Deskew(DeskewOptions)"/>.
    /// </summary>
    float DetectSkew(float maxAngleDegrees);

    /// <summary>Estimates and corrects the skew with the chosen estimator; the canvas expands and corners are filled.</summary>
    IImageProcessingContext Deskew(DeskewOptions options);

    /// <summary>Estimates which quarter turn makes the page upright (see <see cref="OrientationEstimate"/>).</summary>
    OrientationEstimate DetectOrientation();

    /// <summary>Applies the rotation from <see cref="DetectOrientation"/> when its confidence is at least <paramref name="minConfidence"/>.</summary>
    IImageProcessingContext AutoRotateDocument(float minConfidence);

    /// <summary>
    /// Finds the page quadrilateral (corners TL, TR, BR, BL in continuous pixel coordinates, see
    /// <see cref="CorrectPerspective"/>) or <see langword="null"/> when no convincing page outline exists.
    /// </summary>
    PointF[]? DetectPage();

    /// <summary>
    /// Warps the quadrilateral <paramref name="quad"/> (TL, TR, BR, BL) onto an upright rectangle of
    /// <paramref name="targetSize"/> (or the quad's natural size when <see langword="null"/>). Coordinates are
    /// continuous pixel units with the origin at the top-left corner of the image, so the full image is
    /// (0,0), (W,0), (W,H), (0,H).
    /// </summary>
    IImageProcessingContext CorrectPerspective(PointF[] quad, Size? targetSize);

    /// <summary>Detects the page, corrects its perspective and trims dark margins; leaves the image unchanged when no page is found.</summary>
    IImageProcessingContext AutoCropPage();

    /// <summary>Paints large dark scanner borders touching the image edge white.</summary>
    IImageProcessingContext RemoveBorders();

    // ----- Cleanup -----

    /// <summary>Removes straight rules of at least <paramref name="minLength"/> pixels, repairing text strokes that cross them.</summary>
    IImageProcessingContext RemoveLines(int minLength, LineOrientation orientation);

    /// <summary>Removes filled disks near the page edges that look like hole punches.</summary>
    IImageProcessingContext RemoveHolePunches();

    /// <summary>Removes noise components in the outer margin band and large blobs touching the image border.</summary>
    IImageProcessingContext RemoveMarginNoise();

    // ----- Layout -----

    /// <summary>Segments the root frame into text lines and words using projection profiles and RLSA.</summary>
    TextRegions SegmentText();

    /// <summary>Text-line bounding boxes of the root frame, top to bottom.</summary>
    IReadOnlyList<Rectangle> SegmentTextLines();

    /// <summary>Word bounding boxes of the root frame in reading order.</summary>
    IReadOnlyList<Rectangle> SegmentWords();

    /// <summary>Resamples so that content scanned at <paramref name="sourceDpi"/> ends up at <paramref name="targetDpi"/>.</summary>
    IImageProcessingContext NormalizeDpi(float sourceDpi, int targetDpi);

    /// <summary>
    /// OCR preset: grayscale, background normalisation, optional 3x3 median, Hough deskew, automatic binarisation
    /// and despeckling, each step controlled by <paramref name="options"/>.
    /// </summary>
    IImageProcessingContext PrepareForOcr(OcrPreprocessOptions options);
}

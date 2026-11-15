using System.Globalization;

namespace EasyImageSharp.AI;

/// <summary>
/// The five facial landmarks a face detector reports, in source-image pixel coordinates. "Right" and "left" are from
/// the subject's point of view (the right eye usually appears on the left of a frontal photo).
/// </summary>
public readonly struct FaceLandmarks
{
    /// <summary>Creates a landmark set.</summary>
    public FaceLandmarks(PointF rightEye, PointF leftEye, PointF noseTip, PointF rightMouthCorner, PointF leftMouthCorner)
    {
        this.RightEye = rightEye;
        this.LeftEye = leftEye;
        this.NoseTip = noseTip;
        this.RightMouthCorner = rightMouthCorner;
        this.LeftMouthCorner = leftMouthCorner;
    }

    /// <summary>Centre of the subject's right eye.</summary>
    public PointF RightEye { get; }

    /// <summary>Centre of the subject's left eye.</summary>
    public PointF LeftEye { get; }

    /// <summary>Tip of the nose.</summary>
    public PointF NoseTip { get; }

    /// <summary>The subject's right mouth corner.</summary>
    public PointF RightMouthCorner { get; }

    /// <summary>The subject's left mouth corner.</summary>
    public PointF LeftMouthCorner { get; }

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"FaceLandmarks [ RightEye=({this.RightEye.X:0.#},{this.RightEye.Y:0.#}), LeftEye=({this.LeftEye.X:0.#},{this.LeftEye.Y:0.#}), NoseTip=({this.NoseTip.X:0.#},{this.NoseTip.Y:0.#}) ]");
}

/// <summary>One face found by <see cref="FaceImageExtensions.DetectFacesAsync{TPixel}"/>.</summary>
public sealed class DetectedFace
{
    /// <summary>Creates a face record (for example to redact regions you reviewed or found yourself).</summary>
    /// <param name="bounds">The face box in source-image pixels.</param>
    /// <param name="confidence">Detector confidence in 0-1.</param>
    /// <param name="landmarks">The five landmarks in source-image pixels.</param>
    public DetectedFace(RectangleF bounds, float confidence, FaceLandmarks landmarks)
    {
        this.Bounds = bounds;
        this.Confidence = confidence;
        this.Landmarks = landmarks;
    }

    /// <summary>
    /// The face box in source-image pixel coordinates, clipped to the image. It covers roughly forehead to chin and
    /// ear to ear; <see cref="FaceRedactionOptions.Padding"/> widens it for redaction.
    /// </summary>
    public RectangleF Bounds { get; }

    /// <summary>Detector confidence in 0-1 (the geometric mean of the classification and objectness scores).</summary>
    public float Confidence { get; }

    /// <summary>Eyes, nose tip and mouth corners in source-image pixel coordinates (not clipped).</summary>
    public FaceLandmarks Landmarks { get; }

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"DetectedFace [ Bounds=({this.Bounds.X:0.#},{this.Bounds.Y:0.#},{this.Bounds.Width:0.#}x{this.Bounds.Height:0.#}), Confidence={this.Confidence:0.000} ]");
}

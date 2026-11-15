namespace EasyImageSharp.AI;

/// <summary>How a face region is obscured by <c>RedactFaces</c>.</summary>
public enum FaceRedactionStyle
{
    /// <summary>A strong Gaussian blur of the region (see <see cref="FaceRedactionOptions.BlurSigma"/>).</summary>
    Blur,

    /// <summary>Large mosaic blocks over the region (see <see cref="FaceRedactionOptions.PixelSize"/>).</summary>
    Pixelate,

    /// <summary>A solid fill (see <see cref="FaceRedactionOptions.FillColor"/>). The only style that removes every trace of the face.</summary>
    Fill,
}

/// <summary>
/// Controls <see cref="FaceImageExtensions.RedactFacesAsync{TPixel}(Image{TPixel}, ImageAiSession, FaceRedactionOptions?, CancellationToken)"/>.
/// The defaults favour not missing faces: detection includes the full-resolution tiled pass.
/// </summary>
/// <remarks>
/// Redaction is only as complete as detection: very small, heavily occluded, profile or upside-down faces can be missed,
/// so review the result where anonymisation is a legal requirement. Blur and pixelation reduce but do not provably
/// remove identifying information; use <see cref="FaceRedactionStyle.Fill"/> when it must be removed.
/// </remarks>
public sealed class FaceRedactionOptions
{
    /// <summary>How each face region is obscured. Default <see cref="FaceRedactionStyle.Blur"/>.</summary>
    public FaceRedactionStyle Style { get; set; } = FaceRedactionStyle.Blur;

    /// <summary>
    /// Extra margin around each face box, as a fraction of the box width (left and right) and height (top and bottom),
    /// so hair line, ears and chin are covered. Default 0.15; 0 redacts exactly the detected box.
    /// </summary>
    public float Padding { get; set; } = 0.15f;

    /// <summary>
    /// Gaussian sigma in pixels for <see cref="FaceRedactionStyle.Blur"/>; <c>null</c> (default) uses a quarter of the
    /// redacted region's shorter side (at least 2), which leaves no recognisable facial detail.
    /// </summary>
    public float? BlurSigma { get; set; }

    /// <summary>
    /// Mosaic block edge in pixels for <see cref="FaceRedactionStyle.Pixelate"/>; <c>null</c> (default) uses a sixth of
    /// the redacted region's longer side (at least 4), about six blocks across a face.
    /// </summary>
    public int? PixelSize { get; set; }

    /// <summary>Colour for <see cref="FaceRedactionStyle.Fill"/>. Default opaque black.</summary>
    public Color FillColor { get; set; } = Color.Black;

    /// <summary>
    /// How faces are detected. Default: the <see cref="FaceDetectionOptions"/> defaults plus the full-resolution
    /// tiled pass (<see cref="FaceDetectionOptions.TileSize"/> = 640), so small faces in large photos are found too.
    /// </summary>
    public FaceDetectionOptions Detection { get; set; } = new() { TileSize = 640 };

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> / <see cref="ArgumentNullException"/> when a setting is invalid.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(this.Style))
        {
            throw new ArgumentOutOfRangeException(nameof(this.Style), this.Style, "Unknown redaction style.");
        }

        if (!(this.Padding is >= 0f and <= 4f))
        {
            throw new ArgumentOutOfRangeException(nameof(this.Padding), this.Padding, "Padding must be in 0-4 (a fraction of the face size).");
        }

        if (this.BlurSigma is { } sigma && !(sigma is > 0f and <= 1000f))
        {
            throw new ArgumentOutOfRangeException(nameof(this.BlurSigma), sigma, "BlurSigma must be positive (at most 1000) or null.");
        }

        if (this.PixelSize is { } size && size < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(this.PixelSize), size, "PixelSize must be at least 1 or null.");
        }

        ArgumentNullException.ThrowIfNull(this.Detection);
        this.Detection.Validate();
    }
}

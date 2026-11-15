namespace EasyImageSharp.AI;

/// <summary>
/// Controls <see cref="FaceImageExtensions.DetectFacesAsync{TPixel}"/>. The detector (YuNet) finds faces of roughly
/// 10 to 300 pixels at its working resolution, so how the image is scaled decides which faces can be found: the
/// whole image is always scanned once, downscaled to <see cref="MaxInputSize"/>, which finds the larger faces; with
/// <see cref="TileSize"/> set, an image larger than that is also scanned at full resolution in overlapping tiles,
/// which finds the small ones. Detections from every pass are merged with non-maximum suppression.
/// </summary>
public sealed class FaceDetectionOptions
{
    /// <summary>Minimum confidence (0-1) of a reported face. Default 0.6 (the upstream default).</summary>
    public float ScoreThreshold { get; set; } = 0.6f;

    /// <summary>
    /// Intersection-over-union (0-1) above which the lower-scored of two overlapping detections is dropped. Default 0.3.
    /// </summary>
    public float NmsThreshold { get; set; } = 0.3f;

    /// <summary>
    /// The highest-scoring candidates kept before non-maximum suppression (bounds the work on images with very many
    /// candidates). Default 5000.
    /// </summary>
    public int MaxCandidates { get; set; } = 5000;

    /// <summary>
    /// Longest side, in pixels, of the whole-image pass: larger images are downscaled to it (never upscaled), which
    /// also bounds the memory and time of that pass. Faces smaller than about <c>10 * longestSide / MaxInputSize</c>
    /// source pixels are missed unless <see cref="TileSize"/> is set. Default 640; at least 32.
    /// </summary>
    public int MaxInputSize { get; set; } = 640;

    /// <summary>
    /// Edge, in source pixels, of the full-resolution tiles scanned when the image's longest side exceeds
    /// <see cref="MaxInputSize"/>; 0 (default) disables the tiled pass. 640 is a good value for photos of crowds or
    /// high-resolution group shots. When set, at least 64.
    /// </summary>
    public int TileSize { get; set; }

    /// <summary>
    /// Overlap between neighbouring tiles in source pixels. A detection touching an inner tile edge is dropped (it is a
    /// cut-off face that the neighbouring tile sees whole), so the overlap must exceed the largest face the
    /// downscaled pass can miss (about <c>16 * longestSide / MaxInputSize</c>). Default 160; less than
    /// <see cref="TileSize"/>.
    /// </summary>
    public int TileOverlap { get; set; } = 160;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when a setting is out of range.</summary>
    public void Validate()
    {
        if (!(this.ScoreThreshold is >= 0f and <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(this.ScoreThreshold), this.ScoreThreshold, "ScoreThreshold must be in 0-1.");
        }

        if (!(this.NmsThreshold is >= 0f and <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(this.NmsThreshold), this.NmsThreshold, "NmsThreshold must be in 0-1.");
        }

        if (this.MaxCandidates < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(this.MaxCandidates), this.MaxCandidates, "MaxCandidates must be at least 1.");
        }

        if (this.MaxInputSize < 32)
        {
            throw new ArgumentOutOfRangeException(nameof(this.MaxInputSize), this.MaxInputSize, "MaxInputSize must be at least 32.");
        }

        if (this.TileSize != 0 && this.TileSize < 64)
        {
            throw new ArgumentOutOfRangeException(nameof(this.TileSize), this.TileSize, "TileSize must be 0 (no tiling) or at least 64.");
        }

        if (this.TileOverlap < 0 || (this.TileSize > 0 && this.TileOverlap >= this.TileSize))
        {
            throw new ArgumentOutOfRangeException(nameof(this.TileOverlap), this.TileOverlap, "TileOverlap must be non-negative and smaller than TileSize.");
        }
    }
}

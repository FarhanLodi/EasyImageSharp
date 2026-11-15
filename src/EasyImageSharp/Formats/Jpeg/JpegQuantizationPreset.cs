namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// The base quantisation tables <see cref="JpegEncoder"/> scales by its quality setting. Every preset is scaled
/// with the same IJG curve (5000/q below 50, 200 - 2q above, clamped to 1..255), so a quality number means a
/// different file size and fidelity from one preset to the next: compare presets at matched size or matched
/// fidelity, not at matched quality. All presets produce standard baseline-compatible tables any decoder reads.
/// </summary>
public enum JpegQuantizationPreset
{
    /// <summary>
    /// The example luminance and chrominance tables of ITU-T T.81 Annex K, as written by libjpeg and nearly every
    /// other encoder. The default.
    /// </summary>
    AnnexK,

    /// <summary>
    /// Every coefficient quantised with the same step (16 at quality 50) for luminance and chrominance alike: no
    /// perceptual weighting, which favours squared-error metrics such as PSNR over visual quality.
    /// </summary>
    Flat,

    /// <summary>
    /// The table Nicolas Robidoux designed for ImageMagick (for 4:2:0 output around quality 75), derived from the
    /// Klein, Silverstein and Carney table, used for luminance and chrominance alike. It quantises low frequencies
    /// more finely and high frequencies more coarsely than Annex K.
    /// </summary>
    Robidoux,

    /// <summary>
    /// The table from S. A. Klein, A. D. Silverstein and T. Carney, "Relevance of human vision to JPEG-DCT
    /// compression" (Human Vision, Visual Processing and Digital Display III, 1992), used for both table slots.
    /// </summary>
    KleinSilversteinCarney,

    /// <summary>
    /// The DCTune table from A. B. Watson, M. Taylor and R. Borthwick, "DCTune perceptual optimization of
    /// compressed dental X-rays" (1997), used for both table slots. Very fine at low frequencies, very coarse at
    /// high ones.
    /// </summary>
    WatsonTaylorBorthwick,

    /// <summary>
    /// The table from A. J. Ahumada, A. B. Watson and H. A. Peterson, "A visual detection model for DCT
    /// coefficient quantization" (1993), used for both table slots. Comparatively fine at high frequencies.
    /// </summary>
    AhumadaWatsonPeterson,

    /// <summary>
    /// The table from H. A. Peterson, A. J. Ahumada and A. B. Watson, "An improved detection model for DCT
    /// coefficient quantization" (1993), used for both table slots.
    /// </summary>
    PetersonAhumadaWatson,
}

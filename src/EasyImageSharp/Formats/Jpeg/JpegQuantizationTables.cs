namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Base quantisation tables (natural order, quality 50) for every <see cref="JpegQuantizationPreset"/>, and the
/// IJG quality scaling that turns them into the tables written to the file.
/// </summary>
/// <remarks>
/// <para>Provenance of the perceptual tables (the Annex K tables live in <see cref="JpegTables"/>):</para>
/// <list type="bullet">
/// <item><description>
/// Robidoux: ImageMagick <c>config/quantization-table.xml</c>,
/// https://github.com/ImageMagick/ImageMagick/blob/a5d6b7ed51e54b1044e4b28e7602753667b02de8/config/quantization-table.xml
/// (file SHA-256 067c2f58003d417c9a36a79602cfe90fab33cc2f658ef82ab39d3e296a20bcd2, distributed under the
/// ImageMagick License, https://imagemagick.org/license/), where it is credited to Nicolas Robidoux. The same
/// 64 values are mozjpeg's table 3.
/// </description></item>
/// <item><description>
/// Klein-Silverstein-Carney, Watson-Taylor-Borthwick (DCTune), Ahumada-Watson-Peterson and
/// Peterson-Ahumada-Watson: the tables published in the papers named on the enum members, as transcribed in
/// mozjpeg <c>jcparam.c</c> (tables 5-8),
/// https://github.com/mozilla/mozjpeg/blob/ed667d4bfd74c4ebf3d888400a28965058696049/jcparam.c
/// (file SHA-256 6b8f770a0c5785409c6e2a5981981e45e607c69cbe469c39608872f41750c2f1; the file is under the IJG
/// License). mozjpeg uses each of these for luminance and chrominance alike, and so does this encoder.
/// </description></item>
/// </list>
/// <para>
/// mozjpeg's own tables 2 and 4 (tuned by its authors for MS-SSIM and PSNR-HVS on the Kodak set) are not
/// included: unlike the others they did not originate outside that project.
/// </para>
/// </remarks>
internal static class JpegQuantizationTables
{
    private static readonly ushort[] Flat = CreateFlat();

    private static readonly ushort[] Robidoux =
    {
        16, 16, 16, 18, 25, 37, 56, 85,
        16, 17, 20, 27, 34, 40, 53, 75,
        16, 20, 24, 31, 43, 62, 91, 135,
        18, 27, 31, 40, 53, 74, 106, 156,
        25, 34, 43, 53, 69, 94, 131, 189,
        37, 40, 62, 74, 94, 124, 169, 238,
        56, 53, 91, 106, 131, 169, 226, 311,
        85, 75, 135, 156, 189, 238, 311, 418,
    };

    private static readonly ushort[] KleinSilversteinCarney =
    {
        10, 12, 14, 19, 26, 38, 57, 86,
        12, 18, 21, 28, 35, 41, 54, 76,
        14, 21, 25, 32, 44, 63, 92, 136,
        19, 28, 32, 41, 54, 75, 107, 157,
        26, 35, 44, 54, 70, 95, 132, 190,
        38, 41, 63, 75, 95, 125, 170, 239,
        57, 54, 92, 107, 132, 170, 227, 312,
        86, 76, 136, 157, 190, 239, 312, 419,
    };

    private static readonly ushort[] WatsonTaylorBorthwick =
    {
        7, 8, 10, 14, 23, 44, 95, 241,
        8, 8, 11, 15, 25, 47, 102, 255,
        10, 11, 13, 19, 31, 58, 127, 255,
        14, 15, 19, 27, 44, 83, 181, 255,
        23, 25, 31, 44, 72, 136, 255, 255,
        44, 47, 58, 83, 136, 255, 255, 255,
        95, 102, 127, 181, 255, 255, 255, 255,
        241, 255, 255, 255, 255, 255, 255, 255,
    };

    private static readonly ushort[] AhumadaWatsonPeterson =
    {
        15, 11, 11, 12, 15, 19, 25, 32,
        11, 13, 10, 10, 12, 15, 19, 24,
        11, 10, 14, 14, 16, 18, 22, 27,
        12, 10, 14, 18, 21, 24, 28, 33,
        15, 12, 16, 21, 26, 31, 36, 42,
        19, 15, 18, 24, 31, 38, 45, 53,
        25, 19, 22, 28, 36, 45, 55, 65,
        32, 24, 27, 33, 42, 53, 65, 77,
    };

    private static readonly ushort[] PetersonAhumadaWatson =
    {
        14, 10, 11, 14, 19, 25, 34, 45,
        10, 11, 11, 12, 15, 20, 26, 33,
        11, 11, 15, 18, 21, 25, 31, 38,
        14, 12, 18, 24, 28, 33, 39, 47,
        19, 15, 21, 28, 36, 43, 51, 59,
        25, 20, 25, 33, 43, 54, 64, 74,
        34, 26, 31, 39, 51, 64, 77, 91,
        45, 33, 38, 47, 59, 74, 91, 108,
    };

    private static readonly ushort[] AnnexKLuminance = Array.ConvertAll(JpegTables.StdLuminanceQuant, v => (ushort)v);

    private static readonly ushort[] AnnexKChrominance = Array.ConvertAll(JpegTables.StdChrominanceQuant, v => (ushort)v);

    /// <summary>The quality-50 base table for one slot (0 = luminance, 1 = chrominance) of a preset, natural order.</summary>
    public static ReadOnlySpan<ushort> GetBaseTable(JpegQuantizationPreset preset, int slot) => preset switch
    {
        JpegQuantizationPreset.AnnexK => slot == 0 ? AnnexKLuminance : AnnexKChrominance,
        JpegQuantizationPreset.Flat => Flat,
        JpegQuantizationPreset.Robidoux => Robidoux,
        JpegQuantizationPreset.KleinSilversteinCarney => KleinSilversteinCarney,
        JpegQuantizationPreset.WatsonTaylorBorthwick => WatsonTaylorBorthwick,
        JpegQuantizationPreset.AhumadaWatsonPeterson => AhumadaWatsonPeterson,
        JpegQuantizationPreset.PetersonAhumadaWatson => PetersonAhumadaWatson,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown quantisation preset."),
    };

    /// <summary>
    /// Standard IJG quality scaling of a base table (5000/q below 50, 200 - 2q from 50 up, rounded), clamped to
    /// the 1..255 range baseline JPEG requires.
    /// </summary>
    public static ushort[] Scale(ReadOnlySpan<ushort> baseTable, int quality)
    {
        int scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        var result = new ushort[64];
        for (int i = 0; i < 64; i++)
        {
            result[i] = (ushort)Math.Clamp(((baseTable[i] * scale) + 50) / 100, 1, 255);
        }

        return result;
    }

    private static ushort[] CreateFlat()
    {
        var table = new ushort[64];
        table.AsSpan().Fill(16);
        return table;
    }
}

using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Encodes and decodes ThumbHash placeholders: about 25 bytes holding the average colour, aspect ratio,
/// alpha and a few low-frequency DCT terms of the luminance, two chroma channels and alpha.
/// </summary>
/// <remarks>
/// The encoder and decoder reproduce the reference JavaScript implementation operation for operation in
/// double precision (including its round-half-up and truncating conversions), so for an input of at most
/// 100 x 100 pixels the hash bytes and the decoded pixels match the reference. Larger inputs are first
/// scaled to fit in 100 x 100 with a bilinear filter, as the format's documentation asks callers to do; the
/// hash then reflects that downscaled image.
/// </remarks>
public static class ThumbHash
{
    /// <summary>The largest width or height the encoder accepts without downscaling.</summary>
    public const int MaxEncodeDimension = 100;

    /// <summary>Encodes the root frame (straight, non-premultiplied 8-bit RGBA) as a ThumbHash.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    public static byte[] Encode<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        image.EnsureNotDisposed();
        ImageFrame<TPixel> frame = image.Frames.RootFrame;
        int w = frame.Width;
        int h = frame.Height;
        if (w > MaxEncodeDimension || h > MaxEncodeDimension)
        {
            double scale = (double)MaxEncodeDimension / Math.Max(w, h);
            w = Math.Clamp((int)Math.Round(w * scale), 1, MaxEncodeDimension);
            h = Math.Clamp((int)Math.Round(h * scale), 1, MaxEncodeDimension);
            frame = FrameOps.Resize(frame, w, h, KnownResamplers.Triangle);
        }

        Rgba32[] rgba = RowProcessor.ReadRegion(frame, new Rectangle(0, 0, w, h));
        return EncodeRgba(w, h, rgba);
    }

    /// <summary>
    /// Decodes a ThumbHash into a straight-alpha image whose longer side is 32 pixels and whose aspect ratio
    /// is the approximate ratio stored in the hash.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="hash"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">The hash is truncated or declares an impossible layout.</exception>
    public static Image<Rgba32> Decode(byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return Decode(hash.AsSpan());
    }

    /// <summary>Decodes a ThumbHash held in a span; see <see cref="Decode(byte[])"/>.</summary>
    /// <exception cref="FormatException">The hash is truncated or declares an impossible layout.</exception>
    public static Image<Rgba32> Decode(ReadOnlySpan<byte> hash)
    {
        Header header = ReadHeader(hash);
        int lx = header.Lx;
        int ly = header.Ly;
        bool hasAlpha = header.HasAlpha;
        int acStart = hasAlpha ? 6 : 5;
        int acIndex = 0;
        double[] lAc = DecodeChannel(hash, acStart, ref acIndex, lx, ly, header.LScale);
        double[] pAc = DecodeChannel(hash, acStart, ref acIndex, 3, 3, header.PScale * 1.25);
        double[] qAc = DecodeChannel(hash, acStart, ref acIndex, 3, 3, header.QScale * 1.25);
        double[] aAc = hasAlpha ? DecodeChannel(hash, acStart, ref acIndex, 5, 5, header.AScale) : Array.Empty<double>();

        double ratio = GetApproximateAspectRatio(hash);
        int w = (int)JsRound(ratio > 1 ? 32 : 32 * ratio);
        int h = (int)JsRound(ratio > 1 ? 32 / ratio : 32);
        var image = new Image<Rgba32>(w, h);
        ImageFrame<Rgba32> frame = image.Frames.RootFrame;
        double[] fx = new double[Math.Max(lx, hasAlpha ? 5 : 3)];
        double[] fy = new double[Math.Max(ly, hasAlpha ? 5 : 3)];
        for (int y = 0; y < h; y++)
        {
            Span<Rgba32> row = frame.GetRowSpan(y);
            for (int x = 0; x < w; x++)
            {
                double l = header.LDc, p = header.PDc, q = header.QDc, a = header.ADc;
                for (int cx = 0; cx < fx.Length; cx++)
                {
                    fx[cx] = Math.Cos(Math.PI / w * (x + 0.5) * cx);
                }

                for (int cy = 0; cy < fy.Length; cy++)
                {
                    fy[cy] = Math.Cos(Math.PI / h * (y + 0.5) * cy);
                }

                for (int cy = 0, j = 0; cy < ly; cy++)
                {
                    double fy2 = fy[cy] * 2;
                    for (int cx = cy > 0 ? 0 : 1; cx * ly < lx * (ly - cy); cx++, j++)
                    {
                        l += lAc[j] * fx[cx] * fy2;
                    }
                }

                for (int cy = 0, j = 0; cy < 3; cy++)
                {
                    double fy2 = fy[cy] * 2;
                    for (int cx = cy > 0 ? 0 : 1; cx < 3 - cy; cx++, j++)
                    {
                        double f = fx[cx] * fy2;
                        p += pAc[j] * f;
                        q += qAc[j] * f;
                    }
                }

                if (hasAlpha)
                {
                    for (int cy = 0, j = 0; cy < 5; cy++)
                    {
                        double fy2 = fy[cy] * 2;
                        for (int cx = cy > 0 ? 0 : 1; cx < 5 - cy; cx++, j++)
                        {
                            a += aAc[j] * fx[cx] * fy2;
                        }
                    }
                }

                double b = l - (2.0 / 3 * p);
                double r = ((3 * l) - b + q) / 2;
                double g = r - q;
                row[x] = new Rgba32(ToByte(r), ToByte(g), ToByte(b), ToByte(a));
            }
        }

        return image;
    }

    /// <summary>The average colour stored in the hash (straight alpha).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="hash"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">The hash is truncated.</exception>
    public static Color GetAverageColor(byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        Header header = ReadHeader(hash);
        double l = header.LDc, p = header.PDc, q = header.QDc;
        double b = l - (2.0 / 3 * p);
        double r = ((3 * l) - b + q) / 2;
        double g = r - q;
        return new Color(UnitToByte(r), UnitToByte(g), UnitToByte(b), UnitToByte(header.ADc));
    }

    /// <summary>The approximate width / height ratio of the encoded image, as stored in the hash.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="hash"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">The hash is truncated or stores a zero dimension.</exception>
    public static float GetApproximateAspectRatio(byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return (float)GetApproximateAspectRatio(hash.AsSpan());
    }

    internal static byte[] EncodeRgba(int w, int h, ReadOnlySpan<Rgba32> rgba)
    {
        double avgR = 0, avgG = 0, avgB = 0, avgA = 0;
        for (int i = 0; i < w * h; i++)
        {
            double alpha = rgba[i].A / 255.0;
            avgR += alpha / 255 * rgba[i].R;
            avgG += alpha / 255 * rgba[i].G;
            avgB += alpha / 255 * rgba[i].B;
            avgA += alpha;
        }

        if (avgA != 0)
        {
            avgR /= avgA;
            avgG /= avgA;
            avgB /= avgA;
        }

        bool hasAlpha = avgA < w * h;
        int lLimit = hasAlpha ? 5 : 7;
        int lx = Math.Max(1, (int)JsRound((double)(lLimit * w) / Math.Max(w, h)));
        int ly = Math.Max(1, (int)JsRound((double)(lLimit * h) / Math.Max(w, h)));
        double[] l = new double[w * h];
        double[] p = new double[w * h];
        double[] q = new double[w * h];
        double[] a = new double[w * h];

        // Composite atop the average colour and convert to LPQA.
        for (int i = 0; i < w * h; i++)
        {
            double alpha = rgba[i].A / 255.0;
            double r = (avgR * (1 - alpha)) + (alpha / 255 * rgba[i].R);
            double g = (avgG * (1 - alpha)) + (alpha / 255 * rgba[i].G);
            double b = (avgB * (1 - alpha)) + (alpha / 255 * rgba[i].B);
            l[i] = (r + g + b) / 3;
            p[i] = ((r + g) / 2) - b;
            q[i] = r - g;
            a[i] = alpha;
        }

        (double lDc, List<double> lAc, double lScale) = EncodeChannel(l, w, h, Math.Max(3, lx), Math.Max(3, ly));
        (double pDc, List<double> pAc, double pScale) = EncodeChannel(p, w, h, 3, 3);
        (double qDc, List<double> qAc, double qScale) = EncodeChannel(q, w, h, 3, 3);
        (double aDc, List<double> aAc, double aScale) = hasAlpha ? EncodeChannel(a, w, h, 5, 5) : (0, new List<double>(), 0);

        bool isLandscape = w > h;
        int header24 = (int)JsRound(63 * lDc)
            | ((int)JsRound(31.5 + (31.5 * pDc)) << 6)
            | ((int)JsRound(31.5 + (31.5 * qDc)) << 12)
            | ((int)JsRound(31 * lScale) << 18)
            | ((hasAlpha ? 1 : 0) << 23);
        int header16 = (isLandscape ? ly : lx)
            | ((int)JsRound(63 * pScale) << 3)
            | ((int)JsRound(63 * qScale) << 9)
            | ((isLandscape ? 1 : 0) << 15);
        int acStart = hasAlpha ? 6 : 5;
        int acCount = lAc.Count + pAc.Count + qAc.Count + aAc.Count;
        byte[] hash = new byte[acStart + ((acCount + 1) / 2)];
        hash[0] = (byte)(header24 & 255);
        hash[1] = (byte)((header24 >> 8) & 255);
        hash[2] = (byte)(header24 >> 16);
        hash[3] = (byte)(header16 & 255);
        hash[4] = (byte)(header16 >> 8);
        if (hasAlpha)
        {
            hash[5] = (byte)((int)JsRound(15 * aDc) | ((int)JsRound(15 * aScale) << 4));
        }

        int acIndex = 0;
        foreach (List<double> ac in hasAlpha ? new[] { lAc, pAc, qAc, aAc } : new[] { lAc, pAc, qAc })
        {
            foreach (double f in ac)
            {
                hash[acStart + (acIndex >> 1)] |= (byte)((int)JsRound(15 * f) << ((acIndex & 1) << 2));
                acIndex++;
            }
        }

        return hash;
    }

    private static (double Dc, List<double> Ac, double Scale) EncodeChannel(double[] channel, int w, int h, int nx, int ny)
    {
        double dc = 0, scale = 0;
        var ac = new List<double>();
        double[] fx = new double[w];
        for (int cy = 0; cy < ny; cy++)
        {
            for (int cx = 0; cx * ny < nx * (ny - cy); cx++)
            {
                double f = 0;
                for (int x = 0; x < w; x++)
                {
                    fx[x] = Math.Cos(Math.PI / w * cx * (x + 0.5));
                }

                for (int y = 0; y < h; y++)
                {
                    double fy = Math.Cos(Math.PI / h * cy * (y + 0.5));
                    for (int x = 0; x < w; x++)
                    {
                        f += channel[x + (y * w)] * fx[x] * fy;
                    }
                }

                f /= w * h;
                if (cx > 0 || cy > 0)
                {
                    ac.Add(f);
                    scale = Math.Max(scale, Math.Abs(f));
                }
                else
                {
                    dc = f;
                }
            }
        }

        if (scale != 0)
        {
            for (int i = 0; i < ac.Count; i++)
            {
                ac[i] = 0.5 + (0.5 / scale * ac[i]);
            }
        }

        return (dc, ac, scale);
    }

    private static double[] DecodeChannel(ReadOnlySpan<byte> hash, int acStart, ref int acIndex, int nx, int ny, double scale)
    {
        var ac = new List<double>();
        for (int cy = 0; cy < ny; cy++)
        {
            for (int cx = cy > 0 ? 0 : 1; cx * ny < nx * (ny - cy); cx++)
            {
                int position = acStart + (acIndex >> 1);
                if (position >= hash.Length)
                {
                    throw new FormatException("The ThumbHash is truncated: it ends before its last AC coefficient.");
                }

                int nibble = (hash[position] >> ((acIndex & 1) << 2)) & 15;
                acIndex++;
                ac.Add(((nibble / 7.5) - 1) * scale);
            }
        }

        return ac.ToArray();
    }

    private static double GetApproximateAspectRatio(ReadOnlySpan<byte> hash)
    {
        if (hash.Length < 5)
        {
            throw new FormatException("A ThumbHash is at least 5 bytes long.");
        }

        int header = hash[3];
        bool hasAlpha = (hash[2] & 0x80) != 0;
        bool isLandscape = (hash[4] & 0x80) != 0;
        int lx = isLandscape ? (hasAlpha ? 5 : 7) : header & 7;
        int ly = isLandscape ? header & 7 : (hasAlpha ? 5 : 7);
        if (lx == 0 || ly == 0)
        {
            throw new FormatException("The ThumbHash stores a zero luminance dimension.");
        }

        return (double)lx / ly;
    }

    private static Header ReadHeader(ReadOnlySpan<byte> hash)
    {
        if (hash.Length < 5)
        {
            throw new FormatException("A ThumbHash is at least 5 bytes long.");
        }

        int header24 = hash[0] | (hash[1] << 8) | (hash[2] << 16);
        int header16 = hash[3] | (hash[4] << 8);
        bool hasAlpha = (header24 >> 23) != 0;
        if (hasAlpha && hash.Length < 6)
        {
            throw new FormatException("A ThumbHash with alpha is at least 6 bytes long.");
        }

        bool isLandscape = (header16 >> 15) != 0;
        return new Header
        {
            LDc = (header24 & 63) / 63.0,
            PDc = (((header24 >> 6) & 63) / 31.5) - 1,
            QDc = (((header24 >> 12) & 63) / 31.5) - 1,
            LScale = ((header24 >> 18) & 31) / 31.0,
            HasAlpha = hasAlpha,
            PScale = ((header16 >> 3) & 63) / 63.0,
            QScale = ((header16 >> 9) & 63) / 63.0,
            Lx = Math.Max(3, isLandscape ? (hasAlpha ? 5 : 7) : header16 & 7),
            Ly = Math.Max(3, isLandscape ? header16 & 7 : (hasAlpha ? 5 : 7)),
            ADc = hasAlpha ? (hash[5] & 15) / 15.0 : 1,
            AScale = hasAlpha ? (hash[5] >> 4) / 15.0 : 0,
        };
    }

    /// <summary><c>Math.round</c> of JavaScript: the nearest integer, with halves rounded towards positive infinity.</summary>
    private static double JsRound(double value)
    {
        double floor = Math.Floor(value);
        return value - floor >= 0.5 ? floor + 1 : floor;
    }

    /// <summary>The reference's store into a byte array: clamp to 0-255, then truncate.</summary>
    private static byte ToByte(double value) => (byte)Math.Max(0, 255 * Math.Min(1, value));

    private static byte UnitToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);

    private readonly struct Header
    {
        public double LDc { get; init; }

        public double PDc { get; init; }

        public double QDc { get; init; }

        public double LScale { get; init; }

        public bool HasAlpha { get; init; }

        public double PScale { get; init; }

        public double QScale { get; init; }

        public int Lx { get; init; }

        public int Ly { get; init; }

        public double ADc { get; init; }

        public double AScale { get; init; }
    }
}

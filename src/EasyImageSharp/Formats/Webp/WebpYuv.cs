using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// Converts the 4:2:0 planes of a decoded VP8 key frame to RGBA with libwebp's "fancy" upsampler: every
/// output pixel takes a bilinear blend of the four surrounding chroma samples (weights 9/3/3/1) and the
/// YCbCr to RGB matrix is evaluated in the same fixed-point arithmetic as the reference decoder.
/// </summary>
internal static class WebpYuv
{
    private const int Fix = 6;                 // Fractional bits of the fixed-point conversion.
    private const int Mask = (256 << Fix) - 1; // Values outside this range need clamping.

    private const int YScale = 19077;  // 1.164 * 256 * 64
    private const int VToR = 26149;    // 1.596 * 256 * 64
    private const int UToG = 6419;     // 0.391 * 256 * 64
    private const int VToG = 13320;    // 0.813 * 256 * 64
    private const int UToB = 33050;    // 2.018 * 256 * 64
    private const int RCst = -14234;   // -223.1 * 64
    private const int GCst = 8708;     // 136.4 * 64
    private const int BCst = -17685;   // -276.3 * 64

    /// <summary>Converts the visible part of <paramref name="planes"/> into <paramref name="destination"/> as opaque pixels.</summary>
    public static void ToRgba(Vp8Planes planes, Span<Rgba32> destination)
    {
        int width = planes.Width;
        int height = planes.Height;
        byte[] y = planes.Y;
        byte[] u = planes.U;
        byte[] v = planes.V;
        int yStride = planes.YStride;
        int uvStride = planes.UvStride;

        // First output row: the chroma samples are mirrored across the top edge.
        UpsamplePair(y, 0, -1, u, v, 0, 0, destination, 0, -1, width);

        int uvRow = 0;
        int row = 0;
        for (; row + 2 < height; row += 2)
        {
            int top = uvRow * uvStride;
            uvRow++;
            int cur = uvRow * uvStride;
            UpsamplePair(
                y, (row + 1) * yStride, (row + 2) * yStride, u, v, top, cur,
                destination, (row + 1) * width, (row + 2) * width, width);
        }

        if ((height & 1) == 0)
        {
            // Even height: the last row is mirrored across the bottom edge.
            int cur = uvRow * uvStride;
            UpsamplePair(y, (height - 1) * yStride, -1, u, v, cur, cur, destination, (height - 1) * width, -1, width);
        }
    }

    /// <summary>Overwrites the alpha channel of every pixel from a full-resolution alpha plane.</summary>
    public static void ApplyAlpha(Span<Rgba32> pixels, byte[] alpha, int count)
    {
        for (int i = 0; i < count; i++)
        {
            pixels[i].A = alpha[i];
        }
    }

    /// <summary>Packs the two chroma samples of one position into a single word so both interpolate at once.</summary>
    private static uint LoadUv(byte u, byte v) => (uint)(u | (v << 16));

    /// <summary>
    /// Produces one or two output rows from a pair of chroma rows. A row offset of -1 means "not present",
    /// which happens for the very first and (when the height is even) the very last output row.
    /// </summary>
    private static void UpsamplePair(
        byte[] y, int topY, int bottomY,
        byte[] u, byte[] v, int topUv, int curUv,
        Span<Rgba32> destination, int topDst, int bottomDst, int width)
    {
        int lastPair = (width - 1) >> 1;
        uint tlUv = LoadUv(u[topUv], v[topUv]);
        uint lUv = LoadUv(u[curUv], v[curUv]);

        uint first = ((3 * tlUv) + lUv + 0x00020002u) >> 2;
        destination[topDst] = YuvToRgba(y[topY], (byte)first, (byte)(first >> 16));
        if (bottomY >= 0)
        {
            uint firstBottom = ((3 * lUv) + tlUv + 0x00020002u) >> 2;
            destination[bottomDst] = YuvToRgba(y[bottomY], (byte)firstBottom, (byte)(firstBottom >> 16));
        }

        for (int x = 1; x <= lastPair; x++)
        {
            uint tUv = LoadUv(u[topUv + x], v[topUv + x]);
            uint uv = LoadUv(u[curUv + x], v[curUv + x]);
            uint avg = tlUv + tUv + lUv + uv + 0x00080008u;
            uint diag12 = (avg + (2 * (tUv + lUv))) >> 3;
            uint diag03 = (avg + (2 * (tlUv + uv))) >> 3;

            uint uv0 = (diag12 + tlUv) >> 1;
            uint uv1 = (diag03 + tUv) >> 1;
            destination[topDst + (2 * x) - 1] = YuvToRgba(y[topY + (2 * x) - 1], (byte)uv0, (byte)(uv0 >> 16));
            destination[topDst + (2 * x)] = YuvToRgba(y[topY + (2 * x)], (byte)uv1, (byte)(uv1 >> 16));

            if (bottomY >= 0)
            {
                uint bottom0 = (diag03 + lUv) >> 1;
                uint bottom1 = (diag12 + uv) >> 1;
                destination[bottomDst + (2 * x) - 1] = YuvToRgba(y[bottomY + (2 * x) - 1], (byte)bottom0, (byte)(bottom0 >> 16));
                destination[bottomDst + (2 * x)] = YuvToRgba(y[bottomY + (2 * x)], (byte)bottom1, (byte)(bottom1 >> 16));
            }

            tlUv = tUv;
            lUv = uv;
        }

        if ((width & 1) == 0)
        {
            uint last = ((3 * tlUv) + lUv + 0x00020002u) >> 2;
            destination[topDst + width - 1] = YuvToRgba(y[topY + width - 1], (byte)last, (byte)(last >> 16));
            if (bottomY >= 0)
            {
                uint lastBottom = ((3 * lUv) + tlUv + 0x00020002u) >> 2;
                destination[bottomDst + width - 1] = YuvToRgba(y[bottomY + width - 1], (byte)lastBottom, (byte)(lastBottom >> 16));
            }
        }
    }

    private static int MultHi(int v, int coefficient) => (v * coefficient) >> 8;

    private static byte Clip8(int v) => (v & ~Mask) == 0 ? (byte)(v >> Fix) : v < 0 ? (byte)0 : (byte)255;

    private static Rgba32 YuvToRgba(int y, int u, int v)
    {
        int scaledY = MultHi(y, YScale);
        byte r = Clip8(scaledY + MultHi(v, VToR) + RCst);
        byte g = Clip8(scaledY - MultHi(u, UToG) - MultHi(v, VToG) + GCst);
        byte b = Clip8(scaledY + MultHi(u, UToB) + BCst);
        return new Rgba32(r, g, b, 255);
    }
}

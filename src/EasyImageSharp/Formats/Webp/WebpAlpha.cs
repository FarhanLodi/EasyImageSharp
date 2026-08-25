namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// Decodes the ALPH chunk of a lossy WebP image (RFC 9649 section 2.4): a full-resolution 8-bit alpha plane
/// stored either raw or as the green channel of a VP8L image stream, in both cases optionally pre-filtered
/// with one of the three spatial predictors.
/// </summary>
internal static class WebpAlpha
{
    private const int FilterNone = 0;
    private const int FilterHorizontal = 1;
    private const int FilterVertical = 2;
    private const int FilterGradient = 3;

    /// <summary>Decodes an ALPH chunk into a <paramref name="width"/> x <paramref name="height"/> alpha plane.</summary>
    public static byte[] Decode(byte[] data, int start, int length, int width, int height)
    {
        if (length < 1)
        {
            throw new InvalidImageContentException("WebP ALPH chunk is empty.");
        }

        byte header = data[start];
        int method = header & 0x03;
        int filter = (header >> 2) & 0x03;

        byte[] alpha;
        if (method == 0)
        {
            long needed = (long)width * height;
            if (length - 1 < needed)
            {
                throw new InvalidImageContentException("WebP ALPH chunk is shorter than the uncompressed alpha plane.");
            }

            alpha = new byte[width * height];
            Array.Copy(data, start + 1, alpha, 0, alpha.Length);
        }
        else if (method == 1)
        {
            alpha = Vp8LDecoder.DecodeAlpha(data, start + 1, length - 1, width, height);
        }
        else
        {
            throw new InvalidImageContentException($"WebP ALPH chunk uses the reserved compression method {method}.");
        }

        Unfilter(alpha, width, height, filter);
        return alpha;
    }

    private static void Unfilter(byte[] alpha, int width, int height, int filter)
    {
        if (filter == FilterNone)
        {
            return;
        }

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int previous = row - width;
            switch (filter)
            {
                case FilterHorizontal:
                    UnfilterHorizontal(alpha, previous, row, width, y > 0);
                    break;

                case FilterVertical:
                    if (y == 0)
                    {
                        UnfilterHorizontal(alpha, previous, row, width, false);
                    }
                    else
                    {
                        for (int x = 0; x < width; x++)
                        {
                            alpha[row + x] = (byte)(alpha[previous + x] + alpha[row + x]);
                        }
                    }

                    break;

                default:
                    if (y == 0)
                    {
                        UnfilterHorizontal(alpha, previous, row, width, false);
                    }
                    else
                    {
                        int top = alpha[previous];
                        int topLeft = top;
                        int left = top;
                        for (int x = 0; x < width; x++)
                        {
                            top = alpha[previous + x];
                            left = (byte)(alpha[row + x] + GradientPredictor(left, top, topLeft));
                            topLeft = top;
                            alpha[row + x] = (byte)left;
                        }
                    }

                    break;
            }
        }
    }

    private static void UnfilterHorizontal(byte[] alpha, int previous, int row, int width, bool hasPrevious)
    {
        int pred = hasPrevious ? alpha[previous] : 0;
        for (int x = 0; x < width; x++)
        {
            pred = (byte)(pred + alpha[row + x]);
            alpha[row + x] = (byte)pred;
        }
    }

    private static int GradientPredictor(int a, int b, int c)
    {
        int g = a + b - c;
        return (g & ~0xff) == 0 ? g : g < 0 ? 0 : 255;
    }
}

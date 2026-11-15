using System.Numerics;
using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.JpegXl;

/// <summary>The interleaved sample type exchanged with libjxl.</summary>
internal enum SampleType
{
    UInt8,
    UInt16,
    Float32,
}

/// <summary>
/// Row conversions between interleaved libjxl buffers and <c>TPixel</c>. Mirrors the core library's rules:
/// wide formats (<see cref="Rgb48"/>, <see cref="Rgba64"/>, <see cref="L16"/>, <see cref="La32"/>,
/// <see cref="RgbaVector"/>) exchange full precision, every other format goes through <see cref="Rgba32"/>
/// with round-to-nearest narrowing. Float samples are passed to <see cref="RgbaVector"/> unclamped.
/// </summary>
internal static class PixelRows
{
    /// <summary>True for pixel formats with more than 8 bits per component.</summary>
    internal static bool IsHighPrecision<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => typeof(TPixel) == typeof(Rgb48)
            || typeof(TPixel) == typeof(Rgba64)
            || typeof(TPixel) == typeof(L16)
            || typeof(TPixel) == typeof(La32)
            || typeof(TPixel) == typeof(RgbaVector);

    /// <summary>True for the single-channel (optionally with alpha) luminance formats.</summary>
    internal static bool IsGray<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => typeof(TPixel) == typeof(L8)
            || typeof(TPixel) == typeof(L16)
            || typeof(TPixel) == typeof(La16)
            || typeof(TPixel) == typeof(La32);

    /// <summary>True when the pixel format cannot carry alpha.</summary>
    internal static bool IsAlwaysOpaque<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => typeof(TPixel) == typeof(Rgb24)
            || typeof(TPixel) == typeof(Bgr24)
            || typeof(TPixel) == typeof(Rgb48)
            || typeof(TPixel) == typeof(L8)
            || typeof(TPixel) == typeof(L16);

    /// <summary>The size in bytes of one sample.</summary>
    internal static int SampleSize(SampleType type) => type switch { SampleType.UInt8 => 1, SampleType.UInt16 => 2, _ => 4 };

    /// <summary>Converts an interleaved RGBA row (4 samples per pixel) into a pixel row.</summary>
    internal static void FromRgba<TPixel>(ReadOnlySpan<byte> source, SampleType type, Span<TPixel> destination)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        switch (type)
        {
            case SampleType.UInt8:
            {
                ReadOnlySpan<Rgba32> pixels = MemoryMarshal.Cast<byte, Rgba32>(source)[..destination.Length];
                if (typeof(TPixel) == typeof(Rgba32))
                {
                    pixels.CopyTo(MemoryMarshal.Cast<TPixel, Rgba32>(destination));
                    return;
                }

                for (int x = 0; x < destination.Length; x++)
                {
                    destination[x] = TPixel.FromRgba32(pixels[x]);
                }

                return;
            }

            case SampleType.UInt16:
            {
                ReadOnlySpan<Rgba64> pixels = MemoryMarshal.Cast<byte, Rgba64>(source)[..destination.Length];
                if (typeof(TPixel) == typeof(Rgba64))
                {
                    pixels.CopyTo(MemoryMarshal.Cast<TPixel, Rgba64>(destination));
                }
                else if (IsHighPrecision<TPixel>())
                {
                    for (int x = 0; x < destination.Length; x++)
                    {
                        destination[x] = TPixel.FromScaledVector4(pixels[x].ToScaledVector4());
                    }
                }
                else
                {
                    for (int x = 0; x < destination.Length; x++)
                    {
                        destination[x] = TPixel.FromRgba32(pixels[x].ToRgba32());
                    }
                }

                return;
            }

            default:
            {
                ReadOnlySpan<Vector4> pixels = MemoryMarshal.Cast<byte, Vector4>(source)[..destination.Length];
                for (int x = 0; x < destination.Length; x++)
                {
                    destination[x] = TPixel.FromScaledVector4(pixels[x]);
                }

                return;
            }
        }
    }

    /// <summary>
    /// Writes a pixel row as interleaved samples: RGB(A) with <paramref name="color"/> = 3 or grey(A) with 1, alpha
    /// appended when <paramref name="alpha"/> is set. Returns whether every pixel was fully opaque.
    /// </summary>
    internal static bool ToInterleaved<TPixel>(ReadOnlySpan<TPixel> source, SampleType type, int color, bool alpha, Span<byte> destination)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int channels = color + (alpha ? 1 : 0);
        bool opaque = true;
        switch (type)
        {
            case SampleType.UInt8:
                for (int x = 0; x < source.Length; x++)
                {
                    Rgba32 p = source[x].ToRgba32();
                    Span<byte> d = destination.Slice(x * channels, channels);
                    d[0] = p.R;
                    if (color == 3)
                    {
                        d[1] = p.G;
                        d[2] = p.B;
                    }

                    if (alpha)
                    {
                        d[color] = p.A;
                    }

                    opaque &= p.A == byte.MaxValue;
                }

                break;

            case SampleType.UInt16:
            {
                Span<ushort> samples = MemoryMarshal.Cast<byte, ushort>(destination);
                for (int x = 0; x < source.Length; x++)
                {
                    Rgba64 p = typeof(TPixel) == typeof(Rgba64) ? MemoryMarshal.Cast<TPixel, Rgba64>(source)[x]
                        : IsHighPrecision<TPixel>() ? Rgba64.FromScaledVector4(source[x].ToScaledVector4())
                        : Rgba64.FromRgba32(source[x].ToRgba32());
                    Span<ushort> d = samples.Slice(x * channels, channels);
                    d[0] = p.R;
                    if (color == 3)
                    {
                        d[1] = p.G;
                        d[2] = p.B;
                    }

                    if (alpha)
                    {
                        d[color] = p.A;
                    }

                    opaque &= p.A == ushort.MaxValue;
                }

                break;
            }

            default:
            {
                Span<float> samples = MemoryMarshal.Cast<byte, float>(destination);
                for (int x = 0; x < source.Length; x++)
                {
                    Vector4 p = source[x].ToScaledVector4();
                    Span<float> d = samples.Slice(x * channels, channels);
                    d[0] = p.X;
                    if (color == 3)
                    {
                        d[1] = p.Y;
                        d[2] = p.Z;
                    }

                    if (alpha)
                    {
                        d[color] = p.W;
                    }

                    opaque &= p.W >= 1f;
                }

                break;
            }
        }

        return opaque;
    }
}

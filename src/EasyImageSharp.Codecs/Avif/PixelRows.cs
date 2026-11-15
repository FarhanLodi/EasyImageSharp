using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Avif;

/// <summary>
/// Row conversions between interleaved native RGBA buffers and <c>TPixel</c>. Mirrors the core library's
/// rules: wide formats (<see cref="Rgb48"/>, <see cref="Rgba64"/>, <see cref="L16"/>, <see cref="La32"/>,
/// <see cref="RgbaVector"/>) exchange full precision, every other format goes through <see cref="Rgba32"/>
/// with round-to-nearest narrowing.
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

    /// <summary>
    /// True when the pixel format cannot carry alpha, so an alpha plane would always be opaque. Unknown
    /// pixel types are treated as possibly carrying alpha and are scanned instead.
    /// </summary>
    internal static bool IsAlwaysOpaque<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => typeof(TPixel) == typeof(Rgb24)
            || typeof(TPixel) == typeof(Bgr24)
            || typeof(TPixel) == typeof(Rgb48)
            || typeof(TPixel) == typeof(L8)
            || typeof(TPixel) == typeof(L16);

    /// <summary>Converts interleaved 8-bit RGBA into a pixel row.</summary>
    internal static void FromRgba8<TPixel>(ReadOnlySpan<byte> source, Span<TPixel> destination)
        where TPixel : unmanaged, IPixel<TPixel>
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
    }

    /// <summary>Converts interleaved 16-bit RGBA (native endianness) into a pixel row.</summary>
    internal static void FromRgba16<TPixel>(ReadOnlySpan<byte> source, Span<TPixel> destination)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ReadOnlySpan<Rgba64> pixels = MemoryMarshal.Cast<byte, Rgba64>(source)[..destination.Length];
        if (typeof(TPixel) == typeof(Rgba64))
        {
            pixels.CopyTo(MemoryMarshal.Cast<TPixel, Rgba64>(destination));
            return;
        }

        if (IsHighPrecision<TPixel>())
        {
            for (int x = 0; x < destination.Length; x++)
            {
                destination[x] = TPixel.FromScaledVector4(pixels[x].ToScaledVector4());
            }

            return;
        }

        for (int x = 0; x < destination.Length; x++)
        {
            destination[x] = TPixel.FromRgba32(pixels[x].ToRgba32());
        }
    }

    /// <summary>Writes a pixel row as interleaved 8-bit RGBA and reports whether every pixel was opaque.</summary>
    internal static bool ToRgba8<TPixel>(ReadOnlySpan<TPixel> source, Span<byte> destination)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Span<Rgba32> pixels = MemoryMarshal.Cast<byte, Rgba32>(destination)[..source.Length];
        if (typeof(TPixel) == typeof(Rgba32))
        {
            MemoryMarshal.Cast<TPixel, Rgba32>(source).CopyTo(pixels);
        }
        else
        {
            for (int x = 0; x < source.Length; x++)
            {
                pixels[x] = source[x].ToRgba32();
            }
        }

        bool opaque = true;
        foreach (Rgba32 pixel in pixels)
        {
            opaque &= pixel.A == byte.MaxValue;
        }

        return opaque;
    }

    /// <summary>Writes a pixel row as interleaved 16-bit RGBA and reports whether every pixel was opaque.</summary>
    internal static bool ToRgba16<TPixel>(ReadOnlySpan<TPixel> source, Span<byte> destination)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Span<Rgba64> pixels = MemoryMarshal.Cast<byte, Rgba64>(destination)[..source.Length];
        if (typeof(TPixel) == typeof(Rgba64))
        {
            MemoryMarshal.Cast<TPixel, Rgba64>(source).CopyTo(pixels);
        }
        else if (IsHighPrecision<TPixel>())
        {
            for (int x = 0; x < source.Length; x++)
            {
                pixels[x] = Rgba64.FromScaledVector4(source[x].ToScaledVector4());
            }
        }
        else
        {
            for (int x = 0; x < source.Length; x++)
            {
                pixels[x] = Rgba64.FromRgba32(source[x].ToRgba32());
            }
        }

        bool opaque = true;
        foreach (Rgba64 pixel in pixels)
        {
            opaque &= pixel.A == ushort.MaxValue;
        }

        return opaque;
    }
}

using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing.Quantization;

/// <summary>
/// A colour quantization algorithm: builds a palette of at most <see cref="QuantizerOptions.MaxColors"/>
/// colours for an image and maps every pixel to one of them. Instances are immutable descriptions;
/// call <see cref="CreatePixelSpecificQuantizer{TPixel}()"/> to obtain a stateful worker for a pixel format.
/// </summary>
public interface IQuantizer
{
    /// <summary>The options this quantizer was created with.</summary>
    QuantizerOptions Options { get; }

    /// <summary>Creates a stateful quantizer for the given pixel format using <see cref="Options"/>.</summary>
    IQuantizer<TPixel> CreatePixelSpecificQuantizer<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>;

    /// <summary>Creates a stateful quantizer for the given pixel format using the supplied options.</summary>
    IQuantizer<TPixel> CreatePixelSpecificQuantizer<TPixel>(QuantizerOptions options)
        where TPixel : unmanaged, IPixel<TPixel>;
}

/// <summary>
/// A stateful quantizer bound to a pixel format. Feed it colours with <see cref="AddPaletteColors(ImageFrame{TPixel})"/>
/// (optional, for palettes shared by several frames), then call <see cref="QuantizeFrame(ImageFrame{TPixel})"/>
/// to obtain palette indices for a frame; a frame quantized without any prior colours builds its palette from
/// itself. The palette is rebuilt lazily whenever more colours are added.
/// </summary>
/// <typeparam name="TPixel">The pixel format.</typeparam>
public interface IQuantizer<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    /// <summary>The options in effect for this quantizer.</summary>
    QuantizerOptions Options { get; }

    /// <summary>
    /// The current palette. Building it on first access requires that colours have been added (or that the
    /// quantizer uses a fixed palette); otherwise the palette is empty.
    /// </summary>
    ReadOnlyMemory<TPixel> Palette { get; }

    /// <summary>Adds every pixel of <paramref name="frame"/> to the colour statistics used to build the palette.</summary>
    void AddPaletteColors(ImageFrame<TPixel> frame);

    /// <summary>Adds the pixels of <paramref name="frame"/> inside <paramref name="bounds"/> to the colour statistics used to build the palette.</summary>
    void AddPaletteColors(ImageFrame<TPixel> frame, Rectangle bounds);

    /// <summary>Maps every pixel of <paramref name="frame"/> to the palette, dithering as configured.</summary>
    IndexedImageFrame<TPixel> QuantizeFrame(ImageFrame<TPixel> frame);

    /// <summary>Maps the pixels of <paramref name="frame"/> inside <paramref name="bounds"/> to the palette, dithering as configured.</summary>
    IndexedImageFrame<TPixel> QuantizeFrame(ImageFrame<TPixel> frame, Rectangle bounds);

    /// <summary>Returns the palette index nearest to <paramref name="color"/> (without dithering) and the palette colour at that index.</summary>
    byte GetQuantizedColor(TPixel color, out TPixel match);
}

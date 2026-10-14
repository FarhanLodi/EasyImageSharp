# EasyImageSharp.SkiaSharp

Optional native interop, vector drawing, text shaping, and RGB color-space conversion for EasyImageSharp. The core package keeps its managed-only dependency boundary.

```csharp
using EasyImageSharp.SkiaSharp;
using SkiaSharp;

using var result = image.DrawWithSkia(canvas =>
{
    using var builder = new SKPathBuilder();
    builder.MoveTo(10, 10);
    builder.CubicTo(30, 0, 50, 70, 80, 20);
    using var path = builder.Detach();
    using var paint = new SKPaint
    {
        Color = SKColors.RoyalBlue,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 3,
        IsAntialias = true,
    };
    canvas.DrawPath(path, paint);
});

using var font = new SKFont(typeface, 24); // supply a typeface covering the text
using var textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
using var captioned = result.DrawShapedText("مرحبا", new SKPoint(100, 60), font, textPaint);
```

## Pixel ownership and precision

`ToSkiaBitmap` copies a selected frame into an owned, premultiplied RGBA float bitmap. Dispose the returned bitmap. `ToEasyImage<TPixel>` copies a native bitmap through a straight-alpha RGBA float intermediate, respecting native row strides. There are no long-lived pins or shared buffers. Integer target formats quantize to their native range; float targets retain extended-range RGB where Skia supports it. Premultiplication discards hidden RGB at zero alpha.

`DrawWithSkia` returns a new image and changes only the selected frame. The callback may use the complete Skia canvas/path/paint/shader API but must not retain or dispose its canvas. Rendering native pixels allocates additional memory outside core execution budgets. No automatic ICC conversion happens during drawing or ordinary bitmap interop; colors remain in the caller's chosen encoding.

## Text and color capabilities

HarfBuzz shapes one run, supporting the scripts available in the supplied font. This adapter does not implement paragraph bidi ordering, font fallback, line wrapping, or a document layout engine. Load a known font explicitly for reproducible output.

`ConvertColorSpace(sourceSpace, destinationSpace)` converts all frames using Skia RGB color spaces, then clears stale ICC tags. The caller must attach a destination profile when saving to a format requiring explicit profile metadata. This is **not full ICC support**: arbitrary LUT profiles, CMYK, rendering-intent selection, and black-point compensation are outside this adapter. The core `IccMatrixProfile` API separately supports its documented RGB matrix/TRC subset.

## Native deployment

The package references SkiaSharp and SkiaSharp.HarfBuzz 4.153.1. Windows/macOS native dependencies follow those packages. Linux applications must add matching `SkiaSharp.NativeAssets.Linux` (or `.NoDependencies` for explicit font loading) and `HarfBuzzSharp.NativeAssets.Linux` matching the transitive HarfBuzzSharp version. Native AOT and each deployment RID require separate application validation; core AOT smoke results do not certify this optional package.

Sources: [official SkiaSharp package and deployment guidance](https://www.nuget.org/packages/SkiaSharp/4.153.1), [official HarfBuzz integration](https://www.nuget.org/packages/SkiaSharp.HarfBuzz/4.153.1), [Skia color-space API](https://api.skia.org/classSkColorSpace.html).

using EasyImageSharp.PixelFormats;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>
/// Object removal and hole filling with LaMa (<see cref="ModelRegistry.Inpainting"/>): the masked pixels of every frame
/// are replaced in place with content synthesised from their surroundings. Use <c>Clone()</c> first to keep the original.
/// </summary>
/// <remarks>
/// <para>
/// Works on images of any size. The masked pixels are grouped into regions; each region is filled from a crop around it
/// that includes <see cref="InpaintingOptions.ContextMargin"/> of surrounding context and is at least the model's
/// 512x512 working size when the image allows it. Crops larger than 512 px are downscaled for the model and the result is
/// scaled back, so very large solid holes are filled at a lower effective resolution; long thin masks (wires,
/// scribbles, outlines) are split into chunks of half the working size (256 px for the built-in model) that are each
/// filled at full resolution. Crops smaller than 512 px
/// (small images) are mirror-padded instead of upscaled. Only masked pixels change: every other pixel keeps its exact value, and masked
/// pixels keep their alpha.
/// </para>
/// <para>
/// LaMa fills holes with plausible texture and structure; it does not know what was behind an object. Results are
/// best when the mask covers the whole object including its soft edge and shadow (see
/// <see cref="InpaintingOptions.MaskDilation"/>). The built-in export has a fixed 512x512 input and runs one inference
/// per crop.
/// </para>
/// </remarks>
public static class InpaintingExtensions
{
    /// <summary>
    /// Fills the pixels where <paramref name="mask"/> (same size as the image) is at or above
    /// <see cref="InpaintingOptions.MaskThreshold"/>, in every frame. The model download runs asynchronously and
    /// inference runs on the thread pool.
    /// </summary>
    /// <exception cref="ArgumentException">The mask size differs from a frame's size.</exception>
    public static async Task InpaintAsync<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, Image<L8> mask, InpaintingOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        ArgumentNullException.ThrowIfNull(mask);
        InpaintingOptions effective = options ?? InpaintingOptions.Default;
        effective.Validate();
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.Inpainting, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => InpaintingRunner.Inpaint(session, image, mask, effective, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="InpaintAsync{TPixel}"/>.</summary>
    public static void Inpaint<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, Image<L8> mask, InpaintingOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        ArgumentNullException.ThrowIfNull(mask);
        InpaintingOptions effective = options ?? InpaintingOptions.Default;
        effective.Validate();
        InpaintingRunner.Inpaint(ai.GetSession(ModelRegistry.Inpainting, cancellationToken), image, mask, effective, cancellationToken);
    }

    /// <summary>
    /// Fills the given rectangles (clipped to the image; empty ones are ignored) in every frame. Nothing is loaded or run
    /// when no rectangle overlaps the image.
    /// </summary>
    public static async Task InpaintRegionsAsync<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, IEnumerable<Rectangle> regions, InpaintingOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        ArgumentNullException.ThrowIfNull(regions);
        Rectangle[] list = regions.ToArray();
        InpaintingOptions effective = options ?? InpaintingOptions.Default;
        effective.Validate();
        if (!Overlaps(image, list))
        {
            return;
        }

        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.Inpainting, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => InpaintingRunner.Inpaint(session, image, list, effective, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="InpaintRegionsAsync{TPixel}"/>.</summary>
    public static void InpaintRegions<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, IEnumerable<Rectangle> regions, InpaintingOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        ArgumentNullException.ThrowIfNull(regions);
        Rectangle[] list = regions.ToArray();
        InpaintingOptions effective = options ?? InpaintingOptions.Default;
        effective.Validate();
        if (!Overlaps(image, list))
        {
            return;
        }

        InpaintingRunner.Inpaint(ai.GetSession(ModelRegistry.Inpainting, cancellationToken), image, list, effective, cancellationToken);
    }

    private static bool Overlaps<TPixel>(Image<TPixel> image, Rectangle[] regions)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        foreach (ImageFrame<TPixel> frame in image.Frames)
        {
            foreach (Rectangle region in regions)
            {
                if (region.Width > 0 && region.Height > 0 && region.X < frame.Width && region.Y < frame.Height
                    && (long)region.X + region.Width > 0 && (long)region.Y + region.Height > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void Check<TPixel>(Image<TPixel> image, ImageAiSession ai)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ai);
    }
}

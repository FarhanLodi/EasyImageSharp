using System.Buffers;
using System.Numerics;
using EasyImageSharp.PixelFormats;
using CoreProfile = EasyImageSharp.Metadata.Icc.IccProfile;

namespace EasyImageSharp.LittleCms;

/// <summary>ICC-aware image conversion using the optional native Little CMS engine.</summary>
public static class IccImageExtensions
{
    /// <summary>
    /// Converts all image frames between RGB ICC profiles, preserving alpha and attaching the destination profile.
    /// Both profiles are explicit; embedded profiles are not silently substituted. The source is unchanged.
    /// Native allocations are outside core pixel-buffer budgets. Cancellation discards the incomplete result.
    /// </summary>
    public static Image<TPixel> ConvertWithIcc<TPixel>(this Image<TPixel> image, CoreProfile source, CoreProfile destination,
        IccTransformOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        using IccTransform transform = IccTransform.Create(source, destination, options);
        if (transform.InputModel != IccColorModel.Rgb || transform.OutputModel != IccColorModel.Rgb)
            throw new ArgumentException("Image pixel conversion requires RGB profiles. Use interleaved buffers for CMYK/gray/Lab/XYZ.");
        Image<TPixel> result = image.Clone();
        try
        {
            foreach (ImageFrame<TPixel> frame in result.Frames)
            {
                int length = checked(frame.Width * 3);
                float[] input = ArrayPool<float>.Shared.Rent(length);
                float[]? output = null;
                try
                {
                    output = ArrayPool<float>.Shared.Rent(length);
                    for (int y = 0; y < frame.Height; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Span<TPixel> row = frame.GetRowSpan(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            Vector4 value = row[x].ToScaledVector4();
                            input[x * 3] = value.X;
                            input[(x * 3) + 1] = value.Y;
                            input[(x * 3) + 2] = value.Z;
                        }
                        transform.Transform(input.AsSpan(0, length), output.AsSpan(0, length), frame.Width, cancellationToken);
                        for (int x = 0; x < row.Length; x++)
                        {
                            float alpha = row[x].ToScaledVector4().W;
                            row[x] = TPixel.FromScaledVector4(new(output[x * 3], output[(x * 3) + 1], output[(x * 3) + 2], alpha));
                        }
                    }
                    frame.Metadata.IccProfile = destination.DeepClone();
                }
                finally { ArrayPool<float>.Shared.Return(input); if (output is not null) ArrayPool<float>.Shared.Return(output); }
            }
            result.Metadata.IccProfile = destination.DeepClone();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>
    /// Converts interleaved C,M,Y,K percentages (0-100) to a new opaque RGB image using explicit profiles.
    /// Rows may include padding measured in floats. The destination ICC profile is attached to the image.
    /// </summary>
    public static Image<TPixel> FromCmyk<TPixel>(ReadOnlySpan<float> cmyk, int width, int height,
        CoreProfile source, CoreProfile destination, IccTransformOptions? options = null, int rowStride = 0,
        CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        long packed = (long)width * 4;
        long stride = rowStride == 0 ? packed : rowStride;
        if (stride < packed || ((height - 1L) * stride) + packed > cmyk.Length)
            throw new ArgumentException("CMYK source rows do not fit the supplied buffer.", nameof(cmyk));
        cancellationToken.ThrowIfCancellationRequested();
        using IccTransform transform = IccTransform.Create(source, destination, options);
        if (transform.InputModel != IccColorModel.Cmyk || transform.OutputModel != IccColorModel.Rgb)
            throw new ArgumentException("FromCmyk requires a CMYK source and RGB destination profile.");
        var result = new Image<TPixel>(width, height);
        float[]? output = null;
        try
        {
            output = ArrayPool<float>.Shared.Rent(checked(width * 3));
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                transform.Transform(cmyk.Slice((int)(y * stride), (int)packed), output.AsSpan(0, width * 3), width, cancellationToken);
                Span<TPixel> row = result.Frames.RootFrame.GetRowSpan(y);
                for (int x = 0; x < width; x++) row[x] = TPixel.FromScaledVector4(new(output[x * 3], output[(x * 3) + 1], output[(x * 3) + 2], 1));
            }
            result.Metadata.IccProfile = destination.DeepClone();
            result.Frames.RootFrame.Metadata.IccProfile = destination.DeepClone();
            return result;
        }
        catch { result.Dispose(); throw; }
        finally { if (output is not null) ArrayPool<float>.Shared.Return(output); }
    }
}

using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

public static partial class ProcessingExtensions
{
    /// <summary>Processes an image with request-local parallelism, cancellation and pixel allocation limits.</summary>
    /// <remarks>Cancellation may leave an in-place mutation partially applied. Use Clone to preserve the source.</remarks>
    public static void Mutate<TPixel>(this Image<TPixel> source, ImageExecutionOptions options, Action<IImageProcessingContext> operation)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        using var execution = OperationExecution.Enter(options);
        source.Mutate(operation);
        OperationExecution.Checkpoint();
    }

    /// <summary>Processes an independent copy with request-local execution settings.</summary>
    public static Image<TPixel> Clone<TPixel>(this Image<TPixel> source, ImageExecutionOptions options, Action<IImageProcessingContext> operation)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        using var execution = OperationExecution.Enter(options);
        return source.Clone(operation);
    }
}

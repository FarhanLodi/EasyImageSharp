using EasyImageSharp.Formats;
using Microsoft.Extensions.Options;

namespace EasyImageSharp.Extensions.DependencyInjection.Internal;

/// <summary>The registered <see cref="IImageOptionsFactory"/>: maps the current options value onto library types.</summary>
internal sealed class ImageOptionsFactory : IImageOptionsFactory
{
    private readonly IOptionsMonitor<EasyImageSharpOptions> options;
    private readonly IOptions<ImageFormatRegistrations> formats;

    public ImageOptionsFactory(IOptionsMonitor<EasyImageSharpOptions> options, IOptions<ImageFormatRegistrations> formats)
    {
        this.options = options;
        this.formats = formats;
    }

    public DecoderOptions CreateDecoderOptions(CancellationToken cancellationToken = default)
    {
        EasyImageSharpOptions current = this.options.CurrentValue;
        return new DecoderOptions
        {
            MaxPixels = current.MaxPixels,
            MaxTotalPixels = current.MaxTotalPixels,
            MaxFrames = current.MaxFrames,
            MaxEncodedBytes = current.MaxEncodedBytes,
            ExecutionOptions = CreateExecutionOptions(current, cancellationToken),
            AdditionalFormats = this.formats.Value.Formats,
        };
    }

    public ImageExecutionOptions CreateExecutionOptions(CancellationToken cancellationToken = default)
        => CreateExecutionOptions(this.options.CurrentValue, cancellationToken);

    private static ImageExecutionOptions CreateExecutionOptions(EasyImageSharpOptions current, CancellationToken cancellationToken)
        => new()
        {
            Configuration = CreateConfiguration(current),
            CancellationToken = cancellationToken,
            MaxPixelBufferBytes = current.MaxPixelBufferBytes,
        };

    /// <summary>
    /// A request-local <see cref="Configuration"/> when parallelism is configured, otherwise <see langword="null"/>
    /// so the operation captures the process defaults exactly as it would without dependency injection.
    /// </summary>
    private static Configuration? CreateConfiguration(EasyImageSharpOptions current)
    {
        if (current.MaxDegreeOfParallelism is null && current.MinimumPixelsPerTask is null)
        {
            return null;
        }

        return new Configuration
        {
            MaxDegreeOfParallelism = current.MaxDegreeOfParallelism ?? Configuration.Default.MaxDegreeOfParallelism,
            MinimumPixelsPerTask = current.MinimumPixelsPerTask ?? Configuration.Default.MinimumPixelsPerTask,
        };
    }
}

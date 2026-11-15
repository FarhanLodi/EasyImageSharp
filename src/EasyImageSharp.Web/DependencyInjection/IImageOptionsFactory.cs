using EasyImageSharp.Formats;

namespace EasyImageSharp.Extensions.DependencyInjection;

/// <summary>
/// Creates <see cref="DecoderOptions"/> and <see cref="ImageExecutionOptions"/> from the registered
/// <see cref="EasyImageSharpOptions"/>. Registered as a singleton by <c>services.AddEasyImageSharp(...)</c>.
/// </summary>
/// <remarks>
/// Each call reads the current options value (so a reloaded configuration takes effect on the next call) and
/// returns a fresh instance. Pass the request's cancellation token, for example
/// <c>HttpContext.RequestAborted</c>, so decoding and processing stop when the caller goes away.
/// </remarks>
public interface IImageOptionsFactory
{
    /// <summary>
    /// Creates decoder options carrying the configured limits, every format registered with
    /// <c>services.AddEasyImageSharpFormat(...)</c>, and execution options bound to
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels decoding at the library's checkpoints.</param>
    /// <returns>A new <see cref="DecoderOptions"/> instance.</returns>
    DecoderOptions CreateDecoderOptions(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates execution options carrying the configured pixel-buffer budget and parallelism, bound to
    /// <paramref name="cancellationToken"/>. Pass them to <c>Mutate</c> or <c>Clone</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels processing at the library's checkpoints.</param>
    /// <returns>A new <see cref="ImageExecutionOptions"/> instance.</returns>
    ImageExecutionOptions CreateExecutionOptions(CancellationToken cancellationToken = default);
}

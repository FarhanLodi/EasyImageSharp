using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace EasyImageSharp.Streaming;

/// <summary>
/// Carries an exception raised by the caller's own stream through a decoder's malformed-input guard.
/// </summary>
/// <remarks>
/// A buffered decoder only ever parses memory, so it may treat every <see cref="IOException"/> or
/// <see cref="ArgumentException"/> as a symptom of malformed data (<see cref="Formats.DecoderGuard"/>). A
/// streaming source reads the caller's stream while it parses, and a network failure is not malformed data:
/// wrapping the stream's exception in this type keeps the guard from rewording it, and the source's public
/// entry points unwrap it again with <see cref="Rethrow"/>, so the caller sees the original exception with its
/// original stack trace.
/// </remarks>
internal sealed class SourceStreamException : Exception
{
    private readonly Exception original;

    public SourceStreamException(Exception original)
        : base(original.Message, original)
    {
        this.original = original;
    }

    /// <summary>Throws the stream's original exception, preserving its stack trace.</summary>
    [DoesNotReturn]
    public void Rethrow()
    {
        ExceptionDispatchInfo.Capture(this.original).Throw();
        throw this.original; // Unreachable: Throw never returns.
    }
}

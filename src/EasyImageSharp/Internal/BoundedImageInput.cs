using System.Buffers;
using EasyImageSharp.Formats;

namespace EasyImageSharp;

internal static class BoundedImageInput
{
    internal static ReadOnlyMemory<byte> Read(Stream stream, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        using var execution = OperationExecution.Enter(options.ExecutionOptions);
        CheckLength(stream, options);
        using var output = new MemoryStream();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                OperationExecution.Checkpoint();
                int count = stream.Read(chunk, 0, NextReadSize(output.Length, options, chunk.Length));
                if (count == 0) break;
                options.EnsureEncodedWithinLimits(output.Length + count);
                output.Write(chunk, 0, count);
            }

            return output.GetBuffer().AsMemory(0, checked((int)output.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    internal static async Task<ReadOnlyMemory<byte>> ReadAsync(Stream stream, DecoderOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        using var execution = OperationExecution.Enter(options.ExecutionOptions, cancellationToken);
        CheckLength(stream, options);
        using var output = new MemoryStream();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                int count = await stream.ReadAsync(chunk.AsMemory(0, NextReadSize(output.Length, options, chunk.Length)),
                    execution.CancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                options.EnsureEncodedWithinLimits(output.Length + count);
                output.Write(chunk, 0, count);
            }

            return output.GetBuffer().AsMemory(0, checked((int)output.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static int NextReadSize(long length, DecoderOptions options, int chunkSize)
        => (int)Math.Min(chunkSize, Math.Min(options.MaxEncodedBytes, int.MaxValue) - length + 1);

    private static void CheckLength(Stream stream, DecoderOptions options)
    {
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        if (stream.CanSeek) options.EnsureEncodedWithinLimits(stream.Length - stream.Position);
    }
}

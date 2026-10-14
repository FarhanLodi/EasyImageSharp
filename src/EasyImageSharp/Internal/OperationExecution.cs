using System.Runtime.CompilerServices;

namespace EasyImageSharp;

/// <summary>Async-flow-local execution policy; never mutates process defaults.</summary>
internal sealed class OperationExecution : IDisposable
{
    private static readonly AsyncLocal<OperationExecution?> Ambient = new();
    private readonly OperationExecution? parent;
    private readonly CancellationTokenSource? linkedCancellation;
    private readonly long budget;
    private long allocated;

    private OperationExecution(ImageExecutionOptions? options, CancellationToken cancellationToken)
    {
        this.parent = Ambient.Value;
        this.budget = options?.MaxPixelBufferBytes ?? long.MaxValue;
        if (this.budget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Pixel buffer budget must be positive.");
        }

        Configuration settings = options?.Configuration ?? this.parent?.Configuration ?? Configuration.Default;
        this.Configuration = new Configuration
        {
            MaxDegreeOfParallelism = settings.MaxDegreeOfParallelism,
            MinimumPixelsPerTask = settings.MinimumPixelsPerTask,
        };
        CancellationToken[] tokens = new[] { options?.CancellationToken ?? default,
            this.parent?.CancellationToken ?? default, cancellationToken }.Where(t => t.CanBeCanceled).Distinct().ToArray();
        if (tokens.Length > 1)
        {
            this.linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(tokens);
            this.CancellationToken = this.linkedCancellation.Token;
        }
        else
        {
            this.CancellationToken = tokens.Length == 1 ? tokens[0] : default;
        }

        if (this.CancellationToken.IsCancellationRequested)
        {
            this.linkedCancellation?.Dispose();
            this.CancellationToken.ThrowIfCancellationRequested();
        }

        Ambient.Value = this;
    }

    internal static OperationExecution? Current => Ambient.Value;
    internal Configuration Configuration { get; }
    internal CancellationToken CancellationToken { get; }
    internal long AllocatedPixelBytes => Interlocked.Read(ref this.allocated);
    internal static OperationExecution Enter(ImageExecutionOptions? options = null, CancellationToken cancellationToken = default)
        => new(options, cancellationToken);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Checkpoint() => Ambient.Value?.CancellationToken.ThrowIfCancellationRequested();

    internal static void ReservePixelBuffer<TPixel>(long count) where TPixel : unmanaged
    {
        Checkpoint();
        long bytes = checked(count * Unsafe.SizeOf<TPixel>());
        for (OperationExecution? scope = Ambient.Value; scope is not null; scope = scope.parent)
        {
            while (true)
            {
                long used = Interlocked.Read(ref scope.allocated);
                if (bytes > scope.budget - used)
                {
                    throw new ImageSizeLimitExceededException("The operation exceeds its cumulative pixel-buffer allocation budget.");
                }

                if (Interlocked.CompareExchange(ref scope.allocated, used + bytes, used) == used)
                {
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        Ambient.Value = this.parent;
        this.linkedCancellation?.Dispose();
    }
}

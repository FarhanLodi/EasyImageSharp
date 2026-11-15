using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>The output of one batched pass: <c>[N, ...]</c> float32 values and the full output shape.</summary>
/// <remarks>
/// <see cref="Data"/> may alias the runner's reusable output buffer; it is valid until the next
/// <see cref="OrtBatchRunner.Run"/> call on the same runner.
/// </remarks>
internal readonly record struct BatchOutput(Memory<float> Data, long[] Shape)
{
    /// <summary>Number of samples in the batch (the leading dimension).</summary>
    public int Count => checked((int)this.Shape[0]);

    /// <summary>Floats per sample.</summary>
    public int SampleLength => this.Data.Length / Math.Max(1, this.Count);

    /// <summary>The shape of one sample (the output shape without its leading dimension).</summary>
    public long[] SampleShape => this.Shape[1..];

    /// <summary>The values of sample <paramref name="index"/>.</summary>
    public Span<float> Sample(int index) => this.Data.Span.Slice(index * this.SampleLength, this.SampleLength);
}

/// <summary>
/// Runs equally shaped float32 samples through a single-input / single-output graph, stacking up to
/// <see cref="MaxBatch"/> of them along the leading axis when the model's batch dimension is dynamic and one at
/// a time otherwise. Input and output buffers are allocated once on the pinned object heap and reused for every
/// batch of the same shape; once the output shape for an input shape is known, outputs are written straight into
/// the reusable buffer instead of a fresh runtime allocation.
/// </summary>
/// <remarks>
/// One instance serves one logical call on one thread. <see cref="InferenceSession.Run(RunOptions, IReadOnlyCollection{string}, IReadOnlyCollection{OrtValue}, IReadOnlyCollection{string})"/>
/// is safe to call concurrently on a shared session, so concurrent callers each create their own runner and never
/// share buffers.
/// </remarks>
internal sealed class OrtBatchRunner
{
    /// <summary>Upper bound on the floats of one stacked input or output tensor (256 MiB), whatever the batch size.</summary>
    internal const long MaxBatchElements = 64L * 1024 * 1024;

    private readonly InferenceSession session;
    private readonly string inputName;
    private readonly string outputName;
    private readonly Dictionary<ShapeKey, long[]> sampleOutputShapes = new();
    private float[] input = [];
    private float[] output = [];

    public OrtBatchRunner(InferenceSession session, string inputName, string outputName, int maxBatchSize)
    {
        this.session = session;
        this.inputName = inputName;
        this.outputName = outputName;
        this.SupportsBatching = maxBatchSize > 1 && OrtRunner.HasDynamicBatch(session, inputName);
        this.MaxBatch = this.SupportsBatching ? maxBatchSize : 1;
    }

    /// <summary>True while samples are stacked; false for fixed-batch models or after a stacked run was rejected.</summary>
    public bool SupportsBatching { get; private set; }

    /// <summary>The largest number of samples one <see cref="Run"/> call stacks.</summary>
    public int MaxBatch { get; private set; }

    /// <summary>How many samples of <paramref name="sampleLength"/> floats one pass may stack.</summary>
    public int BatchCapacity(long sampleLength)
        => (int)Math.Clamp(MaxBatchElements / Math.Max(1, sampleLength), 1, this.MaxBatch);

    /// <summary>
    /// Runs <paramref name="samples"/> (each of <paramref name="sampleShape"/>, without the batch axis) in one pass
    /// and returns the stacked output. The caller keeps <paramref name="samples"/>.Count at or below
    /// <see cref="BatchCapacity"/>. When the model rejects a stacked tensor (a graph that declares a dynamic batch but
    /// hard-codes 1 internally), batching is switched off for the rest of the call and the caller is told to retry
    /// one sample at a time through <paramref name="rejected"/>.
    /// </summary>
    public BatchOutput Run(IReadOnlyList<float[]> samples, long[] sampleShape, CancellationToken cancellationToken, out bool rejected)
    {
        rejected = false;
        int count = samples.Count;
        if (count < 1 || count > this.MaxBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(samples), count, $"A batch holds 1 to {this.MaxBatch} samples.");
        }

        int sampleLength = samples[0].Length;
        long[] shape = new long[sampleShape.Length + 1];
        shape[0] = count;
        sampleShape.CopyTo(shape, 1);

        // A single sample needs no staging copy: feed the caller's array directly.
        Memory<float> data;
        if (count == 1)
        {
            data = samples[0];
        }
        else
        {
            int total = checked(sampleLength * count);
            if (this.input.Length < total)
            {
                // Size for the largest batch of this sample length, but never beyond the per-pass element bound.
                this.input = GC.AllocateUninitializedArray<float>(checked(sampleLength * this.BatchCapacity(sampleLength)), pinned: true);
            }

            for (int i = 0; i < count; i++)
            {
                if (samples[i].Length != sampleLength)
                {
                    throw new ArgumentException("Every sample of a batch must have the same length.", nameof(samples));
                }

                samples[i].CopyTo(this.input, i * sampleLength);
            }

            data = this.input.AsMemory(0, total);
        }

        var key = new ShapeKey(sampleShape);
        try
        {
            if (this.sampleOutputShapes.TryGetValue(key, out long[]? sampleOut))
            {
                long[] outShape = new long[sampleOut.Length + 1];
                outShape[0] = count;
                sampleOut.CopyTo(outShape, 1);
                long perSample = Product(sampleOut);
                int outTotal = checked((int)(perSample * count));
                if (this.output.Length < outTotal)
                {
                    this.output = GC.AllocateUninitializedArray<float>(checked((int)(perSample * Math.Max(count, this.BatchCapacity(perSample)))), pinned: true);
                }

                Memory<float> target = this.output.AsMemory(0, outTotal);
                OrtRunner.RunInto(this.session, this.inputName, data, shape, this.outputName, target, outShape, cancellationToken);
                return new BatchOutput(target, outShape);
            }

            TensorResult result = OrtRunner.RunCancelable(this.session, this.inputName, data, shape, [this.outputName], cancellationToken)[0];
            if (result.Shape.Length >= 2 && result.Shape[0] == count && Product(result.Shape) == result.Data.Length)
            {
                // Learn the per-sample output shape so later batches of this input shape reuse the output buffer.
                this.sampleOutputShapes[new ShapeKey((long[])sampleShape.Clone())] = result.Shape[1..];
                return new BatchOutput(result.Data, result.Shape);
            }

            if (count == 1)
            {
                // A rank-1 / unbatched output for a single sample: present it as one sample.
                return new BatchOutput(result.Data, [1, .. result.Shape]);
            }

            throw new ModelContractException(
                $"The model returned shape [{string.Join(",", result.Shape)}] for a batch of {count}; expected a leading batch dimension.");
        }
        catch (Exception ex) when (count > 1 && !cancellationToken.IsCancellationRequested && ex is OnnxRuntimeException or ModelContractException)
        {
            this.SupportsBatching = false;
            this.MaxBatch = 1;
            rejected = true;
            return default;
        }
    }

    private static long Product(long[] shape)
    {
        long product = 1;
        foreach (long dim in shape)
        {
            product *= dim;
        }

        return product;
    }

    /// <summary>Value-equality key over a sample shape.</summary>
    private readonly struct ShapeKey : IEquatable<ShapeKey>
    {
        private readonly long[] dims;

        public ShapeKey(long[] dims) => this.dims = dims;

        public bool Equals(ShapeKey other) => this.dims.AsSpan().SequenceEqual(other.dims);

        public override bool Equals(object? obj) => obj is ShapeKey other && this.Equals(other);

        public override int GetHashCode()
        {
            var hash = default(HashCode);
            foreach (long dim in this.dims)
            {
                hash.Add(dim);
            }

            return hash.ToHashCode();
        }
    }
}

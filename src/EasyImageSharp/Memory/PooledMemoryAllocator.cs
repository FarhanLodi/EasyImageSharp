using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace EasyImageSharp.Memory;

/// <summary>
/// The default allocator. It keeps released buffers and hands them out again, so a service that decodes and
/// resizes images all day reuses a working set of large arrays instead of allocating a fresh one on the
/// large-object heap for every frame - which is what drives generation 2 collections and heap fragmentation
/// under load.
/// </summary>
/// <remarks>
/// <para>
/// Requests are rounded up to one of four size classes per power of two, so a pooled buffer is at most 25%
/// (typically under 15%) larger than asked for; the caller only ever sees the requested length. Buffers are
/// kept per element type and size class, and the pool never keeps more than
/// <see cref="PooledMemoryAllocatorOptions.MaximumRetainedBytes"/>. Requests below
/// <see cref="PooledMemoryAllocatorOptions.MinimumPooledBufferBytes"/> or above
/// <see cref="PooledMemoryAllocatorOptions.MaximumPooledBufferBytes"/> bypass the pool. With
/// <see cref="PooledMemoryAllocatorOptions.TrimOnMemoryPressure"/> on, full garbage collections give back the
/// buffers nobody has rented for a minute (ten seconds while the machine is short of memory); the working set a
/// steady load keeps renting is left alone, because dropping it would only force the same allocation again.
/// </para>
/// <para>
/// A rented buffer may still hold the pixels of an image disposed earlier. The library requests
/// <see cref="AllocationOptions.Clean"/> memory for every frame whose pixels it does not overwrite in full before
/// the frame becomes reachable, so one image never exposes another's pixels; code that allocates from the pool
/// directly must make the same choice. Whatever the options, the slack between the requested length and the end
/// of the size class is cleared on every rental, so nothing foreign is reachable through the underlying array.
/// </para>
/// <para>The allocator is thread-safe.</para>
/// </remarks>
public sealed class PooledMemoryAllocator : MemoryAllocator
{
    /// <summary>
    /// A retained buffer nobody has rented for this long is released at the next full collection; so is a whole size
    /// class that has gone this long without being used.
    /// </summary>
    internal const long IdleTrimMilliseconds = 60_000;

    /// <summary>The same limit while the machine is short of memory.</summary>
    internal const long PressureIdleTrimMilliseconds = 10_000;

    private readonly long maximumRetainedBytes;
    private readonly long maximumPooledBufferBytes;
    private readonly long minimumPooledBufferBytes;
    private readonly ConcurrentDictionary<Type, ElementPool> pools = new();
    private long retainedBytes;
    private int lastTrimmedGeneration = -1;
    private Func<long> clock = static () => Environment.TickCount64;
    private int fullCollectionCallbacks;

    /// <summary>Creates a pool with the default <see cref="PooledMemoryAllocatorOptions"/>.</summary>
    public PooledMemoryAllocator()
        : this(new PooledMemoryAllocatorOptions())
    {
    }

    /// <summary>Creates a pool with the given bounds and policies.</summary>
    /// <param name="options">The pool's bounds; they are read once, here.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A byte limit is negative.</exception>
    public PooledMemoryAllocator(PooledMemoryAllocatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumRetainedBytes, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumPooledBufferBytes, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.MinimumPooledBufferBytes, nameof(options));
        this.maximumRetainedBytes = options.MaximumRetainedBytes;
        this.maximumPooledBufferBytes = options.MaximumPooledBufferBytes;
        this.minimumPooledBufferBytes = options.MinimumPooledBufferBytes;
        if (options.TrimOnMemoryPressure)
        {
            Gen2GcCallback.Register(static target => ((PooledMemoryAllocator)target).OnFullCollection(), this);
        }
    }

    /// <summary>How much trimming a full collection should do.</summary>
    internal enum TrimLevel
    {
        /// <summary>
        /// Every size class gives back its surplus - the buffers that sat in it, unrented, for at least
        /// <see cref="IdleTrimMilliseconds"/> - and classes unused for that long are emptied.
        /// </summary>
        Surplus,

        /// <summary>As <see cref="Surplus"/>, with <see cref="PressureIdleTrimMilliseconds"/> as the limit.</summary>
        Pressure,

        /// <summary>Everything.</summary>
        All,
    }

    /// <summary>The bytes currently kept for reuse, across all element types and sizes. Buffers in use do not count.</summary>
    public long RetainedBytes => Interlocked.Read(ref this.retainedBytes);

    /// <summary>How many times the full-collection callback has run; a test hook.</summary>
    internal int FullCollectionCallbacks => Volatile.Read(ref this.fullCollectionCallbacks);

    /// <summary>The millisecond clock idle times are measured with; a test hook, set before the pool is used.</summary>
    internal Func<long> Clock
    {
        get => this.clock;
        set => this.clock = value;
    }

    /// <inheritdoc/>
    public override IMemoryOwner<T> Allocate<T>(int length, AllocationOptions options = AllocationOptions.Clean)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        bool clean = (options & AllocationOptions.Clean) != 0;
        long bytes = (long)length * Unsafe.SizeOf<T>();
        if (bytes < this.minimumPooledBufferBytes
            || bytes > this.maximumPooledBufferBytes
            || !SizeClasses.TryGet(length, out int sizeClass, out int classLength)
            || (long)classLength * Unsafe.SizeOf<T>() > this.maximumRetainedBytes)
        {
            // Too small to be worth pooling, too large to keep, or never retainable: an exact array the GC owns.
            return new ArrayMemoryOwner<T>(this.NewArray<T>(length, clean), length);
        }

        long now = this.clock();
        Bucket<T> bucket = this.GetPool<T>().GetBucket(sizeClass, classLength, now);
        T[]? array = bucket.TryPop(now);
        if (array is null)
        {
            array = this.NewArray<T>(classLength, clean);
            if (!clean)
            {
                // An uninitialized array may hold whatever the managed heap last kept there. The caller overwrites
                // the part it asked for; the slack past it is cleared so that nothing foreign is reachable through
                // the array at all, even by code that looks past the memory it was given.
                array.AsSpan(length).Clear();
            }
        }
        else
        {
            Interlocked.Add(ref this.retainedBytes, -bucket.Bytes);

            // A reused array still holds the pixels of the image that used it last. Clean memory is cleared in
            // full; otherwise the caller overwrites its part and only the slack is cleared, for the reason above.
            (clean ? array.AsSpan() : array.AsSpan(length)).Clear();
        }

        return new PooledOwner<T>(this, bucket, array, length);
    }

    /// <summary>Drops every retained buffer. Buffers in use go back into the (now empty) pool when released.</summary>
    public override void ReleaseRetainedResources() => this.Trim(TrimLevel.All, this.clock());

    /// <summary>
    /// Maps the machine's memory load to a trim level: past 70% of the garbage collector's high-load threshold the
    /// machine counts as short of memory. Even then the working set is kept: a buffer that is rented again on the next
    /// request would only be allocated anew, so dropping it frees nothing for long and costs a large-object-heap
    /// allocation. When an allocation actually fails, the pool drops everything (see <see cref="NewArray{T}"/>).
    /// </summary>
    internal static TrimLevel ClassifyMemoryLoad(long memoryLoadBytes, long highMemoryLoadThresholdBytes)
        => highMemoryLoadThresholdBytes > 0 && memoryLoadBytes >= highMemoryLoadThresholdBytes / 10 * 7
            ? TrimLevel.Pressure
            : TrimLevel.Surplus;

    /// <summary>Releases retained buffers according to <paramref name="level"/> and returns how many bytes were released.</summary>
    internal long Trim(TrimLevel level, long nowMilliseconds)
    {
        long released = 0;
        foreach (ElementPool pool in this.pools.Values)
        {
            released += pool.Trim(level, nowMilliseconds);
        }

        Interlocked.Add(ref this.retainedBytes, -released);
        return released;
    }

    /// <summary>Takes a buffer back from an owner; called at most once per rented array.</summary>
    private void Return<T>(Bucket<T> bucket, T[] array)
    {
        // Reserve room under the cap first, so concurrent returns can never push the pool past it.
        long bytes = bucket.Bytes;
        while (true)
        {
            long current = Interlocked.Read(ref this.retainedBytes);
            if (bytes > this.maximumRetainedBytes - current)
            {
                return; // The pool is full: the garbage collector reclaims the array.
            }

            if (Interlocked.CompareExchange(ref this.retainedBytes, current + bytes, current) == current)
            {
                break;
            }
        }

        bucket.Push(array, this.clock());
    }

    /// <summary>Allocates a new array; when the heap is exhausted, drops every retained buffer and tries once more.</summary>
    private T[] NewArray<T>(int length, bool clean)
    {
        try
        {
            return clean ? new T[length] : GC.AllocateUninitializedArray<T>(length);
        }
        catch (OutOfMemoryException) when (this.RetainedBytes > 0)
        {
            // The retained buffers may be exactly the memory the process is short of.
            this.ReleaseRetainedResources();
            return clean ? new T[length] : GC.AllocateUninitializedArray<T>(length);
        }
    }

    private ElementPool<T> GetPool<T>()
        => (ElementPool<T>)this.pools.GetOrAdd(typeof(T), static _ => new ElementPool<T>());

    private bool OnFullCollection()
    {
        Interlocked.Increment(ref this.fullCollectionCallbacks);

        // The first callbacks may come from generation 0 or 1 collections, before the callback object has been
        // promoted; only a completed full collection is a reason to trim.
        int generation = GC.CollectionCount(2);
        if (generation != this.lastTrimmedGeneration)
        {
            this.lastTrimmedGeneration = generation;
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            this.Trim(ClassifyMemoryLoad(info.MemoryLoadBytes, info.HighMemoryLoadThresholdBytes), this.clock());
        }

        return true;
    }

    /// <summary>
    /// The pool's size classes: four per power of two, at 1, 1.25, 1.5 and 1.75 times it. Class lengths are
    /// counted in elements, so each element type has its own set of buckets.
    /// </summary>
    internal static class SizeClasses
    {
        /// <summary>Lengths 5 to 2^31 - 1 map to class indices 0 to 115.</summary>
        public const int Count = 116;

        /// <summary>Finds the smallest class that holds <paramref name="length"/> elements.</summary>
        /// <returns><see langword="false"/> for lengths too short to pool or classes beyond the largest array.</returns>
        public static bool TryGet(int length, out int index, out int classLength)
        {
            index = 0;
            classLength = 0;
            if (length < 5)
            {
                return false;
            }

            // 2^e <= length - 1 < 2^(e+1), and the four classes above 2^e are 2^e + k * 2^(e-2) for k = 1..4.
            int e = BitOperations.Log2((uint)(length - 1));
            int k = ((length - 1 - (1 << e)) >> (e - 2)) + 1;
            long size = (1L << e) + ((long)k << (e - 2));
            if (size > Array.MaxLength)
            {
                return false;
            }

            index = ((e - 2) * 4) + (k - 1);
            classLength = (int)size;
            return true;
        }
    }

    /// <summary>The buckets of one element type.</summary>
    private abstract class ElementPool
    {
        public abstract long Trim(TrimLevel level, long nowMilliseconds);
    }

    private sealed class ElementPool<T> : ElementPool
    {
        private readonly Bucket<T>?[] buckets = new Bucket<T>?[SizeClasses.Count];

        public Bucket<T> GetBucket(int sizeClass, int classLength, long nowMilliseconds)
        {
            Bucket<T>? bucket = Volatile.Read(ref this.buckets[sizeClass]);
            if (bucket is not null)
            {
                return bucket;
            }

            var created = new Bucket<T>(classLength, nowMilliseconds);
            return Interlocked.CompareExchange(ref this.buckets[sizeClass], created, null) ?? created;
        }

        public override long Trim(TrimLevel level, long nowMilliseconds)
        {
            long released = 0;
            foreach (Bucket<T>? bucket in this.buckets)
            {
                if (bucket is not null)
                {
                    released += bucket.Trim(level, nowMilliseconds);
                }
            }

            return released;
        }
    }

    /// <summary>The retained arrays of one element type and size class; every array in it has exactly <see cref="Length"/> elements.</summary>
    private sealed class Bucket<T>
    {
        private readonly Stack<T[]> arrays = new();
        private long lastUsed;

        /// <summary>
        /// The fewest arrays the bucket held at any point since <see cref="periodStart"/>: that many were never rented
        /// in the meantime, so once the period is long enough they are surplus to the working set.
        /// </summary>
        private int lowWater;

        /// <summary>When the current observation period began: the bucket's creation or its last surplus trim.</summary>
        private long periodStart;

        public Bucket(int length, long nowMilliseconds)
        {
            this.lastUsed = nowMilliseconds;
            this.periodStart = nowMilliseconds;
            this.Length = length;
            this.Bytes = (long)length * Unsafe.SizeOf<T>();
        }

        public int Length { get; }

        public long Bytes { get; }

        public T[]? TryPop(long nowMilliseconds)
        {
            lock (this.arrays)
            {
                this.lastUsed = nowMilliseconds;
                T[]? array = this.arrays.Count > 0 ? this.arrays.Pop() : null;
                this.lowWater = Math.Min(this.lowWater, this.arrays.Count);
                return array;
            }
        }

        public void Push(T[] array, long nowMilliseconds)
        {
            lock (this.arrays)
            {
                this.lastUsed = nowMilliseconds;
                this.arrays.Push(array);
            }
        }

        public long Trim(TrimLevel level, long nowMilliseconds)
        {
            lock (this.arrays)
            {
                int count = this.arrays.Count;
                long limit = level == TrimLevel.Pressure ? PressureIdleTrimMilliseconds : IdleTrimMilliseconds;
                int drop;
                if (level == TrimLevel.All || nowMilliseconds - this.lastUsed >= limit)
                {
                    drop = count;
                }
                else if (nowMilliseconds - this.periodStart >= limit)
                {
                    drop = Math.Min(this.lowWater, count);
                }
                else
                {
                    // Too short a period to tell surplus from working set: collections can come in quick succession,
                    // and a buffer not rented in the last few milliseconds may well be rented in the next.
                    return 0;
                }

                for (int i = 0; i < drop; i++)
                {
                    this.arrays.Pop();
                }

                // A new period starts: whatever is left counts as surplus unless it is rented before the next trim.
                this.lowWater = this.arrays.Count;
                this.periodStart = nowMilliseconds;
                return drop * this.Bytes;
            }
        }
    }

    /// <summary>Owns one rented array and gives it back to its bucket exactly once.</summary>
    private sealed class PooledOwner<T> : IMemoryOwner<T>
    {
        private readonly PooledMemoryAllocator allocator;
        private readonly Bucket<T> bucket;
        private readonly int length;
        private T[]? array;

        public PooledOwner(PooledMemoryAllocator allocator, Bucket<T> bucket, T[] array, int length)
        {
            this.allocator = allocator;
            this.bucket = bucket;
            this.array = array;
            this.length = length;
        }

        public Memory<T> Memory
        {
            get
            {
                T[] current = Volatile.Read(ref this.array) ?? throw new ObjectDisposedException(nameof(PooledMemoryAllocator));
                return current.AsMemory(0, this.length);
            }
        }

        public void Dispose()
        {
            // The exchange is what makes a second Dispose - from any thread - a no-op instead of a second return,
            // which would hand the same array to two images.
            T[]? returned = Interlocked.Exchange(ref this.array, null);
            if (returned is not null)
            {
                this.allocator.Return(this.bucket, returned);
            }
        }
    }
}

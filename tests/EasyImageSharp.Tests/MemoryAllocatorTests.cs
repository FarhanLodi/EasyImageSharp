using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyImageSharp.Formats;
using EasyImageSharp.Memory;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Pixel memory allocation: the pooled default, the plain GC and native allocators, and the ownership rules that keep
/// a pooled buffer from ever being shared by two live images. Every test uses its own allocator through a
/// request-local configuration, so nothing here depends on, or disturbs, the process-wide pool other tests use.
/// </summary>
public class MemoryAllocatorTests
{
    // ----- Defaults and configuration -----

    [Fact]
    public void Defaults_ArePooledAndShared()
    {
        Assert.IsType<PooledMemoryAllocator>(MemoryAllocator.Shared);
        Assert.Same(MemoryAllocator.Shared, new Configuration().MemoryAllocator);
        Assert.Throws<ArgumentNullException>(() => new Configuration { MemoryAllocator = null! });
    }

    [Fact]
    public void RequestLocalAllocator_ServesDecodeCloneAndProcessing_AndLeavesTheDefaultAlone()
    {
        var counting = new CountingAllocator(new GCMemoryAllocator());
        var options = new ImageExecutionOptions { Configuration = new Configuration { MemoryAllocator = counting } };
        byte[] png = EncodePng(Pattern(40, 30, seed: 1));

        using Image<Rgba32> decoded = Image.Load<Rgba32>(png, new DecoderOptions { ExecutionOptions = options });
        int afterDecode = counting.Allocations;
        Assert.True(afterDecode >= 1, "The decoder did not allocate its frame from the request-local allocator.");

        using Image<Rgba32> resized = decoded.Clone(options, ctx => ctx.Resize(20, 15));
        Assert.True(counting.Allocations > afterDecode);

        // Outside the request scope the process default serves the allocation.
        int before = counting.Allocations;
        using (Image<Rgba32> unrelated = decoded.Clone(ctx => ctx.Resize(10, 10)))
        {
            Assert.Equal(before, counting.Allocations);
        }

        Assert.Same(MemoryAllocator.Shared, Configuration.Default.MemoryAllocator);
    }

    [Fact]
    public void EveryOwnerIsDisposedExactlyOnce_AcrossDecodeProcessCloneAndDispose()
    {
        var counting = new CountingAllocator(new PooledMemoryAllocator(TestPoolOptions()));
        var options = new ImageExecutionOptions { Configuration = new Configuration { MemoryAllocator = counting } };
        byte[] png = EncodePng(Pattern(64, 48, seed: 2));

        using (Use(counting))
        {
            using Image<Rgba32> source = Image.Load<Rgba32>(png);
            source.Mutate(ctx => ctx.Resize(80, 60).Grayscale().Crop(new Rectangle(4, 4, 60, 40)));
            using Image<Rgba32> clone = source.Clone(ctx => ctx.Rotate(RotateMode.Rotate90));
            using Image<Rgba32> copy = source.Clone();
            using Image<Rgba32> unchanged = source.Clone(_ => { });
            using Image<L8> gray = source.CloneAs<L8>();
            source.Frames.AddFrame(copy.Frames.RootFrame);
            source.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(50, 50), Mode = ResizeMode.Pad }));
        }

        Assert.Equal(0, counting.DoubleDisposals);
        Assert.Equal(counting.Allocations, counting.Disposals);
    }

    [Fact]
    public void PixelBudget_ChargesTheRequestedBytes_NotThePoolSizeClass()
    {
        foreach (MemoryAllocator allocator in new MemoryAllocator[] { NewPool(), new GCMemoryAllocator(), new UnmanagedMemoryAllocator() })
        {
            using OperationExecution scope = Use(allocator);
            using (var image = new Image<Rgba32>(101, 37))
            {
                Assert.Equal(101L * 37 * 4, scope.AllocatedPixelBytes);
            }

            using (var image = new Image<Rgba32>(101, 37))
            using (Image<Rgba32> clone = image.Clone())
            {
                Assert.Equal(3 * 101L * 37 * 4, scope.AllocatedPixelBytes);
            }
        }
    }

    [Fact]
    public void PixelBudget_StillRejectsBeforeAllocating()
    {
        var counting = new CountingAllocator(NewPool());
        var options = new ImageExecutionOptions
        {
            Configuration = new Configuration { MemoryAllocator = counting },
            MaxPixelBufferBytes = 1000,
        };
        using var image = new Image<Rgba32>(16, 16);
        Assert.Throws<ImageSizeLimitExceededException>(() => image.Clone(options, ctx => ctx.Resize(64, 64)));
        Assert.Equal(0, counting.Allocations);
    }

    // ----- Reuse -----

    [Fact]
    public void DisposedImageBuffer_IsReusedByTheNextImageOfTheSameSizeClass()
    {
        PooledMemoryAllocator pool = NewPool();
        using OperationExecution scope = Use(pool);

        var first = new Image<Rgba32>(120, 90);
        Rgba32[] firstArray = BackingArray(first);
        Assert.Equal(0, pool.RetainedBytes);
        first.Dispose();
        Assert.True(pool.RetainedBytes >= 120 * 90 * 4);

        // 119x91 lands in the same size class as 120x90.
        using var second = new Image<Rgba32>(119, 91);
        Assert.Same(firstArray, BackingArray(second));
        Assert.Equal(0, pool.RetainedBytes);
    }

    [Fact]
    public void ResizeAndOtherBufferSwaps_ReturnTheReplacedBuffer()
    {
        PooledMemoryAllocator pool = NewPool();
        var counting = new CountingAllocator(pool);
        using OperationExecution scope = Use(counting);

        using Image<Rgba32> image = Pattern(200, 160, seed: 3);
        Rgba32[] original = BackingArray(image);
        image.Mutate(ctx => ctx.Resize(100, 80));
        Assert.True(pool.RetainedBytes > 0, "The replaced buffer was not handed back.");

        using (var reuse = new Image<Rgba32>(200, 160))
        {
            Assert.Same(original, BackingArray(reuse));
        }

        // Rotate, flip, crop and pad all swap buffers too; each must hand its predecessor (and any intermediate)
        // back, so that at every step the image's own buffer is the only one outstanding.
        foreach (Action<IImageProcessingContext> operation in new Action<IImageProcessingContext>[]
        {
            ctx => ctx.Rotate(RotateMode.Rotate90),
            ctx => ctx.Flip(FlipMode.Vertical),
            ctx => ctx.Crop(new Rectangle(1, 1, 70, 70)),
            ctx => ctx.Pad(90, 90),
            ctx => ctx.Resize(new ResizeOptions { Size = new Size(60, 40), Mode = ResizeMode.Crop }),
            ctx => ctx.Rotate(17f),
            ctx => ctx.Resize(30, 20).Grayscale().Resize(31, 21),
        })
        {
            Rgba32[] before = BackingArray(image);
            int disposals = counting.Disposals;
            image.Mutate(operation);
            Assert.NotSame(before, BackingArray(image));
            Assert.True(counting.Disposals > disposals, "An operation that swapped the buffer did not hand the old one back.");
            Assert.Equal(1, counting.Allocations - counting.Disposals);
        }

        Assert.Equal(0, counting.DoubleDisposals);
    }

    [Fact]
    public void ResizeWithPadding_HandsTheScaledIntermediateBack()
    {
        PooledMemoryAllocator pool = NewPool();
        using OperationExecution scope = Use(pool);
        using Image<Rgba32> image = Pattern(300, 100, seed: 4);
        pool.ReleaseRetainedResources();

        // Pad scales to 150x50 and then places that on a 150x150 canvas: both the source buffer and the 150x50
        // intermediate go back to the pool.
        image.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(150, 150), Mode = ResizeMode.Pad }));
        long expected = ClassBytes<Rgba32>(300 * 100) + ClassBytes<Rgba32>(150 * 50);
        Assert.Equal(expected, pool.RetainedBytes);
    }

    // ----- Zeroing: no image may ever see another image's pixels -----

    [Fact]
    public void ReusedBuffers_AreZeroed_ForNewImagesFramesAndPaddedCanvases()
    {
        PooledMemoryAllocator pool = NewPool();
        using OperationExecution scope = Use(pool);

        Image<Rgba32> secret = Filled(64, 64, new Rgba32(0xDE, 0xAD, 0xBE, 0xEF));
        Rgba32[] secretArray = BackingArray(secret);
        secret.Dispose();

        using var fresh = new Image<Rgba32>(64, 64);
        Assert.Same(secretArray, BackingArray(fresh));
        Assert.True(AllZero(fresh), "A new image exposed a disposed image's pixels.");

        // The same holds for a frame created through the frame collection.
        Image<Rgba32> secret2 = Filled(48, 48, new Rgba32(1, 2, 3, 4));
        secret2.Dispose();
        ImageFrame<Rgba32> created = fresh.Frames.CreateFrame(48, 48);
        Assert.True(created.PixelSpan.IndexOfAnyExcept(default(Rgba32)) < 0);
    }

    [Fact]
    public void SlackPastTheRequestedLength_NeverHoldsForeignPixels()
    {
        // 70x70 = 4900 and 65x65 = 4225 share the 5120-element size class.
        PooledMemoryAllocator pool = NewPool();
        using OperationExecution scope = Use(pool);
        Image<Rgba32> secret = Filled(70, 70, new Rgba32(9, 9, 9, 9));
        Rgba32[] array = BackingArray(secret);
        Assert.Equal(5120, array.Length);
        secret.Dispose();

        // Uninitialized rental: the caller overwrites its 4225 pixels, and the slack is cleared by the pool.
        ImageFrame<Rgba32> frame = FrameFactory.CreateUninitialized<Rgba32>(65, 65);
        Assert.True(frame.TryGetBackingArray(out Rgba32[]? backing));
        Assert.Same(array, backing);
        Assert.True(backing.AsSpan(65 * 65).IndexOfAnyExcept(default(Rgba32)) < 0, "Slack past the frame kept another image's pixels.");
        frame.ReleaseBuffer();

        // Clean rental: the whole array, slack included, is zero.
        Image<Rgba32> secret2 = Filled(70, 70, new Rgba32(7, 7, 7, 7));
        secret2.Dispose();
        using var clean = new Image<Rgba32>(65, 65);
        Assert.Same(array, BackingArray(clean));
        Assert.True(array.AsSpan().IndexOfAnyExcept(default(Rgba32)) < 0);
    }

    [Fact]
    public void FullyWrittenDestinations_NeverShowStalePixels()
    {
        // Every operation allocating without clearing must overwrite the whole frame. Poison the pool with a colour
        // that no operation below can produce, then check none of it survives.
        var poison = new Rgba32(0x13, 0x37, 0x42, 0x01);
        PooledMemoryAllocator pool = NewPool();
        using OperationExecution scope = Use(pool);
        Image<Rgba32> source = Pattern(96, 64, seed: 5, opaque: true);
        foreach (Action<IImageProcessingContext> operation in new Action<IImageProcessingContext>[]
        {
            ctx => ctx.Resize(57, 41),
            ctx => ctx.Resize(57, 41, KnownResamplers.NearestNeighbor),
            ctx => ctx.Rotate(RotateMode.Rotate90),
            ctx => ctx.Rotate(RotateMode.Rotate180),
            ctx => ctx.Rotate(RotateMode.Rotate270),
            ctx => ctx.Rotate(33f),
            ctx => ctx.Flip(FlipMode.Horizontal),
            ctx => ctx.Crop(new Rectangle(5, 6, 50, 40)),
            ctx => ctx.Pad(120, 90, Color.White),
            ctx => ctx.Resize(new ResizeOptions { Size = new Size(70, 70), Mode = ResizeMode.Crop }),
            ctx => ctx.Resize(new ResizeOptions { Size = new Size(70, 70), Mode = ResizeMode.BoxPad, PadColor = Color.White }),
        })
        {
            Poison(pool, poison);
            using Image<Rgba32> result = source.Clone(operation);
            Assert.True(result.Frames.RootFrame.PixelSpan.IndexOf(poison) < 0, "A destination kept a pixel of an earlier image.");
        }

        foreach (ImageFormat format in new[] { ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Bmp, ImageFormat.Tiff, ImageFormat.Webp, ImageFormat.Qoi, ImageFormat.Tga })
        {
            byte[] encoded;
            using (var stream = new MemoryStream())
            {
                source.Save(stream, format);
                encoded = stream.ToArray();
            }

            Poison(pool, poison);
            using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
            Assert.True(decoded.Frames.RootFrame.PixelSpan.IndexOf(poison) < 0, $"The {format.Name} decoder left a pixel of an earlier image.");
        }

        Poison(pool, poison);
        using (Image<Rgba32> copy = source.Clone())
        {
            Assert.True(copy.Frames.RootFrame.PixelSpan.IndexOf(poison) < 0);
        }

        Poison(pool, poison);
        using (Image<Rgba32> background = new(80, 80, new Rgba32(1, 1, 1)))
        {
            Assert.True(background.Frames.RootFrame.PixelSpan.IndexOf(poison) < 0);
        }

        source.Dispose();
    }

    // ----- Ownership: exactly-once return, never while shared -----

    [Fact]
    public void DoubleDispose_ReturnsTheBufferOnce()
    {
        PooledMemoryAllocator pool = NewPool();
        using OperationExecution scope = Use(pool);
        var image = new Image<Rgba32>(100, 100);
        image.Dispose();
        long once = pool.RetainedBytes;
        image.Dispose();
        Assert.Equal(once, pool.RetainedBytes);

        using var a = new Image<Rgba32>(100, 100);
        using var b = new Image<Rgba32>(100, 100);
        Assert.NotSame(BackingArray(a), BackingArray(b));
    }

    [Fact]
    public void ConcurrentDispose_ReturnsTheBufferOnce()
    {
        PooledMemoryAllocator pool = NewPool();
        for (int round = 0; round < 50; round++)
        {
            Image<Rgba32> image;
            using (Use(pool))
            {
                image = new Image<Rgba32>(64, 64);
            }

            pool.ReleaseRetainedResources();
            Parallel.For(0, 4, _ => image.Dispose());
            Assert.Equal(ClassBytes<Rgba32>(64 * 64), pool.RetainedBytes);
        }
    }

    [Fact]
    public void PoolOwner_DoubleDispose_IsANoOp()
    {
        PooledMemoryAllocator pool = NewPool();
        IMemoryOwner<int> owner = pool.Allocate<int>(10_000);
        owner.Dispose();
        owner.Dispose();
        Assert.Equal(ClassBytes<int>(10_000), pool.RetainedBytes);
        Assert.Throws<ObjectDisposedException>(() => owner.Memory);

        using IMemoryOwner<int> first = pool.Allocate<int>(10_000);
        using IMemoryOwner<int> second = pool.Allocate<int>(10_000);
        Assert.False(first.Memory.Span.Overlaps(second.Memory.Span));
    }

    [Fact]
    public void CopyOnWriteClone_KeepsTheSharedBufferUntilBothImagesLetGo()
    {
        PooledMemoryAllocator pool = NewPool();
        using OperationExecution scope = Use(pool);
        Image<Rgba32> source = Pattern(90, 70, seed: 6);
        Rgba32[] expected = Pixels(source);

        // The internal first step of Clone(operation): the clone points at the source's buffer.
        Image<Rgba32> shallow = FrameFactory.ShallowClone(source);
        Assert.Same(BackingArray(source), BackingArray(shallow));

        source.Dispose();
        Assert.Equal(0, pool.RetainedBytes);
        using (Image<Rgba32> scavenger = Filled(90, 70, new Rgba32(255, 0, 0)))
        {
            Assert.NotSame(BackingArray(scavenger), BackingArray(shallow));
            Assert.Equal(expected, Pixels(shallow));
        }

        long beforeLastRelease = pool.RetainedBytes;
        shallow.Dispose();
        Assert.Equal(beforeLastRelease + ClassBytes<Rgba32>(90 * 70), pool.RetainedBytes);
    }

    [Theory]
    [InlineData("resize")]
    [InlineData("same-size resize")]
    [InlineData("grayscale")]
    [InlineData("rotate")]
    [InlineData("nothing")]
    [InlineData("throws")]
    public void CloneWithOperation_UnderPooling_NeverCorruptsEitherImage(string name)
    {
        Action<IImageProcessingContext> operation = name switch
        {
            "resize" => ctx => ctx.Resize(45, 35),
            "same-size resize" => ctx => ctx.Resize(90, 70),
            "grayscale" => ctx => ctx.Grayscale(),
            "rotate" => ctx => ctx.Rotate(RotateMode.Rotate180),
            "nothing" => _ => { },
            _ => _ => throw new InvalidOperationException("boom"),
        };

        // Reference result with plain GC memory.
        Rgba32[] sourcePixels;
        Rgba32[]? clonePixels = null;
        using (Use(new GCMemoryAllocator()))
        using (Image<Rgba32> reference = Pattern(90, 70, seed: 7))
        {
            sourcePixels = Pixels(reference);
            if (name != "throws")
            {
                using Image<Rgba32> referenceClone = reference.Clone(operation);
                clonePixels = Pixels(referenceClone);
            }
        }

        foreach (bool disposeSourceFirst in new[] { true, false })
        {
            PooledMemoryAllocator pool = NewPool();
            using OperationExecution scope = Use(pool);
            Image<Rgba32> source = Pattern(90, 70, seed: 7);
            if (name == "throws")
            {
                Assert.Throws<InvalidOperationException>(() => source.Clone(operation));

                // The failed clone let go of the buffer it shared, and the source still owns it.
                Churn(pool, 90, 70);
                Assert.Equal(sourcePixels, Pixels(source));
                source.Dispose();
                continue;
            }

            Image<Rgba32> clone = source.Clone(operation);
            Image<Rgba32> first = disposeSourceFirst ? source : clone;
            Image<Rgba32> survivor = disposeSourceFirst ? clone : source;
            Rgba32[] survivorPixels = disposeSourceFirst ? clonePixels! : sourcePixels;

            first.Dispose();
            Churn(pool, 90, 70);
            Churn(pool, 45, 35);
            Assert.Equal(survivorPixels, Pixels(survivor));
            survivor.Dispose();
        }
    }

    [Fact]
    public void CloneWithOperation_NeverAliasesTheSource_UnderPooling()
    {
        using OperationExecution scope = Use(NewPool());
        foreach (Action<IImageProcessingContext> operation in new Action<IImageProcessingContext>[]
        {
            ctx => ctx.Resize(50, 40),
            ctx => ctx.Resize(90, 70),
            ctx => ctx.Grayscale(),
            _ => { },
        })
        {
            using Image<Rgba32> source = Pattern(90, 70, seed: 8);
            using Image<Rgba32> clone = source.Clone(operation);
            Assert.False(source.Frames.RootFrame.PixelSpan.Overlaps(clone.Frames.RootFrame.PixelSpan));
        }
    }

    [Fact]
    public void WrappedMemory_IsNeverHandedToTheAllocator()
    {
        PooledMemoryAllocator pool = NewPool();
        using OperationExecution scope = Use(pool);
        var callerBuffer = new Rgba32[64 * 64];
        callerBuffer.AsSpan().Fill(new Rgba32(5, 6, 7, 8));
        using (Image<Rgba32> wrapped = Image<Rgba32>.WrapMemory(callerBuffer.AsMemory(), 64, 64))
        {
            wrapped.Mutate(ctx => ctx.Grayscale());
        }

        Assert.Equal(0, pool.RetainedBytes);

        // A size change moves the wrapped image onto a pooled buffer, which goes back; the caller's never does.
        Image<Rgba32> resized = Image<Rgba32>.WrapMemory(callerBuffer.AsMemory(), 64, 64);
        resized.Mutate(ctx => ctx.Resize(32, 32));
        resized.Dispose();
        Assert.Equal(ClassBytes<Rgba32>(32 * 32), pool.RetainedBytes);
        using var next = new Image<Rgba32>(64, 64);
        Assert.NotSame(callerBuffer, BackingArray(next));
        Assert.Equal(ClassBytes<Rgba32>(32 * 32), pool.RetainedBytes);
        Assert.True(callerBuffer.AsSpan().IndexOfAnyExcept(callerBuffer[0]) < 0, "The caller's buffer was written after the image left it.");
    }

    [Fact]
    public void AdoptedArrays_StayWithTheGarbageCollector()
    {
        PooledMemoryAllocator pool = NewPool();
        using OperationExecution scope = Use(pool);
        var array = new Rgba32[50 * 50];
        var image = new Image<Rgba32>(new ImageFrame<Rgba32>(50, 50, array));
        image.Dispose();
        Assert.Equal(0, pool.RetainedBytes);
    }

    [Fact]
    public void PixelArray_OfAPooledFrame_IsACopyOrAnAbandonedBuffer()
    {
        PooledMemoryAllocator pool = NewPool();
        using OperationExecution scope = Use(pool);

        // 90x70 = 6300 pixels in the 7168-element class: the frame's array is longer than the frame, so a copy.
        Image<Rgba32> rounded = Pattern(90, 70, seed: 9);
        Rgba32[] copy = rounded.Frames.RootFrame.PixelArray;
        Assert.Equal(90 * 70, copy.Length);
        Assert.NotSame(BackingArray(rounded), copy);
        Assert.Equal(Pixels(rounded), copy);
        rounded.Dispose();
        Assert.Equal(ClassBytes<Rgba32>(90 * 70), pool.RetainedBytes);

        // 64x80 = 5120 pixels is exactly a size class: the array itself escapes and is never pooled again.
        pool.ReleaseRetainedResources();
        Image<Rgba32> exact = Pattern(64, 80, seed: 10);
        Rgba32[] escaped = exact.Frames.RootFrame.PixelArray;
        Assert.Same(BackingArray(exact), escaped);
        exact.Dispose();
        Assert.Equal(0, pool.RetainedBytes);
        using var next = new Image<Rgba32>(64, 80);
        Assert.NotSame(escaped, BackingArray(next));
    }

    [Fact]
    public void TakeBuffer_MovesOwnershipAndConsumesTheSource()
    {
        PooledMemoryAllocator pool = NewPool();
        using OperationExecution scope = Use(pool);
        using Image<Rgba32> target = Pattern(80, 60, seed: 11);
        ImageFrame<Rgba32> built = FrameFactory.CreateUninitialized<Rgba32>(40, 30);
        built.PixelSpan.Fill(new Rgba32(1, 2, 3, 4));
        Rgba32[] builtArray = built.TryGetBackingArray(out Rgba32[]? a) ? a : throw new InvalidOperationException();

        target.Frames.RootFrame.TakeBuffer(built);
        Assert.Equal(new Size(40, 30), target.Size);
        Assert.Same(builtArray, BackingArray(target));
        Assert.Throws<ObjectDisposedException>(() => built.PixelSpan.Length);
        Assert.Equal(ClassBytes<Rgba32>(80 * 60), pool.RetainedBytes);

        // Releasing the consumed frame again must not return the moved buffer.
        built.ReleaseBuffer();
        Assert.Equal(ClassBytes<Rgba32>(80 * 60), pool.RetainedBytes);

        // Taking a frame's own buffer is a no-op.
        target.Frames.RootFrame.TakeBuffer(target.Frames.RootFrame);
        Assert.Equal(new Rgba32(1, 2, 3, 4), target[39, 29]);
    }

    [Fact]
    public void RemovedFrame_StaysUsable_AndItsBufferIsNotPooled()
    {
        PooledMemoryAllocator pool = NewPool();
        using OperationExecution scope = Use(pool);
        using Image<Rgba32> image = Pattern(40, 40, seed: 12);
        ImageFrame<Rgba32> second = image.Frames.CreateFrame(40, 40);
        second.PixelSpan.Fill(new Rgba32(3, 3, 3, 3));
        image.Frames.RemoveFrame(1);
        Assert.Equal(0, pool.RetainedBytes);
        Churn(pool, 40, 40);
        Assert.Equal(new Rgba32(3, 3, 3, 3), second[39, 39]);
    }

    [Fact]
    public void ExportedFrame_IsOwnedByTheNewImage()
    {
        PooledMemoryAllocator pool = NewPool();
        using OperationExecution scope = Use(pool);
        using Image<Rgba32> image = Pattern(40, 40, seed: 13);
        image.Frames.CreateFrame(40, 40).PixelSpan.Fill(new Rgba32(4, 4, 4, 4));
        Image<Rgba32> exported = image.Frames.ExportFrame(1);
        Churn(pool, 40, 40);
        Assert.Equal(new Rgba32(4, 4, 4, 4), exported[0, 0]);
        long retained = pool.RetainedBytes;
        exported.Dispose();
        Assert.Equal(retained + ClassBytes<Rgba32>(40 * 40), pool.RetainedBytes);
    }

    // ----- Stress -----

    [Fact]
    public async Task ConcurrentDecodeResizeCloneDispose_NeverMixesImages()
    {
        // A small cap forces constant churn, so buffers move between threads and images all the time. Any
        // ownership slip - a double return, a return while a clone still reads - shows up as a wrong pixel.
        var pool = new PooledMemoryAllocator(new PooledMemoryAllocatorOptions
        {
            MinimumPooledBufferBytes = 0,
            MaximumRetainedBytes = 2 << 20,
            TrimOnMemoryPressure = false,
        });
        var options = new ImageExecutionOptions { Configuration = new Configuration { MemoryAllocator = pool, MaxDegreeOfParallelism = 1 } };

        const int Inputs = 6;
        var encoded = new byte[Inputs][];
        var expectedResized = new Rgba32[Inputs][];
        var expectedRotated = new Rgba32[Inputs][];
        using (Use(new GCMemoryAllocator()))
        {
            for (int i = 0; i < Inputs; i++)
            {
                using Image<Rgba32> image = Pattern(60 + (i * 13), 40 + (i * 7), seed: 100 + i);
                encoded[i] = EncodePng(image);
                using Image<Rgba32> resized = image.Clone(ctx => ctx.Resize(37 + i, 29 + i));
                expectedResized[i] = Pixels(resized);
                using Image<Rgba32> rotated = image.Clone(ctx => ctx.Rotate(RotateMode.Rotate90).Grayscale());
                expectedRotated[i] = Pixels(rotated);
            }
        }

        int failures = 0;
        await Task.WhenAll(Enumerable.Range(0, Math.Max(4, Environment.ProcessorCount)).Select(worker => Task.Run(() =>
        {
            var random = new Random(worker);
            for (int iteration = 0; iteration < 60; iteration++)
            {
                int i = random.Next(Inputs);
                using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded[i], new DecoderOptions { ExecutionOptions = options });
                using Image<Rgba32> rotated = decoded.Clone(options, ctx => ctx.Rotate(RotateMode.Rotate90).Grayscale());
                decoded.Mutate(options, ctx => ctx.Resize(37 + i, 29 + i));
                if (!Pixels(decoded).AsSpan().SequenceEqual(expectedResized[i])
                    || !Pixels(rotated).AsSpan().SequenceEqual(expectedRotated[i]))
                {
                    Interlocked.Increment(ref failures);
                }
            }
        })));

        Assert.Equal(0, failures);
        Assert.True(pool.RetainedBytes <= 2 << 20);
    }

    // ----- Unmanaged allocator -----

    [Fact]
    public void UnmanagedAllocator_ServesAlignedNativeFrames_ThroughTheWholePipeline()
    {
        var native = new UnmanagedMemoryAllocator(256);
        Assert.Equal(256, native.Alignment);
        Rgba32[] expected;
        using (Use(new GCMemoryAllocator()))
        using (Image<Rgba32> reference = Pattern(75, 51, seed: 14))
        using (Image<Rgba32> resized = reference.Clone(ctx => ctx.Resize(40, 30).Rotate(RotateMode.Rotate90)))
        {
            expected = Pixels(resized);
        }

        using OperationExecution scope = Use(native);
        using Image<Rgba32> image = Pattern(75, 51, seed: 14);
        Assert.True(image.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> memory));
        Assert.False(MemoryMarshal.TryGetArray<Rgba32>(memory, out _));
        Assert.Throws<NotSupportedException>(() => memory.Pin());
        nint address = Unsafe.ByteOffset(ref Unsafe.NullRef<Rgba32>(), ref MemoryMarshal.GetReference(memory.Span));
        Assert.Equal(0L, (long)address % 256);

        using Image<Rgba32> clone = image.Clone(ctx => ctx.Resize(40, 30).Rotate(RotateMode.Rotate90));
        Assert.Equal(expected, Pixels(clone));
        using Image<L8> gray = image.CloneAs<L8>();
        byte[] png = EncodePng(clone);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(png);
        Assert.Equal(expected, Pixels(decoded));
        image.Mutate(ctx => ctx.Resize(20, 20));
        Assert.Equal(new Size(20, 20), image.Size);
    }

    [Fact]
    public void UnmanagedAllocator_CleansAndFreesBuffers()
    {
        var native = new UnmanagedMemoryAllocator();
        Assert.Equal(64, native.Alignment);
        IMemoryOwner<long> owner = native.Allocate<long>(4096);
        Assert.True(owner.Memory.Span.IndexOfAnyExcept(0L) < 0);
        owner.Memory.Span.Fill(-1);
        owner.Dispose();
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owner.Memory.Span.Length);
        using IMemoryOwner<byte> empty = native.Allocate<byte>(0);
        Assert.Equal(0, empty.Memory.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnmanagedMemoryAllocator(48));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnmanagedMemoryAllocator(8192));
        Assert.Throws<ArgumentOutOfRangeException>(() => native.Allocate<byte>(-1));
    }

    // ----- Allocator contracts -----

    [Fact]
    public void GCAllocator_HandsOutExactFreshArrays()
    {
        var gc = new GCMemoryAllocator();
        using IMemoryOwner<int> owner = gc.Allocate<int>(1234);
        Assert.Equal(1234, owner.Memory.Length);
        Assert.True(MemoryMarshal.TryGetArray<int>(owner.Memory, out ArraySegment<int> segment));
        Assert.Equal(1234, segment.Array!.Length);
        Assert.True(owner.Memory.Span.IndexOfAnyExcept(0) < 0);

        using OperationExecution scope = Use(gc);
        using var image = new Image<Rgba32>(30, 30);
        Assert.Equal(900, BackingArray(image).Length);
    }

    [Fact]
    public void AllocatorReturningTooLittle_IsRejectedAndItsOwnerReleased()
    {
        var shortchanging = new ShortAllocator();
        using OperationExecution scope = Use(shortchanging);
        Assert.Throws<InvalidOperationException>(() => new Image<Rgba32>(10, 10));
        Assert.Equal(1, shortchanging.Disposed);
    }

    [Fact]
    public void PooledAllocator_ValidatesOptionsAndBypassesThePoolOutsideItsBounds()
    {
        Assert.Throws<ArgumentNullException>(() => new PooledMemoryAllocator(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PooledMemoryAllocator(new PooledMemoryAllocatorOptions { MaximumRetainedBytes = -1 }));

        var pool = new PooledMemoryAllocator(new PooledMemoryAllocatorOptions
        {
            MinimumPooledBufferBytes = 1024,
            MaximumPooledBufferBytes = 64 * 1024,
            TrimOnMemoryPressure = false,
        });

        // Too small and too large: exact arrays that never come back.
        foreach (int length in new[] { 100, 20_000 })
        {
            IMemoryOwner<int> owner = pool.Allocate<int>(length);
            Assert.True(MemoryMarshal.TryGetArray<int>(owner.Memory, out ArraySegment<int> segment));
            Assert.Equal(length, segment.Array!.Length);
            owner.Dispose();
            Assert.Equal(0, pool.RetainedBytes);
        }

        Assert.Equal(0, pool.Allocate<byte>(0).Memory.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Allocate<byte>(-1));
    }

    [Fact]
    public void PooledAllocator_NeverRetainsMoreThanItsCap()
    {
        var pool = new PooledMemoryAllocator(new PooledMemoryAllocatorOptions
        {
            MinimumPooledBufferBytes = 0,
            MaximumRetainedBytes = 100_000,
            TrimOnMemoryPressure = false,
        });
        IMemoryOwner<byte>[] owners = Enumerable.Range(0, 10).Select(_ => pool.Allocate<byte>(30_000)).ToArray();
        foreach (IMemoryOwner<byte> owner in owners)
        {
            owner.Dispose();
        }

        Assert.Equal(3 * ClassBytes<byte>(30_000), pool.RetainedBytes);

        var none = new PooledMemoryAllocator(new PooledMemoryAllocatorOptions { MaximumRetainedBytes = 0, TrimOnMemoryPressure = false });
        none.Allocate<byte>(1 << 20).Dispose();
        Assert.Equal(0, none.RetainedBytes);
    }

    [Fact]
    public void PooledAllocator_TrimsIdleClassesAndSurplus_ButKeepsTheWorkingSet()
    {
        long now = 0;
        PooledMemoryAllocator pool = NewPool();
        pool.Clock = () => now;
        long byteClass = ClassBytes<byte>(50_000);
        long intClass = ClassBytes<int>(50_000);
        pool.Allocate<byte>(50_000).Dispose();
        pool.Allocate<int>(50_000).Dispose();
        Assert.Equal(byteClass + intClass, pool.RetainedBytes);

        // Half a minute in, nothing has been idle for long enough.
        now = 30_000;
        Assert.Equal(0, Trim(PooledMemoryAllocator.TrimLevel.Surplus));
        RentAndReturn(1);

        // A minute after its last use the int class is emptied; the byte buffer was rented 31 seconds ago and stays.
        now = 61_000;
        Assert.Equal(intClass, Trim(PooledMemoryAllocator.TrimLevel.Surplus));
        Assert.Equal(byteClass, pool.RetainedBytes);
        now = 100_000;
        RentAndReturn(1);
        now = 125_000;
        Assert.Equal(0, Trim(PooledMemoryAllocator.TrimLevel.Surplus));

        // Collections in quick succession must not mistake the working set for surplus.
        now = 126_000;
        RentAndReturn(1);
        now = 127_000;
        Assert.Equal(0, Trim(PooledMemoryAllocator.TrimLevel.Surplus));
        Assert.Equal(0, Trim(PooledMemoryAllocator.TrimLevel.Pressure));
        now = 127_001;
        Assert.Equal(0, Trim(PooledMemoryAllocator.TrimLevel.Pressure));
        Assert.Equal(byteClass, pool.RetainedBytes);

        // Under memory pressure ten idle seconds are enough.
        now = 137_000;
        Assert.Equal(byteClass, Trim(PooledMemoryAllocator.TrimLevel.Pressure));
        Assert.Equal(0, pool.RetainedBytes);

        // A burst of four leaves four buffers behind; a steady load then only ever needs one of them at a time.
        now = 200_000;
        RentAndReturn(4);
        now = 210_000;
        RentAndReturn(1);
        now = 255_000;
        Assert.Equal(0, Trim(PooledMemoryAllocator.TrimLevel.Surplus));
        for (now = 260_000; now <= 320_000; now += 10_000)
        {
            RentAndReturn(1);
        }

        Assert.Equal(3 * byteClass, Trim(PooledMemoryAllocator.TrimLevel.Surplus));
        Assert.Equal(byteClass, pool.RetainedBytes);

        // The buffer that load keeps using survives trim after trim, even under pressure.
        for (int round = 0; round < 6; round++)
        {
            now += 5_000;
            RentAndReturn(1);
            now += 1_000;
            Assert.Equal(0, Trim(PooledMemoryAllocator.TrimLevel.Pressure));
        }

        Assert.Equal(byteClass, pool.RetainedBytes);
        pool.ReleaseRetainedResources();
        Assert.Equal(0, pool.RetainedBytes);

        long Trim(PooledMemoryAllocator.TrimLevel level) => pool.Trim(level, now);

        void RentAndReturn(int concurrent)
        {
            IMemoryOwner<byte>[] owners = Enumerable.Range(0, concurrent).Select(_ => pool.Allocate<byte>(50_000)).ToArray();
            foreach (IMemoryOwner<byte> owner in owners)
            {
                owner.Dispose();
            }
        }
    }

    [Fact]
    public void PooledAllocator_MapsMemoryLoadToTrimLevels()
    {
        Assert.Equal(PooledMemoryAllocator.TrimLevel.Surplus, PooledMemoryAllocator.ClassifyMemoryLoad(10, 0));
        Assert.Equal(PooledMemoryAllocator.TrimLevel.Surplus, PooledMemoryAllocator.ClassifyMemoryLoad(600, 1000));
        Assert.Equal(PooledMemoryAllocator.TrimLevel.Pressure, PooledMemoryAllocator.ClassifyMemoryLoad(700, 1000));
        Assert.Equal(PooledMemoryAllocator.TrimLevel.Pressure, PooledMemoryAllocator.ClassifyMemoryLoad(990, 1000));
    }

    [Fact]
    public void PooledAllocator_HearsAboutFullCollections()
    {
        var pool = new PooledMemoryAllocator(new PooledMemoryAllocatorOptions { MinimumPooledBufferBytes = 0 });
        for (int i = 0; i < 5 && pool.FullCollectionCallbacks == 0; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.True(pool.FullCollectionCallbacks > 0);
        GC.KeepAlive(pool);
    }

    [Fact]
    public void SizeClasses_AreMonotonicAndWithinAQuarterOfTheRequest()
    {
        Assert.False(PooledMemoryAllocator.SizeClasses.TryGet(4, out _, out _));
        int previousIndex = -1;
        int previousLength = 0;
        for (int length = 5; length < 1 << 20; length += 1 + (length / 97))
        {
            Assert.True(PooledMemoryAllocator.SizeClasses.TryGet(length, out int index, out int classLength));
            Assert.InRange(classLength, length, length + (length / 4) + 1);
            Assert.True(index >= previousIndex && classLength >= previousLength);
            Assert.InRange(index, 0, PooledMemoryAllocator.SizeClasses.Count - 1);
            previousIndex = index;
            previousLength = classLength;
        }

        Assert.True(PooledMemoryAllocator.SizeClasses.TryGet(int.MaxValue / 2, out int bigIndex, out _));
        Assert.InRange(bigIndex, 0, PooledMemoryAllocator.SizeClasses.Count - 1);
        Assert.False(PooledMemoryAllocator.SizeClasses.TryGet(int.MaxValue, out _, out _));
    }

    // ----- Helpers -----

    private static PooledMemoryAllocatorOptions TestPoolOptions() => new()
    {
        MinimumPooledBufferBytes = 0,
        MaximumRetainedBytes = 256 << 20,
        TrimOnMemoryPressure = false,
    };

    private static PooledMemoryAllocator NewPool() => new(TestPoolOptions());

    private static OperationExecution Use(MemoryAllocator allocator)
        => OperationExecution.Enter(new ImageExecutionOptions { Configuration = new Configuration { MemoryAllocator = allocator } });

    private static long ClassBytes<T>(int length)
        where T : unmanaged
    {
        Assert.True(PooledMemoryAllocator.SizeClasses.TryGet(length, out _, out int classLength));
        return (long)classLength * Unsafe.SizeOf<T>();
    }

    private static T[] BackingArray<T>(Image<T> image)
        where T : unmanaged, IPixel<T>
    {
        Assert.True(image.DangerousTryGetSinglePixelMemory(out Memory<T> memory));
        Assert.True(MemoryMarshal.TryGetArray<T>(memory, out ArraySegment<T> segment));
        Assert.Equal(0, segment.Offset);
        return segment.Array!;
    }

    private static Rgba32[] Pixels(Image<Rgba32> image)
    {
        var pixels = new Rgba32[image.Width * image.Height];
        image.CopyPixelDataTo(pixels);
        return pixels;
    }

    private static bool AllZero(Image<Rgba32> image) => image.Frames.RootFrame.PixelSpan.IndexOfAnyExcept(default(Rgba32)) < 0;

    private static Image<Rgba32> Filled(int width, int height, Rgba32 color) => new(width, height, color);

    private static Image<Rgba32> Pattern(int width, int height, int seed, bool opaque = false)
    {
        var random = new Random(seed);
        var image = new Image<Rgba32>(width, height);
        Span<Rgba32> pixels = image.Frames.RootFrame.PixelSpan;
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), opaque ? (byte)255 : (byte)random.Next(1, 256));
        }

        return image;
    }

    /// <summary>Fills the pool with buffers of many sizes painted in <paramref name="color"/>.</summary>
    private static void Poison(PooledMemoryAllocator pool, Rgba32 color)
    {
        pool.ReleaseRetainedResources();
        var owners = new List<IMemoryOwner<Rgba32>>();
        for (int length = 16; length < 40_000; length = (length * 9 / 8) + 1)
        {
            IMemoryOwner<Rgba32> owner = pool.Allocate<Rgba32>(length, AllocationOptions.None);
            if (MemoryMarshal.TryGetArray<Rgba32>(owner.Memory, out ArraySegment<Rgba32> segment))
            {
                segment.Array.AsSpan().Fill(color);
            }

            owners.Add(owner);
        }

        foreach (IMemoryOwner<Rgba32> owner in owners)
        {
            owner.Dispose();
        }
    }

    /// <summary>Rents and scribbles over a few buffers of the given size, to catch anybody still holding one.</summary>
    private static void Churn(PooledMemoryAllocator pool, int width, int height)
    {
        using (Use(pool))
        {
            for (int i = 0; i < 3; i++)
            {
                using Image<Rgba32> scribble = Filled(width, height, new Rgba32(0xFF, 0x00, 0xFF, (byte)i));
            }
        }
    }

    private static byte[] EncodePng(Image<Rgba32> image)
    {
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    /// <summary>Counts allocations and owner disposals, and flags any owner disposed twice.</summary>
    private sealed class CountingAllocator : MemoryAllocator
    {
        private readonly MemoryAllocator inner;
        private int allocations;
        private int disposals;
        private int doubleDisposals;

        public CountingAllocator(MemoryAllocator inner) => this.inner = inner;

        public int Allocations => Volatile.Read(ref this.allocations);

        public int Disposals => Volatile.Read(ref this.disposals);

        public int DoubleDisposals => Volatile.Read(ref this.doubleDisposals);

        public override IMemoryOwner<T> Allocate<T>(int length, AllocationOptions options = AllocationOptions.Clean)
        {
            Interlocked.Increment(ref this.allocations);
            return new Owner<T>(this, this.inner.Allocate<T>(length, options));
        }

        private sealed class Owner<T> : IMemoryOwner<T>
        {
            private readonly CountingAllocator parent;
            private readonly IMemoryOwner<T> inner;
            private int disposed;

            public Owner(CountingAllocator parent, IMemoryOwner<T> inner)
            {
                this.parent = parent;
                this.inner = inner;
            }

            public Memory<T> Memory => this.inner.Memory;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref this.disposed, 1) != 0)
                {
                    Interlocked.Increment(ref this.parent.doubleDisposals);
                    return;
                }

                Interlocked.Increment(ref this.parent.disposals);
                this.inner.Dispose();
            }
        }
    }

    /// <summary>A broken allocator that hands out less memory than asked for.</summary>
    private sealed class ShortAllocator : MemoryAllocator
    {
        public int Disposed;

        public override IMemoryOwner<T> Allocate<T>(int length, AllocationOptions options = AllocationOptions.Clean)
            => new Owner<T>(this, new T[Math.Max(0, length - 1)]);

        private sealed class Owner<T> : IMemoryOwner<T>
        {
            private readonly ShortAllocator parent;

            public Owner(ShortAllocator parent, T[] array)
            {
                this.parent = parent;
                this.Memory = array;
            }

            public Memory<T> Memory { get; }

            public void Dispose() => Interlocked.Increment(ref this.parent.Disposed);
        }
    }
}

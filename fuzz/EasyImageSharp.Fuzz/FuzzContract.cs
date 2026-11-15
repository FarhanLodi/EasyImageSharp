using System.Globalization;
using EasyImageSharp.Formats;

namespace EasyImageSharp.Fuzz;

/// <summary>A call made on one input, taking the input bytes as a span so no copy is made per call.</summary>
/// <param name="data">The fuzz input.</param>
internal delegate void InputCall(ReadOnlySpan<byte> data);

/// <summary>
/// Raised when an input breaks the decoder contract. It is never caught by the harness: under libFuzzer it
/// escapes into SharpFuzz, which reports the input as a crash; in replay mode it is printed and the process
/// exits non-zero.
/// </summary>
internal sealed class FuzzContractViolationException : Exception
{
    public FuzzContractViolationException(string message)
        : base(message)
    {
    }

    public FuzzContractViolationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>How one guarded call ended, for the replay summary.</summary>
internal enum CallOutcome
{
    /// <summary>The call returned normally.</summary>
    Success,

    /// <summary><see cref="UnknownImageFormatException"/>: the bytes are not a recognised format.</summary>
    UnknownFormat,

    /// <summary><see cref="InvalidImageContentException"/>: recognised but malformed.</summary>
    InvalidContent,

    /// <summary><see cref="ImageSizeLimitExceededException"/>: rejected by a <see cref="DecoderOptions"/> limit.</summary>
    SizeLimit,

    /// <summary>Any other <see cref="ImageFormatException"/> subclass.</summary>
    OtherFormatException,

    /// <summary><see cref="NotSupportedException"/>: a recognised feature the library does not implement.</summary>
    NotSupported,
}

/// <summary>
/// The closed exception contract every decoder entry point promises on untrusted input, enforced on each
/// fuzz input: a call may return, or throw <see cref="ImageFormatException"/> (any subclass) or
/// <see cref="NotSupportedException"/>. Anything else — an <see cref="IndexOutOfRangeException"/>, a
/// <see cref="NullReferenceException"/>, an <see cref="OutOfMemoryException"/>, an <see cref="OverflowException"/>
/// — is a bug and is turned into a <see cref="FuzzContractViolationException"/>. So is a call that allocates
/// more managed memory than <see cref="MaxAllocatedBytesPerCall"/>, because the limits below are meant to be
/// enforced before allocation, not after it.
/// </summary>
/// <remarks>
/// Hangs are not detected here. Under libFuzzer, <c>-timeout=N</c> reports an input that runs for longer than N
/// seconds; in replay mode <see cref="Replay"/> applies its own deadline.
/// </remarks>
internal static class FuzzContract
{
    /// <summary>
    /// Ceiling on managed bytes allocated by one guarded call. Overridden by <c>EASYIMAGESHARP_FUZZ_MAX_ALLOC_MB</c>.
    /// With <see cref="Options"/> a well-behaved decode stays far below this: the largest legal frame is 1 MiP,
    /// which is 4 MiB as <c>Rgba32</c> and 8 MiB as <c>Rgba64</c>, and all frames together are 4 MiP. What a
    /// decode allocates beyond that is scratch, and scratch sized from an unchecked header field is exactly
    /// the bug this ceiling exists to catch.
    /// </summary>
    public static readonly long MaxAllocatedBytesPerCall = EnvMegabytes("EASYIMAGESHARP_FUZZ_MAX_ALLOC_MB", 128);

    /// <summary>
    /// Deliberately small limits. A fuzzer spends its time where inputs are cheap, so small frames mean many
    /// more executions per second; and an input that is rejected by a limit costs nothing, so the limits double
    /// as a check that each decoder enforces them before allocating.
    /// </summary>
    public static readonly DecoderOptions Options = new()
    {
        MaxPixels = 1L << 20,
        MaxTotalPixels = 4L << 20,
        MaxFrames = 64,
        MaxEncodedBytes = 16L << 20,
        ExecutionOptions = new ImageExecutionOptions
        {
            // Single-threaded, so the same input always takes the same path through the instrumented code and a
            // reproducer replays exactly as it was found.
            Configuration = new Configuration { MaxDegreeOfParallelism = 1 },
            MaxPixelBufferBytes = 128L << 20,
        },
    };

    private static readonly long[] Counts = new long[Enum.GetValues<CallOutcome>().Length];

    /// <summary>Per-outcome call counts since the process started (replay mode prints them).</summary>
    public static IReadOnlyList<long> OutcomeCounts => Counts;

    /// <summary>True for the exceptions the contract allows a decoder to throw on malformed input.</summary>
    public static bool IsAllowed(Exception ex) => ex is ImageFormatException or NotSupportedException;

    /// <summary>
    /// Runs <paramref name="call"/> on <paramref name="data"/> under the contract. Returns <see langword="true"/>
    /// when the call returned normally and <see langword="false"/> when it threw an allowed exception.
    /// </summary>
    /// <param name="name">Names the call in a violation message, e.g. <c>PngDecoder.Decode&lt;Rgba32&gt;</c>.</param>
    /// <param name="data">The fuzz input.</param>
    /// <param name="call">The library call to make.</param>
    /// <exception cref="FuzzContractViolationException">Any other exception escaped, or the allocation ceiling was passed.</exception>
    public static bool Guard(string name, ReadOnlySpan<byte> data, InputCall call)
    {
        long before = GC.GetTotalAllocatedBytes(precise: false);
        CallOutcome outcome;
        try
        {
            call(data);
            outcome = CallOutcome.Success;
        }
        catch (Exception ex) when (IsAllowed(ex))
        {
            outcome = Classify(ex);
        }
        catch (Exception ex)
        {
            throw new FuzzContractViolationException(
                $"{name} let {ex.GetType().FullName} escape the decoder contract on a {data.Length}-byte input: {ex.Message}", ex);
        }

        long allocated = GC.GetTotalAllocatedBytes(precise: false) - before;
        if (allocated > MaxAllocatedBytesPerCall)
        {
            throw new FuzzContractViolationException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} allocated {1:F1} MiB on a {2}-byte input (ceiling {3:F1} MiB, outcome {4}); DecoderOptions limits must be enforced before allocating.",
                name,
                allocated / 1048576.0,
                data.Length,
                MaxAllocatedBytesPerCall / 1048576.0,
                outcome));
        }

        Counts[(int)outcome]++;
        return outcome == CallOutcome.Success;
    }

    /// <summary>Asserts a round-trip invariant; a failure is a violation, not an allowed outcome.</summary>
    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new FuzzContractViolationException(message);
        }
    }

    private static CallOutcome Classify(Exception ex) => ex switch
    {
        UnknownImageFormatException => CallOutcome.UnknownFormat,
        InvalidImageContentException => CallOutcome.InvalidContent,
        ImageSizeLimitExceededException => CallOutcome.SizeLimit,
        ImageFormatException => CallOutcome.OtherFormatException,
        _ => CallOutcome.NotSupported,
    };

    private static long EnvMegabytes(string variable, long fallback)
        => long.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) && value > 0
            ? value << 20
            : fallback << 20;
}

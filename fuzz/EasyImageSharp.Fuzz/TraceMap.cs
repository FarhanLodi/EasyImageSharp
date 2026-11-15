using System.Runtime.InteropServices;

namespace EasyImageSharp.Fuzz;

/// <summary>
/// Gives SharpFuzz's coverage probes somewhere to write before (or without) libFuzzer.
/// </summary>
/// <remarks>
/// <para>
/// <c>sharpfuzz</c> rewrites every branch of the instrumented EasyImageSharp.dll to update
/// <c>SharpFuzz.Common.Trace.SharedMem</c>, a raw pointer that only <c>Fuzzer.LibFuzzer.Run</c> points at the
/// memory shared with libfuzzer-dotnet. Until then it is null, so the first instrumented instruction to execute
/// — a static constructor, a decoder constructor, anything — dereferences null and the process dies before it
/// has told the driver it started, which leaves the driver waiting forever.
/// </para>
/// <para>
/// <see cref="InstallScratch"/> points the probes at a private 64 KiB scratch map at start-up. Under libFuzzer,
/// <c>Run</c> then replaces it with the shared map, and coverage from code that ran earlier is simply not
/// reported. In replay mode the scratch map is all there is, so an instrumented build replays exactly like an
/// uninstrumented one. With an uninstrumented build nothing reads the field and this is a no-op.
/// </para>
/// <para>
/// This is the only unsafe code in the harness (the field is a <c>byte*</c>), and the map is deliberately never
/// freed: probes may write to it until the process exits.
/// </para>
/// </remarks>
internal static class TraceMap
{
    /// <summary>The size of the AFL-style edge map SharpFuzz instruments against (<c>1 &lt;&lt; 16</c>).</summary>
    private const int MapSize = 1 << 16;

    /// <summary>Points the coverage probes at a zeroed scratch map unless something already mapped them.</summary>
    public static unsafe void InstallScratch()
    {
        if (SharpFuzz.Common.Trace.SharedMem is null)
        {
            SharpFuzz.Common.Trace.SharedMem = (byte*)NativeMemory.AllocZeroed(MapSize);
        }
    }
}

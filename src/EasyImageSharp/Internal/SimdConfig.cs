namespace EasyImageSharp;

/// <summary>
/// Global switch used by the vectorised kernels. Every kernel that has a hardware-accelerated path also
/// keeps a scalar reference path; setting <see cref="ForceScalarFallback"/> selects the reference path so
/// tests can prove the two agree byte for byte on machines that do have the intrinsics.
/// </summary>
internal static class SimdConfig
{
    /// <summary>When true every vectorised kernel falls back to its scalar reference implementation.</summary>
    internal static bool ForceScalarFallback;

    /// <summary>True when a kernel may use its 128-bit vector path.</summary>
    internal static bool Vector128Enabled
        => System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated && !ForceScalarFallback;

    /// <summary>True when a kernel may use its 256-bit vector path.</summary>
    internal static bool Vector256Enabled
        => System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated && !ForceScalarFallback;
}

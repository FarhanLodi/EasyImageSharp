using EasyImageSharp.Avif;
using EasyImageSharp.JpegXl;
using Xunit;

namespace EasyImageSharp.LittleCms.Tests;

/// <summary>
/// EasyImageSharp.Codecs holds three native bindings in one assembly, and the runtime accepts only one
/// DllImport resolver per assembly. Each binding registers its resolver from a static constructor, so if they did
/// not share one, touching the second binding would throw ("a resolver is already set") whether or not its native
/// library is installed. This touches LittleCMS, libavif and libjxl in the same process, in that order and again.
/// </summary>
/// <remarks>
/// Only LittleCMS is installed on every CI runner. libavif and libjxl are bundled for Windows but usually absent on
/// Linux and macOS, where their bindings must report the missing library through <c>LoadFailure</c>, not throw.
/// </remarks>
public class SharedNativeResolverTests
{
    [Fact]
    public void AllThreeBindingsRegisterAndResolveInOneProcess()
    {
        Assert.True(NativeCms.Version() > 0);

        // Running each static constructor is the point: with separate resolvers this is where it would throw.
        string? avifFailure = NativeAvif.LoadFailure;
        string? jxlFailure = NativeJxl.LoadFailure;
        Assert.Equal(avifFailure is null, AvifFormat.IsNativeLibraryAvailable);
        Assert.Equal(jxlFailure is null, JpegXlFormat.IsNativeLibraryAvailable);

        // LittleCMS still resolves after the other two registered theirs.
        Assert.True(NativeCms.Version() > 0);

        // On Windows the package bundles all three runtimes, so there both must load.
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(avifFailure);
            Assert.Null(jxlFailure);
        }
    }
}

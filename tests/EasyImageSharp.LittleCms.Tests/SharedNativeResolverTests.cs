using EasyImageSharp.Avif;
using EasyImageSharp.JpegXl;
using Xunit;

namespace EasyImageSharp.LittleCms.Tests;

/// <summary>
/// EasyImageSharp.Codecs holds three native bindings in one assembly, and the runtime accepts only one
/// DllImport resolver per assembly. This loads LittleCMS, libavif and libjxl in the same process, in that order
/// and again, which fails with "a resolver is already set" (or loads nothing) unless they share one resolver.
/// </summary>
public class SharedNativeResolverTests
{
    [Fact]
    public void AllThreeNativeLibrariesLoadInOneProcess()
    {
        Assert.True(NativeCms.Version() > 0);
        Assert.Null(NativeAvif.LoadFailure);
        Assert.Null(NativeJxl.LoadFailure);
        Assert.True(AvifFormat.IsNativeLibraryAvailable);
        Assert.True(JpegXlFormat.IsNativeLibraryAvailable);
        Assert.True(NativeCms.Version() > 0);
    }
}

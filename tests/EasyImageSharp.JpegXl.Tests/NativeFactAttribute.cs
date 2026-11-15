using Xunit;

namespace EasyImageSharp.JpegXl.Tests;

/// <summary>
/// A fact that needs the native libjxl runtime. It is reported as skipped, with the loader's reason, when libjxl
/// cannot be loaded; set <c>EASYIMAGESHARP_REQUIRE_NATIVE_CODECS=1</c> (as CI jobs that ship the runtime should) to
/// turn that skip into a failure instead.
/// </summary>
public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute()
    {
        if (NativeJxl.LoadFailure is { } reason && !NativeRequirement.Required)
        {
            this.Skip = "libjxl is not available: " + reason;
        }
    }
}

/// <summary>The <see cref="TheoryAttribute"/> counterpart of <see cref="NativeFactAttribute"/>.</summary>
public sealed class NativeTheoryAttribute : TheoryAttribute
{
    public NativeTheoryAttribute()
    {
        if (NativeJxl.LoadFailure is { } reason && !NativeRequirement.Required)
        {
            this.Skip = "libjxl is not available: " + reason;
        }
    }
}

internal static class NativeRequirement
{
    internal static bool Required => Environment.GetEnvironmentVariable("EASYIMAGESHARP_REQUIRE_NATIVE_CODECS") == "1";
}

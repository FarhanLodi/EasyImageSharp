using System.Runtime.InteropServices;

namespace EasyImageSharp.LittleCms;

internal static unsafe partial class NativeCms
{
    static NativeCms()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeCms).Assembly, (name, assembly, path) =>
        {
            if (name != "lcms2") return IntPtr.Zero;
            string[] candidates = OperatingSystem.IsWindows() ? ["lcms2.dll"]
                : OperatingSystem.IsMacOS() ? ["liblcms2.2.dylib", "liblcms2.dylib", "/opt/homebrew/lib/liblcms2.2.dylib", "/usr/local/lib/liblcms2.2.dylib", "/opt/homebrew/opt/little-cms2/lib/liblcms2.2.dylib", "/usr/local/opt/little-cms2/lib/liblcms2.2.dylib"]
                : ["liblcms2.so.2", "liblcms2.so"];
            foreach (string candidate in candidates)
                if (NativeLibrary.TryLoad(candidate, assembly, path, out IntPtr handle)) return handle;
            return IntPtr.Zero;
        });
    }

    [LibraryImport("lcms2", EntryPoint = "cmsGetEncodedCMMversion")]
    internal static partial int Version();
    [LibraryImport("lcms2", EntryPoint = "cmsOpenProfileFromMem")]
    internal static partial IntPtr OpenProfile(byte* data, uint size);
    [LibraryImport("lcms2", EntryPoint = "cmsCloseProfile")]
    internal static partial int CloseProfile(IntPtr profile);
    [LibraryImport("lcms2", EntryPoint = "cmsGetColorSpace")]
    internal static partial uint ColorSpace(IntPtr profile);
    [LibraryImport("lcms2", EntryPoint = "cmsCreateTransform")]
    internal static partial IntPtr CreateTransform(IntPtr input, uint inputFormat, IntPtr output, uint outputFormat, uint intent, uint flags);
    [LibraryImport("lcms2", EntryPoint = "cmsDeleteTransform")]
    internal static partial void DeleteTransform(IntPtr transform);
    [LibraryImport("lcms2", EntryPoint = "cmsDoTransform")]
    internal static partial void DoTransform(IntPtr transform, float* source, float* destination, uint count);
    [LibraryImport("lcms2", EntryPoint = "cmsCreate_sRGBProfile")]
    internal static partial IntPtr CreateSrgb();
    [LibraryImport("lcms2", EntryPoint = "cmsSaveProfileToMem")]
    internal static partial int SaveProfile(IntPtr profile, byte* data, ref uint size);
    [LibraryImport("lcms2", EntryPoint = "cmsCreateLab4Profile")]
    internal static partial IntPtr CreateLab(IntPtr whitePoint);
    [LibraryImport("lcms2", EntryPoint = "cmsCreateXYZProfile")]
    internal static partial IntPtr CreateXyz();
    [LibraryImport("lcms2", EntryPoint = "cmsCreateGrayProfile")]
    internal static partial IntPtr CreateGray(double* whitePoint, IntPtr curve);
    [LibraryImport("lcms2", EntryPoint = "cmsBuildGamma")]
    internal static partial IntPtr BuildGamma(IntPtr context, double gamma);
    [LibraryImport("lcms2", EntryPoint = "cmsFreeToneCurve")]
    internal static partial void FreeCurve(IntPtr curve);
}

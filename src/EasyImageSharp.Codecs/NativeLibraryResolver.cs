using System.Reflection;
using System.Runtime.InteropServices;

namespace EasyImageSharp.Codecs;

/// <summary>
/// The single <see cref="NativeLibrary.SetDllImportResolver"/> registration of this assembly. The runtime accepts only
/// one resolver per assembly, and libavif, libjxl and LittleCMS each need their own probing rules, so every binding
/// class registers its resolver here and this one asks them in turn. Each resolver returns
/// <see cref="IntPtr.Zero"/> for library names it does not own.
/// </summary>
internal static class NativeLibraryResolver
{
    private static readonly object Gate = new();
    private static DllImportResolver[] resolvers = [];
    private static bool installed;

    /// <summary>Adds a resolver; installs the shared assembly resolver on first use.</summary>
    public static void Register(DllImportResolver resolver)
    {
        lock (Gate)
        {
            resolvers = [.. resolvers, resolver];
            if (!installed)
            {
                NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
                installed = true;
            }
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        foreach (DllImportResolver resolver in Volatile.Read(ref resolvers))
        {
            IntPtr handle = resolver(name, assembly, searchPath);
            if (handle != IntPtr.Zero)
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }
}

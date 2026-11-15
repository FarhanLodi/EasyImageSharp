using System.Diagnostics.CodeAnalysis;

namespace EasyImageSharp.Fonts;

/// <summary>
/// The fonts installed on the machine, discovered on first use from the platform's font directories:
/// the Windows fonts folders (system and per-user), <c>/System/Library/Fonts</c>, <c>/Library/Fonts</c> and
/// <c>~/Library/Fonts</c> on macOS, and <c>/usr/share/fonts</c>, <c>/usr/local/share/fonts</c>,
/// <c>~/.local/share/fonts</c> and <c>~/.fonts</c> elsewhere.
/// </summary>
/// <remarks>
/// Discovery reads only the names and styles of each file; a face is parsed when it is first used. Files that
/// cannot be read or are not TrueType/OpenType/WOFF fonts are skipped. Containers such as Alpine images
/// often ship no fonts at all, so portable code should load a font file it bundles with
/// <see cref="FontFace.Load(string)"/> rather than rely on a system family being present.
/// </remarks>
public static class SystemFonts
{
    /// <summary>Most font files indexed, which bounds the scan on machines with very large font folders.</summary>
    private const int MaxFiles = 20_000;

    private static readonly Lazy<FontCollection> Lazy = new(Discover, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The installed fonts. The first access scans the font directories.</summary>
    public static FontCollection Collection => Lazy.Value;

    /// <summary>The installed font families, ordered by name.</summary>
    public static IReadOnlyList<FontFamily> Families => Collection.Families;

    /// <summary>The directories searched on this platform (whether or not they exist).</summary>
    public static IReadOnlyList<string> SearchDirectories { get; } = GetSearchDirectories();

    /// <summary>Gets an installed family by name (case-insensitive).</summary>
    public static bool TryGetFamily(string name, [NotNullWhen(true)] out FontFamily? family)
        => Collection.TryGetFamily(name, out family);

    /// <summary>Creates a font from an installed family, in pixels.</summary>
    /// <exception cref="KeyNotFoundException">No installed family has that name.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The size is not positive and finite, or too large.</exception>
    public static Font CreateFont(string familyName, float sizeInPixels, FontStyle style)
        => Collection.GetFamily(familyName).CreateFont(sizeInPixels, style);

    /// <summary>Creates a font from the regular face of an installed family, in pixels.</summary>
    /// <inheritdoc cref="CreateFont(string, float, FontStyle)" path="/exception"/>
    public static Font CreateFont(string familyName, float sizeInPixels)
        => CreateFont(familyName, sizeInPixels, FontStyle.Regular);

    private static FontCollection Discover()
    {
        var collection = new FontCollection();
        int files = 0;
        foreach (string directory in SearchDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            IEnumerable<string> candidates;
            try
            {
                candidates = Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 8,
                    AttributesToSkip = 0,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in candidates)
            {
                if (!IsFontFile(file))
                {
                    continue;
                }

                if (++files > MaxFiles)
                {
                    return collection;
                }

                collection.AddLazy(file);
            }
        }

        return collection;
    }

    private static bool IsFontFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".otf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".otc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".woff", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".woff2", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] GetSearchDirectories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return NonEmpty(
                system.Length > 0 ? system : Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Fonts"),
                local.Length > 0 ? Path.Combine(local, "Microsoft", "Windows", "Fonts") : string.Empty);
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            return NonEmpty(
                "/System/Library/Fonts",
                "/Library/Fonts",
                home.Length > 0 ? Path.Combine(home, "Library", "Fonts") : string.Empty);
        }

        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return NonEmpty(
            "/usr/share/fonts",
            "/usr/local/share/fonts",
            !string.IsNullOrEmpty(dataHome) ? Path.Combine(dataHome, "fonts") : home.Length > 0 ? Path.Combine(home, ".local", "share", "fonts") : string.Empty,
            home.Length > 0 ? Path.Combine(home, ".fonts") : string.Empty);
    }

    private static string[] NonEmpty(params string[] directories)
        => directories.Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
}

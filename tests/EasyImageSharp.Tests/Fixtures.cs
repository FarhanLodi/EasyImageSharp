namespace EasyImageSharp.Tests;

/// <summary>Resolves paths to the tool-generated sample files under <c>Fixtures/</c>.</summary>
internal static class FixturePath
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>Returns the absolute path of a fixture, e.g. <c>Get("jpeg/progressive.jpg")</c>.</summary>
    public static string Get(string relativePath)
    {
        string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fixture '{relativePath}' is missing; run Fixtures/generate.py.", path);
        }

        return path;
    }

    public static byte[] Read(string relativePath) => File.ReadAllBytes(Get(relativePath));

    public static bool Exists(string relativePath)
        => File.Exists(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}

using System.Text.Json;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Benchmarks;

/// <summary>
/// Locates and loads the generated benchmark corpus.
/// <para>
/// The corpus is never committed and never copied into bin/: it is tens of megabytes, and a copy rule would
/// pay for that copy on every build. Instead the directory is found at run time by walking up from
/// <see cref="AppContext.BaseDirectory"/> until <c>benchmarks/corpus/generate.py</c> appears.
/// </para>
/// <para>
/// Nothing here uses EasyImageSharp to produce a corpus file. Every input is written by Pillow, that is by
/// libjpeg-turbo, libwebp and zlib, so a decode benchmark decodes a foreign encoder's output - the same
/// discipline the test fixture corpus follows.
/// </para>
/// </summary>
internal static class Corpus
{
    /// <summary>The stem of the canonical 3032x2008 photograph; every container shares these pixels.</summary>
    public const string Photo = "photo";

    /// <summary>The A4-at-300-DPI grayscale page: 2480x3508, 8 bits per pixel.</summary>
    public const string Scan = "scan.png";

    /// <summary>The command that rebuilds the corpus, quoted verbatim in every failure message.</summary>
    public const string GenerateCommand = "python benchmarks/corpus/generate.py";

    /// <summary>The nine container extensions <c>photo.*</c> is written in, and the decode benchmark's parameters.</summary>
    public static readonly string[] Formats = ["png", "jpeg", "bmp", "tiff", "webp", "gif", "tga", "qoi", "ppm"];

    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly string Root = Path.Combine(RepoRoot, "benchmarks", "corpus");

    /// <summary>Resolves a path given relative to the repository root, using the platform's separator.</summary>
    public static string RepoRelative(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        return Path.GetFullPath(Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>Absolute path of one corpus file; <paramref name="name"/> uses forward slashes.</summary>
    public static string FilePath(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Checks the corpus against the manifest generate.py wrote and throws one actionable error if it is
    /// missing or stale. Called once from Main so a missing corpus is reported before BenchmarkDotNet starts,
    /// rather than as forty identical <c>FileNotFoundException</c>s out of forty <c>[GlobalSetup]</c> methods.
    /// </summary>
    public static void EnsurePresent()
    {
        string manifestPath = FilePath("manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"The benchmark corpus is missing from {Root}. Run: {GenerateCommand}");
        }

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement root = manifest.RootElement;

        foreach (JsonElement entry in root.GetProperty("files").EnumerateArray())
        {
            string name = entry.GetProperty("name").GetString() ?? string.Empty;
            long expected = entry.GetProperty("bytes").GetInt64();
            var file = new FileInfo(FilePath(name));
            if (!file.Exists)
            {
                throw new InvalidOperationException(
                    $"The benchmark corpus is incomplete: {name} is missing. Run: {GenerateCommand} --force");
            }

            if (file.Length != expected)
            {
                throw new InvalidOperationException(
                    $"The benchmark corpus is stale: {name} is {file.Length} bytes, the manifest says " +
                    $"{expected}. Run: {GenerateCommand} --force");
            }
        }

        if (root.TryGetProperty("small", out JsonElement small) && small.ValueKind == JsonValueKind.True)
        {
            Console.WriteLine(
                "NOTE: this is the small corpus (EASYIMAGESHARP_BENCH_SMALL). Every dimension is an eighth of " +
                "the real one, so the timings below are only good for proving the benchmarks run.");
        }
    }

    /// <summary>Reads one corpus file into memory. Benchmarks call this from setup, never from a measured method.</summary>
    public static byte[] Bytes(string name) => File.ReadAllBytes(FilePath(name));

    /// <summary>Decodes one corpus file to 32-bit RGBA.</summary>
    public static Image<Rgba32> LoadRgba32(string name) => Image.Load<Rgba32>(Bytes(name));

    /// <summary>Decodes one corpus file to 8-bit grayscale.</summary>
    public static Image<L8> LoadL8(string name) => Image.Load<L8>(Bytes(name));

    /// <summary>The twenty batch JPEGs, in ordinal name order, as absolute paths.</summary>
    public static string[] BatchJpegs()
    {
        string directory = FilePath("batch");
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException(
                $"The benchmark corpus has no batch directory at {directory}. Run: {GenerateCommand}");
        }

        string[] files = Directory.GetFiles(directory, "*.jpg");
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    private static string FindRepoRoot()
    {
        // The benchmark binary lives at <repo>/benchmarks/EasyImageSharp.Benchmarks/bin/<config>/<tfm>/, and
        // BenchmarkDotNet's generated host lives deeper still, so the walk has to be open-ended rather than a
        // fixed number of "..". The marker is generate.py, which is one of only two files git tracks under
        // benchmarks/corpus and therefore exists in every checkout, corpus generated or not.
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "benchmarks", "corpus", "generate.py")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"No directory containing benchmarks/corpus/generate.py was found above {AppContext.BaseDirectory}. " +
            "Run the benchmarks from a checkout of the repository.");
    }
}

#if SOURCE_IMAGING
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
#endif
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EasyOcrSharp.Services;
using PaddleOcrNet.Services;

if (args is ["--self-test"])
{
    Check(ErrorRate("hello".ToCharArray(), "hallo".ToCharArray()), 0.2);
    Check(ErrorRate("hello".ToCharArray(), "hell".ToCharArray()), 0.2);
    Check(ErrorRate("hello".ToCharArray(), "hello!".ToCharArray()), 0.2);
    Check(ErrorRate("😀a".EnumerateRunes().ToArray(), "😀b".EnumerateRunes().ToArray()), 0.5);
    Check(ErrorRate(Array.Empty<string>(), Array.Empty<string>()), 0);
    Check(ErrorRate(Array.Empty<string>(), new[] { "unexpected" }), 1);
    Check(ErrorRate(new[] { "the", "cat", "sat" }, new[] { "the", "dog", "sat" }), 1d / 3);
    Console.WriteLine("Seven text metric self-tests passed.");
    return 0;

    static void Check(double actual, double expected)
    {
        if (Math.Abs(actual - expected) > 1e-12) throw new InvalidOperationException($"Metric expected {expected}, got {actual}.");
    }
}

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: OcrCorpusExport <manifest.json> <easyocr|paddleocr> <output.json>");
    return 2;
}

string manifestPath = Path.GetFullPath(args[0]);
string root = Path.GetDirectoryName(manifestPath)!;
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
Manifest manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath), json)
    ?? throw new InvalidDataException("Manifest is empty.");
string engine = args[1].ToLowerInvariant();
if (engine is not ("easyocr" or "paddleocr")) throw new ArgumentException("Unknown OCR engine.");
if (manifest.Images.Length == 0) throw new ArgumentException("Manifest must contain images.");
if (manifest.Images.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Images.Length)
    throw new ArgumentException("Image identifiers must be unique.");
string cache = Resolve(engine == "easyocr" ? manifest.EasyOcrCache : manifest.PaddleOcrCache);
if (manifest.AllowModelDownload) Directory.CreateDirectory(cache);
if (!Directory.Exists(cache)) throw new DirectoryNotFoundException(cache);

await using var easy = engine == "easyocr" ? new EasyOcrService(new EasyOcrServiceOptions
{
    ModelCachePath = cache,
    ExecutionProvider = EasyOcrSharp.Services.OcrExecutionProvider.Cpu,
    IntraOpNumThreads = 1,
    InterOpNumThreads = 1,
    Download = new EasyOcrSharp.Services.ModelDownloadOptions { Offline = !manifest.AllowModelDownload },
}) : null;
await using var paddle = engine == "paddleocr" ? new PaddleOcrService(new PaddleOcrServiceOptions
{
    ModelCachePath = cache,
    ExecutionProvider = PaddleOcrNet.Services.OcrExecutionProvider.Cpu,
    IntraOpNumThreads = 1,
    InterOpNumThreads = 1,
    Download = new PaddleOcrNet.Services.ModelDownloadOptions { Offline = !manifest.AllowModelDownload },
}) : null;

var rows = new List<Observation>();
foreach (InputImage input in manifest.Images.OrderBy(i => i.Id, StringComparer.Ordinal))
{
    string imagePath = Resolve(input.Path);
    string? truth = input.GroundTruthFile is null ? input.GroundTruth : (await File.ReadAllTextAsync(Resolve(input.GroundTruthFile))).Trim();
    string text = "";
    int lines = 0;
    string? error = null;
    string? temporaryImage = null;
    string inferencePath = imagePath;
    long before = GC.GetTotalAllocatedBytes(true);
    var timer = Stopwatch.StartNew();
    try
    {
#if SOURCE_IMAGING
    if (manifest.PreprocessingProfile is not null)
    {
        using var original = Image.Load<Rgba32>(imagePath);
        using var prepared = original.PreprocessDocument(new DocumentPreprocessingOptions
        {
            Profile = Enum.Parse<DocumentPreprocessingProfile>(manifest.PreprocessingProfile),
            ExecutionOptions = new ImageExecutionOptions { Configuration = new Configuration { MaxDegreeOfParallelism = 1 } },
        });
        temporaryImage = Path.Combine(Path.GetTempPath(), "ocr-ablation-" + Guid.NewGuid().ToString("N") + ".png");
        prepared.Image.SaveAsPng(temporaryImage);
        inferencePath = temporaryImage;
    }
#else
    if (manifest.PreprocessingProfile is not null) throw new InvalidOperationException("Preprocessing requires the source imaging build.");
#endif
    if (easy is not null)
    {
        var result = await easy.ExtractTextFromImage(inferencePath, input.EasyOcrLanguages);
        text = result.FullText.Trim();
        lines = result.Lines.Count;
    }
    else
    {
        PaddleOcrNet.Models.OcrLanguage[] languages = input.PaddleOcrLanguages
            .Select(l => Enum.Parse<PaddleOcrNet.Models.OcrLanguage>(l, ignoreCase: true)).ToArray();
        var result = await paddle!.ExtractTextFromImage(inferencePath, languages);
        text = result.FullText.Trim();
        lines = result.Lines.Count;
    }

    }
    catch (Exception exception)
    {
        error = exception.GetType().Name + ": " + exception.Message;
        Console.Error.WriteLine($"{input.Id}: {error}");
    }
    finally
    {
        if (temporaryImage is not null && File.Exists(temporaryImage)) File.Delete(temporaryImage);
    }
    timer.Stop();
    long allocated = GC.GetTotalAllocatedBytes(false) - before;
    double? cer = truth is null ? null : ErrorRate(truth.EnumerateRunes().ToArray(), text.EnumerateRunes().ToArray());
    double? wer = truth is null ? null : ErrorRate(truth.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    rows.Add(new Observation(input.Id, input.Category, input.Split, Hash(imagePath), truth, text, lines,
        timer.Elapsed.TotalMilliseconds, allocated, cer, wer, error));
    Console.WriteLine($"{engine}: {input.Id}: {lines} lines, {timer.Elapsed.TotalMilliseconds:F1} ms, CER={cer?.ToString("F4") ?? "unlabeled"}");
}

AssetFingerprint[] models = Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories)
    .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".onnx" or ".txt" or ".json")
    .OrderBy(p => p, StringComparer.Ordinal)
    .Select(p => new AssetFingerprint(Path.GetRelativePath(cache, p), Hash(p))).ToArray();
string outputPath = Path.GetFullPath(args[2]);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
var report = new
{
    SchemaVersion = 1,
    Engine = engine,
    ManifestSha256 = Hash(manifestPath),
    Runtime = RuntimeInformation.FrameworkDescription,
    Platform = RuntimeInformation.OSDescription,
    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    ExecutionProvider = "CPU",
    IntraOpThreads = 1,
    InterOpThreads = 1,
    TimingIncludesFirstUseModelInitialization = true,
    TextNormalization = "Trim only; CER counts Unicode scalar values; WER splits whitespace; case-sensitive",
    ModelsScope = "All cached ONNX, dictionary and JSON assets after inference",
    ModelDownloadsAllowed = manifest.AllowModelDownload,
    manifest.PreprocessingProfile,
    FailedImages = rows.Count(row => row.Error is not null),
    ModelCacheSnapshot = models,
    Assemblies = new[] { typeof(EasyOcrService).Assembly, typeof(PaddleOcrService).Assembly, typeof(EasyImageSharp.Image).Assembly }
        .Select(a => new { Name = a.GetName().Name, Version = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, Sha256 = Hash(a.Location) }),
    Rows = rows,
};
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, json));
return 0;

string Resolve(string path)
{
    path = Environment.ExpandEnvironmentVariables(path);
    return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
}
static string Hash(string path)
{
    using FileStream stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static double ErrorRate<T>(T[] reference, T[] prediction)
{
    int[] previous = Enumerable.Range(0, prediction.Length + 1).ToArray();
    int[] current = new int[prediction.Length + 1];
    for (int i = 1; i <= reference.Length; i++)
    {
        current[0] = i;
        for (int j = 1; j <= prediction.Length; j++)
            current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                previous[j - 1] + (EqualityComparer<T>.Default.Equals(reference[i - 1], prediction[j - 1]) ? 0 : 1));
        (previous, current) = (current, previous);
    }

    return reference.Length == 0 ? (prediction.Length == 0 ? 0 : 1) : (double)previous[prediction.Length] / reference.Length;
}

internal sealed class Manifest
{
    public string? PreprocessingProfile { get; set; }
    public bool AllowModelDownload { get; set; }
    public string EasyOcrCache { get; set; } = "";
    public string PaddleOcrCache { get; set; } = "";
    public InputImage[] Images { get; set; } = [];
}

internal sealed class InputImage
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Category { get; set; } = "unspecified";
    public string Split { get; set; } = "evaluation";
    public string? GroundTruth { get; set; }
    public string? GroundTruthFile { get; set; }
    public string[] EasyOcrLanguages { get; set; } = ["en"];
    public string[] PaddleOcrLanguages { get; set; } = ["English"];
}

internal sealed record AssetFingerprint(string Name, string Sha256);
internal sealed record Observation(string Id, string Category, string Split, string ImageSha256,
    string? GroundTruth, string Prediction, int Lines, double ElapsedMilliseconds, long ManagedAllocatedBytes,
    double? CharacterErrorRate, double? WordErrorRate, string? Error);

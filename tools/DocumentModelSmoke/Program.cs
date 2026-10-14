using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EasyImageSharp;
using EasyImageSharp.AI;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

// Offline opt-in integration smoke. Usage:
// dotnet run -c Release --project tools/DocumentModelSmoke -- input.png orientation.onnx UVDoc.onnx output-directory
if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: DocumentModelSmoke <input-image> <orientation-model> <dewarp-model> <output-directory>");
    return 2;
}

string inputPath = Path.GetFullPath(args[0]);
string outputDirectory = Path.GetFullPath(args[3]);
var options = new ImageAiOptions { Offline = true, ExecutionProvider = ExecutionProvider.Cpu, IntraOpNumThreads = 2 };
options.ModelPathOverrides["doc-orientation"] = Path.GetFullPath(args[1]);
options.ModelPathOverrides["doc-dewarp"] = Path.GetFullPath(args[2]);
var hashes = new Dictionary<string, string>();
foreach (ModelDescriptor model in new[] { ModelRegistry.DocumentOrientation, ModelRegistry.DocumentDewarp })
{
    string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.ModelPathOverrides[model.Name])));
    if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"{model.Name} checksum does not match the pinned registry model; this smoke expects the published export.");
        return 2;
    }

    hashes.Add(model.Name, actual);
}

using var ai = new ImageAiSession(options);
using Image<Rgb24> input = Image.Load<Rgb24>(inputPath);
string before = PixelHash(input);
var timer = Stopwatch.StartNew();
using DocumentPreprocessingResult<Rgb24> output = await input.PreprocessDocumentAIAsync(ai,
    new DocumentAiPreprocessingOptions { AutoOrient = true, Dewarp = true });
double elapsed = timer.Elapsed.TotalMilliseconds;
bool unchanged = before == PixelHash(input);
bool unavailable = !output.Pages[0].Coordinates.TryMapToSource(new PointF(20, 20), out _)
    && !output.Pages[0].Coordinates.TryMapToProcessed(new PointF(20, 20), out _);
if (!unchanged || !unavailable)
{
    throw new InvalidOperationException("Source ownership or image-only dewarp mapping contract failed.");
}

Directory.CreateDirectory(outputDirectory);
output.Image.SaveAsPng(Path.Combine(outputDirectory, "processed.png"));
var report = new
{
    SchemaVersion = 1,
    EvidenceKind = "integration-smoke",
    AccuracyEvaluation = false,
    TimingPurpose = "Single cold run including analysis and inference; not a benchmark or performance claim.",
    InputFile = Path.GetFileName(inputPath),
    InputFileSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inputPath))),
    InputWidth = input.Width,
    InputHeight = input.Height,
    OutputWidth = output.Image.Width,
    OutputHeight = output.Image.Height,
    SourceUnchanged = unchanged,
    CoordinateMapUnavailable = unavailable,
    ElapsedMs = elapsed,
    Runtime = RuntimeInformation.FrameworkDescription,
    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    ExecutionProvider = ai.ActiveProvider.ToString(),
    IntraOpNumThreads = 2,
    Models = hashes,
    Pages = output.Pages.Select(page => new
    {
        page.FrameIndex,
        Stages = page.Stages.Select(stage => new { stage.Name, stage.Applied, stage.Reason, stage.Confidence, ElapsedMs = stage.Elapsed.TotalMilliseconds }),
    }),
    OutputMeanLuminance = output.Image.AnalyzeDocument()[0].MeanLuminance,
};
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(outputDirectory, "report.json"), json + Environment.NewLine);
Console.WriteLine(json);
return 0;

static string PixelHash(Image<Rgb24> image)
{
    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (ImageFrame<Rgb24> frame in image.Frames)
    {
        for (int y = 0; y < frame.Height; y++)
        {
            hash.AppendData(MemoryMarshal.AsBytes(frame.GetRowSpan(y)));
        }
    }

    return Convert.ToHexString(hash.GetHashAndReset());
}

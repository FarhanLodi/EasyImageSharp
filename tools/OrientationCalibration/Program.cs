using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EasyImageSharp;
using EasyImageSharp.AI;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

if (args is ["--self-test"])
{
    var observations = new[]
    {
        new Observation("a", "a", "Latin", "tuning", "x", 90, 270, 270, .8, 0),
        new Observation("b", "b", "Latin", "tuning", "y", 0, 0, 90, .4, 0),
        new Observation("c", "c", "Latin", "tuning", "z", 0, 0, 0, .2, 0),
    };
    double selected = SelectThreshold(observations);
    Metrics metrics = Measure(observations, selected);
    if (selected <= .4 || selected > .8 || metrics.WrongCorrections != 0 || metrics.Accepted != 1
        || Measure(observations, 0).WrongCorrections != 1 || Measure(observations, 0).FinalUpright != 2
        || SelectThreshold([observations[1]]) <= .4)
        throw new InvalidOperationException("Threshold selection/abstention self-test failed.");
    Console.WriteLine("Orientation threshold and abstention self-tests passed.");
    return 0;
}

if (args.Length is not (2 or 3))
{
    Console.Error.WriteLine("Usage: OrientationCalibration <manifest.json> <report.json> [published-orientation.onnx] | --self-test");
    return 2;
}

string manifestPath = Path.GetFullPath(args[0]);
string root = Path.GetDirectoryName(manifestPath)!;
string? modelHash = args.Length == 3 ? Hash(args[2]) : null;
if (modelHash is not null && !modelHash.Equals(ModelRegistry.DocumentOrientation.Sha256, StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("Model checksum differs from the pinned published document-orientation model.");
var aiOptions = new ImageAiOptions { Offline = true, ExecutionProvider = ExecutionProvider.Cpu, IntraOpNumThreads = 1, InterOpNumThreads = 1 };
if (modelHash is not null) aiOptions.ModelPathOverrides[ModelRegistry.DocumentOrientation.Name] = Path.GetFullPath(args[2]);
using var ai = modelHash is null ? null : new ImageAiSession(aiOptions);
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
Manifest manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), json)
    ?? throw new InvalidDataException("Empty manifest.");
Input[] inputs = manifest.Images.Where(i => i.Category == "digital").OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();
if (inputs.Length == 0 || inputs.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != inputs.Length)
    throw new InvalidDataException("Expected nonempty digital inputs with unique ids.");
if (inputs.Any(i => string.IsNullOrWhiteSpace(i.Family) || string.IsNullOrWhiteSpace(i.Script)
    || i.Split is not ("tuning" or "held-out")))
    throw new InvalidDataException("Each digital input requires family, script and tuning/held-out split.");
if (!inputs.Any(i => i.Split == "tuning") || !inputs.Any(i => i.Split == "held-out")
    || inputs.GroupBy(i => i.Family).Any(g => g.Select(i => i.Split).Distinct().Count() != 1))
    throw new InvalidDataException("Require both splits and no family crossing splits.");

var rows = new List<Observation>();
var hashSplits = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (Input input in inputs)
{
    string path = Path.GetFullPath(Path.Combine(root, input.Path));
    string fingerprint = Hash(path);
    if (input.Sha256 is { Length: > 0 } expectedHash && !fingerprint.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"Checksum mismatch for {input.Id}.");
    if (hashSplits.TryGetValue(fingerprint, out string? previousSplit) && previousSplit != input.Split)
        throw new InvalidDataException("Identical encoded images cross splits.");
    hashSplits[fingerprint] = input.Split;
    using Image<Rgb24> source = Image.Load<Rgb24>(path);
    foreach (int rotation in new[] { 0, 90, 180, 270 })
    {
        using Image<Rgb24> rotated = source.Clone();
        rotated.Mutate(c => c.Rotate((RotateMode)rotation));
        var timer = Stopwatch.StartNew();
        OrientationEstimate estimate;
        if (ai is null)
        {
            estimate = rotated.DetectOrientation();
        }
        else
        {
            OrientationResult prediction = await rotated.DetectOrientationAsync(ai);
            estimate = new OrientationEstimate(prediction.Correction, prediction.Confidence);
        }
        timer.Stop();
        if (!float.IsFinite(estimate.Confidence) || estimate.Confidence is < 0 or > 1
            || (int)estimate.Rotation is not (0 or 90 or 180 or 270))
            throw new InvalidDataException("Detector returned an invalid orientation estimate.");
        rows.Add(new(input.Id, input.Family, input.Script, input.Split, fingerprint, rotation,
            (360 - rotation) % 360, (int)estimate.Rotation, estimate.Confidence, timer.Elapsed.TotalMilliseconds));
    }
    Console.WriteLine($"Measured four rotations: {input.Id}");
}

Observation[] tuning = rows.Where(r => r.Split == "tuning").ToArray();
Observation[] heldOut = rows.Where(r => r.Split == "held-out").ToArray();
double threshold = SelectThreshold(tuning);
var report = new
{
    SchemaVersion = 1,
    manifest.CorpusId,
    manifest.Source,
    manifest.Revision,
    manifest.License,
    ManifestSha256 = Hash(manifestPath),
    Detector = ai is null ? "EasyImageSharp classical DetectOrientation" : "PP-LCNet_x1_0_doc_ori via EasyImageSharp.AI",
    ModelSha256 = modelHash,
    AiLibrarySha256 = ai is null ? null : Hash(typeof(ImageAiSession).Assembly.Location),
    ExecutionProvider = ai?.ActiveProvider.ToString(),
    IntraOpNumThreads = ai is null ? (int?)null : 1,
    InterOpNumThreads = ai is null ? (int?)null : 1,
    LibraryVersion = typeof(Image).Assembly.GetName().Version?.ToString(),
    LibrarySha256 = Hash(typeof(Image).Assembly.Location),
    Runtime = RuntimeInformation.FrameworkDescription,
    Threshold = threshold,
    AcceptanceRule = "Confidence >= threshold; rejection leaves supplied image unchanged",
    SelectionRule = "Tuning only: minimize wrong applied nonzero corrections; then maximize accepted estimates; then lowest threshold",
    Limitations = new[]
    {
        "Detector score (heuristic or model softmax); threshold selection does not calibrate confidence as empirical correctness probability.",
        "Digital source pages are assumed upright. This must be audited against the dataset before deployment.",
        "Four rotations from each page are dependent observations, not independent samples.",
        "Public multilingual subset, not a representative deployment benchmark; no confidence intervals claimed.",
        "Baseline 0 accepts every estimate; a rejected estimate preserves original orientation and may remain wrong.",
        "Timing covers detection only and includes first-use JIT/model loading; this is not a throughput benchmark.",
    },
    Tuning = Compare(tuning, threshold),
    HeldOut = Compare(heldOut, threshold),
    ByScript = rows.GroupBy(r => (r.Split, r.Script)).Select(g => new { g.Key.Split, g.Key.Script, Results = Compare(g.ToArray(), threshold) }),
    Rows = rows.Select(r => new
    {
        r.Id, r.Family, r.Script, r.Split, r.Sha256, r.AppliedRotation, r.ExpectedCorrection,
        r.PredictedCorrection, r.Confidence, r.LatencyMs,
        Correct = r.PredictedCorrection == r.ExpectedCorrection,
        Accepted = r.Confidence >= threshold,
        AppliedCorrection = r.Confidence >= threshold ? r.PredictedCorrection : 0,
        FinalUpright = (r.AppliedRotation + (r.Confidence >= threshold ? r.PredictedCorrection : 0)) % 360 == 0,
    }),
};
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, json) + Environment.NewLine);
Console.WriteLine($"Selected threshold {threshold:R}; tuning and held-out summaries written to {Path.GetFileName(output)}.");
return 0;

static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static object Compare(Observation[] rows, double threshold) => new
{
    BasePages = rows.Select(r => r.Id).Distinct().Count(),
    Families = rows.Select(r => r.Family).Distinct().Count(),
    BaselineAcceptAll = Measure(rows, 0),
    SelectedThreshold = Measure(rows, threshold),
    AbstainAll = Measure(rows, Math.BitIncrement(1d)),
};

static double SelectThreshold(Observation[] tuning)
{
    // Crossing a confidence value is necessary because acceptance is inclusive.
    double[] candidates = tuning.Select(r => (double)MathF.BitIncrement((float)r.Confidence)).Append(0).Append(MathF.BitIncrement(1f)).Distinct().ToArray();
    return candidates.Select(t => (Threshold: t, Metrics: Measure(tuning, t)))
        .OrderBy(p => p.Metrics.WrongCorrections).ThenByDescending(p => p.Metrics.Accepted)
        .ThenBy(p => p.Threshold).First().Threshold;
}

static Metrics Measure(Observation[] rows, double threshold)
{
    Observation[] accepted = rows.Where(r => r.Confidence >= threshold).ToArray();
    int corrections = accepted.Count(r => r.PredictedCorrection != 0);
    int wrong = accepted.Count(r => r.PredictedCorrection != 0 && r.PredictedCorrection != r.ExpectedCorrection);
    int correctAccepted = accepted.Count(r => r.PredictedCorrection == r.ExpectedCorrection);
    int finalUpright = rows.Count(r => (r.AppliedRotation + (r.Confidence >= threshold ? r.PredictedCorrection : 0)) % 360 == 0);
    return new(rows.Length, accepted.Length, accepted.Length / (double)rows.Length, correctAccepted,
        accepted.Length - correctAccepted, corrections, wrong, corrections == 0 ? null : wrong / (double)corrections,
        finalUpright, finalUpright / (double)rows.Length);
}

sealed record Manifest(string CorpusId, Input[] Images, string? Source, string? Revision, string? License);
sealed record Input(string Id, string Path, string Category, string Split, string Family, string Script, string? Sha256);
sealed record Observation(string Id, string Family, string Script, string Split, string Sha256,
    int AppliedRotation, int ExpectedCorrection, int PredictedCorrection, double Confidence, double LatencyMs);
sealed record Metrics(int Samples, int Accepted, double Coverage, int CorrectAccepted, int WrongAccepted,
    int AppliedCorrections, int WrongCorrections, double? WrongCorrectionRate, int FinalUpright, double FinalUprightRate);

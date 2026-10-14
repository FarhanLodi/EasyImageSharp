using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using EasyImageSharp;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;

if (args is ["worker", string workload, string parallel, string repeats, string input])
{
    int concurrency = int.Parse(parallel), iterations = int.Parse(repeats);
    if (concurrency is < 1 or > 64 || iterations is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(args));
    using Image<Rgb24> source = input == "synthetic" ? MakeImage() : Image.Load<Rgb24>(input);
    using var stream = new MemoryStream();
    source.Save(stream, new JpegEncoder());
    byte[] jpeg = input != "synthetic" && new[] { ".jpg", ".jpeg" }.Contains(Path.GetExtension(input).ToLowerInvariant())
        ? File.ReadAllBytes(input) : stream.ToArray();
    var execution = new ImageExecutionOptions { Configuration = new Configuration { MaxDegreeOfParallelism = 1 } };
    var tensorOptions = new TensorWriteOptions { ExecutionOptions = execution };
    var resize = new ResizeOptions { Size = new Size(960, 960), Mode = ResizeMode.Pad };
    float[][] destinations = Enumerable.Range(0, concurrency).Select(_ => new float[960 * 960 * 3]).ToArray();
    long[] checksums = new long[concurrency];
    double[][] durations = Enumerable.Range(0, concurrency).Select(_ => new double[iterations]).ToArray();
    void Operation(int worker)
    {
        if (workload == "fused")
        {
            source.ResizeToTensor(destinations[worker], resize, tensorOptions);
            checksums[worker] += BitConverter.SingleToInt32Bits(destinations[worker][480 * 960 + 480]);
        }
        else if (workload == "materialized")
        {
            using Image<Rgb24> resized = source.Clone(execution, context => context.Resize(resize));
            resized.WriteTensor(destinations[worker], tensorOptions);
            checksums[worker] += BitConverter.SingleToInt32Bits(destinations[worker][480 * 960 + 480]);
        }
        else if (workload.StartsWith("jpeg", StringComparison.Ordinal))
        {
            int denominator = int.Parse(workload[4..]);
            using Image<Rgb24> decoded = new JpegDecoder().DecodeReduced<Rgb24>(jpeg, denominator, new DecoderOptions { ExecutionOptions = execution });
            checksums[worker] += decoded.Width * decoded.Height + decoded[0, 0].R;
        }
        else throw new ArgumentException("Unknown workload.");
    }

    // Warm each buffer and the runtime before collecting timings. Every request uses one library worker.
    var warmup = Stopwatch.StartNew();
    int warmupOperations = 0;
    do
    {
        Operation(warmupOperations % concurrency);
        warmupOperations++;
    }
    while (warmupOperations < concurrency * 4 || warmup.Elapsed < TimeSpan.FromSeconds(2));
    warmup.Stop();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    using Process process = Process.GetCurrentProcess();
    process.Refresh();
    long baselineWorkingSet = process.WorkingSet64, peakWorkingSet = baselineWorkingSet;
    int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    using var ready = new CountdownEvent(concurrency);
    using var start = new ManualResetEventSlim();
    Task[] workers = Enumerable.Range(0, concurrency).Select(w => Task.Factory.StartNew(() =>
    {
        ready.Signal(); start.Wait();
        for (int i = 0; i < iterations; i++)
        {
            long begin = Stopwatch.GetTimestamp();
            Operation(w);
            durations[w][i] = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
    ready.Wait();
    long allocatedBefore = GC.GetTotalAllocatedBytes(true);
    long measuredStart = Stopwatch.GetTimestamp();
    start.Set();
    Task completion = Task.WhenAll(workers);
    while (!completion.IsCompleted)
    {
        process.Refresh(); peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        await Task.WhenAny(completion, Task.Delay(10));
    }
    await completion;
    double elapsed = Stopwatch.GetElapsedTime(measuredStart).TotalSeconds;
    long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
    process.Refresh(); peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
    double[] samples = durations.SelectMany(row => row).Order().ToArray();
    int[] measuredCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
    long lifetimePeakWorkingSet = process.PeakWorkingSet64;
    double Percentile(double p) => samples[Math.Clamp((int)Math.Ceiling(samples.Length * p) - 1, 0, samples.Length - 1)];
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Workload = workload, Concurrency = concurrency, IterationsPerWorker = iterations,
        WarmupOperations = warmupOperations, WarmupSeconds = warmup.Elapsed.TotalSeconds,
        InputKind = input == "synthetic" ? "deterministic synthetic gradient/checker, not an accuracy corpus" : "local file",
        SourceWidth = source.Width, SourceHeight = source.Height,
        EncodedJpegSha256 = Convert.ToHexString(SHA256.HashData(jpeg)),
        CoreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Image).Assembly.Location))),
        ToolSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location))),
        Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        ProcessorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
        ProcessorCount = Environment.ProcessorCount, LibraryThreadsPerRequest = 1,
        Requests = samples.Length, ElapsedSeconds = elapsed, RequestsPerSecond = samples.Length / elapsed,
        LatencyP50Ms = Percentile(0.50), LatencyP95Ms = Percentile(0.95), LatencyP99Ms = Percentile(0.99),
        ManagedAllocatedBytes = allocated, ManagedAllocatedBytesPerRequest = (double)allocated / samples.Length,
        BaselineWorkingSetBytes = baselineWorkingSet, SampledPeakWorkingSetBytes = peakWorkingSet,
        ProcessLifetimePeakWorkingSetBytes = lifetimePeakWorkingSet,
        GcCollections = measuredCollections,
        Checksum = checksums.Sum(),
    }));
    return;
}

if (args.Length is < 1 or > 3)
    throw new ArgumentException("Usage: CoreThroughput output.json [iterations-per-worker=20] [input-image|synthetic]");
string destination = Path.GetFullPath(args[0]);
string count = args.Length > 1 ? args[1] : "20";
string imageInput = args.Length > 2 && args[2] != "synthetic" ? Path.GetFullPath(args[2]) : "synthetic";
var results = new List<JsonElement>();
foreach (string work in new[] { "materialized", "fused", "jpeg1", "jpeg2", "jpeg4", "jpeg8" })
foreach (int concurrent in new[] { 1, 2, 4 })
{
    var launch = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(launch.FileName), "dotnet", StringComparison.OrdinalIgnoreCase)) launch.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (string value in new[] { "worker", work, concurrent.ToString(), count, imageInput }) launch.ArgumentList.Add(value);
    using Process child = Process.Start(launch)!;
    Task<string> output = child.StandardOutput.ReadToEndAsync(), errors = child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync();
    if (child.ExitCode != 0) throw new InvalidOperationException(await errors);
    using JsonDocument report = JsonDocument.Parse(await output);
    results.Add(report.RootElement.Clone());
    Console.Error.WriteLine($"{work} concurrency={concurrent}: {report.RootElement.GetProperty("RequestsPerSecond").GetDouble():F2} requests/s");
}
Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
File.WriteAllText(destination, JsonSerializer.Serialize(new
{
    GeneratedUtc = DateTimeOffset.UtcNow,
    Method = "Each case runs in a fresh process, warms at least four operations per worker and two seconds per case, then measures concurrent requests. Allocation totals include small harness/sampling overhead. Working set sampled every 10ms can miss short peaks; lifetime peak includes startup/warmup. Results are diagnostics, not accuracy or a hard memory cap.",
    Results = results,
}, new JsonSerializerOptions { WriteIndented = true }));

static Image<Rgb24> MakeImage()
{
    var image = new Image<Rgb24>(1200, 800);
    for (int y = 0; y < image.Height; y++)
    for (int x = 0; x < image.Width; x++)
        image[x, y] = new Rgb24((byte)((x * 3 + y) & 255), (byte)((x + y * 5) & 255), (byte)(((x / 16) ^ (y / 16)) % 2 * 180));
    return image;
}

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EasyImageSharp.Benchmarks;

/// <summary>
/// Turns a completed BenchmarkDotNet run into the exact Markdown table the root README quotes, so that
/// "the README's Performance numbers" is a thing a machine produces rather than a thing somebody typed.
/// <para>
/// It reads the <c>*-report-full.json</c> files BenchmarkDotNet's JSON exporter writes under
/// <c>&lt;artifacts&gt;/results</c>. The JSON is used rather than the CSV because its Mean, Median and
/// BytesAllocatedPerOperation are plain numbers - nanoseconds and bytes - while the CSV carries
/// culture-formatted strings with unit suffixes that would have to be parsed back.
/// </para>
/// <para>
/// The seven rows are addressed by (type, method, parameters). If any one of them is absent the table is
/// not printed at all and the process exits non-zero, so a renamed benchmark is a loud failure instead of a
/// README row that silently disappears.
/// </para>
/// </summary>
internal static class ReadmeTable
{
    /// <summary>Where the finished table is committed. Raw BenchmarkDotNet output is not.</summary>
    private const string OutputPath = "benchmarks/results/README-performance.md";

    private static readonly Row[] Rows =
    [
        new("JPEG decode", "3032x2008 -> Rgba32", nameof(DecodeBenchmarks), nameof(DecodeBenchmarks.Decode), "Format=jpeg"),
        new("PNG decode", "3032x2008 -> Rgba32", nameof(DecodeBenchmarks), nameof(DecodeBenchmarks.Decode), "Format=png"),
        new("Resize, bicubic x0.5", "3032x2008 Rgba32", nameof(ResizeBenchmarks), nameof(ResizeBenchmarks.BicubicHalfRgba32), ""),
        new("Resize, bicubic x0.5", "3032x2008 L8", nameof(ResizeBenchmarks), nameof(ResizeBenchmarks.BicubicHalfL8), ""),
        new("Grayscale, in place", "A4 at 300 DPI, L8", nameof(ProcessingBenchmarks), nameof(ProcessingBenchmarks.Grayscale), ""),
        new("Otsu threshold, in place", "A4 at 300 DPI, L8", nameof(ProcessingBenchmarks), nameof(ProcessingBenchmarks.OtsuThreshold), ""),
        new("Load -> resize -> save", "20 JPEGs", nameof(PipelineBenchmarks), nameof(PipelineBenchmarks.LoadResizeSave), ""),
    ];

    /// <summary>Reads the run under <paramref name="artifactsPath"/> and writes the table. Returns a process exit code.</summary>
    public static int Emit(string artifactsPath)
    {
        ArgumentNullException.ThrowIfNull(artifactsPath);

        string resultsDirectory = Path.Combine(artifactsPath, "results");
        if (!Directory.Exists(resultsDirectory))
        {
            Console.Error.WriteLine(
                $"::error:: No BenchmarkDotNet results under {resultsDirectory}. Run the benchmarks first: " +
                "dotnet run -c Release -f net10.0 --project benchmarks/EasyImageSharp.Benchmarks -- --filter \"*\"");
            return 1;
        }

        List<Result> results = [];
        string machine = string.Empty;
        foreach (string file in Directory.GetFiles(resultsDirectory, "*-report-full.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
            JsonElement root = document.RootElement;
            if (machine.Length == 0 && root.TryGetProperty("HostEnvironmentInfo", out JsonElement host))
            {
                machine = DescribeMachine(host);
            }

            if (!root.TryGetProperty("Benchmarks", out JsonElement benchmarks))
            {
                continue;
            }

            foreach (JsonElement benchmark in benchmarks.EnumerateArray())
            {
                Result? result = ReadResult(benchmark);
                if (result is not null)
                {
                    results.Add(result);
                }
            }
        }

        var missing = new List<string>();
        var lines = new List<string>();
        foreach (Row row in Rows)
        {
            Result? match = results.Find(r => r.Type == row.Type && r.Method == row.Method && r.Parameters == row.Parameters);
            if (match is null)
            {
                missing.Add(row.Parameters.Length == 0
                    ? $"{row.Type}.{row.Method}"
                    : $"{row.Type}.{row.Method} [{row.Parameters}]");
                continue;
            }

            lines.Add($"| {row.Label} | {row.Input} | {FormatTime(row, match.MeanNanoseconds)} | {FormatBytes(match.BytesPerOperation)} |");
        }

        if (missing.Count > 0)
        {
            foreach (string name in missing)
            {
                Console.Error.WriteLine(
                    $"::error:: The README table needs {name}, and the run under {resultsDirectory} does not " +
                    "contain it. Either the benchmark was renamed and ReadmeTable.Rows was not updated, or the " +
                    "run was filtered. Re-run with --filter \"*\".");
            }

            return 1;
        }

        var table = new StringBuilder();
        table.Append(machine.Length == 0 ? "BenchmarkDotNet, Release." : machine).Append('\n').Append('\n');
        table.Append("| Operation | Input | Time | Allocated |\n");
        table.Append("|---|---|---:|---:|\n");
        foreach (string line in lines)
        {
            table.Append(line).Append('\n');
        }

        string markdown = table.ToString();
        Console.Write(markdown);

        string outputPath = Corpus.RepoRelative(OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, Header() + markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine();
        Console.WriteLine($"written to {outputPath}");
        return 0;
    }

    private static string Header() =>
        "<!-- Generated by `dotnet run -c Release -f net10.0 --project benchmarks/EasyImageSharp.Benchmarks -- --readme-table`.\n" +
        "     Do not edit by hand: re-run the benchmarks and regenerate. See benchmarks/README.md. -->\n\n" +
        "# Performance\n\n";

    private static Result? ReadResult(JsonElement benchmark)
    {
        if (!benchmark.TryGetProperty("Statistics", out JsonElement statistics) ||
            statistics.ValueKind != JsonValueKind.Object ||
            !statistics.TryGetProperty("Mean", out JsonElement mean))
        {
            return null;
        }

        string type = benchmark.TryGetProperty("Type", out JsonElement t) ? (t.GetString() ?? string.Empty) : string.Empty;
        string method = benchmark.TryGetProperty("Method", out JsonElement m) ? (m.GetString() ?? string.Empty) : string.Empty;
        string parameters = benchmark.TryGetProperty("Parameters", out JsonElement p) ? (p.GetString() ?? string.Empty) : string.Empty;

        long bytes = 0;
        if (benchmark.TryGetProperty("Memory", out JsonElement memory) &&
            memory.ValueKind == JsonValueKind.Object &&
            memory.TryGetProperty("BytesAllocatedPerOperation", out JsonElement allocated) &&
            allocated.ValueKind == JsonValueKind.Number)
        {
            bytes = allocated.GetInt64();
        }

        return new Result(type, method, parameters, mean.GetDouble(), bytes);
    }

    private static string DescribeMachine(JsonElement host)
    {
        string caption = Text(host, "BenchmarkDotNetCaption");
        string version = Text(host, "BenchmarkDotNetVersion");
        string processor = Text(host, "ProcessorName");
        string cores = host.TryGetProperty("PhysicalCoreCount", out JsonElement count) && count.ValueKind == JsonValueKind.Number
            ? $"{count.GetInt32()}-core "
            : string.Empty;
        string runtime = Text(host, "RuntimeVersion");
        string configuration = Text(host, "Configuration");

        var line = new StringBuilder();
        line.Append(caption.Length == 0 ? "BenchmarkDotNet" : caption);
        if (version.Length > 0)
        {
            line.Append(' ').Append('v').Append(version);
        }

        if (processor.Length > 0)
        {
            line.Append(", ").Append(cores).Append(processor);
        }

        if (runtime.Length > 0)
        {
            line.Append(", ").Append(runtime);
        }

        line.Append(", ").Append(configuration.Length == 0 ? "Release" : Capitalise(configuration)).Append('.');
        return line.ToString();
    }

    private static string Capitalise(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string FormatTime(Row row, double nanoseconds)
    {
        double milliseconds = nanoseconds / 1_000_000.0;
        string time = milliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms";

        // The pipeline row is quoted per image and with the throughput that follows from it; every other row
        // is a single operation, where a rate would mean nothing.
        if (row.Method != nameof(PipelineBenchmarks.LoadResizeSave))
        {
            return time;
        }

        double perSecond = milliseconds <= 0.0 ? 0.0 : 1000.0 / milliseconds;
        return $"{time} each ({perSecond.ToString("0", CultureInfo.InvariantCulture)} img/s)";
    }

    private static string FormatBytes(long bytes)
    {
        const double Kilobyte = 1024.0;
        const double Megabyte = 1024.0 * 1024.0;

        if (bytes >= Megabyte)
        {
            return (bytes / Megabyte).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        double kilobytes = bytes / Kilobyte;
        string format = kilobytes < 10.0 ? "0.0" : "0";
        return kilobytes.ToString(format, CultureInfo.InvariantCulture) + " KB";
    }

    private sealed record Row(string Label, string Input, string Type, string Method, string Parameters);

    private sealed record Result(string Type, string Method, string Parameters, double MeanNanoseconds, long BytesPerOperation);
}

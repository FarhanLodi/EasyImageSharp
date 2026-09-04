using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace EasyImageSharp.Benchmarks;

/// <summary>
/// Entry point for the benchmark suite. The whole reproduction sequence, from a clean checkout:
/// <code>
/// python -m pip install "pillow&gt;=11" "numpy&gt;=2"
/// python benchmarks/corpus/generate.py
/// dotnet run -c Release -f net10.0 --project benchmarks/EasyImageSharp.Benchmarks -- --filter "*"
/// dotnet run -c Release -f net10.0 --project benchmarks/EasyImageSharp.Benchmarks -- --readme-table
/// </code>
/// <c>--filter</c>, <c>--job</c>, <c>--list</c> and everything else BenchmarkDotNet understands are passed
/// straight through; <c>--job Dry</c> runs every benchmark exactly once, which proves the suite works
/// without measuring anything. The one argument handled here is <c>--readme-table</c>, which runs no
/// benchmarks and instead turns the last completed run into the Markdown table the root README quotes.
/// <para>
/// The artifacts path is pinned under <c>benchmarks/</c> for two reasons: it is already gitignored, and it
/// puts BenchmarkDotNet's auto-generated project inside the scope of benchmarks/Directory.Build.props,
/// which is what exempts that generated code from this repository's warnings-as-errors.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>The argument that prints the README table instead of running anything.</summary>
    private const string ReadmeTableArgument = "--readme-table";

    private static readonly string[] InformationalArguments = ["--help", "-h", "--version", "--list", "--info"];

    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string artifactsPath = Corpus.RepoRelative("benchmarks/BenchmarkDotNet.Artifacts");

        if (Array.Exists(args, a => string.Equals(a, ReadmeTableArgument, StringComparison.Ordinal)))
        {
            return ReadmeTable.Emit(artifactsPath);
        }

        // A missing corpus is reported once, here, with the command that fixes it. Left to the benchmarks it
        // would surface as one FileNotFoundException per [GlobalSetup], forty times over, none of them saying
        // what to do about it.
        if (!Array.Exists(args, a => Array.Exists(InformationalArguments, k => string.Equals(a, k, StringComparison.Ordinal))))
        {
            Corpus.EnsurePresent();
        }

        ManualConfig config = ManualConfig.Create(DefaultConfig.Instance)
            .AddDiagnoser(MemoryDiagnoser.Default)

            // DefaultConfig already exports GitHub-flavoured Markdown; adding it again makes BenchmarkDotNet
            // report a duplicate-exporter config issue. Only the JSON exporter has to be asked for, and it is
            // what --readme-table reads.
            .AddExporter(JsonExporter.Full)
            .AddColumn(StatisticColumn.Median)
            .WithArtifactsPath(artifactsPath);

        IEnumerable<Summary> summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);

        // BenchmarkDotNet reports a benchmark that threw as a failed report and still exits zero, which in CI
        // would make a suite that never ran look like a suite that passed.
        int failures = 0;
        foreach (Summary summary in summaries)
        {
            if (summary.HasCriticalValidationErrors)
            {
                failures++;
                continue;
            }

            foreach (BenchmarkReport report in summary.Reports)
            {
                if (!report.Success)
                {
                    Console.Error.WriteLine($"::error:: {report.BenchmarkCase.DisplayInfo} did not complete.");
                    failures++;
                }
            }
        }

        return failures == 0 ? 0 : 1;
    }
}

using System.Diagnostics;
using System.Globalization;

namespace EasyImageSharp.Fuzz;

/// <summary>
/// Runs a target over files on disk without libFuzzer: the way to reproduce a crash, to check a fix, and to
/// smoke-test the seed corpus on a machine (or a CI leg) that has no libFuzzer driver at all. The code under
/// test is identical to what libFuzzer drives; only the input source differs.
/// </summary>
internal static class Replay
{
    /// <summary>Exit code when every input honoured the contract.</summary>
    public const int ExitClean = 0;

    /// <summary>Exit code when at least one input violated the contract.</summary>
    public const int ExitViolation = 1;

    /// <summary>Exit code when an input ran past the deadline (the process cannot abort the call, so it stops there).</summary>
    public const int ExitTimeout = 3;

    /// <summary>
    /// Per-input deadline. Matches the 30 seconds <c>FuzzSmokeTests</c> allows: a hang detector, not a speed
    /// limit. Overridden by <c>EASYIMAGESHARP_FUZZ_TIMEOUT_S</c>.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("EASYIMAGESHARP_FUZZ_TIMEOUT_S"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > 0
            ? s
            : 30);

    /// <summary>Replays every file under <paramref name="paths"/> (directories recursively) through <paramref name="target"/>.</summary>
    public static int Run(FuzzTarget target, IEnumerable<string> paths, TextWriter output)
    {
        List<string> files = Expand(paths);
        if (files.Count == 0)
        {
            output.WriteLine($"{target.Name}: no input files found.");
            return ExitClean;
        }

        int violations = 0;
        TimeSpan slowest = TimeSpan.Zero;
        string slowestFile = string.Empty;
        long[] before = FuzzContract.OutcomeCounts.ToArray();
        var total = Stopwatch.StartNew();

        foreach (string file in files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    target.Run(bytes);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            })
            {
                IsBackground = true,
                Name = "fuzz-replay",
            };

            var watch = Stopwatch.StartNew();
            thread.Start();
            if (!thread.Join(Deadline))
            {
                output.WriteLine($"TIMEOUT {target.Name} {file}: still running after {Deadline.TotalSeconds:F0} s ({bytes.Length} bytes).");
                output.Flush();
                return ExitTimeout;
            }

            watch.Stop();
            if (watch.Elapsed > slowest)
            {
                slowest = watch.Elapsed;
                slowestFile = file;
            }

            if (failure is not null)
            {
                violations++;
                output.WriteLine($"CRASH {target.Name} {file} ({bytes.Length} bytes)");
                output.WriteLine("  " + failure.ToString().ReplaceLineEndings(Environment.NewLine + "  "));
            }
        }

        total.Stop();
        IReadOnlyList<long> after = FuzzContract.OutcomeCounts;
        string outcomes = string.Join(", ", Enum.GetValues<CallOutcome>().Select(o => $"{o}={after[(int)o] - before[(int)o]}"));
        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1} input(s) in {2:F1} s, {3} violation(s); calls: {4}; slowest {5:F0} ms ({6})",
            target.Name,
            files.Count,
            total.Elapsed.TotalSeconds,
            violations,
            outcomes,
            slowest.TotalMilliseconds,
            slowestFile));
        return violations == 0 ? ExitClean : ExitViolation;
    }

    private static List<string> Expand(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith('.'))
                    .Order(StringComparer.Ordinal));
            }
            else if (File.Exists(path))
            {
                files.Add(path);
            }
            else
            {
                throw new FileNotFoundException($"Replay input '{path}' does not exist.", path);
            }
        }

        return files;
    }
}

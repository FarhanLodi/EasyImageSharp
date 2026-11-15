namespace EasyImageSharp.Fuzz;

/// <summary>
/// Entry point. Under libfuzzer-dotnet the process is started with the target name as its only argument
/// (<c>--target_arg=png</c>), or with no argument and <c>EASYIMAGESHARP_FUZZ_TARGET</c> set when the driver
/// launches <c>dotnet EasyImageSharp.Fuzz.dll</c>; it then serves inputs until libFuzzer stops it. Outside
/// libFuzzer, any further arguments are files or directories to replay.
/// </summary>
internal static class Program
{
    /// <summary>The environment variable libfuzzer-dotnet sets for its child; its presence means "fuzzing".</summary>
    private const string LibFuzzerSharedMemoryVariable = "__LIBFUZZER_SHM_ID";

    private const string TargetVariable = "EASYIMAGESHARP_FUZZ_TARGET";

    private const int ExitUsage = 2;

    public static int Main(string[] args)
    {
        // Before anything can reach instrumented library code; see TraceMap for why this cannot wait.
        TraceMap.InstallScratch();

        if (args.Length == 1 && args[0] is "list" or "--list")
        {
            foreach (FuzzTarget t in FuzzTargets.All)
            {
                string dictionaries = t.Dictionaries.Length == FuzzTargets.AllDictionaries.Length ? "all" : string.Join('+', t.Dictionaries);
                Console.WriteLine($"{t.Name,-16} dict: {dictionaries,-12} {t.Description}");
            }

            return Replay.ExitClean;
        }

        if (args.Length >= 1 && args[0] == "all")
        {
            return ReplayAll(args.Skip(1).ToArray());
        }

        string? name = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable(TargetVariable);
        if (string.IsNullOrEmpty(name) || args is [("-h" or "--help" or "help"), ..])
        {
            return Usage(name is null or "" ? null : "help requested");
        }

        FuzzTarget? target = FuzzTargets.Find(name);
        if (target is null)
        {
            return Usage($"unknown target '{name}'");
        }

        string[] inputs = args.Length > 0 ? args[1..] : Array.Empty<string>();
        if (Environment.GetEnvironmentVariable(LibFuzzerSharedMemoryVariable) is not null)
        {
            // Never returns while libFuzzer is driving: it reports each input's status over the IPC pipe and
            // exits after the first crash, exactly as a native libFuzzer target does.
            SharpFuzz.Fuzzer.LibFuzzer.Run(target.Run);
            return Replay.ExitClean;
        }

        if (inputs.Length == 0)
        {
            return Usage($"'{target.Name}' is not running under libfuzzer-dotnet, so it needs files or directories to replay");
        }

        return Replay.Run(target, inputs, Console.Out);
    }

    /// <summary><c>all &lt;root&gt;</c>: replays <c>&lt;root&gt;/&lt;target&gt;/</c> for every target that has one.</summary>
    private static int ReplayAll(string[] roots)
    {
        if (roots.Length != 1 || !Directory.Exists(roots[0]))
        {
            return Usage("'all' takes one directory containing a sub-directory per target (the layout fuzz/scripts/build_corpus.py writes)");
        }

        int worst = Replay.ExitClean;
        int replayed = 0;
        foreach (FuzzTarget target in FuzzTargets.All)
        {
            string directory = Path.Combine(roots[0], target.Name);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            replayed++;
            int code = Replay.Run(target, new[] { directory }, Console.Out);
            if (code == Replay.ExitTimeout)
            {
                return code;
            }

            worst = Math.Max(worst, code);
        }

        if (replayed == 0)
        {
            return Usage($"'{roots[0]}' has no per-target sub-directories");
        }

        return worst;
    }

    private static int Usage(string? problem)
    {
        if (problem is not null)
        {
            Console.Error.WriteLine("error: " + problem);
            Console.Error.WriteLine();
        }

        Console.Error.WriteLine("""
            EasyImageSharp.Fuzz - SharpFuzz/libFuzzer harness for the EasyImageSharp decoders

              EasyImageSharp.Fuzz list                       list the targets and their dictionaries
              EasyImageSharp.Fuzz <target>                   serve libFuzzer (run it through libfuzzer-dotnet)
              EasyImageSharp.Fuzz <target> <file|dir>...     replay inputs without libFuzzer
              EasyImageSharp.Fuzz all <corpus-root>          replay <corpus-root>/<target>/ for every target

            With no argument the target is read from EASYIMAGESHARP_FUZZ_TARGET, for drivers that launch
            `dotnet EasyImageSharp.Fuzz.dll` and can pass only that one argument.
            Exit codes: 0 clean, 1 contract violation, 2 usage, 3 an input ran past the replay deadline.
            See fuzz/README.md.
            """);
        return ExitUsage;
    }
}

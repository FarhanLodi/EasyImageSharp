using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>
/// Turns a requested <see cref="ExecutionProvider"/> into the concrete provider that will actually be used and
/// the matching ONNX Runtime <see cref="SessionOptions"/>. Only one ONNX Runtime native package can be
/// referenced by an application, so the set of usable accelerators is fixed at build time; the resolver asks
/// the loaded runtime what it contains and never guesses a provider whose native code is absent.
/// </summary>
internal static class ExecutionProviderResolver
{
    private const string CudaName = "CUDAExecutionProvider";
    private const string DmlName = "DmlExecutionProvider";
    private const string CoreMlName = "CoreMLExecutionProvider";

    /// <summary>
    /// Resolves <see cref="ExecutionProvider.Auto"/> to the best accelerator the loaded runtime reports for this
    /// OS (or CPU). Explicit requests are returned unchanged; attaching them may still fall back to CPU.
    /// </summary>
    public static ExecutionProvider Resolve(ExecutionProvider requested, Action<string>? log)
    {
        if (requested != ExecutionProvider.Auto)
        {
            return requested;
        }

        string[] available;
        try
        {
            available = OrtEnv.Instance().GetAvailableProviders();
        }
        catch (Exception ex)
        {
            log?.Invoke($"Auto-detect: could not query ONNX Runtime providers ({ex.Message}); using CPU.");
            return ExecutionProvider.Cpu;
        }

        foreach (ExecutionProvider candidate in CandidatesForCurrentOs())
        {
            if (Array.IndexOf(available, NativeName(candidate)) >= 0)
            {
                log?.Invoke($"Auto-detect: ONNX Runtime providers [{string.Join(", ", available)}] -> {candidate}.");
                return candidate;
            }
        }

        log?.Invoke($"Auto-detect: no accelerated provider in the loaded runtime [{string.Join(", ", available)}]; using CPU.");
        return ExecutionProvider.Cpu;
    }

    /// <summary>
    /// Builds session options for a concrete provider. Attaching a GPU provider that is missing or fails to
    /// initialise logs the reason and leaves the options on CPU; the provider actually in effect is returned.
    /// </summary>
    public static SessionOptions BuildSessionOptions(ExecutionProvider provider, ImageAiOptions options, out ExecutionProvider active)
    {
        var sessionOptions = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        try
        {
            if (options.IntraOpNumThreads is { } intra and > 0)
            {
                sessionOptions.IntraOpNumThreads = intra;
            }

            if (options.InterOpNumThreads is { } inter and > 0)
            {
                sessionOptions.InterOpNumThreads = inter;
            }

            active = ExecutionProvider.Cpu;
            switch (provider)
            {
                case ExecutionProvider.Cuda:
                    if (TryAppend(options.Log, "CUDA", "Microsoft.ML.OnnxRuntime.Gpu", () => sessionOptions.AppendExecutionProvider_CUDA(options.DeviceId)))
                    {
                        active = ExecutionProvider.Cuda;
                    }

                    break;

                case ExecutionProvider.DirectML:
                    // DirectML requires sequential execution with memory patterns disabled.
                    sessionOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    sessionOptions.EnableMemoryPattern = false;
                    if (TryAppend(options.Log, "DirectML", "Microsoft.ML.OnnxRuntime.DirectML", () => sessionOptions.AppendExecutionProvider_DML(options.DeviceId)))
                    {
                        active = ExecutionProvider.DirectML;
                    }

                    break;

                case ExecutionProvider.CoreML:
                    if (TryAppend(options.Log, "CoreML", "a CoreML-enabled ONNX Runtime build (macOS)", () => sessionOptions.AppendExecutionProvider("CoreML")))
                    {
                        active = ExecutionProvider.CoreML;
                    }

                    break;

                default:
                    break;
            }

            return sessionOptions;
        }
        catch
        {
            sessionOptions.Dispose();
            throw;
        }
    }

    private static bool TryAppend(Action<string>? log, string name, string package, Action append)
    {
        try
        {
            append();
            log?.Invoke($"ONNX Runtime: {name} execution provider enabled.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"ONNX Runtime: {name} execution provider unavailable ({ex.Message}); falling back to CPU. Reference {package} to enable it.");
            return false;
        }
    }

    private static ExecutionProvider[] CandidatesForCurrentOs()
    {
        if (OperatingSystem.IsWindows())
        {
            return [ExecutionProvider.DirectML, ExecutionProvider.Cuda];
        }

        if (OperatingSystem.IsMacOS())
        {
            return [ExecutionProvider.CoreML];
        }

        if (OperatingSystem.IsLinux())
        {
            return [ExecutionProvider.Cuda];
        }

        return [];
    }

    private static string NativeName(ExecutionProvider provider) => provider switch
    {
        ExecutionProvider.Cuda => CudaName,
        ExecutionProvider.DirectML => DmlName,
        ExecutionProvider.CoreML => CoreMlName,
        _ => "CPUExecutionProvider",
    };
}

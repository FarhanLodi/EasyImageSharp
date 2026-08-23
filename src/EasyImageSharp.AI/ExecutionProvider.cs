namespace EasyImageSharp.AI;

/// <summary>
/// Which ONNX Runtime execution provider an <see cref="ImageAiSession"/> should try to use. Every non-CPU
/// provider needs the matching ONNX Runtime native package referenced by the application (only one native
/// package can be referenced at a time: <c>Microsoft.ML.OnnxRuntime</c> is CPU-only, <c>.Gpu</c> adds CUDA,
/// <c>.DirectML</c> adds DirectML, CoreML ships in the base package on macOS). If a requested provider is
/// missing or fails to initialise, the session falls back to CPU instead of throwing; the outcome is
/// visible through <see cref="ImageAiSession.ActiveProvider"/>.
/// </summary>
public enum ExecutionProvider
{
    /// <summary>
    /// The default. Ask the loaded ONNX Runtime which providers it actually contains and pick the best one for
    /// the current OS (DirectML then CUDA on Windows, CUDA on Linux, CoreML on macOS), falling back to
    /// <see cref="Cpu"/> when no accelerator is present. With the base package this always resolves to CPU.
    /// </summary>
    Auto = 0,

    /// <summary>Pure CPU. Always available.</summary>
    Cpu = 1,

    /// <summary>NVIDIA CUDA. Requires <c>Microsoft.ML.OnnxRuntime.Gpu</c> and a CUDA 12 toolkit on the machine.</summary>
    Cuda = 2,

    /// <summary>DirectML on any DirectX 12 GPU (Windows). Requires <c>Microsoft.ML.OnnxRuntime.DirectML</c>.</summary>
    DirectML = 3,

    /// <summary>Apple CoreML (macOS / Apple Silicon). Requires a CoreML-enabled ONNX Runtime build.</summary>
    CoreML = 4,
}

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using EasyImageSharp;
using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Microsoft.Win32.SafeHandles;

// A separate process is necessary to bound third-party/native allocations, which a managed
// pixel allocator cannot intercept. This tool deliberately does not label a GC estimate a hard cap.
if (args is ["worker"])
{
    Console.WriteLine("READY");
    string request = await Console.In.ReadLineAsync() ?? throw new InvalidDataException("No request.");
    WorkerRequest operation = JsonSerializer.Deserialize<WorkerRequest>(request)!;
    try
    {
        if (operation.TestDelayMilliseconds > 0)
            await Task.Delay(operation.TestDelayMilliseconds);
        if (operation.TestAllocationBytes > 0)
        {
            IntPtr allocation = Marshal.AllocHGlobal(checked((nint)operation.TestAllocationBytes));
            try { Marshal.WriteByte(allocation, 0, 1); }
            finally { Marshal.FreeHGlobal(allocation); }
            Console.WriteLine(JsonSerializer.Serialize(new WorkerResult(true, 0, 0, "Unexpected allocation success.")));
            return 0;
        }
        using Image<Rgba32> input = Image.Load<Rgba32>(operation.Input!, new DecoderOptions
        {
            MaxEncodedBytes = operation.MaxEncodedBytes,
            MaxPixels = operation.MaxPixels,
            MaxTotalPixels = operation.MaxPixels,
            MaxFrames = 1,
        });
        using var prepared = input.PreprocessDocument(new DocumentPreprocessingOptions
        {
            Profile = operation.Profile,
            ExecutionOptions = new ImageExecutionOptions
            {
                Configuration = new Configuration { MaxDegreeOfParallelism = operation.Threads },
            },
        });
        prepared.Image.SaveAsPng(operation.Output!);
        Console.WriteLine(JsonSerializer.Serialize(new WorkerResult(true, prepared.Image.Width, prepared.Image.Height, null)));
        return 0;
    }
    catch (OutOfMemoryException)
    {
        Console.WriteLine("{\"Success\":false,\"Width\":0,\"Height\":0,\"Error\":\"Memory allocation rejected.\"}");
        return 3;
    }
    catch (Exception error)
    {
        Console.WriteLine(JsonSerializer.Serialize(new WorkerResult(false, 0, 0, error.GetType().Name + ": " + error.Message)));
        return 2;
    }
}

if (args is ["--self-test"])
{
    string directory = Path.Combine(Path.GetTempPath(), "EasyImageSharp-isolation-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string input = Path.Combine(directory, "input.png"), output = Path.Combine(directory, "output.png");
    using (var image = new Image<Rgba32>(96, 64, new Rgba32(20, 70, 130))) image.SaveAsPng(input);
    long selfTestLimit = OperatingSystem.IsLinux() ? 4L << 30 : 512L << 20;
    RunResult normal = await IsolatedRunner.Run(new WorkerRequest(input, output), selfTestLimit, TimeSpan.FromSeconds(30));
    if (!normal.Result.Success || normal.Result.Width != 96 || !File.Exists(output)) throw new Exception("Normal worker failed: " + normal.Result.Error);
    RunResult denied = await IsolatedRunner.Run(new WorkerRequest(null, null) { TestAllocationBytes = Math.Max(2L << 30, selfTestLimit * 2) },
        selfTestLimit, TimeSpan.FromSeconds(30));
    if (denied.Result.Success) throw new Exception("Native allocation escaped process memory cap.");
    string delayedOutput = Path.Combine(directory, "delayed.png");
    RunResult expired = await IsolatedRunner.Run(new WorkerRequest(input, delayedOutput) { TestDelayMilliseconds = 10000 },
        selfTestLimit, TimeSpan.FromSeconds(2));
    if (expired.Result.Success || expired.Result.Error != "Worker deadline exceeded." || File.Exists(delayedOutput))
        throw new Exception("Deadline did not stop the worker before output.");
    RunResult? tooSmall = null;
    if (OperatingSystem.IsLinux())
    {
        tooSmall = await IsolatedRunner.Run(new WorkerRequest(input, delayedOutput), 128L << 20, TimeSpan.FromSeconds(30));
        if (tooSmall.Result.Success || !tooSmall.Result.Error!.Contains("startup virtual address", StringComparison.Ordinal))
            throw new Exception("Insufficient virtual address allowance was not rejected clearly.");
    }
    Console.WriteLine(JsonSerializer.Serialize(new { Normal = normal, RejectedNativeAllocation = denied, Deadline = expired,
        InsufficientVirtualAllowance = tooSmall, Checks = OperatingSystem.IsLinux() ? 4 : 3 }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (args.Length is < 3 or > 5)
{
    Console.Error.WriteLine("Usage: IsolatedImageWorker <input> <output.png> <memory-MiB> [timeout-seconds=60] [profile=NeuralOcr]\n       IsolatedImageWorker --self-test");
    return 2;
}
string inputPath = Path.GetFullPath(args[0]), outputPath = Path.GetFullPath(args[1]);
if (File.Exists(outputPath)) throw new IOException("Output already exists; choose a new output path.");
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
string temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".partial";
long limit = checked(long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) * 1024 * 1024);
int timeout = args.Length >= 4 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 60;
DocumentPreprocessingProfile profile = args.Length >= 5 ? Enum.Parse<DocumentPreprocessingProfile>(args[4], true) : DocumentPreprocessingProfile.NeuralOcr;
try
{
    RunResult run = await IsolatedRunner.Run(new WorkerRequest(inputPath, temporary) { Profile = profile }, limit, TimeSpan.FromSeconds(timeout));
    if (run.Result.Success) File.Move(temporary, outputPath);
    Console.WriteLine(JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
    return run.Result.Success ? 0 : 1;
}
finally { if (File.Exists(temporary)) File.Delete(temporary); }

internal sealed record WorkerRequest(string? Input, string? Output)
{
    public DocumentPreprocessingProfile Profile { get; init; } = DocumentPreprocessingProfile.NeuralOcr;
    public long MaxEncodedBytes { get; init; } = 128L << 20;
    public long MaxPixels { get; init; } = 100_000_000;
    public int Threads { get; init; } = 1;
    public long TestAllocationBytes { get; init; }
    public int TestDelayMilliseconds { get; init; }
}
internal sealed record WorkerResult(bool Success, int Width, int Height, string? Error);
internal sealed record RunResult(WorkerResult Result, long MemoryLimitBytes, string LimitKind, long PeakWorkingSetBytes, double ElapsedMs, int ExitCode, long ManagedHeapLimitBytes);

internal static class IsolatedRunner
{
    public static async Task<RunResult> Run(WorkerRequest request, long memoryBytes, TimeSpan timeout)
    {
        if (memoryBytes < 128L << 20) throw new ArgumentOutOfRangeException(nameof(memoryBytes), "Allow at least 128 MiB for the runtime and image work.");
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Hard process caps are implemented on Windows and Linux.");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("worker");
        // Avoid .NET reserving an enormous GC virtual address range before Linux RLIMIT_AS is applied.
        long managedHeapLimit = OperatingSystem.IsLinux() ? Math.Min(memoryBytes / 4, 128L << 20) : memoryBytes / 4;
        start.Environment["DOTNET_GCHeapHardLimit"] = managedHeapLimit.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["DOTNET_gcServer"] = "0";
        var watch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(timeout);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start worker.");
        using SafeFileHandle? job = OperatingSystem.IsWindows() ? NativeLimits.AttachWindows(process, memoryBytes) : null;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        long peak = 0;
        try
        {
            string? ready = await process.StandardOutput.ReadLineAsync(deadline.Token);
            if (ready != "READY") throw new InvalidOperationException("Worker did not reach its resource-limit handshake.");
            if (OperatingSystem.IsLinux())
            {
                process.Refresh();
                long startupVirtualBytes = process.VirtualMemorySize64;
                if (startupVirtualBytes >= memoryBytes)
                {
                    return new(new(false, 0, 0, $"Requested Linux virtual-address cap {memoryBytes} bytes is below the worker's startup virtual address reservation {startupVirtualBytes} bytes. Increase the virtual allowance (typically at least 4 GiB); this is not a resident-memory cap."),
                        memoryBytes, "Linux RLIMIT_AS virtual-address cap", process.PeakWorkingSet64, watch.Elapsed.TotalMilliseconds, -1, managedHeapLimit);
                }

                NativeLimits.AttachLinux(process.Id, memoryBytes);
            }
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            process.StandardInput.Close();
            Task<string> response = process.StandardOutput.ReadToEndAsync();
            Task exit = process.WaitForExitAsync(deadline.Token);
            while (!process.HasExited)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try { process.Refresh(); peak = Math.Max(peak, process.PeakWorkingSet64); }
                catch (InvalidOperationException) { }
                await Task.WhenAny(exit, Task.Delay(20, deadline.Token));
            }
            string raw = await response;
            string errorText = await errors;
            WorkerResult result;
            try { result = JsonSerializer.Deserialize<WorkerResult>(raw) ?? throw new JsonException(); }
            catch (JsonException) { result = new(false, 0, 0, "Worker exited without a valid result. " + errorText[..Math.Min(errorText.Length, 1024)]); }
            if (process.ExitCode != 0 && result.Success) result = new(false, 0, 0, "Worker exited unsuccessfully.");
            return new(result, memoryBytes, OperatingSystem.IsWindows() ? "Windows job committed-memory cap" : "Linux RLIMIT_AS virtual-address cap",
                peak, watch.Elapsed.TotalMilliseconds, process.ExitCode, managedHeapLimit);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return new(new(false, 0, 0, "Worker deadline exceeded."), memoryBytes, "process deadline", peak, watch.Elapsed.TotalMilliseconds, process.ExitCode, managedHeapLimit);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
}

internal static class NativeLimits
{
    internal static SafeFileHandle AttachWindows(Process process, long bytes)
    {
        SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var limits = new JobLimits
            {
                Basic = new BasicLimits { Flags = 0x100 | 0x200 | 0x2000 },
                ProcessMemory = (nuint)bytes, JobMemory = (nuint)bytes,
            };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()) || !AssignProcessToJobObject(job, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return job;
        }
        catch { job.Dispose(); if (!process.HasExited) process.Kill(true); throw; }
    }
    internal static void AttachLinux(int pid, long bytes)
    {
        var limits = new RLimit { Current = (ulong)bytes, Maximum = (ulong)bytes };
        if (Prlimit(pid, 9, ref limits, IntPtr.Zero) != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)] private struct RLimit { public ulong Current, Maximum; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcessTime, PerJobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref JobLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("libc", EntryPoint = "prlimit64", SetLastError = true)]
    private static extern int Prlimit(int pid, int resource, ref RLimit limit, IntPtr oldLimit);
}

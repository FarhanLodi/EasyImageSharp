# Hard process limits for image work

```powershell
dotnet run -c Release --project tools/IsolatedImageWorker -- input.jpg output.png 512 60 NeuralOcr
dotnet run -c Release --project tools/IsolatedImageWorker -- --self-test
```

The launcher starts a separate worker, establishes the OS limit before sending an image request, applies a deadline, and writes a final output only when the worker succeeds. Existing output files are not overwritten. Arguments are input path, output PNG path, memory MiB, optional timeout seconds (default 60), and optional document preprocessing profile (default `NeuralOcr`). A JSON report includes worker status, elapsed time, observed peak working set, exit code, limit size, and limit type.

On Windows the worker belongs to a Job Object with process and job committed-memory limits and kill-on-close. This covers native allocations as well as managed allocations. On Linux the launcher applies `RLIMIT_AS` using `prlimit64`; this limits virtual address space, which includes runtime reservations and differs from a resident-memory limit. A workload can fail below the requested resident usage. Linux managed heaps are additionally capped at 128 MiB to avoid excessive runtime reservations; large managed images can therefore fail before reaching the virtual-address cap. Other operating systems reject the operation explicitly. The minimum accepted argument is 128 MiB, but Linux must accommodate startup virtual reservations: use 4096 MiB or more as an initial allowance, then measure your runtime. A cap below the startup reservation is rejected with an explanatory result before image processing. For a Linux resident/committed-memory boundary, use an appropriately configured cgroup/container in deployment.

The worker performs header/decode validation and document preprocessing with one library worker. Its decoder also limits encoded bytes to 128 MiB, source/total pixels to 100 million, and frames to 1. Timeout termination kills the process tree. This is resource isolation, not a security sandbox: the worker runs with the launcher's account permissions and filesystem access. Keep the CLI outside an unauthenticated endpoint unless the host validates allowed paths and request policy.

`--self-test` runs a normal small image, rejects an oversized native allocation, and terminates a delayed operation before it writes output. Windows uses a 512 MiB cap and a 2 GiB allocation attempt. Linux uses a 4 GiB virtual allowance and an 8 GiB allocation attempt, and also verifies a clearly rejected insufficient startup allowance. Linux must be checked on its deployment environment because virtual-address reservations depend on the runtime and host. In CI, build the tool and run this command on the intended OS independently of unit tests. No elevated permission is expected when setting limits on the launcher's own child process.

Linux x64 verification in WSL on 2026-10-14 passed all four checks: normal decode/preprocessing succeeded, the 8 GiB native allocation was rejected, the delayed worker was killed at about 2.02 seconds without output, and an insufficient 128 MiB virtual allowance returned the startup-reservation explanation. This validates that runtime/host combination; it does not turn the virtual allowance into a physical-memory budget.

Windows x64 verification the same day passed all three applicable checks: normal processing, rejected 2 GiB native allocation under the 512 MiB committed-memory cap, and deadline termination at about 2.05 seconds without output. CI runs these checks on Windows and Linux; macOS is explicitly excluded because this tool has no hard-limit implementation there.

The [Windows report](../../benchmarks/resource-isolation/windows-x64-2026-10-14.json) and [Linux report](../../benchmarks/resource-isolation/linux-wsl-x64-2026-10-14.json) retain commands, binary hashes, enforced limits, observed timings and check results.

The measured peak working set is diagnostic; it is not the committed/virtual quantity enforced by the OS. The library's in-process execution options remain useful for cooperative cancellation and pixel limits, while this separate process provides an enforceable boundary for native and other allocations.

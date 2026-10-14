# Platform verification

The 1.2.0 working tree is tested locally on Windows x64. An isolated Ubuntu 24.04 WSL2 distribution
also supplies real Linux x64 native execution and ARM64 emulation. Emulation verifies behavior;
it does not establish physical ARM64 throughput or replace the required ARM64 CI job.

The core smoke executable exercises codecs, processing, tensors, document diagnostics, limits,
reduced-IDCT JPEG and seekable TIFF regions. AI and native adapters have separate tests.

## Native AOT

Install a .NET 10 SDK, `clang` and `zlib1g-dev` on Linux, then run:

```sh
dotnet publish samples/AotSmoke -c Release -r linux-x64 -p:PublishAot=true -o artifacts/aot-x64
test ! -f artifacts/aot-x64/AotSmoke.dll
./artifacts/aot-x64/AotSmoke
```

Cross-architecture compilation additionally needs target C libraries, ARM64 zlib, GCC/binutils
and QEMU user emulation. Follow Microsoft's [Native AOT cross-compilation prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile)
for the distribution's package sources. On the isolated Ubuntu environment, `zlib1g-dev:arm64`,
`gcc-aarch64-linux-gnu`, `binutils-aarch64-linux-gnu` and `qemu-user` were installed.

```sh
dotnet publish samples/AotSmoke -c Release -r linux-arm64 -p:PublishAot=true \
  -p:ObjCopyName=aarch64-linux-gnu-objcopy -o artifacts/aot-arm64
test ! -f artifacts/aot-arm64/AotSmoke.dll
file artifacts/aot-arm64/AotSmoke
qemu-aarch64 artifacts/aot-arm64/AotSmoke
```

`ObjCopyName` selects a tool that understands the ARM64 binary when extracting debug symbols.
On a physical ARM64 runner, compile and execute directly without QEMU. CI does this separately.
Cross-OS publishing needs an appropriate OS environment; the Windows host's missing C++ linker
was not bypassed by pretending a managed publish was native.

## Optional native packages and process limits

LittleCMS tests run against the reproducibly built Windows 2.19.1 library and Ubuntu's system
2.14 library. Both are compared with the pinned independent `transicc` reference fixtures.
See the [native color package](../src/EasyImageSharp.LittleCms/README.md) for deployment details.

The [isolated worker](../tools/IsolatedImageWorker/README.md) self-tests exercise normal processing,
native allocation rejection and deadline termination on Windows and Linux. Linux additionally
checks that an allowance below the runtime's virtual reservation is rejected clearly. Its Linux
limit controls virtual address space, while Windows limits committed memory; neither report
confuses those quantities with sampled resident memory.

Run large OCR evaluations and cross-compilation serially on memory-constrained development
machines. A shared-host allocation failure is not codec correctness evidence, and overlapping
builds/inference invalidate latency comparisons. The corpus runner recycles processes by language
and retains failures in its reports.

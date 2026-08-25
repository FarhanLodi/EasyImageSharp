# Security Policy

EasyImageSharp decodes files that its callers did not create. That is the whole point of an imaging
library, and it means the decoders are an attack surface. This document describes what we consider a
vulnerability, how to report one privately, and what the library guarantees when it is handed a
hostile file.

---

## Supported versions

| Version | Supported | Notes |
|---|---|---|
| 1.0.x | Yes | current stable line; security fixes ship here |
| 0.x | No | pre-release; upgrade to 1.0.x |

Security fixes are released as a new patch of the newest minor line. When a 1.1 line ships, 1.0 will
continue to receive security patches for six months from that date, and this table will say so
explicitly with the end-of-support date.

Both packages — `EasyImageSharp` and `EasyImageSharp.AI` — are covered by this policy and version in
lockstep.

---

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Report privately through GitHub's private vulnerability reporting:

> **[Report a vulnerability](https://github.com/FarhanLodi/EasyImageSharp/security/advisories/new)**
> — the "Security" tab of the repository, then "Report a vulnerability".

If that is unavailable to you, open a normal issue containing **only** the words "security report,
requesting a private channel" and no technical detail, and a maintainer will open a private advisory
and invite you to it.

### What to include

- The package and version (`EasyImageSharp 1.0.0`), the target framework, and the OS.
- The offending input file, or a script that generates it. A file is worth ten paragraphs.
- The API call that triggers it — `Image.Load<Rgba32>`, `Image.Identify`, a specific decoder — and
  the `DecoderOptions` in force.
- What happens: the exception and stack trace, or the observed hang, or the memory figure.
- What you expected instead, with reference to the [threat model](#the-decoder-threat-model) below.

### What to expect

| Stage | Target |
|---|---|
| Acknowledgement that the report was received | 3 business days |
| First assessment: confirmed / not-a-vulnerability / need more information | 10 business days |
| Fix released, or a public timeline if it will take longer | 90 days from confirmation |

We will keep you updated through the advisory thread, credit you in the advisory and the changelog
unless you ask us not to, and coordinate the disclosure date with you. Please give us the 90 days
before disclosing publicly; if the issue is already being exploited in the wild, tell us and we will
move faster.

There is no bug bounty. This is an MIT-licensed project maintained in people's spare time.

---

## The decoder threat model

### The assumption

**Every byte handed to a decoder is assumed to be hostile.** The library is designed to be called
directly on user uploads — an HTTP request body, a scanned file from an untrusted source, an
attachment — without the caller having to sanitise or pre-validate anything first.

The core `EasyImageSharp` package is 100% managed code with no native dependencies and no `unsafe`
pixel loops, so the classic image-library failure mode — a heap overflow in a C decoder reachable
from a malformed header — does not exist here. The realistic threats are **denial of service**
(memory exhaustion, unbounded CPU) and **unexpected exception types** crashing a caller that was
only prepared to catch image errors.

### The guarantees

For any input whatsoever, `Image.Load`, `Image.LoadAsync`, `Image.Identify`, `Image.IdentifyAsync`
and any `IImageDecoder` implementation in this repository must either succeed or throw one of exactly
three things:

| Exception | Meaning |
|---|---|
| `InvalidImageContentException` | the format was recognised but the data is malformed, truncated or internally inconsistent |
| `NotSupportedException` | the format and feature were recognised, and this version does not implement them |
| `ImageSizeLimitExceededException` | the image declares dimensions beyond the configured `DecoderOptions` limits |

`UnknownImageFormatException` is thrown when the bytes match no registered format. All four
image exceptions derive from `ImageFormatException`, so `catch (ImageFormatException)` is a complete
guard for "this file is not usable".

Additionally, for any input:

- **No framework exception escapes a decoder for malformed input.** An `IndexOutOfRangeException`,
  `ArgumentException`, `InvalidDataException`, `OverflowException` or `IOException` reaching the
  caller from a decode is a bug, and a reportable one. Decoders wrap these through a shared guard.
- **No unbounded loop.** A decoder must make forward progress or fail.
- **No allocation driven by unvalidated header fields.** Sizes are computed in 64-bit arithmetic and
  checked against the configured limit *before* any pixel buffer is allocated.

These are not aspirations. `tests/EasyImageSharp.Tests/FuzzSmokeTests.cs` enforces all of them on
every commit: a fixed-seed mutation fuzzer runs roughly 130,000 decode and identify calls over
mutated versions of the entire fixture corpus plus the library's own encoder output, and fails the
build if any call escapes the contract, exceeds a 2-second timeout, or allocates more than 100 MB.
`CorruptInputTests.cs` holds around 150 hand-crafted malformed inputs pinning specific behaviours.

### The limits you configure

`DecoderOptions` is accepted by every load and identify path. **If you decode untrusted input, set
it explicitly** — the defaults are chosen so that ordinary applications work, not so that a hostile
upload is cheap.

```csharp
var options = new DecoderOptions
{
    MaxPixels = 40_000_000,   // ~40 MP; the default is 268_435_456 (256 MP) per frame
    MaxFrames = 8,            // TIFF pages / GIF frames; the default is unlimited
};

using Image<Rgba32> image = Image.Load<Rgba32>(untrustedBytes, options);
```

| Option | Default | What it bounds |
|---|---|---|
| `MaxPixels` | 268,435,456 (256 MP) per frame | peak pixel-buffer allocation; 256 MP of RGBA is 1 GiB |
| `MaxFrames` | unlimited | how many frames of a multi-page TIFF or animated GIF are decoded |

Enforcement happens immediately after the header is parsed and **before** any pixel memory is
allocated, so a file claiming 65535 x 65535 pixels is rejected for the cost of reading its header.
`Identify` is deliberately never subject to size limits: it reads header-level information only, so
callers can always inspect the declared dimensions first and decide whether to decode at all. The
recommended shape for an upload endpoint is `Identify` first, apply your own policy to the reported
size, then `Load` with matching `DecoderOptions`.

Two things `DecoderOptions` does **not** bound, which you must handle yourself:

- **Input length.** The library reads the stream you give it. Cap the request body at your web
  framework or copy into a length-limited buffer before calling `Load`.
- **Total memory across concurrent requests.** `MaxPixels` is per decode. Ten simultaneous
  decodes at the limit use ten times the memory. Bound your concurrency.

Also worth knowing: `Configuration.MaxDegreeOfParallelism` bounds the threads a single processing
operation may use. On a shared server, lower it rather than letting one large image saturate the
pool.

---

## What is in scope

- Any input reaching a decoder that causes: a framework exception to escape, an unbounded loop, an
  allocation not bounded by `MaxPixels`, or a crash.
- `DecoderOptions` limits being bypassed or applied after allocation rather than before.
- A path-traversal or arbitrary-write in any API that takes a path, including model cache paths in
  `EasyImageSharp.AI`.
- In `EasyImageSharp.AI`: a model being loaded whose SHA-256 does not match the pinned checksum, a
  download falling back to plaintext HTTP, or the offline mode reaching the network.
- Metadata parsing (EXIF, ICC, XMP, PNG text) mishandling hostile input — these run on the same
  contract as the pixel decoders.

## What is out of scope

- **Memory use within the configured limits.** Decoding a legitimately huge image is expensive; set
  `MaxPixels` to what your application can afford.
- **Decoding being slower than you would like.** Report it as a performance issue.
- Vulnerabilities in ONNX Runtime itself — report those to that project. Note that
  `EasyImageSharp.AI` is optional and the core package does not reference it.
- **The models fetched by `EasyImageSharp.AI`.** Their weights come from Hugging Face and are pinned
  by SHA-256; we verify integrity, not that a third-party model behaves sensibly on your data. A
  model producing a bad result is not a security issue. `ImageAiOptions.AllowUnverifiedModels` and
  `AllowInsecureModelSource` exist for local development and deliberately weaken these checks —
  setting them is your decision, and doing so is not a vulnerability in the library.
- Findings from a scanner with no demonstrated impact, and issues requiring the attacker to already
  control the process.

---

## The model supply chain

`EasyImageSharp.AI` downloads neural-network weights at run time, which is a supply-chain surface
distinct from the decoders. Four properties bound it:

1. **First-party hosting.** Models are published to the project's own Hugging Face repository
   (`huggingface.co/EasyImageSharp/EasyImageSharp-models`), so the bytes you receive are under the
   same ownership as the library. Where a model is still served from its upstream repository, the
   descriptor says so in its XML documentation.
2. **Content pinning, not source trust.** Every file is verified against an upper-case hex SHA-256
   compiled into `ModelRegistry`. Verification is fail-closed: a mismatch deletes the downloaded file
   and throws `ModelChecksumException` rather than running it. Because trust is by content hash, a
   compromised host cannot serve you different weights, and an internal mirror serving identical
   bytes validates identically.
3. **Immutability.** A published file is never overwritten. A re-export is published under a new file
   name with a new checksum, so a pinned version keeps resolving the exact bytes it was tested with.
4. **No implicit network access.** The core `EasyImageSharp` package never downloads anything. In the
   add-on, `ImageAiOptions.Offline` disables the network entirely, `CachePath` relocates the store for
   pre-seeding, and downloads are HTTPS-only unless `AllowInsecureModelSource` is explicitly set.

For an air-gapped deployment, pre-seed the cache directory and set `Offline = true`; a missing model
then raises `OfflineModelMissingException` instead of reaching the network.

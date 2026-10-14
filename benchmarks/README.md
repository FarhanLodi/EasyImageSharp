# Benchmarks

The numbers in the root [README](../README.md)'s Performance table come from here, and nowhere else. Every
row is produced by a named benchmark against a corpus that any checkout can rebuild from a script, so a
figure in the README is a claim somebody else can check rather than a number that was once true on one
machine.

## What this measures

`TensorBenchmarks` additionally compares allocating RGB conversion with caller-owned RGB/BGR
tensor writing for 320x48 recognizer crops and 960x960 detector inputs. Setup checks RGB parity
within `1e-6`; resize, inference, and model loading are excluded. Run with
`--filter "*TensorBenchmarks*" --job Short` for an exploratory comparison, or omit `--job Short`
for a longer measurement. Existing corpus validation still runs at startup.

An exploratory local result with hardware metadata is recorded in
[`results/tensor-performance-2026-10-14.json`](results/tensor-performance-2026-10-14.json).
Detector timings varied substantially across short runs; this is not an end-to-end OCR performance claim.

## OCR prediction evaluation

`ocr/evaluate.py` scores exported predictions without installing OCR engines or downloading models:

```bash
python benchmarks/ocr/evaluate.py corpus.json predictions.json --output report.json
python -m unittest discover -s benchmarks/ocr -p "test_*.py"
```

The corpus JSON contract is:

```json
{
  "schema_version": 1,
  "corpus_id": "receipts-v1",
  "license": "Your dataset license",
  "provenance": "Dataset version and source; record image hashes with the corpus",
  "split": "heldout",
  "samples": [
    { "id": "page-001", "text": "Total 123", "category": "receipt", "script": "Latin" }
  ]
}
```

Predictions use the same corpus ID and exactly one entry per sample:

```json
{
  "schema_version": 1,
  "corpus_id": "receipts-v1",
  "metadata": {
    "engine": "PaddleOCRNet",
    "engine_version": "record the actual package version",
    "model_id": "record the exact export identifier",
    "model_sha256": "replace with the actual 64-hex-digit model or model-bundle manifest SHA-256",
    "preprocessing": { "profile": "record exact settings" },
    "environment": {
      "runtime": "record runtime version", "hardware": "record CPU/GPU",
      "provider": "record execution provider", "threads": 1, "warmup": "record policy"
    }
  },
  "predictions": [
    { "id": "page-001", "text": "Total 123", "latency_ms": 10.5 }
  ]
}
```

The example metadata contains placeholders and must be replaced with actual run information.
Model metadata is recorded, not independently verified against model bytes. Use a versioned bundle
manifest hash for pipelines with multiple models. Do not include warmup/model download in steady-state
latency; explicitly record the measured scope in the environment metadata.

Reports include micro-averaged CER/WER, raw edit counts, exact matches, failures, and per-category/script
breakdowns. Text is normalized to Unicode NFC; case and punctuation are retained. CER counts Unicode
code points, not grapheme clusters. WER splits on Unicode whitespace and is not a suitable standalone
metric for unsegmented CJK text. Empty-reference categories have null rates while retaining insertion
counts; CER/WER can exceed 1. Failed inference must supply empty `text` plus an `error` string, so its
deletions remain in the aggregate. Missing/duplicate predictions are rejected.

Optional `latency_ms` must cover every sample or none. Percentiles use nearest rank and include failed
requests: inspect failure counts alongside timings. Reports hash both input JSON files; those hashes do
not verify the source images. Keep image provenance and hashes in your corpus records. This scorer does
not yet evaluate detection boxes, peak memory, concurrency, or statistical significance, and ships no
labeled image corpus. Use separate tuning/heldout sets; `smoke` is for synthetic validation only.

## Existing image benchmark mapping

| README row | Benchmark | Input |
|---|---|---|
| JPEG decode | `DecodeBenchmarks.Decode`, `Format=jpeg` | `corpus/photo.jpeg`, 3032×2008 → `Rgba32` |
| PNG decode | `DecodeBenchmarks.Decode`, `Format=png` | `corpus/photo.png`, 3032×2008 → `Rgba32` |
| Resize, bicubic ×0.5 | `ResizeBenchmarks.BicubicHalfRgba32` | `corpus/photo.png` as `Rgba32` |
| Resize, bicubic ×0.5 | `ResizeBenchmarks.BicubicHalfL8` | `corpus/photo.png` as `L8` |
| Grayscale, in place | `ProcessingBenchmarks.Grayscale` | `corpus/scan.png`, 2480×3508 `L8` |
| Otsu threshold, in place | `ProcessingBenchmarks.OtsuThreshold` | `corpus/scan.png`, 2480×3508 `L8` |
| Load → resize → save | `PipelineBenchmarks.LoadResizeSave` | the twenty JPEGs in `corpus/batch` |

`ReadmeTable.cs` holds that mapping as code. If a benchmark in it is renamed or filtered out, `--readme-table`
prints an error naming the missing benchmark and exits non-zero — a README row cannot go missing quietly.

Everything else in the suite is measured because a change to it has to be defensible, not because the README
quotes it:

- **`DecodeBenchmarks`.** Decode to `Rgba32` and to `L8`, plus header-only `Identify`, for all nine
  containers the library reads. `Identify` is separate because it is exempt from the `DecoderOptions` pixel
  budget and is the only work an upload validator pays for.
- **`EncodeBenchmarks`.** Every encoder the library ships, at settings a caller would plausibly choose. Each
  appears twice: once into `Stream.Null`, which isolates the encoder's own allocation, and once into a
  pre-sized `MemoryStream`, which is what a caller who keeps the bytes actually pays.
- **`ResizeBenchmarks` and `ResamplerBenchmarks`.** The two README resize rows, rotate-flip and crop; then the
  same half-scale resize through five resamplers.
- **`ProcessingBenchmarks`.** Grayscale and Otsu, then Sauvola, Bradley, box and Gaussian blur, deskew and the
  whole `PrepareForOcr` pipeline.
- **`PipelineBenchmarks`.** Load, resize, save, twenty times, synchronously and through the async surface.
- **`InflateBenchmarks`.** The PNG inflate backend: the library's managed inflater against the runtime's
  `ZLibStream`. See [Which inflate backend](#which-inflate-backend) below.

## Reproducing the table from a clean checkout

```bash
python -m pip install "pillow>=11" "numpy>=2"
python benchmarks/corpus/generate.py
dotnet run -c Release -f net10.0 --project benchmarks/EasyImageSharp.Benchmarks -- --filter "*"
dotnet run -c Release -f net10.0 --project benchmarks/EasyImageSharp.Benchmarks -- --readme-table
```

The last command runs nothing. It reads the JSON reports the previous run left under
`benchmarks/BenchmarkDotNet.Artifacts/results`, prints the seven-row table, and writes it to
`benchmarks/results/README-performance.md`, which **is** committed — the raw BenchmarkDotNet output is not.

Narrower runs, all of which take the same arguments BenchmarkDotNet understands:

```bash
# one class
dotnet run -c Release -f net10.0 --project benchmarks/EasyImageSharp.Benchmarks -- --filter "*DecodeBenchmarks*"

# one method, one parameter value
dotnet run -c Release -f net10.0 --project benchmarks/EasyImageSharp.Benchmarks -- --filter "*Decode(Format: png)*"

# correctness only: every benchmark runs exactly once and the timings mean nothing
dotnet run -c Release -f net10.0 --project benchmarks/EasyImageSharp.Benchmarks -- --filter "*" --job Dry
```

`--job Dry` is what CI runs, against the small corpus, to prove the suite still executes end to end. It is
also the right thing to run locally after adding a benchmark, before spending twenty minutes on a real
measurement.

## Which inflate backend

Which decompressor the PNG decoder should use is a per-target-framework question, not a matter of taste.
.NET 8's `ZLibStream` calls the classic native zlib; .NET 10's calls zlib-ng, which is hand-written SIMD and
much faster on some inputs. So the same managed inflater can be the right choice on one framework and the
wrong one on the next, and the answer moves whenever the runtime's zlib does.

`InflateBenchmarks` is the measurement that question is settled by. Run it on both frameworks — the ratio
column is the answer, and it is not the same number on both:

```bash
dotnet run -c Release -f net8.0  --project benchmarks/EasyImageSharp.Benchmarks -- --filter "*Inflate*"
dotnet run -c Release -f net10.0 --project benchmarks/EasyImageSharp.Benchmarks -- --filter "*Inflate*"
```

`ZLibStreamRows` is the shape the decoder used before the managed backend existed — every IDAT chunk
concatenated into one pooled buffer, then read a scanline at a time. `InflaterRows` is the streaming shape,
which takes each scanline as a span into the inflater's own window and copies nothing. The concatenation the
streaming path removes is therefore counted in `ZLibStream`'s favour, so a win for the inflater here is a
lower bound. Both backends are checked to produce identical bytes in `[GlobalSetup]` before either is timed.

`Inflater`, `InflateTables`, `Adler32` and `SimdConfig` are `internal` to EasyImageSharp, so the project file
compiles those four source files into the benchmark assembly as well. Compiling the same sources, rather than
copying them, is what stops the benchmark drifting from the implementation the library ships.

Read the two `Source` rows as two different questions, because they answer differently: `photo.png` is a
10 MiB photograph, where inflate is dominated by literal decoding, and `scan.png` is a 0.7 MiB document that
compresses about 13:1, where it is dominated by match copying. A backend that wins one can lose the other,
so a decision taken from a single input is not a decision. If you are re-checking the choice after a runtime
upgrade, run both frameworks on the same corpus in the same sitting on an otherwise idle machine; comparing
against numbers measured on a different corpus proves nothing.

## What you will not reproduce

Absolute milliseconds. The committed table was measured on one machine, in Release, on one runtime, and the
line above the table says which. Other hardware will produce different numbers, and that is fine.

What **is** reproducible from any checkout is the corpus definition, the exact operation under test, the
ratios between rows, and the Allocated column, which is a property of the code rather than of the machine.

Anyone who replaces the table in the root README must re-run the benchmarks themselves and state their own
hardware on the line above it. Do not copy a row forward from the previous table because it "did not change" —
the previous corpus is gone, and the previous numbers cannot be checked against anything.

## Corpus

`benchmarks/corpus/` is generated, never committed. A nested `.gitignore` there ignores everything except
itself and `generate.py`, so a generated corpus can never be staged by accident; `git status --porcelain
benchmarks/corpus` is empty after a run.

```bash
python benchmarks/corpus/generate.py           # write anything missing or out of date
python benchmarks/corpus/generate.py --force   # rewrite everything
EASYIMAGESHARP_BENCH_SMALL=1 python benchmarks/corpus/generate.py   # every dimension divided by eight
```

A full corpus is about 111 MiB and takes three to five minutes to write; that is why it is not in git. The
small corpus is about 1.4 MiB and takes six seconds, and exists so CI can run `--job Dry` quickly. It is only
good for proving the benchmarks execute — `Corpus.EnsurePresent` prints a warning when a run is using it.

What `generate.py` writes:

| File | What it is |
|---|---|
| `photo.png` | 3032×2008 RGB. Octaves of value noise down to eight pixels per cell, a grain term under them, two soft shapes, a vignette and some hard edges. It compresses like a real photograph — about 10 MiB as PNG — which a frame built only from smooth noise does not. |
| `photo.{jpeg,bmp,ppm,tga,tiff,webp,gif,qoi}` | The same pixels in the other eight containers. |
| `scan.png` | 2480×3508 8-bit grayscale: an A4 page at 300 DPI, blocks of synthetic glyphs under an illumination gradient, with sparse scanner speckle. The gradient is what makes a local threshold behave differently from a global one; the speckle is deliberately sparse rather than Gaussian-per-pixel, because per-pixel noise puts two bits of entropy under every pixel and the page stops compressing like a document — and `InflateBenchmarks` uses this file as its highly-compressible case. It lands around 0.7 MiB, roughly 13:1. |
| `batch/00.jpg` … `batch/19.jpg` | Twenty distinct 1920×1280 photographs at quality 88. |
| `manifest.json` | Name, size and SHA-256 of every file. `Corpus.EnsurePresent` reads it and refuses to benchmark a corpus that does not match. |

Every file is written by Pillow — that is, by libjpeg-turbo, libwebp and zlib — and never by EasyImageSharp.
A decode benchmark therefore decodes a foreign encoder's output, which is the same discipline the test fixture
corpus follows. `generate.py` re-opens everything it wrote and checks the lossless containers are byte-exact
and the lossy ones are within a PSNR bound before it writes the manifest.

## Adding a benchmark

1. Add the method to the class it belongs in, or add a new `[MemoryDiagnoser]` class. Return something from
   every benchmark so the JIT cannot delete the work, and dispose intermediate images inside the method so the
   Allocated column stays per-operation.
2. Add it to `ReadmeTable.Rows` **only** if the root README quotes it. That list is the contract between the
   suite and the README; everything else is measured without being published.
3. Run `--filter "*YourClass*" --job Dry` locally. That is the check CI performs.
4. If you re-measured a published row, commit the regenerated `benchmarks/results/README-performance.md`
   alongside the README edit.

## Why this project is outside EasyImageSharp.slnx

The solution's test leg builds and runs with `-f net8.0` and `-f net10.0` explicitly, and every project in the
solution has to answer to both. Keeping the benchmarks out of the solution keeps that leg honest and keeps a
`BenchmarkDotNet` package reference out of the packaging graph entirely; `samples/` is excluded for the same
reason and is likewise referenced by path.

The project itself targets both `net8.0` and `net10.0`, because the inflate-backend comparison above is only
meaningful when both can be measured. `dotnet run` therefore needs an explicit `-f`; the committed table is
measured on `net10.0`.


## Detection geometry and model export tools

The Python evaluator optionally accepts `boxes` on every reference and prediction. Each box is four
finite `[x,y]` points ordered around a convex quadrilateral, in the same source-image coordinate system.
An empty array represents no text boxes. Coverage must be complete; failed predictions use empty text
and empty boxes. `metadata.detection_iou_threshold` defaults to 0.5 and must lie in (0,1].

Detection scoring uses convex polygon intersection and maximum-cardinality one-to-one matching above
the IoU threshold. Duplicate detections count as false positives. Reports include micro precision,
recall and F1 overall and by category/script; undefined denominators remain null. Ignored regions,
transcription-aware matching and benchmark-specific protocols are not implemented. These metrics must
not be presented as official ICDAR/COCO scores.

[OcrCorpusExport](../tools/OcrCorpusExport/README.md) runs the actual local OCR consumers offline and
writes its own PascalCase audit report with model-cache/image/assembly hashes. Its trimmed-text Unicode
scalar CER differs from the Python scorer's NFC contract; compare reports using the same evaluator.
[DocumentModelSmoke](../tools/DocumentModelSmoke/README.md) verifies the pinned orientation/dewarp export
contracts. The [recorded reports](ocr/results) are smoke evidence, not a broad held-out corpus baseline.

`ResizeTensorBenchmarks` compares materialized resize plus reusable writing against fused resizing.
Both reuse the output tensor. The rolling fused implementation minimizes temporary storage; it is not
a promise of lower latency at every image size. Report allocation and timing separately.

Latest short-run results: [fused tensor measurements](results/fused-tensor-performance-2026-10-14.json).
At 960x960, fused preprocessing allocated about 50 KiB versus 4.45 MiB for materialized resize, but latency
was 12.61 ms versus 6.46 ms. Prefer the existing parallel resize plus reusable writer when latency is the
priority; measure fused preprocessing when temporary memory is the constraint. The byte-output LUT
optimization did not remove the larger workload's serial filtering cost.

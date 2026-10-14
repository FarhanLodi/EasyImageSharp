# EasyImageSharp implementation plan

Date: 2026-10-13

Status: completion pass implemented in the working tree on 2026-10-14; planned package version 1.2.0. Not published. Implementation, measured evidence, external release gates and capability boundaries are distinguished below.

## Implemented in this pass

| Area | Delivered |
| --- | --- |
| Precision | Built-in wide pixel tensors, resize, rotation, affine/projective and perspective sampling; premultiplied alpha; invalid geometry/overflow guards. |
| Tensors | Reusable and fused CHW/HWC RGB/BGR/grayscale preprocessing, offsets/strides, normalization, cancellation policy and coordinate maps. |
| OCR consumers | Opt-in source migrations for EasyOCRSharp DB/TrOCR and PaddleOCRNet DB/SVTR; exact tensor parity tests and actual cached-model smoke comparisons. |
| Documents | Independent pages; NeuralOcr, ScannedDocument, PhoneDocument and Screenshot profiles; diagnostics, dHash similarity, decisions/reasons/timings and coordinate mapping. |
| AI | Async shared preprocessing results, corrected published orientation/UVDoc contracts, full-resolution BGR, explicit custom image/grid output contracts and truthful unavailable mappings. |
| Resources | Request-local parallelism/cancellation, cumulative pixel-buffer budgets, bounded encoded input, incremental six-format headers, lazy TIFF pages, direct reduced-IDCT JPEG, seekable BigTIFF ROI/tiles and isolated process memory/deadline controls. |
| Color and 2D | RGB/XYZ/Lab/Lch/HSV/HSL, Bradford adaptation, managed matrix/TRC ICC subset; optional LittleCMS LUT/CMYK/intents/BPC; optional Skia bitmap interop, paths/brushes and HarfBuzz text runs. |
| Extensibility | Per-request custom format providers and public ImageInfo factory; core stays managed and dependency-free. |
| Evaluation/adoption | Licensed pinned multilingual corpus tools, annotated-region CER/WER, quadrilateral scorer, classical/AI orientation audit, profile ablations, source/release parity, concurrent workloads, recipes and platform/package validation. |

Implementation details and API examples: [advanced imaging guide](docs/ADVANCED_IMAGING.md).

## Verified results

- Analyzer-enabled Release solution tests: **4,360 core + 173 AI + 6 Skia + 90 LittleCMS on .NET 10**, and
  **4,372 core + 173 AI + 6 Skia + 90 LittleCMS on .NET 8**; 9,270 passing tests, no failures/skips.
- Additional scalar-only .NET 10 core run: **4,360 tests passed** with `DOTNET_EnableHWIntrinsic=0`.
- Linux x64 .NET 10 core: **4,360 tests passed**, zero failures/skips.
- Python evaluation tests: 29 passed, including convex quadrilateral overlap and maximum-cardinality matching.
- Complete fixture regeneration: **1,298 files matched**, zero differences; three provenance-comparison regression tests passed.
- Six downstream tensor parity tests passed (two EasyOCRSharp, four PaddleOCRNet); real cached-model smoke
  tests passed in both consumers. Each engine also produced identical successful text and line counts on
  all 40 multilingual annotated regions against imaging 1.1.0 versus the updated source, with matching
  model hashes and zero failures. This establishes compatibility on this corpus, not universal accuracy.
- Published UVDoc/orientation checksum-matched model smoke passed on real and synthetic documents;
  full-size dimensions retained and mappings correctly reported unavailable for image-only model output.
- Framework-dependent and self-contained trimmed Windows smoke: **52 checks, zero failures** each. Linux x64 Native AOT and ARM64 Native AOT under QEMU also passed 52 each; see [platform evidence](docs/platform-verification.md).
- All four local 1.2.0 packages and symbol packages built; README/license/icon, both frameworks/XML docs,
  core dependency freedom and optional-package core dependency version verified. No upload/publish performed.
- Earlier reusable tensor ShortRun: 320x48 allocating helper 90.35 us versus reusable writer 29.84 us;
  approximately 184.6 KB output allocation avoided. Large-image timings varied; no universal speedup claimed.
  See [recorded tensor measurements](benchmarks/results/tensor-performance-2026-10-14.json).
- Fused resize ShortRun (after byte-output optimization) reduced temporary allocations at 960x960 from
  about 4.45 MiB to 50.3 KiB, but took 12.61 ms versus 6.46 ms for materialized parallel resize. It is an opt-in
  memory tradeoff; no large-image latency gain is claimed. See [fused measurements](benchmarks/results/fused-tensor-performance-2026-10-14.json).
- Warmed concurrent core run at concurrency 1: fused resize/tensor allocated **0.057 MB/request** versus
  **4.689 MB/request** materialized (98.8% lower), at 72.3 versus 74.3 requests/s. Reduced JPEG at half,
  quarter and eighth size allocated 1.457/0.376/0.105 MB versus 5.778 MB for full decode. Concurrency and
  sampled working-set results vary; [complete workload measurements](benchmarks/core-throughput-notes.md)
  explicitly avoid a universal speedup or proportional peak-memory claim.
- Audited orientation experiment: 19 pages, 10 tuning and 9 held-out source-page families. The tuning-selected
  AI threshold accepted 22/36 held-out rotations, all correct; the classical threshold accepted none.
  [Probability calibration](benchmarks/ocr/results/orientation-probability-calibration-2026-10-14.json)
  improved the classical Brier score but worsened AI calibration; no defaults were changed.
- Six OCR configurations completed all **240 observations without processing failures**. On 20 held-out
  regions from 10 source pages, character-weighted Paddle CER was **51.04%** with baseline preprocessing,
  **41.70%** with ScannedDocument and **47.19%** with PhoneDocument; EasyOCR baseline was **35.17%**.
  Lower is better. These paired clean/synthetic crops are a small annotation-selected task, not real-photo
  or full-page validation. Both profiles improved the controlled subset but worsened clean-crop CER
  (baseline 39.73%, ScannedDocument 41.94%, PhoneDocument 49.84%); defaults remain unchanged.
  See [results and per-script comparison](benchmarks/ocr/RESULTS-2026-10-14.md), [methods](benchmarks/ocr/README.md) and [complete quality report](benchmarks/ocr/results/mdp-block-quality-summary.json).
- Reproducible source/release/model reports: [OCR results](benchmarks/ocr/results).

## Completion status and capability boundaries

| Phase | Implemented scope and evidence limits |
| --- | --- |
| 1: baselines | Corpus download/selection/provenance, text/detection metrics, per-script/category reports and concurrent core workloads. Public data is held out from local threshold tuning, not proven absent from model training. Full-page annotation gaps and photo-coordinate mismatch are explicitly separated from valid text-region evaluation. |
| 2: correctness | Documented wide-precision/alpha/page paths implemented and regression-tested. This does not promise wide precision in every legacy effect. |
| 3: tensor preprocessing | Reusable/fused tensors and both consumer migrations implemented; independent tensors and source/release comparisons provide parity evidence. Fusing is an opt-in memory/latency tradeoff. TrOCR tensor contracts are tested; its real model remains unavailable locally. |
| 4: adaptive preprocessing | Profiles, diagnostics, maps, actual AI contracts, orientation threshold experiments and profile ablation tooling implemented. Scores are not calibrated probabilities; small-corpus experiments do not establish a production threshold or universal accuracy improvement. |
| 5: resources and throughput | Request-local settings, encoded/pixel limits, cooperative cancellation, headers/pages, direct reduced JPEG, seekable 64-bit TIFF ROI/tiles, process isolation and warmed concurrent measurements implemented. In-process pixel limits exclude scratch/native memory; use the separate OS envelope. Linux virtual-address limits differ from Windows committed-memory limits. |
| 6: general 2D | Managed color conversions, optional full standard-profile LittleCMS transforms, interop/drawing/shaped runs and codec-provider APIs implemented. Paragraph layout, device-link/named-color workflows and bundled PDF/SVG/HEIF/AVIF rasterizers are outside this release. |

Large TIFF regions are supported through the separate seekable API, including tested sparse multi-gigabyte
files and ten-gigapixel pages. Supported sample/layout/compression contracts and segment budgets are listed
in [TIFF regions](docs/tiff-region-decoding.md). Ordinary buffered decoders still require encoded files below
2 GiB. [Reduced JPEG](docs/reduced-jpeg-decoding.md) saves component/output storage while retaining full
progressive coefficient grids. Neither path means every image can be fully materialized in memory.

Native ICC is verified on Windows and Linux; deployment requirements remain in the optional package.
Windows trimming, Linux x64 Native AOT and emulated ARM64 Native AOT are verified locally. Physical ARM64
and macOS CI execution remain external validation, not inferred from configuration. Windows Native AOT
still needs the host C++ linker; Linux native execution is separate evidence.

Production traffic, broader manually audited multilingual/photo geometry ground truth and consumer-specific
accuracy thresholds remain deployment validation. No promise of increased downloads or successful OCR on
all images is made. Publishing packages, running remote CI and measuring adoption are release activities,
not silently performed by this implementation pass.

Sibling source migrations remain opt-in through `EasyImageSharpSourceProject` until the coordinated 1.2.0
release. Default NuGet paths preserve released-package compatibility. The original design below records
motivation and aspirational outcome gates; implemented APIs do not automatically satisfy empirical claims.

## Objective

Improve image-processing correctness, OCR accuracy, speed, memory efficiency, and general 2D image support while preserving the managed core and compatibility with EasyOCRSharp and PaddleOCRNet.

Prioritize measurable improvements to existing workflows before expanding the codec list. Broader adoption should come from reliable capabilities, useful integrations, and reproducible results.

## Review baseline

The review covered core imaging, document processing, tensor conversion, AI integration, tests, benchmarks, and relevant code in the local EasyOCRSharp and PaddleOCRNet repositories.

- Core tests passed: 4,145 tests, zero failures or skips, .NET 10, Release.
- Command: `dotnet test tests/EasyImageSharp.Tests/EasyImageSharp.Tests.csproj -c Release -f net10.0 --no-restore --verbosity quiet`.
- Performance benchmarks were inspected, not rerun. Proposed performance gains remain hypotheses until measured.
- The AI test suite, other runtimes/platforms, and full downstream OCR evaluations were not run during this review.
- Existing functionality includes ten codecs, advanced thresholding, morphology, page detection, perspective correction, SIMD, parallel processing, and optional AI orientation/dewarping.
- `ROADMAP.md` contains outdated statements about missing APNG, BigTIFF decoding, API tracking, samples, benchmarks, and verification jobs. Reconcile it with the current implementation.

## Findings that guide priorities

| Finding | Implementation evidence | Impact |
| --- | --- | --- |
| OCR preprocessing uses a fixed sequence, always starts with grayscale, and defaults to binarization | `src/EasyImageSharp/Processing/Document/ImageProcessingContext.Document.cs`, `DocumentTypes.cs` | Different models and image types need different preprocessing; quality must be evaluated per workflow. |
| Tensor helpers allocate output arrays and convert through 8-bit pixels | `src/EasyImageSharp/Tensors/ImageTensorExtensions.cs` | Opportunities to reuse buffers, reduce conversions, and preserve precision. |
| PaddleOCRNet recognition expects BGR, padded batches, and specific resizing behavior | Downstream `Internal/Recognition/SvtrRecognizer.cs` | Existing RGB helpers cannot replace model preprocessing without explicit contracts and parity tests. |
| Arbitrary rotation converts the whole source to `Rgba32` and iterates destination rows serially | `src/EasyImageSharp/Processing/FrameOps.cs` | High-bit-depth precision is lost on this path; allocations and parallelism warrant investigation. |
| Automatic document rotation/cropping analyzes the root frame and applies the resulting operation across frames | `src/EasyImageSharp/Processing/Document/ImageProcessingContext.Document.cs` | Independent TIFF pages may need different orientation and crop decisions. |
| Stream loading and identification buffer encoded input; async loading then decodes synchronously | `src/EasyImageSharp/Image.cs` | Large inputs need bounded buffering, incremental reading, and processing cancellation. |
| Execution configuration is process-wide | `src/EasyImageSharp/Configuration.cs` | Concurrent requests cannot independently control image-processing parallelism. |
| Classical orientation is based on Latin-script heuristics | `src/EasyImageSharp/Processing/Document/OrientationOps.cs` | Multilingual and sparse documents need evaluated alternatives and confidence-based abstention. |

## Implementation principles

- Keep new APIs additive and preserve existing behavior unless a compatibility-reviewed correctness fix requires otherwise.
- Retain the managed, inference-independent core. Put inference and platform/native integrations in optional packages.
- Reuse existing algorithms and AI operations before introducing competing implementations.
- Treat model channel order, normalization, sampling, rounding, padding, and coordinate conventions as contracts.
- Measure OCR quality separately from visual quality. Attractive preprocessing is not evidence of better recognition.
- Publish a capability matrix rather than promising to handle every possible image without limits.
- Set numeric performance targets after establishing representative baselines.

## Phase 1: establish accuracy and performance baselines

Priority: P0. Dependency: none.

### Work

- Extend existing fixtures and benchmarks with clean scans, phone photos, receipts, screenshots, faint text, shadows, transparent images, mixed-orientation TIFF pages, and large inputs.
- Include Latin, Arabic/Urdu, Devanagari, CJK, and mixed-script documents.
- Maintain a licensed, reproducible corpus with labeled text and geometry where applicable; separate tuning data from held-out evaluation data.
- Pin EasyOCRSharp/PaddleOCRNet model versions and preprocessing settings in evaluation reports.
- Measure character and word error rates, text detection precision/recall, latency, allocations, peak memory, and concurrent throughput.
- Separate cold-start/model loading from steady-state inference and preprocessing.
- Record hardware, framework, execution provider, thread settings, and image dimensions.
- Reconcile the existing roadmap and stale implementation comments with actual capabilities.

### Completion gate

- Reproducible baseline reports exist for both OCR consumers and core image operations.
- Each proposed optimization has a relevant workload and correctness reference.
- Numeric targets and tolerated regressions are agreed from measured baselines, before tuning.

## Phase 2: fix precision and document correctness

Priority: P0. Dependency: baseline fixtures from phase 1.

### Work

- Preserve 16-bit/float precision in arbitrary rotation and tensor conversion; retain efficient 8-bit paths.
- Audit alpha behavior in rotation, perspective correction, resizing, and compositing against independent references.
- Add explicit per-page analysis for independent document frames.
- Keep animation geometry semantics separate, where consistent transformations across frames may be intentional.
- Validate degenerate quadrilaterals, non-finite parameters, extreme output sizes, and tensor normalization parameters.
- Document which operations preserve precision and which intentionally reduce it.
- Track public API additions using the existing API analyzer workflow.

### Completion gate

- High-precision gradients retain detail through supported processing paths.
- Transparent-edge fixtures show correct compositing and resampling.
- Mixed-page documents receive independent corrections.
- Existing compatibility tests pass, with any intentional behavior changes documented.

## Phase 3: build reusable tensor preprocessing

Priority: P0. Dependencies: phases 1 and 2 for numerical and precision guarantees.

### Proposed API capabilities

The following originally proposed names are now implemented: `WriteTensor(...)` and `ResizeToTensor(...)`.

- Caller-provided destination memory with clear ownership and lifetime rules.
- RGB, BGR, and grayscale channel order.
- CHW/HWC layouts, batch offsets, and explicit row/plane strides.
- Normalization, padding values, and shape validation.
- Model-specific resize rounding and sampling behavior.
- Returned scale/padding information for mapping OCR coordinates to the original image.

### Implementation sequence

1. Implement destination-writing APIs without changing existing allocating helpers.
2. Add golden tensor tests against current downstream preprocessing and independent model reference pipelines.
3. Migrate one detector and one recognizer in each OCR consumer.
4. Fuse resize, channel conversion, and normalization where profiling justifies it.
5. Extend adoption after tensor parity, OCR quality, and allocation results are established.

Preserve existing intermediate rounding before introducing a different numerical path. SIMD and fused implementations must have a scalar/reference comparison.

The existing AI integration already uses `OrtValue`; reusable input/output buffers can extend that design rather than replacing it wholesale.

### Completion gate

- Reference tensors match within explicitly declared tolerances.
- Held-out OCR quality is unchanged or improved.
- Allocation and latency improvements are demonstrated in both representative detector and recognizer workloads.
- Buffer ownership remains correct under concurrent requests and cancellation.

## Phase 4: introduce adaptive OCR preprocessing

Priority: P1. Dependencies: baseline evaluation and coordinate/tensor contracts.

Keep the existing preset available. Introduce opt-in profiles and diagnostics through additive APIs.

| Proposed profile | Intended behavior |
| --- | --- |
| Neural OCR | Preserve color/detail and apply only model-compatible corrections. |
| Scanned document | Optional background normalization, deskew, and binarization. |
| Phone document | Orientation and page geometry first, with optional shadow correction and dewarp. |
| Screenshot | Preserve small text and avoid unnecessary smoothing. |

### Work

- Add document analysis and a preprocessing result that reports quality estimates, confidence, applied/skipped operations, per-stage timings, and reasons for abstaining.
- Return original-to-processed coordinate mappings; use an appropriate mapping representation for nonlinear dewarping rather than assuming every transform is a homography.
- Evaluate inexpensive quality signals before choosing expensive or destructive processing.
- Reuse existing AI orientation/dewarping capabilities and inspect opportunities to consolidate duplicate downstream logic.
- Audit the dewarp model/export contract and full-resolution detail preservation. Enlarging a small rectified output does not restore lost text detail.
- Evaluate each stage independently and in combination, including the unprocessed baseline.
- Calibrate confidence on held-out data; skip uncertain automatic corrections.

### Completion gate

- Held-out OCR results improve without material regressions on clean documents.
- Results are reported per image category and script, not only as an aggregate.
- Returned geometry maps OCR results correctly to source coordinates.
- Optional AI failures and unavailable models have explicit, documented behavior.

## Phase 5: improve memory use and server throughput

Priority: P1. Dependency: measured workloads from phase 1; coordinate with phase 3.

### Work

- Add per-operation execution settings while retaining current defaults.
- Coordinate image parallelism with OCR batch/inference parallelism to avoid CPU oversubscription.
- Reuse luminance planes, intermediate analysis, and scratch buffers where ownership permits.
- Add encoded-input and working-memory budgets beyond existing pixel/frame limits.
- Propagate cancellation into long-running decode and processing loops.
- Introduce incremental header identification, followed by streaming/page-by-page decode where codec structure permits.
- Investigate reduced-resolution JPEG decoding and tiled/region TIFF access using workload evidence.
- Optimize measured hotspots and verify accelerated/scalar behavior on x64 and ARM64.
- Treat gigapixel processing as a separate tiled storage/processing design. BigTIFF parsing alone does not provide it.

### Completion gate

- Peak memory, latency, and concurrent throughput improve on representative workloads.
- Limits reject oversized work before the corresponding allocation.
- Cancellation is observed during expensive processing, not only input reads.
- Scalar, SIMD, and parallel paths satisfy declared correctness tolerances.

## Phase 6: expand general 2D capabilities

Priority: P2. Dependency: stable correctness and performance foundations; prioritize using actual user demand.

| Addition | Scope and rationale |
| --- | --- |
| Color correctness | Add color-space conversion and validated ICC transforms; ICC passthrough already exists. |
| Interop | Optional adapters for desktop and computer-vision buffers, with explicit stride, pixel layout, ownership, and copy behavior. |
| Drawing and text | Richer paths and Unicode shaping in optional packages; preserve lightweight annotation support in core. |
| Format extension points | Optional codec providers and document rasterizers, keeping platform dependencies explicit. |
| Image diagnostics | Blur, exposure, contrast, blank-page detection, and duplicate-page similarity. |

Publish a capability matrix covering formats, bit depths, color spaces, dimensions, metadata behavior, and supported operations. Unsupported inputs must produce precise errors.

### Completion gate

- Each addition has an identified consumer use case, independent validation, and an explicit maintenance owner/scope.
- Optional integrations do not silently change core deployment requirements.

## Adoption and release plan

Position the library around managed .NET imaging, proven OCR preprocessing, and efficient model input preparation.

- Publish working integration examples for EasyOCRSharp and PaddleOCRNet.
- Show before/after images alongside recognized text and measured accuracy.
- Publish reproducible benchmarks with equivalent settings and quality targets.
- Clearly separate core features, AI dependencies, and required model downloads.
- Provide recipes for receipts, phone scans, thumbnails, and multi-page TIFF workflows.
- Track downloads, dependent projects, and onboarding issues per release. Downloads are an outcome to measure, not a guaranteed consequence of adding features.
- Preserve existing consumers through additive APIs and staged migration.
- Run relevant core/AI tests, downstream compatibility checks, API validation, AOT/trimming checks, and platform coverage for affected components.

## Recommended first milestone

Deliver a focused release containing:

1. Reproducible OCR and performance baseline evaluation.
2. Independent document-page orientation/crop handling.
3. Precision fixes for arbitrary rotation and tensor conversion.
4. Reusable RGB/BGR tensor writing with padding/stride contracts.
5. Pilot migrations of detector/recognizer preprocessing in both OCR libraries.

Adaptive preprocessing follows once its accuracy can be measured. Streaming, large-image architecture, new codecs, and rich typography should not block this milestone.

## External technical references

- [ONNX Runtime C# guidance](https://onnxruntime.ai/docs/get-started/with-csharp): reusable tensor memory and `OrtValue` integration.
- [PaddleOCR document preprocessing](https://paddlepaddle.github.io/PaddleOCR/main/en/version3.x/pipeline_usage/doc_preprocessor.html): separate orientation and geometric unwarping stages.

These references support implementation direction; they do not establish performance or accuracy gains for EasyImageSharp.

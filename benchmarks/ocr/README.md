# Reproducible OCR integration and quality evaluation

The corpus experiments compare the same sibling EasyOcrSharp/PaddleOcrNet source against released and current-source EasyImageSharp dependencies. They are small, local experiments, not official OCR benchmark scores or universal accuracy claims. Reports fingerprint the imaging/OCR assemblies, input images, model cache assets, source annotation revision and corpus manifests.

## Corpus and labels

`fetch_mdpbench.py` selects four document families for each of five languages from a pinned public MDPBench revision: English, Arabic, Hindi, Chinese and Japanese. The selection order is a fixed SHA256 function of family identity, independent of predictions. Two families per language form the tuning split and two form held-out. Original clean/photo variants always remain together.

Full-page text references omit some page elements. In addition, the photographic annotations reuse clean-page polygon coordinates despite different canvas dimensions and perspective. Consequently, the full-page Paddle export is **diagnostic only**; it cannot establish full-page OCR accuracy or validate photo-region detection.

For example, `en_newspaper_30` is 6792 × 8534 pixels while its selected photo is 1536 × 2048, yet both annotation records contain the same polygon coordinates. The crop generator rejected the out-of-bounds photo region before evaluation; it did not invent a scale or homography.

`prepare_block_corpus.py` creates a narrower labeled task from the 20 digital originals. It selects the largest polygon-area nonempty `text_block`, with original annotation order as the tie-breaker. If there is no text block, it selects the largest eligible title/header/footer/figure-caption/table-caption/page-footnote. The one fallback in this subset is the sideways table caption in `en_paper_41`; it is retained without correcting its orientation. No selection criterion uses OCR predictions.

The selected polygon is cropped using its clipped floor/ceil bounding box; pixels outside the polygon become white. The crop is saved as RGB PNG without resampling or EXIF orientation correction. Exact block text becomes the label. Source-image, crop and label hashes, annotation category/index/polygon and crop coordinates are retained. Pillow 11.3.0 performs preparation with an explicit 150-million-source-pixel limit; the 137.5-megapixel Japanese original yields a much smaller block without bypassing OCR service limits on the resulting input.

Each clean crop has one explicitly synthetic counterpart: 12-pixel white border, 2° clockwise bilinear expanded rotation, smooth multiplicative illumination from 166/255 to 1, then Gaussian blur radius 0.45 pixels. These fixed transforms were specified before OCR comparison. All text and the exact label are retained. These are controlled challenges, **not substitutes for real phone photographs**. There are 40 crops from 20 source-page families: 20 tuning and 20 held-out observations, each containing paired clean/controlled views. Photo originals are listed as excluded because their annotation geometry is unusable without registration.

This measures **text-block recognition with OCR detection**, not page-layout detection or end-to-end full-page recognition. It favors large annotated text, retains annotation errors and difficult orientations, and does not represent all small text, scripts, document styles, or deployment populations. Public model-training overlap and shared upstream book/document provenance are unknown. The split prevents same-page variant leakage, not necessarily shared-book leakage. Only 10 source-page families form the held-out split; statistical independence across those families is not established.

## Run

Use Python 3.11 and Pillow 11.3.0. The exporter requires the sibling EasyOcrSharp and PaddleOcr repositories; project locations are configurable in its `.csproj`. Build each dependency mode into a separate artifact directory, and preserve both outputs. The `EasyImageSharpSourceProject` MSBuild property selects the current imaging project; omitting it selects the sibling libraries' released package dependency.

```powershell
python -m pip install Pillow==11.3.0
python benchmarks/ocr/fetch_mdpbench.py artifacts/ocr-corpus
python benchmarks/ocr/prepare_block_corpus.py artifacts/ocr-corpus/manifest.json artifacts/ocr-corpus/MDPBench_public.json artifacts/ocr-block-corpus

dotnet build tools/OcrCorpusExport -c Release -p:EasyImageSharpSourceProject="$PWD/src/EasyImageSharp/EasyImageSharp.csproj" -o artifacts/corpus-source
dotnet build tools/OcrCorpusExport -c Release -p:EasyImageSharpSourceProject= -o artifacts/corpus-release

python -u benchmarks/ocr/run_roi_matrix.py artifacts/corpus-source/OcrCorpusExport.exe artifacts/corpus-release/OcrCorpusExport.exe artifacts/ocr-block-corpus/manifest.json artifacts/ocr-evaluation tmp/roi-logs
python -m unittest discover -s benchmarks/ocr -p "test_*.py"
```

The matrix runs six configurations sequentially: both OCR engines with source/released imaging, then Paddle with `ScannedDocument` and `PhoneDocument` profiles. The profile ablations use the same source build and unchanged default profile settings; no tuning is performed on held-out results. Source baselines may download required registry models; released baselines and profile runs are offline. Missing models fail visibly. Reports record whether downloads were allowed.

`run_export_batches.py` starts a fresh process per language (eight crops), with one intra-op and one inter-op CPU thread. Only one OCR engine process runs at a time. It samples resident memory every 0.5 seconds and terminates a batch over 4 GiB or 30 minutes. This sampled guard is not a hard allocation cap. A batch-level guard failure aborts the run rather than emitting an incomplete, apparently successful report. Per-image exceptions remain explicit rows, with empty prediction and their full error contribution. Completed batches resume only when manifest and assembly hashes match. Model hash changes between batches fail aggregation. Repeated reads/hash scans can make finalization slower than the last image log suggests.

## Interpret reports

`summarize_exports.py` produces character-weighted CER and word-weighted WER separately by split, script, clean/controlled category, and their intersections. CER counts Unicode scalar values. WER splits whitespace and is not language-neutral for Chinese/Japanese. Text is trimmed only, case-sensitive, with no prediction-driven normalization. Errors can exceed 100% when insertions dominate. Failed images remain in denominators. Cold timings include loading and model initialization and are excluded from the quality comparison.

`compare_exports.py` checks exact successful text parity, line counts, failure transitions, identical image/label inputs, and common model-cache asset hashes. Successful means no recorded exception, not a correct transcription; identical predictions can be equally wrong. Empty failed predictions are not counted as successful parity. Cache snapshots include unused assets, so additional cached files are reported separately and do not prove a model was used. These comparisons validate this corpus and configuration, not all possible OCR inputs or binary compatibility.

The compact machine-readable summaries, per-observation metrics, parity fingerprints and corpus provenance are tracked under [results](results); see [the measured results](RESULTS-2026-10-14.md). Full-text combined/per-language exports are retained locally in ignored `artifacts/ocr-evaluation`, and images/annotations in ignored corpus artifact directories. Report paths are relative to each summary file. The matrix writes its summaries beside raw exports; copy the three compact summaries to `benchmarks/ocr/results` and regenerate them with that output path to retain correct relative raw-report references. Deterministic preparation/orchestration scripts are tracked. The dataset metadata records its upstream declared Apache-2.0 license and pinned source revision. No private user documents are included.

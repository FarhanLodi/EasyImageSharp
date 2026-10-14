# Orientation acceptance threshold experiment

First fetch the corpus with `benchmarks/ocr/fetch_mdpbench.py`, then derive a separate manifest from the hash-verified visual audit:

```powershell
python benchmarks/ocr/create_orientation_manifest.py artifacts/ocr-corpus/manifest.json benchmarks/results/orientation-source-audit-2026-10-14.json artifacts/ocr-corpus/orientation-audited-manifest.json
dotnet run --project tools/OrientationCalibration -c Release -- --self-test
dotnet run --project tools/OrientationCalibration -c Release -- artifacts/ocr-corpus/orientation-audited-manifest.json benchmarks/ocr/results/orientation-threshold-2026-10-14.json
# Optional published AI model comparison, offline and pinned by registry SHA256:
dotnet run --project tools/OrientationCalibration -c Release -- artifacts/ocr-corpus/orientation-audited-manifest.json benchmarks/ocr/results/orientation-ai-threshold-2026-10-14.json path/to/PP-LCNet_x1_0_doc_ori.onnx
```

The tool uses only `category: digital` pages, assumes those source pages are upright, and evaluates exact clockwise rotations 0/90/180/270. Ground truth is the inverse correction: 0/270/180/90. A source image is never changed. Each manifest row needs `id`, relative `path`, `family`, `script`, and `split` (`tuning` or `held-out`); optional `sha256` values are verified. Families and identical encoded files cannot cross splits.

With the optional third argument, the same experiment uses `DetectOrientationAsync` and the published PP-LCNet document orientation export. Its checksum must match the registry. The run uses offline CPU execution with one intra-op and one inter-op thread, and records model and AI library hashes. Model softmax scores are not assumed to be calibrated probabilities of correct decisions. The classical and AI reports select thresholds independently from the same tuning pages; neither uses held-out outcomes for selection.

The acceptance rule is `confidence >= threshold`. Rejected predictions leave the supplied image unchanged. Threshold candidates include zero, values immediately above each observed tuning confidence, and a value above one representing abstain-all. Select on **tuning only** by minimizing wrong applied nonzero corrections, then maximizing accepted estimates, then choosing the smallest threshold. The held-out split never affects this selection. A threshold above one means the experimental policy rejects all estimates; it is deliberately not a valid value for existing APIs accepting only [0,1]. Such a result means the experiment found no safe accepted correction, not a recommendation to bypass validation.

The report includes accept-all and abstain-all baselines, coverage, accepted correct/incorrect estimates, applied nonzero corrections, wrong corrections and their rate, final upright rate, page/family counts, per-script summaries, and every observation's confidence and correctness. Wrong accepted no-op predictions are included in `WrongAccepted`; `WrongCorrections` counts only a nonzero action that leaves a wrong orientation. Rejecting a rotated page can lower final upright rate even while avoiding a wrong correction. Read these metrics together; coverage alone is not quality.

Confidence remains an uncalibrated detector score, not an empirically verified correctness probability. Four rotations of one page are dependent observations; do not treat 76 rotation rows as 76 independent documents. The small public multilingual subset does not establish performance on phone photos, arbitrary scripts, low-text pages, or a deployment population. Source upright orientation must be audited. Timings measure detection and include first-use JIT/model-loading effects, so they are diagnostic rather than a throughput claim.

Reports contain source and library hashes, runtime and library version, and no machine-specific input paths. The self-test checks inclusive acceptance, threshold selection, wrong correction counting and abstention outcomes independently of detector behavior. This experiment does not change library defaults.

The [2026-10-14 run](../../benchmarks/ocr/results/orientation-threshold-2026-10-14.json) used 19 visually audited digital pages across five scripts (10 tuning families and 9 held-out families). Its selected threshold was approximately 0.85494. It accepted 3/40 tuning rotations and **0/36 held-out rotations**. Accept-all produced 22/36 correct held-out orientation decisions and 9 wrong nonzero corrections among 25 applied corrections. The selected policy applied no held-out corrections, leaving just the 9 originally upright views upright. This is evidence that threshold selection alone did **not** produce useful held-out coverage; it does not support a new production default or a claim of improved multilingual orientation accuracy.

The [published AI model comparison](../../benchmarks/ocr/results/orientation-ai-threshold-2026-10-14.json) evaluated the identical pages and rotations. Accept-all produced 31/36 correct held-out decisions, with 4 wrong nonzero corrections among 27 applied corrections. Its tuning-selected threshold was approximately 0.92251: held-out coverage was 22/36 (61.1%), all 22 accepted predictions were correct, and all 16 applied nonzero corrections were correct. Final upright count was 25/36 (69.4%); the remaining rejected rotated views were unchanged. All five incorrect unthresholded held-out predictions were on Arabic pages. This suggests a useful optional AI alternative on this subset, while demonstrating script-specific limitations and the coverage/abstention tradeoff. Zero observed accepted errors on these few pages is not a guarantee of zero deployment error. No library default was changed.

The [source audit](../../benchmarks/results/orientation-source-audit-2026-10-14.json) records every original source image hash. `en_paper_41` was excluded for semantic label ambiguity: its running header/footer are upright while the dominant sideways table requires a 90-degree clockwise correction. This eligibility decision was based on the source layout, before rerun prediction analysis; both detectors used the same 19 eligible sources. The original OCR manifest and ROI manifests were unchanged. The new manifest records its original manifest hash and audit hash. Exact residual skew was not measured; these are cardinal orientation labels. The successful AI rerun needed a 3 GiB managed heap cap for the 137-megapixel magazine image after a 2 GiB capped attempt exhausted memory; no source was silently omitted.

## Experimental probability calibration

The separate offline analysis fits isotonic regression to **correct-estimate outcomes on tuning rows only**. It groups tied scores with their observation counts, applies weighted pool-adjacent-violators (PAV), interpolates between fitted scores, and clamps predictions outside the tuning score range. It uses all estimates regardless of acceptance threshold. Held-out labels/scores never enter the fit; source families, encoded-image hashes and IDs cannot cross splits. This analysis does not modify the detector, acceptance thresholds, or library defaults.

```powershell
python -m unittest discover -s benchmarks/ocr -p test_orientation_calibration.py
python benchmarks/ocr/calibrate_orientation.py benchmarks/ocr/results/orientation-threshold-2026-10-14.json benchmarks/ocr/results/orientation-ai-threshold-2026-10-14.json benchmarks/ocr/results/orientation-probability-calibration-2026-10-14.json
```

The [tracked analysis](../../benchmarks/ocr/results/orientation-probability-calibration-2026-10-14.json) records input report hashes, fitted knots, held-out Brier scores, five fixed equal-width reliability bins/ECE, and per-script counts and metrics. Empty bins have null means; the last bin includes probability 1. Lower Brier/ECE is better for these fixed definitions.

| Detector | Held-out raw Brier | Isotonic Brier | Raw ECE | Isotonic ECE |
| --- | ---: | ---: | ---: | ---: |
| Classical | 0.26991 | 0.25485 | 0.26622 | 0.16349 |
| Published AI | 0.10277 | 0.10318 | 0.04206 | 0.07901 |

Calibration improved these classical metrics but **worsened both AI metrics** on the held-out subset. Ten tuning families supply 40 dependent rotated views; nine held-out families supply 36 views. Sparse per-script results and empty/small reliability bins cannot establish population calibration. ECE depends on the chosen bins; Brier reflects discrimination as well as calibration. No production-calibrated confidence claim, statistical significance claim, or recommendation to enable these fitted maps follows from this small experiment.

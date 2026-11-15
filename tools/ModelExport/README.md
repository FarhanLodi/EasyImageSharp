# ModelExport

Reproducible scripts that produce and evaluate the int8 variants of the EasyImageSharp.AI registry models, plus the
numpy reference of the YuNet face-detector post-processing that the C# `FaceDetector` follows.

| File | Purpose |
| --- | --- |
| `quantize_int8.py` | Quantises each requested model (static QDQ), evaluates int8 against fp32 with a task metric, keeps it only when the pre-declared tolerance holds, and writes `int8-report.json`. |
| `common.py` | Synthetic document pages, image loading, tensor conversion mirroring the C# operations, PSNR / SSIM / IoU. |
| `yunet_reference.py` | YuNet decoding (score, boxes, five landmarks) and greedy NMS in numpy; the executable specification of `Operations/Faces/FaceDetector.cs`. |
| `fetch_inputs.py`, `inputs.tsv` | Downloads the YuNet fp32 file and the calibration / evaluation photos from their sources and verifies every SHA-256. |

## Procedure

Python 3.11 with `onnx` 1.19, `onnxruntime` 1.29, `numpy` and `Pillow` 11. Run everything with `python -I` and keep
downloads in fresh directories outside the repository.

```bash
# 1. Inputs: YuNet fp32 + licence, scikit-image photos, NASA crew portraits (all checksum-verified).
python -I tools/ModelExport/fetch_inputs.py --dest /tmp/mx-inputs

# 2. fp32 registry models: copy them from a warmed cache (already verified against the pinned SHA-256 by ModelHub),
#    for example after ImageAiSession.WarmUpAsync(), and add the YuNet file.
mkdir /tmp/mx-fp32
cp "$LOCALAPPDATA/EasyImageSharp/models/"*.onnx /tmp/mx-fp32/
cp /tmp/mx-inputs/models/face_detection_yunet_2026may.onnx /tmp/mx-fp32/

# 3. Quantise and evaluate (a few minutes per small model; UVDoc and U2-Net take much longer).
python -I tools/ModelExport/quantize_int8.py --fp32-dir /tmp/mx-fp32 \
    --images-dir /tmp/mx-inputs/images --faces-dir /tmp/mx-inputs/faces --out-dir /tmp/mx-int8 --threads 2
```

Passing int8 files are copied to `--out-dir` next to `int8-report.json`; failing ones are not. With the inputs above
and onnxruntime 1.29.0 the run is deterministic (the staged YuNet and PP-LCNet files were reproduced byte for byte).

## Method and tolerances

Every registry model is a convolutional network, so static quantisation is used throughout (dynamic quantisation pays
off for MatMul-heavy graphs). Weights are per-channel symmetric int8, activations asymmetric uint8, in QDQ format.
Calibration statistics are accumulated two samples at a time to bound memory. The strategies are tried in a fixed
order, stopping at the first that meets the tolerance: MinMax over every supported op; percentile 99.999 with the
first and last convolution kept in float; the same restricted to Conv nodes.

The tolerances are fixed in `TOLERANCES` and were set before any model was evaluated; do not tune them to a result.

| Model | Metric (evaluation split disjoint from calibration) | Tolerance |
| --- | --- | --- |
| doc-orientation | accuracy on 240 rotated synthetic page views, agreement with fp32 | accuracy change >= -1 point, agreement >= 99% |
| doc-dewarp | PSNR / SSIM of the int8 output against the fp32 output on 10 warped synthetic pages | >= 30 dB and >= 0.95 |
| super-resolution-x4 | PSNR / SSIM against the true high-resolution crop (24 crops) | mean change >= -0.3 dB and >= -0.01; PSNR vs fp32 >= 35 dB |
| denoise-gray | PSNR against the clean crop at sigma 15 / 25 / 50 (36 cases) | mean change >= -0.3 dB, worst >= -0.6 dB |
| saliency, saliency-fast | IoU of the 0.5-thresholded masks against fp32, map MAE | mean IoU >= 0.95, min >= 0.85, MAE <= 0.02 |
| binarization | black/white agreement and ink IoU against fp32 (12 pages) | >= 99.5% and >= 0.97 |
| face-detection | detections at score 0.6 matched at IoU 0.5 against fp32 | recall and precision >= 0.98, mean IoU >= 0.90, landmark error <= 5% of the face width |

Results of the 2026-11-04 run: YuNet, PP-LCNet orientation and SauvolaNet passed; Real-ESRGAN, DnCNN, U2-Net,
U2-Net-p and UVDoc did not (their int8 outputs drift too far from fp32 under these tolerances). The model card staged
with the files lists every metric.

## Data provenance

`inputs.tsv` is the authoritative list (URL, licence, SHA-256). In short: the YuNet weights come from
`opencv/opencv_zoo` at commit `26cc381e4d2594bb9f47a26eb8fd96c94a13660d` (MIT, Shiqi Yu); the photos are the
scikit-image 0.24.0 samples (public domain / CC0) and NASA crew portraits from Wikimedia Commons (public domain).
None of the data is committed to this repository.

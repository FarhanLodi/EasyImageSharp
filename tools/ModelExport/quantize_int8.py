"""Produces and evaluates int8 (static QDQ) variants of the EasyImageSharp.AI registry models.

Usage (see README.md for the full procedure):

    python -I tools/ModelExport/quantize_int8.py --fp32-dir <dir with the verified fp32 files>
        --images-dir <dir of CC0/public-domain photos> --faces-dir <dir of public-domain group photos>
        --out-dir <staging dir> [--models doc-orientation,denoise-gray,...]

For every requested model the script

1. pre-processes the fp32 graph (symbolic shape inference + graph optimisation),
2. calibrates activations on a deterministic calibration split and writes a static QDQ model
   (per-channel int8 weights, uint8 activations), trying the strategies in STRATEGIES in their fixed
   order (MinMax over every supported op; percentile with the first and last convolution left in float;
   the same restricted to Conv nodes so element-wise ops, resizes and sigmoids stay float) and stopping
   at the first one that meets the tolerance; every attempt is recorded,
3. evaluates fp32 and int8 on a disjoint evaluation split with a task-appropriate metric,
4. keeps the int8 file only when every tolerance below holds, and records the verdict, metrics,
   SHA-256 and timing in int8-report.json next to the staged files.

Every model here is a convolutional network, so static quantisation is used throughout (dynamic
quantisation only pays off for MatMul-heavy graphs and has no fast path for Conv on CPU).

The tolerances are fixed in TOLERANCES before any model is evaluated; do not tune them to a result.
"""
from __future__ import annotations

import argparse
import os
import shutil
import sys
import tempfile
import time
from typing import Callable

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import numpy as np  # noqa: E402
import onnx  # noqa: E402
import onnx.version_converter  # noqa: E402
import onnxruntime as ort  # noqa: E402
from onnxruntime.quantization import (  # noqa: E402
    CalibrationDataReader,
    CalibrationMethod,
    QuantFormat,
    QuantType,
    quantize_static,
)
from onnxruntime.quantization.shape_inference import quant_pre_process  # noqa: E402
from PIL import Image  # noqa: E402

import common  # noqa: E402
import yunet_reference  # noqa: E402

TOLERANCES = {
    # Orientation: int8 may lose at most one percentage point of accuracy and must agree with fp32 on 99% of views.
    "doc-orientation": {"min_accuracy_delta": -0.01, "min_agreement": 0.99},
    # Dewarp has no ground truth here: the int8 rectified page must stay close to the fp32 one.
    "doc-dewarp": {"min_psnr_vs_fp32": 30.0, "min_ssim_vs_fp32": 0.95},
    # Super-resolution: against the true high-resolution crop, int8 may lose 0.3 dB PSNR / 0.01 SSIM on average.
    "super-resolution-x4": {"min_psnr_delta": -0.3, "min_ssim_delta": -0.01, "min_psnr_vs_fp32": 35.0},
    # Denoise: against the clean crop, int8 may lose 0.3 dB on average and 0.6 dB in the worst case.
    "denoise-gray": {"min_psnr_delta": -0.3, "min_worst_psnr_delta": -0.6},
    # Saliency: thresholded masks (0.5 after the operation's min-max stretch) compared with fp32.
    "saliency": {"min_mean_iou": 0.95, "min_iou": 0.85, "max_mae": 0.02},
    "saliency-fast": {"min_mean_iou": 0.95, "min_iou": 0.85, "max_mae": 0.02},
    # Binarisation: black/white decisions compared with fp32.
    "binarization": {"min_pixel_agreement": 0.995, "min_ink_iou": 0.97},
    # Faces: detections at score >= 0.6 matched to fp32 detections at IoU >= 0.5.
    "face-detection": {"min_recall": 0.98, "min_precision": 0.98, "min_mean_iou": 0.90, "max_landmark_nme": 0.05},
}

SPECS = {
    "doc-orientation": ("PP-LCNet_x1_0_doc_ori.onnx", "PP-LCNet_x1_0_doc_ori.int8.onnx", "Apache-2.0"),
    "doc-dewarp": ("UVDoc.onnx", "UVDoc.int8.onnx", "MIT"),
    "super-resolution-x4": ("realesrgan_general_x4v3.onnx", "realesrgan_general_x4v3.int8.onnx", "BSD-3-Clause"),
    "denoise-gray": ("dncnn_gray_blind.onnx", "dncnn_gray_blind.int8.onnx", "MIT"),
    "saliency": ("u2net.onnx", "u2net.int8.onnx", "Apache-2.0"),
    "saliency-fast": ("u2netp.onnx", "u2netp.int8.onnx", "Apache-2.0"),
    "binarization": ("sauvolanet.onnx", "sauvolanet.int8.onnx", "MIT"),
    "face-detection": ("face_detection_yunet_2026may.onnx", "face_detection_yunet_2026may.int8.onnx", "MIT"),
}


class ListReader(CalibrationDataReader):
    """Feeds a precomputed list of {input_name: tensor} dictionaries.

    Supports the range protocol of ``CalibStridedMinMax`` so calibration statistics are accumulated a
    couple of samples at a time instead of holding every intermediate activation of every sample in
    memory at once (UVDoc and U2-Net need several GB otherwise). MinMax ranges merge exactly, so the
    stride does not change MinMax results.
    """

    def __init__(self, feeds: list[dict[str, np.ndarray]]):
        self.feeds = feeds
        self.start = 0
        self.end = len(feeds)
        self.index = 0

    def __len__(self) -> int:
        return len(self.feeds)

    def set_range(self, start_index: int, end_index: int) -> None:
        self.start, self.end = start_index, min(end_index, len(self.feeds))
        self.index = self.start

    def get_next(self):
        if self.index >= self.end:
            return None
        feed = self.feeds[self.index]
        self.index += 1
        return feed

    def rewind(self):
        self.index = self.start


def session(path: str, threads: int) -> ort.InferenceSession:
    options = ort.SessionOptions()
    options.intra_op_num_threads = threads
    options.inter_op_num_threads = 1
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    return ort.InferenceSession(path, options, providers=["CPUExecutionProvider"])


def run_first(sess: ort.InferenceSession, tensor: np.ndarray) -> np.ndarray:
    return sess.run([sess.get_outputs()[0].name], {sess.get_inputs()[0].name: tensor})[0]


# Quantisation strategies, tried in this fixed order until a variant meets its tolerance. Every attempt is
# recorded in the report, so a pass on the second strategy is visible as such.
STRATEGIES = (
    {"name": "minmax", "method": CalibrationMethod.MinMax, "keep_first_last_float": False, "conv_only": False},
    {"name": "percentile-99.999-first-last-float", "method": CalibrationMethod.Percentile, "keep_first_last_float": True,
     "conv_only": False},
    {"name": "percentile-99.999-conv-only-first-last-float", "method": CalibrationMethod.Percentile,
     "keep_first_last_float": True, "conv_only": True},
)


def quantize(fp32: str, int8: str, feeds: list[dict[str, np.ndarray]], op_types: list[str] | None, work: str,
             strategy: dict) -> None:
    source = fp32
    model = onnx.load(fp32)
    opset = next((o.version for o in model.opset_import if o.domain in ("", "ai.onnx")), 0)
    if opset < 13:
        # Per-channel QDQ needs the `axis` attribute of (De)QuantizeLinear, introduced in opset 13.
        source = os.path.join(work, "opset13.onnx")
        onnx.save(onnx.version_converter.convert_version(model, 13), source)
    del model
    pre = os.path.join(work, "pre.onnx")
    try:
        quant_pre_process(source, pre, skip_symbolic_shape=False)
    except Exception:  # symbolic inference cannot resolve every graph (YuNet); ONNX shape inference still runs
        quant_pre_process(source, pre, skip_symbolic_shape=True)

    model = onnx.load(pre)
    for i, node in enumerate(model.graph.node):
        if not node.name:
            node.name = f"{node.op_type}_{i}"
    onnx.save(model, pre)
    exclude = []
    if strategy["keep_first_last_float"]:
        convs = [n.name for n in model.graph.node if n.op_type == "Conv"]
        exclude = sorted({convs[0], convs[-1]}) if convs else []
    del model

    if strategy.get("conv_only"):
        op_types = ["Conv"]
    # Accumulate statistics two samples at a time (see ListReader); the stride must divide the sample count.
    stride = 2 if len(feeds) % 2 == 0 else 1
    extra = {"ActivationSymmetric": False, "WeightSymmetric": True, "CalibStridedMinMax": stride}
    if strategy["method"] == CalibrationMethod.Percentile:
        extra["CalibPercentile"] = 99.999
    quantize_static(
        pre,
        int8,
        ListReader(feeds),
        quant_format=QuantFormat.QDQ,
        op_types_to_quantize=op_types,
        nodes_to_exclude=exclude,
        per_channel=True,
        activation_type=QuantType.QUInt8,
        weight_type=QuantType.QInt8,
        calibrate_method=strategy["method"],
        extra_options=extra,
    )


def latency(sess: ort.InferenceSession, tensor: np.ndarray, repeats: int = 5) -> float:
    run_first(sess, tensor)  # warm-up
    best = float("inf")
    for _ in range(repeats):
        start = time.perf_counter()
        run_first(sess, tensor)
        best = min(best, time.perf_counter() - start)
    return best * 1000.0


# ----- Per-model data, quantisation op set and evaluation -----

def orientation_tensor(image: Image.Image) -> np.ndarray:
    scale = 256.0 / min(image.size)
    w, h = round(image.width * scale), round(image.height * scale)
    resized = image.resize((w, h), Image.BILINEAR)
    left, top = (w - 224) // 2, (h - 224) // 2
    return common.to_chw(resized.crop((left, top, left + 224, top + 224)), common.IMAGENET_MEAN, common.IMAGENET_STD)


ROTATIONS = {0: None, 1: Image.Transpose.ROTATE_270, 2: Image.Transpose.ROTATE_180, 3: Image.Transpose.ROTATE_90}


def rotated_page(seed: int, cls: int) -> Image.Image:
    page = common.degrade_page(common.render_page(seed), seed + 1)
    # Class i means "the page is rotated i*90 degrees clockwise"; PIL's ROTATE_270 turns counter-clockwise by 270 = clockwise 90.
    return page if ROTATIONS[cls] is None else page.transpose(ROTATIONS[cls])


def doc_orientation(ctx) -> dict:
    rng = np.random.default_rng(11)
    calib = [{"x": orientation_tensor(rotated_page(1000 + i, int(rng.integers(0, 4))))} for i in range(48)]
    ctx.quantize(calib, None)
    fp32, int8 = ctx.sessions()
    labels, preds32, preds8 = [], [], []
    tensors = []
    for i in range(60):
        for cls in range(4):
            tensors.append(orientation_tensor(rotated_page(5000 + 7 * i, cls)))
            labels.append(cls)
    for chunk in common.batched(tensors, 16):
        batch = np.concatenate(chunk, axis=0)  # PP-LCNet has a dynamic batch axis
        preds32.extend(np.argmax(run_first(fp32, batch), axis=1).tolist())
        preds8.extend(np.argmax(run_first(int8, batch), axis=1).tolist())
    labels = np.array(labels)
    acc32 = float(np.mean(np.array(preds32) == labels))
    acc8 = float(np.mean(np.array(preds8) == labels))
    agreement = float(np.mean(np.array(preds32) == np.array(preds8)))
    tol = TOLERANCES["doc-orientation"]
    return ctx.verdict(
        {"views": len(labels), "pages": 60, "fp32_accuracy": round(acc32, 4), "int8_accuracy": round(acc8, 4),
         "agreement": round(agreement, 4)},
        acc8 - acc32 >= tol["min_accuracy_delta"] and agreement >= tol["min_agreement"],
        tensors[0])


def doc_dewarp(ctx) -> dict:
    def page(seed):
        warped = common.warp_page(common.degrade_page(common.render_page(seed, 488, 640), seed + 3), seed + 5)
        return common.pad_to_multiple(common.to_chw(warped, bgr=True), 32)

    calib = [{"image": page(2000 + i)} for i in range(16)]
    ctx.quantize(calib, None)
    fp32, int8 = ctx.sessions()
    psnrs, ssims = [], []
    for i in range(10):
        tensor = page(6000 + 11 * i)
        a = np.clip(run_first(fp32, tensor), 0, 1)
        b = np.clip(run_first(int8, tensor), 0, 1)
        psnrs.append(common.psnr(a, b))
        ssims.append(common.ssim(a, b))
    p, s = common.Summary.of(psnrs), common.Summary.of(ssims)
    tol = TOLERANCES["doc-dewarp"]
    return ctx.verdict(
        {"pages": 10, "psnr_vs_fp32_db": p.as_dict(2), "ssim_vs_fp32": s.as_dict()},
        p.mean >= tol["min_psnr_vs_fp32"] and s.mean >= tol["min_ssim_vs_fp32"],
        page(6000))


def super_resolution(ctx) -> dict:
    calib_imgs, eval_imgs = ctx.natural_split()
    lr_patches = [p.resize((64, 64), Image.BICUBIC) for p in common.random_patches(calib_imgs, 48, 256, seed=21)]
    ctx.quantize([{"input": common.to_chw(p)} for p in lr_patches], None)
    fp32, int8 = ctx.sessions()
    d_psnr, d_ssim, vs_fp32, p32s = [], [], [], []
    for hr in common.random_patches(eval_imgs, 24, 256, seed=22):
        lr = common.to_chw(hr.resize((64, 64), Image.BICUBIC))
        target = common.to_chw(hr)
        a = np.clip(run_first(fp32, lr), 0, 1)
        b = np.clip(run_first(int8, lr), 0, 1)
        qa, qb = np.round(a * 255) / 255, np.round(b * 255) / 255
        p32 = common.psnr(qa, target)
        p32s.append(p32)
        d_psnr.append(common.psnr(qb, target) - p32)
        d_ssim.append(common.ssim(qb, target) - common.ssim(qa, target))
        vs_fp32.append(common.psnr(qa, qb))
    dp, ds, vf = common.Summary.of(d_psnr), common.Summary.of(d_ssim), common.Summary.of(vs_fp32)
    tol = TOLERANCES["super-resolution-x4"]
    return ctx.verdict(
        {"crops": len(d_psnr), "fp32_psnr_vs_hr_db": common.Summary.of(p32s).as_dict(2),
         "int8_minus_fp32_psnr_vs_hr_db": dp.as_dict(3), "int8_minus_fp32_ssim_vs_hr": ds.as_dict(4),
         "psnr_int8_vs_fp32_db": vf.as_dict(2)},
        dp.mean >= tol["min_psnr_delta"] and ds.mean >= tol["min_ssim_delta"] and vf.mean >= tol["min_psnr_vs_fp32"],
        common.to_chw(lr_patches[0]))


def denoise(ctx) -> dict:
    calib_imgs, eval_imgs = ctx.natural_split()
    rng = np.random.default_rng(31)
    calib = []
    for patch in common.random_patches(calib_imgs, 40, 128, seed=32):
        clean = common.to_gray(patch)
        sigma = float(rng.choice([10, 15, 25, 35, 50])) / 255.0
        calib.append({"input": np.clip(clean + rng.normal(0, sigma, clean.shape), 0, 1).astype(np.float32)})
    ctx.quantize(calib, None)
    fp32, int8 = ctx.sessions()
    rows = []
    rng = np.random.default_rng(33)
    for patch in common.random_patches(eval_imgs, 12, 192, seed=34):
        clean = common.to_gray(patch)
        for sigma in (15, 25, 50):
            noisy = np.clip(clean + rng.normal(0, sigma / 255.0, clean.shape), 0, 1).astype(np.float32)
            a = np.clip(noisy - run_first(fp32, noisy), 0, 1)
            b = np.clip(noisy - run_first(int8, noisy), 0, 1)
            rows.append((sigma, common.psnr(a, clean), common.psnr(b, clean)))
    deltas = [r[2] - r[1] for r in rows]
    per_sigma = {str(s): {"fp32_db": round(float(np.mean([r[1] for r in rows if r[0] == s])), 3),
                          "int8_db": round(float(np.mean([r[2] for r in rows if r[0] == s])), 3)} for s in (15, 25, 50)}
    d = common.Summary.of(deltas)
    tol = TOLERANCES["denoise-gray"]
    return ctx.verdict(
        {"crops": 12, "sigmas": [15, 25, 50], "psnr_vs_clean": per_sigma, "int8_minus_fp32_db": d.as_dict(3)},
        d.mean >= tol["min_psnr_delta"] and d.minimum >= tol["min_worst_psnr_delta"],
        calib[0]["input"])


def saliency_map(sess, tensor) -> np.ndarray:
    """First output plane, sigmoid when out of range, then the operation's min-max stretch."""
    plane = run_first(sess, tensor)[0, 0].astype(np.float64)
    if plane.min() < 0 or plane.max() > 1:
        plane = 1 / (1 + np.exp(-plane))
    span = plane.max() - plane.min()
    return (plane - plane.min()) / span if span > 1e-6 else plane


def saliency(ctx, name: str) -> dict:
    calib_imgs, eval_imgs = ctx.natural_split()
    inp = ctx.input_name()

    def tensor(image):
        return common.to_chw(image.resize((320, 320), Image.BILINEAR), common.IMAGENET_MEAN, common.IMAGENET_STD)

    calib = [{inp: tensor(img)} for img in calib_imgs] + [{inp: tensor(p)} for p in common.random_patches(calib_imgs, 16, 320, seed=41)]
    ctx.quantize(calib, None)
    fp32, int8 = ctx.sessions()
    ious, maes = [], []
    for img in list(eval_imgs) + common.random_patches(eval_imgs, 10, 320, seed=42):
        a = saliency_map(fp32, tensor(img))
        b = saliency_map(int8, tensor(img))
        ious.append(common.iou(a >= 0.5, b >= 0.5))
        maes.append(float(np.mean(np.abs(a - b))))
    i, m = common.Summary.of(ious), common.Summary.of(maes)
    tol = TOLERANCES[name]
    return ctx.verdict(
        {"images": len(ious), "mask_iou_vs_fp32": i.as_dict(), "map_mae_vs_fp32": m.as_dict()},
        i.mean >= tol["min_mean_iou"] and i.minimum >= tol["min_iou"] and m.mean <= tol["max_mae"],
        tensor(eval_imgs[0]))


def binarization(ctx) -> dict:
    def page(seed):
        return common.to_gray(common.degrade_page(common.render_page(seed, 600, 780), seed + 9))

    # The graph computes local statistics with integral images (Slice/Sub/Div chains); only its
    # convolutions are quantised so that arithmetic stays in float.
    ctx.quantize([{"input": page(3000 + i)} for i in range(24)], ["Conv"])
    fp32, int8 = ctx.sessions()
    agreements, ious = [], []
    for i in range(12):
        lum = page(7000 + 13 * i)
        a = lum >= run_first(fp32, lum)
        b = lum >= run_first(int8, lum)
        agreements.append(float(np.mean(a == b)))
        ious.append(common.iou(~a, ~b))
    g, i = common.Summary.of(agreements), common.Summary.of(ious)
    tol = TOLERANCES["binarization"]
    return ctx.verdict(
        {"pages": 12, "pixel_agreement_vs_fp32": g.as_dict(5), "ink_iou_vs_fp32": i.as_dict()},
        g.mean >= tol["min_pixel_agreement"] and i.mean >= tol["min_ink_iou"],
        page(7000))


def faces(ctx) -> dict:
    face_imgs = [img for _, img in common.load_images(ctx.args.faces_dir)]
    calib_faces, eval_faces = common.split(face_imgs)
    calib_nat, _ = ctx.natural_split()

    def fit(image, longest=640):
        scale = min(1.0, longest / max(image.size))
        return image.resize((max(1, round(image.width * scale)), max(1, round(image.height * scale))), Image.BILINEAR)

    calib = []
    for image in calib_faces + calib_nat[:4]:
        calib.append({"input": yunet_reference.prepare(np.asarray(fit(image)))[0]})
    for patch in common.random_patches(calib_faces, 8, 320, seed=51):
        calib.append({"input": yunet_reference.prepare(np.asarray(patch))[0]})
    ctx.quantize(calib, None)
    fp32, int8 = ctx.sessions()
    matched = total32 = total8 = 0
    ious, nmes, dscore = [], [], []
    samples = [fit(img, 1280) for img in eval_faces] + [fit(img, 640) for img in eval_faces]
    for image in samples:
        rgb = np.asarray(image)
        a = yunet_reference.detect(fp32, rgb, 0.6)
        b = yunet_reference.detect(int8, rgb, 0.6)
        total32 += len(a)
        total8 += len(b)
        used = set()
        for fa in a:
            best, best_j = 0.0, -1
            for j, fb in enumerate(b):
                if j not in used:
                    v = yunet_reference.box_iou(fa, fb)
                    if v > best:
                        best, best_j = v, j
            if best >= 0.5:
                used.add(best_j)
                matched += 1
                ious.append(best)
                fb = b[best_j]
                nmes.append(float(np.mean(np.hypot(fa[4:14:2] - fb[4:14:2], fa[5:14:2] - fb[5:14:2])) / max(fa[2], 1.0)))
                dscore.append(float(abs(fa[14] - fb[14])))
    recall = matched / max(total32, 1)
    precision = matched / max(total8, 1)
    tol = TOLERANCES["face-detection"]
    mean_iou = float(np.mean(ious)) if ious else 0.0
    nme = float(np.mean(nmes)) if nmes else 1.0
    return ctx.verdict(
        {"images": len(samples), "fp32_faces": total32, "int8_faces": total8, "matched": matched,
         "recall_vs_fp32": round(recall, 4), "precision_vs_fp32": round(precision, 4),
         "mean_box_iou": round(mean_iou, 4), "landmark_nme": round(nme, 4),
         "mean_abs_score_delta": round(float(np.mean(dscore)) if dscore else 0.0, 4)},
        recall >= tol["min_recall"] and precision >= tol["min_precision"] and mean_iou >= tol["min_mean_iou"]
        and nme <= tol["max_landmark_nme"],
        yunet_reference.prepare(np.asarray(fit(eval_faces[0])))[0])


EVALUATORS: dict[str, Callable] = {
    "doc-orientation": doc_orientation,
    "doc-dewarp": doc_dewarp,
    "super-resolution-x4": super_resolution,
    "denoise-gray": denoise,
    "saliency": lambda ctx: saliency(ctx, "saliency"),
    "saliency-fast": lambda ctx: saliency(ctx, "saliency-fast"),
    "binarization": binarization,
    "face-detection": faces,
}


class Context:
    def __init__(self, args, name: str, work: str):
        self.args = args
        self.name = name
        fp32_name, int8_name, licence = SPECS[name]
        self.fp32 = os.path.join(args.fp32_dir, fp32_name)
        self.int8 = os.path.join(work, int8_name)
        self.int8_name = int8_name
        self.fp32_name = fp32_name
        self.licence = licence
        self.work = work
        self._natural = None
        self.strategy = STRATEGIES[0]

    def input_name(self) -> str:
        return onnx.load(self.fp32, load_external_data=False).graph.input[0].name

    def natural_split(self):
        if self._natural is None:
            images = [img for _, img in common.load_images(self.args.images_dir)]
            images += [img for _, img in common.load_images(self.args.faces_dir)]
            self._natural = common.split(images)
        return self._natural

    def quantize(self, feeds, op_types):
        start = time.perf_counter()
        quantize(self.fp32, self.int8, feeds, op_types, self.work, self.strategy)
        self.calibration_samples = len(feeds)
        self.quantize_seconds = time.perf_counter() - start

    def sessions(self):
        return session(self.fp32, self.args.threads), session(self.int8, self.args.threads)

    def verdict(self, metrics: dict, passed: bool, timing_input: np.ndarray) -> dict:
        fp32, int8 = self.sessions()
        return {
            "model": self.name,
            "fp32_file": self.fp32_name,
            "fp32_sha256": common.sha256_file(self.fp32),
            "fp32_bytes": os.path.getsize(self.fp32),
            "int8_file": self.int8_name,
            "int8_sha256": common.sha256_file(self.int8),
            "int8_bytes": os.path.getsize(self.int8),
            "license": self.licence,
            "strategy": self.strategy["name"],
            "calibration_samples": self.calibration_samples,
            "tolerance": TOLERANCES[self.name],
            "metrics": metrics,
            "passed": bool(passed),
            "latency_ms_best_of_5": {
                "input_shape": list(timing_input.shape),
                "threads": self.args.threads,
                "fp32": round(latency(fp32, timing_input), 1),
                "int8": round(latency(int8, timing_input), 1),
            },
        }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--fp32-dir", required=True)
    parser.add_argument("--images-dir", required=True, help="CC0 / public-domain natural photos (PNG/JPEG)")
    parser.add_argument("--faces-dir", required=True, help="public-domain photos that contain faces")
    parser.add_argument("--out-dir", required=True, help="staging directory for passing int8 files and the report")
    parser.add_argument("--models", default=",".join(SPECS))
    parser.add_argument("--threads", type=int, default=2)
    args = parser.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)
    report_path = os.path.join(args.out_dir, "int8-report.json")
    report = {}
    if os.path.exists(report_path):
        import json
        with open(report_path, encoding="utf-8") as handle:
            report = json.load(handle)
    report.setdefault("environment", {})
    report["environment"].update({"onnxruntime": ort.__version__, "onnx": onnx.__version__, "numpy": np.__version__,
                                  "method": "static QDQ, per-channel symmetric int8 weights, asymmetric uint8 activations"})
    report.setdefault("models", {})

    # Symbolic shape inference writes temporary *.data files into the current directory: run each model inside its
    # own throw-away work directory (so every path argument is made absolute first).
    for key in ("fp32_dir", "images_dir", "faces_dir", "out_dir"):
        setattr(args, key, os.path.abspath(getattr(args, key)))
    report_path = os.path.abspath(report_path)
    home = os.getcwd()
    for name in [m.strip() for m in args.models.split(",") if m.strip()]:
        work = tempfile.mkdtemp(prefix=f"q-{name}-")
        os.chdir(work)
        try:
            ctx = Context(args, name, work)
            attempts = []
            result = None
            for strategy in STRATEGIES:
                ctx.strategy = strategy
                print(f"[{name}] quantising {ctx.fp32_name} with {strategy['name']} ...", flush=True)
                result = EVALUATORS[name](ctx)
                result["quantize_seconds"] = round(ctx.quantize_seconds, 1)
                attempts.append({"strategy": strategy["name"], "passed": result["passed"], "metrics": result["metrics"]})
                print(f"[{name}] {'PASS' if result['passed'] else 'FAIL'} {result['metrics']}", flush=True)
                if result["passed"]:
                    break
            result["attempts"] = attempts
            staged = os.path.join(args.out_dir, ctx.int8_name)
            if result["passed"]:
                shutil.copyfile(ctx.int8, staged)
            elif os.path.exists(staged):
                os.remove(staged)
            report["models"][name] = result
            common.write_json(report_path, report)
        finally:
            os.chdir(home)
            shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

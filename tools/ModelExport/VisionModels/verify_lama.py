"""Checks the Carve/LaMa-ONNX `lama_fp32.onnx` export (big-lama, Apache-2.0) against the reference output of the
original PyTorch model that the exporter published next to it, and records the I/O contract.

Usage:

    python -I verify_lama.py <Carve/LaMa-ONNX dir with lama_fp32.onnx, image.jpg, mask.png, output_orig.png>

Contract (from the exporter's `ExportLama` wrapper):
    image  float32 [N,3,512,512] RGB in 0-1
    mask   float32 [N,1,512,512] 1 = hole, 0 = keep (binarised with > 0)
    output float32 [N,3,512,512] clamp(255 * (mask * prediction + (1 - mask) * image), 0, 255): already
           composited over the input and scaled to 0-255.

The sample is preprocessed exactly like the exporter's notebook (PIL default resize to 512x512, mask > 0) and the
run fails when the PSNR against the original model's output inside the hole drops below 30 dB.
"""
from __future__ import annotations

import os
import sys

import numpy as np
import onnxruntime as ort
from PIL import Image


def psnr(a: np.ndarray, b: np.ndarray) -> float:
    mse = float(np.mean((a.astype(np.float64) - b.astype(np.float64)) ** 2))
    return float("inf") if mse == 0 else 10 * np.log10(255.0 ** 2 / mse)


def main() -> None:
    folder = sys.argv[1]
    image = Image.open(os.path.join(folder, "image.jpg")).convert("RGB").resize((512, 512))
    mask = Image.open(os.path.join(folder, "mask.png")).convert("L").resize((512, 512))
    x = (np.asarray(image, dtype=np.float32) / 255.0).transpose(2, 0, 1)[None]
    m = (np.asarray(mask, dtype=np.float32)[None, None] > 0).astype(np.float32)

    session = ort.InferenceSession(os.path.join(folder, "lama_fp32.onnx"), providers=["CPUExecutionProvider"])
    output = session.run(["output"], {"image": x, "mask": m})[0][0].transpose(1, 2, 0)
    produced = output.astype(np.uint8)  # the exporter's post-processing truncates

    hole = m[0, 0] > 0
    keep = ~hole
    reference = np.asarray(Image.open(os.path.join(folder, "output_orig.png")).convert("RGB"))
    exporter_onnx = np.asarray(Image.open(os.path.join(folder, "output_onnx_fp32.png")).convert("RGB"))

    print(f"hole covers {hole.mean() * 100:.1f}% of the frame")
    print(f"outside the hole, max |output - 255 * input| = {np.abs(output[keep] - x[0].transpose(1, 2, 0)[keep] * 255).max():.4f}")
    hole_psnr = psnr(produced[hole], reference[hole])
    print(f"PSNR vs original PyTorch output, inside hole: {hole_psnr:.2f} dB; whole frame: {psnr(produced, reference):.2f} dB")
    print(f"PSNR vs exporter's own ONNX output, inside hole: {psnr(produced[hole], exporter_onnx[hole]):.2f} dB")
    if hole_psnr < 30:
        raise SystemExit("The export does not reproduce the original model's output.")


if __name__ == "__main__":
    main()

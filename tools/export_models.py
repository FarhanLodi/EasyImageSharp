#!/usr/bin/env python3
"""Exports the EasyImageSharp.AI models to ONNX with the exact contracts the library expects.

Each network is defined here rather than pulled in as a dependency: these are small, well-documented
architectures, and reimplementing them keeps the export reproducible from weights alone. The state dict
is loaded strictly, so a mismatch between this definition and the published weights fails loudly instead
of silently producing a model that runs but predicts nonsense.

    pip install torch onnx onnxruntime numpy requests
    python tools/export_models.py --out ./models            # export everything it can
    python tools/export_models.py --out ./models --only realesrgan

Every export is validated after writing: shapes, dynamic axes, and agreement with the PyTorch module on
a random input. Contracts are documented in src/EasyImageSharp.AI/Models/ModelRegistry.cs and must match.
"""
from __future__ import annotations

import argparse
import os
import sys

import numpy as np
import torch
import torch.nn as nn

OPSET = 17


# =====================================================================================================
# Weight download
# =====================================================================================================

def fetch(url: str, path: str) -> str:
    """Downloads to path unless it is already there, and returns the path."""
    if os.path.exists(path) and os.path.getsize(path) > 0:
        print(f"    cached  {os.path.basename(path)} ({os.path.getsize(path) / 1e6:.1f} MB)")
        return path

    import requests
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    print(f"    fetching {url}")
    with requests.get(url, stream=True, timeout=300) as response:
        response.raise_for_status()
        temporary = path + ".part"
        with open(temporary, "wb") as handle:
            for chunk in response.iter_content(1 << 20):
                handle.write(chunk)
        os.replace(temporary, path)
    print(f"    got      {os.path.basename(path)} ({os.path.getsize(path) / 1e6:.1f} MB)")
    return path


# =====================================================================================================
# Real-ESRGAN: SRVGGNetCompact (realesr-general-x4v3)
#
# A plain VGG-style body with a pixel-shuffle tail and a bilinear skip connection, from the Real-ESRGAN
# project. Contract: input [1,3,H,W] RGB in 0-1 with dynamic H/W; output [1,3,4H,4W] in 0-1.
# =====================================================================================================

class SRVGGNetCompact(nn.Module):
    def __init__(self, num_in_ch=3, num_out_ch=3, num_feat=64, num_conv=32, upscale=4):
        super().__init__()
        self.upscale = upscale
        body: list[nn.Module] = [nn.Conv2d(num_in_ch, num_feat, 3, 1, 1), nn.PReLU(num_parameters=num_feat)]
        for _ in range(num_conv):
            body.append(nn.Conv2d(num_feat, num_feat, 3, 1, 1))
            body.append(nn.PReLU(num_parameters=num_feat))
        body.append(nn.Conv2d(num_feat, num_out_ch * upscale * upscale, 3, 1, 1))
        self.body = nn.Sequential(*body)
        self.upsampler = nn.PixelShuffle(upscale)

    def forward(self, x):
        out = self.upsampler(self.body(x))
        # The network predicts a residual over a bilinear upscale of the input.
        return out + torch.nn.functional.interpolate(x, scale_factor=self.upscale, mode="bilinear", align_corners=False)


def export_realesrgan(out_dir: str, cache: str) -> str:
    path = fetch(
        "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesr-general-x4v3.pth",
        os.path.join(cache, "realesr-general-x4v3.pth"))

    state = torch.load(path, map_location="cpu")
    state = state.get("params", state.get("params_ema", state))

    model = SRVGGNetCompact(num_feat=64, num_conv=32, upscale=4)
    model.load_state_dict(state, strict=True)
    model.eval()

    target = os.path.join(out_dir, "realesrgan_general_x4v3.onnx")
    dummy = torch.rand(1, 3, 64, 64)
    torch.onnx.export(
        model, dummy, target,
        input_names=["input"], output_names=["output"],
        dynamic_axes={"input": {2: "height", 3: "width"}, "output": {2: "height4", 3: "width4"}},
        opset_version=OPSET, do_constant_folding=True)
    validate(target, model, (1, 3, 48, 72), expect_scale=4)
    return target


# =====================================================================================================
# DnCNN: blind grayscale denoiser
#
# Conv+ReLU, then 15 Conv+BN+ReLU blocks, then a final Conv. Contract: input [1,1,H,W] luminance in 0-1
# with dynamic H/W; output [1,1,H,W] is the predicted NOISE RESIDUAL, so clean = input - output.
# =====================================================================================================

class DnCNN(nn.Module):
    """The KAIR blind-denoising variant: 20 convolutions with bias, ReLU between, and no batch norm.

    The Sequential predicts the NOISE, not the clean image — KAIR's wrapper returns ``x - model(x)``.
    We export the noise-predicting Sequential, which is the contract the library documents and applies
    (``clean = input - output``). ``verify_residual_convention`` below checks that empirically rather
    than trusting this comment.
    """

    def __init__(self, depth=20, n_channels=64, image_channels=1, kernel_size=3):
        super().__init__()
        padding = kernel_size // 2
        layers: list[nn.Module] = [nn.Conv2d(image_channels, n_channels, kernel_size, padding=padding, bias=True)]
        for _ in range(depth - 2):
            layers.append(nn.ReLU(inplace=True))
            layers.append(nn.Conv2d(n_channels, n_channels, kernel_size, padding=padding, bias=True))
        layers.append(nn.ReLU(inplace=True))
        layers.append(nn.Conv2d(n_channels, image_channels, kernel_size, padding=padding, bias=True))
        self.model = nn.Sequential(*layers)

    def forward(self, x):
        return self.model(x)


def verify_residual_convention(model: nn.Module) -> None:
    """Confirms the network predicts noise rather than the clean image.

    Adds known Gaussian noise to a smooth ramp and checks that subtracting the output moves the result
    closer to the original than the noisy input was. If the network instead returned the clean image,
    subtracting it would roughly double the error and this would fail.
    """
    torch.manual_seed(0)
    height = width = 64
    ramp = torch.linspace(0.15, 0.85, width).repeat(height, 1).view(1, 1, height, width)
    noisy = (ramp + torch.randn_like(ramp) * 0.08).clamp(0, 1)

    with torch.no_grad():
        predicted = model(noisy)

    as_residual = (noisy - predicted - ramp).abs().mean().item()
    as_clean = (predicted - ramp).abs().mean().item()
    before = (noisy - ramp).abs().mean().item()

    print(f"    check    mean abs error: noisy {before:.4f} -> "
          f"{as_residual:.4f} treating the output as noise, {as_clean:.4f} treating it as the image")

    if as_residual >= before:
        raise SystemExit(
            "FAIL dncnn: subtracting the output did not reduce the error, so it does not predict noise. "
            "The library's contract (clean = input - output) would be wrong for these weights.")
    if as_clean < as_residual:
        raise SystemExit(
            "FAIL dncnn: the output is closer to the clean image than the residual interpretation is. "
            "These weights predict the image directly and do not match the documented contract.")


def export_dncnn(out_dir: str, cache: str) -> str:
    path = fetch(
        "https://github.com/cszn/KAIR/releases/download/v1.0/dncnn_gray_blind.pth",
        os.path.join(cache, "dncnn_gray_blind.pth"))

    state = torch.load(path, map_location="cpu")
    state = state.get("params", state)

    model = DnCNN(depth=20, n_channels=64, image_channels=1)
    model.load_state_dict(state, strict=True)
    model.eval()

    verify_residual_convention(model)

    target = os.path.join(out_dir, "dncnn_gray_blind.onnx")
    dummy = torch.rand(1, 1, 64, 64)
    torch.onnx.export(
        model, dummy, target,
        input_names=["input"], output_names=["output"],
        dynamic_axes={"input": {2: "height", 3: "width"}, "output": {2: "height", 3: "width"}},
        opset_version=OPSET, do_constant_folding=True)
    validate(target, model, (1, 1, 56, 40), expect_scale=1)
    return target


# =====================================================================================================
# Validation
# =====================================================================================================

def validate(path: str, module: nn.Module, shape: tuple[int, ...], expect_scale: int) -> None:
    """Runs the exported graph at a size it was not traced at and compares it with PyTorch."""
    import onnx
    import onnxruntime as ort

    onnx.checker.check_model(onnx.load(path))

    x = torch.rand(*shape)
    with torch.no_grad():
        expected = module(x).numpy()

    session = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    actual = session.run(None, {session.get_inputs()[0].name: x.numpy()})[0]

    want = (shape[0], expected.shape[1], shape[2] * expect_scale, shape[3] * expect_scale)
    if tuple(actual.shape) != want:
        raise SystemExit(f"FAIL {os.path.basename(path)}: shape {actual.shape}, expected {want}")

    difference = float(np.abs(actual - expected).max())
    if difference > 1e-3:
        raise SystemExit(f"FAIL {os.path.basename(path)}: differs from PyTorch by {difference:.2e}")

    size = os.path.getsize(path) / 1e6
    print(f"    ok       {os.path.basename(path)}  {size:.1f} MB  "
          f"dynamic {shape[2]}x{shape[3]} -> {actual.shape[2]}x{actual.shape[3]}  max diff {difference:.2e}")


# =====================================================================================================

EXPORTERS = {
    "realesrgan": export_realesrgan,
    "dncnn": export_dncnn,
}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", default="models", help="directory to write the .onnx files into")
    parser.add_argument("--cache", default=".model-cache", help="directory for downloaded weights")
    parser.add_argument("--only", action="append", choices=sorted(EXPORTERS), help="export just this model")
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)
    os.makedirs(args.cache, exist_ok=True)

    selected = args.only or sorted(EXPORTERS)
    written, failed = [], []
    for name in selected:
        print(f"\n{name}")
        try:
            written.append(EXPORTERS[name](args.out, args.cache))
        except Exception as error:  # noqa: BLE001 - report and continue with the rest
            print(f"    FAILED   {type(error).__name__}: {error}")
            failed.append(name)

    print(f"\nExported {len(written)} model(s) to {args.out}")
    for path in written:
        print(f"  {os.path.basename(path)}")
    if failed:
        print(f"Failed: {', '.join(failed)}")
    print("\nNext: python tools/pin-models.py " + args.out)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())

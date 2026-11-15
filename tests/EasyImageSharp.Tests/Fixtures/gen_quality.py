"""Reference values for the image quality metrics (Fixtures/quality/).

The expected numbers are computed here with numpy, written straight from the papers, never from the library
under test:

  PSNR      10 log10(1 / MSE) on values in 0-1; 'rgb' pools the squared errors of R, G and B.
  SSIM      Wang, Bovik, Sheikh, Simoncelli (2004): 11x11 Gaussian window, sigma 1.5, K1 0.01, K2 0.03, data
            range 1, population (co)variances, mean over the 'valid' window positions; 'rgb' averages the three
            channel SSIMs. Cross-checked against scikit-image 0.24 structural_similarity(gaussian_weights=True,
            sigma=1.5, use_sample_covariance=False, data_range=1), which agrees to better than 1e-12.
  MS-SSIM   Wang, Simoncelli, Bovik (2003): five scales, weights 0.0448 0.2856 0.3001 0.2363 0.1333, the mean
            contrast-structure term at the four finer scales and the mean SSIM at the coarsest; 2x2 average plus
            2x subsampling between scales with the last row/column repeated for odd sizes (the authors' MATLAB
            code: imfilter(ones(2)/4, 'symmetric') then img(1:2:end, 1:2:end)); negative terms clamp to 0.
  Sharpness variance of the 4-neighbour Laplacian and mean squared 3x3 Sobel magnitude (Tenengrad) of BT.601
            luma in 0-255, over the interior pixels.
  Noise     Immerkaer (1996): sqrt(pi/2) / (6 (W-2)(H-2)) * sum |I * [1 -2 1; -2 4 -2; 1 -2 1]| on luma 0-255.
  Exposure  BT.601 luma mean and clipping fractions (all channels / any channel at 0 or 255).

Luma is BT.601 Y' = 0.299 R' + 0.587 G' + 0.114 B' on encoded values in 0-1. Noise comes from a fixed-seed
integer generator with a Box-Muller transform, so the images are identical on every machine.

  ref.png           192x176 RGB synthetic scene (gradients, discs, stripes, a sinusoidal texture)
  noise.png         ref plus Gaussian noise (sigma 8 in 8-bit units), rounded and clipped
  blur.png          ref after a 5x5 box blur with edge replication, rounded
  posterize.png     ref quantised to 6 levels per channel
  bright.png        ref + 24 per channel, clipped (a pure luminance shift)
  flatnoise.png     96x80 grey 128 plus Gaussian noise of sigma 5: Immerkaer must recover about 5
  exposure.png      64x48 RGBA with known black, white, saturated and transparent areas
  manifest.json     every expected value
"""
from __future__ import annotations

import json
import math
import os

import numpy as np
from PIL import Image

W, H = 192, 176
WEIGHTS = [0.0448, 0.2856, 0.3001, 0.2363, 0.1333]


class _Rng:
    """xorshift64* with a Box-Muller normal: identical streams on every platform and numpy version."""

    def __init__(self, seed: int) -> None:
        self.state = seed & 0xFFFFFFFFFFFFFFFF

    def uniform(self) -> float:
        x = self.state
        x ^= (x >> 12)
        x ^= (x << 25) & 0xFFFFFFFFFFFFFFFF
        x ^= (x >> 27)
        self.state = x
        return (((x * 0x2545F4914F6CDD1D) & 0xFFFFFFFFFFFFFFFF) >> 11) / float(1 << 53)

    def normal(self) -> float:
        u1 = max(self.uniform(), 1e-300)
        u2 = self.uniform()
        return math.sqrt(-2.0 * math.log(u1)) * math.cos(2.0 * math.pi * u2)


def _reference() -> np.ndarray:
    ys, xs = np.mgrid[0:H, 0:W]
    r = xs * 255.0 / (W - 1)
    g = 60 + 120 * (ys / (H - 1)) + 40 * np.sin(xs / 7.0) * np.cos(ys / 9.0)
    b = 128 + 100 * np.sin((xs + 2 * ys) / 11.0)
    img = np.stack([r, g, b], axis=-1)
    for cx, cy, rad, col in [(50, 60, 28, (240, 40, 30)), (140, 110, 35, (20, 30, 200)), (100, 40, 15, (250, 250, 250))]:
        mask = (xs - cx) ** 2 + (ys - cy) ** 2 < rad * rad
        img[mask] = col
    stripes = ((xs // 4) % 2 == 0) & (ys > 140)
    img[stripes] = img[stripes] * 0.3
    return np.clip(np.round(img), 0, 255).astype(np.uint8)


def _noisy(img: np.ndarray, sigma: float, seed: int) -> np.ndarray:
    rng = _Rng(seed)
    flat = img.astype(np.float64).reshape(-1)
    noise = np.array([rng.normal() for _ in range(flat.size)])
    return np.clip(np.round(flat + sigma * noise), 0, 255).astype(np.uint8).reshape(img.shape)


def _box_blur(img: np.ndarray, radius: int) -> np.ndarray:
    pad = np.pad(img.astype(np.float64), ((radius, radius), (radius, radius), (0, 0)), mode="edge")
    acc = np.zeros(img.shape, dtype=np.float64)
    size = 2 * radius + 1
    for dy in range(size):
        for dx in range(size):
            acc += pad[dy:dy + img.shape[0], dx:dx + img.shape[1]]
    return np.clip(np.floor(acc / (size * size) + 0.5), 0, 255).astype(np.uint8)


def _planes(img: np.ndarray, channels: str) -> list[np.ndarray]:
    f = img[..., :3].astype(np.float64) / 255.0
    if channels == "luma":
        return [0.299 * f[..., 0] + 0.587 * f[..., 1] + 0.114 * f[..., 2]]
    return [f[..., 0], f[..., 1], f[..., 2]]


def _gauss_window() -> np.ndarray:
    d = np.arange(11) - 5
    w = np.exp(-(d * d) / (2 * 1.5 * 1.5))
    return w / w.sum()


def _valid_filter(x: np.ndarray, w: np.ndarray) -> np.ndarray:
    n = len(w)
    rows = sum(w[k] * x[:, k:x.shape[1] - n + 1 + k] for k in range(n))
    return sum(w[k] * rows[k:rows.shape[0] - n + 1 + k, :] for k in range(n))


def _ssim_cs(a: np.ndarray, b: np.ndarray) -> tuple[float, float]:
    w = _gauss_window()
    c1, c2 = 0.01 ** 2, 0.03 ** 2
    ma, mb = _valid_filter(a, w), _valid_filter(b, w)
    va = _valid_filter(a * a, w) - ma * ma
    vb = _valid_filter(b * b, w) - mb * mb
    cov = _valid_filter(a * b, w) - ma * mb
    cs = (2 * cov + c2) / (va + vb + c2)
    lum = (2 * ma * mb + c1) / (ma * ma + mb * mb + c1)
    return float(np.mean(lum * cs)), float(np.mean(cs))


def _downsample(x: np.ndarray) -> np.ndarray:
    padded = np.pad(x, ((0, 1), (0, 1)), mode="symmetric")
    avg = (padded[:-1, :-1] + padded[1:, :-1] + padded[:-1, 1:] + padded[1:, 1:]) / 4.0
    return avg[0::2, 0::2]


def psnr(a: np.ndarray, b: np.ndarray, channels: str) -> float:
    pa, pb = _planes(a, channels), _planes(b, channels)
    mse = sum(float(np.sum((x - y) ** 2)) for x, y in zip(pa, pb)) / (pa[0].size * len(pa))
    return math.inf if mse == 0 else 10 * math.log10(1 / mse)


def ssim(a: np.ndarray, b: np.ndarray, channels: str) -> float:
    pa, pb = _planes(a, channels), _planes(b, channels)
    return sum(_ssim_cs(x, y)[0] for x, y in zip(pa, pb)) / len(pa)


def ms_ssim(a: np.ndarray, b: np.ndarray, channels: str) -> float:
    total = 0.0
    for x, y in zip(_planes(a, channels), _planes(b, channels)):
        product = 1.0
        for level, weight in enumerate(WEIGHTS):
            s, cs = _ssim_cs(x, y)
            last = level == len(WEIGHTS) - 1
            product *= max(0.0, s if last else cs) ** weight
            if not last:
                x, y = _downsample(x), _downsample(y)
        total += product
    return total / len(_planes(a, channels))


def _luma255(img: np.ndarray) -> np.ndarray:
    return _planes(img, "luma")[0] * 255.0


def sharpness(img: np.ndarray) -> tuple[float, float]:
    y = _luma255(img)
    c = y[1:-1, 1:-1]
    lap = y[:-2, 1:-1] + y[2:, 1:-1] + y[1:-1, :-2] + y[1:-1, 2:] - 4 * c
    gx = (y[:-2, 2:] + 2 * y[1:-1, 2:] + y[2:, 2:]) - (y[:-2, :-2] + 2 * y[1:-1, :-2] + y[2:, :-2])
    gy = (y[2:, :-2] + 2 * y[2:, 1:-1] + y[2:, 2:]) - (y[:-2, :-2] + 2 * y[:-2, 1:-1] + y[:-2, 2:])
    return float(np.mean(lap * lap) - np.mean(lap) ** 2), float(np.mean(gx * gx + gy * gy))


def immerkaer(img: np.ndarray) -> float:
    y = _luma255(img)
    h, w = y.shape
    k = [[1, -2, 1], [-2, 4, -2], [1, -2, 1]]
    response = sum(k[dy][dx] * y[dy:h - 2 + dy, dx:w - 2 + dx] for dy in range(3) for dx in range(3))
    return math.sqrt(math.pi / 2) * float(np.sum(np.abs(response))) / (6.0 * (w - 2) * (h - 2))


def exposure(img: np.ndarray) -> dict:
    rgb = img[..., :3].astype(np.int64)
    visible = img[..., 3] > 0 if img.shape[-1] == 4 else np.ones(img.shape[:2], dtype=bool)
    sel = rgb[visible]
    luma = (0.299 * sel[:, 0] + 0.587 * sel[:, 1] + 0.114 * sel[:, 2]) / 255.0
    return {
        "pixels": int(sel.shape[0]),
        "mean_luma": float(np.mean(luma)),
        "shadow": float(np.mean(np.all(sel == 0, axis=1))),
        "highlight": float(np.mean(np.all(sel == 255, axis=1))),
        "channel_shadow": float(np.mean(np.any(sel == 0, axis=1))),
        "channel_highlight": float(np.mean(np.any(sel == 255, axis=1))),
        "median_luma_sorted": float(np.sort(luma)[int(math.ceil(0.5 * luma.size)) - 1]),
    }


def _exposure_image() -> np.ndarray:
    a = np.zeros((48, 64, 4), dtype=np.uint8)
    a[..., :3] = 128
    a[..., 3] = 255
    a[0:12, :, :3] = 0            # 768 black pixels
    a[12:18, :, :3] = 255         # 384 white pixels
    a[18:24, 0:32, :3] = (255, 0, 0)  # 192 saturated red: one channel high, two low
    a[24:30, :, :3] = (40, 80, 120)
    a[30:36, 0:16, 3] = 0         # 96 transparent pixels, ignored even though they are grey
    return a


def _rounded(value):
    """12 significant digits: far below every test tolerance, and immune to last-bit differences between the
    SIMD paths numpy picks on different CPUs, so regenerating on another machine rewrites nothing."""
    if isinstance(value, float):
        return float(f"{value:.12g}")
    if isinstance(value, dict):
        return {k: _rounded(v) for k, v in value.items()}
    if isinstance(value, list):
        return [_rounded(v) for v in value]
    return value


def gen_quality(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    ref = _reference()
    images = {
        "ref": ref,
        "noise": _noisy(ref, 8.0, 0x5EED1234),
        "blur": _box_blur(ref, 2),
        "posterize": (np.floor(ref / 256.0 * 6) * (255 / 5)).round().astype(np.uint8),
        "bright": np.clip(ref.astype(np.int64) + 24, 0, 255).astype(np.uint8),
    }
    for name, img in images.items():
        Image.fromarray(img).save(os.path.join(out_dir, f"{name}.png"))

    # One noise field repeated into R, G and B, so the luma noise equals the channel noise.
    flat = np.repeat(_noisy(np.full((80, 96, 1), 128, dtype=np.uint8), 5.0, 0xF1A7), 3, axis=2)
    Image.fromarray(flat).save(os.path.join(out_dir, "flatnoise.png"))
    exp = _exposure_image()
    Image.fromarray(exp).save(os.path.join(out_dir, "exposure.png"))

    comparisons = []
    for name in ("noise", "blur", "posterize", "bright", "ref"):
        entry = {"reference": "ref.png", "distorted": f"{name}.png"}
        for ch in ("luma", "rgb"):
            p = psnr(ref, images[name], ch)
            entry[f"psnr_{ch}"] = None if math.isinf(p) else p
            entry[f"ssim_{ch}"] = ssim(ref, images[name], ch)
            entry[f"msssim_{ch}"] = ms_ssim(ref, images[name], ch)
        comparisons.append(entry)

    no_reference = []
    for name, img in list(images.items()) + [("flatnoise", flat)]:
        lap, ten = sharpness(img)
        no_reference.append({"file": f"{name}.png", "laplacian_variance": lap, "tenengrad": ten, "noise_sigma": immerkaer(img)})

    manifest = {
        "notes": "numpy references written from the papers; see the module docstring of gen_quality.py",
        "comparisons": comparisons,
        "no_reference": no_reference,
        "exposure": {"file": "exposure.png", **exposure(exp)},
        "flatnoise_true_sigma": 5.0,
    }
    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(_rounded(manifest), f, indent=2)
        f.write("\n")
    print(f"quality: {len(comparisons)} comparisons, {len(no_reference)} no-reference entries")


if __name__ == "__main__":
    gen_quality(os.path.join(os.path.dirname(os.path.abspath(__file__)), "quality"))

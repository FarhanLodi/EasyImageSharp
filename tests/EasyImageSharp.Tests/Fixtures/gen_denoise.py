"""Edge-preserving filter references (Fixtures/denoise/), computed with numpy, never by the library under test.

  noisy.png                 48x40 RGB: flat regions, a diagonal edge, a colour bar, plus Gaussian noise (sigma 10)
  noisy_large.png           96x80 RGB built the same way, for the grid approximation
  guide.png                 48x40 RGB noise-free edges, the guide of the joint guided filter
  bilateral_r3.png          brute-force bilateral of noisy.png: circular window radius 3, spatial sigma 2, range
                            sigma 0.15 on the Euclidean RGB distance of values in 0-1; neighbours outside the
                            image are skipped (not padded)                                       -> within +-1
  bilateral_r5.png          the same with radius 5, spatial sigma 3, range sigma 0.3            -> within +-1
  bilateral_large_r12.png   noisy_large.png, radius 12, spatial sigma 6, range sigma 0.12: the reference for
                            the grid approximation, which is only required to be close (mean and max error)
  guided_r2.png             self-guided filter (He, Sun, Tang 2013) per channel, radius 2, epsilon 0.01, box
                            means over windows clipped at the border (boxfilter(I, r) ./ N)     -> within +-1
  guided_r4.png             the same with radius 4, epsilon 0.04                                -> within +-1
  guided_joint_r3.png       every channel of noisy.png guided by the BT.601 luma of guide.png, radius 3,
                            epsilon 0.005                                                       -> within +-1
  manifest.json             the parameters above
"""
from __future__ import annotations

import json
import math
import os

import numpy as np
from PIL import Image


def _normals(seed: int, count: int) -> np.ndarray:
    """xorshift64* plus Box-Muller: the same stream on every platform."""
    state = seed & 0xFFFFFFFFFFFFFFFF
    out = np.empty(count, dtype=np.float64)

    def uniform() -> float:
        nonlocal state
        x = state
        x ^= (x >> 12)
        x ^= (x << 25) & 0xFFFFFFFFFFFFFFFF
        x ^= (x >> 27)
        state = x
        return (((x * 0x2545F4914F6CDD1D) & 0xFFFFFFFFFFFFFFFF) >> 11) / float(1 << 53)

    for i in range(count):
        u1 = max(uniform(), 1e-300)
        out[i] = math.sqrt(-2.0 * math.log(u1)) * math.cos(2.0 * math.pi * uniform())
    return out


def _clean(w: int, h: int) -> np.ndarray:
    ys, xs = np.mgrid[0:h, 0:w]
    img = np.zeros((h, w, 3), dtype=np.float64)
    img[...] = (60, 90, 140)
    img[xs + ys > (w + h) // 2] = (210, 180, 70)
    bar = (xs > w // 5) & (xs < w // 5 + max(3, w // 8))
    img[bar] = (200, 40, 40)
    img[(ys > 3 * h // 4) & (xs > w // 2)] = (30, 160, 60)
    return img


def _noisy(w: int, h: int, seed: int) -> np.ndarray:
    img = _clean(w, h) + 10.0 * _normals(seed, w * h * 3).reshape(h, w, 3)
    return np.clip(np.round(img), 0, 255).astype(np.uint8)


def bilateral(img: np.ndarray, radius: int, sigma_s: float, sigma_r: float) -> np.ndarray:
    f = img.astype(np.float64) / 255.0
    h, w, _ = f.shape
    num = np.zeros_like(f)
    den = np.zeros((h, w), dtype=np.float64)
    for dy in range(-radius, radius + 1):
        for dx in range(-radius, radius + 1):
            if dx * dx + dy * dy > radius * radius:
                continue
            ys0, ys1 = max(0, -dy), min(h, h - dy)
            xs0, xs1 = max(0, -dx), min(w, w - dx)
            centre = f[ys0:ys1, xs0:xs1]
            neighbour = f[ys0 + dy:ys1 + dy, xs0 + dx:xs1 + dx]
            d2 = np.sum((neighbour - centre) ** 2, axis=-1)
            weight = math.exp(-(dx * dx + dy * dy) / (2 * sigma_s * sigma_s)) * np.exp(-d2 / (2 * sigma_r * sigma_r))
            num[ys0:ys1, xs0:xs1] += weight[..., None] * neighbour
            den[ys0:ys1, xs0:xs1] += weight
    return num / den[..., None]


def _box_mean(x: np.ndarray, r: int) -> np.ndarray:
    h, w = x.shape
    c = np.cumsum(np.pad(x, ((1, 0), (1, 0))), axis=0).cumsum(axis=1)
    ys, xs = np.mgrid[0:h, 0:w]
    y0, y1 = np.maximum(0, ys - r), np.minimum(h - 1, ys + r) + 1
    x0, x1 = np.maximum(0, xs - r), np.minimum(w - 1, xs + r) + 1
    s = c[y1, x1] - c[y0, x1] - c[y1, x0] + c[y0, x0]
    return s / ((y1 - y0) * (x1 - x0))


def guided(p_img: np.ndarray, r: int, eps: float, guide: np.ndarray | None = None) -> np.ndarray:
    f = p_img.astype(np.float64) / 255.0
    out = np.zeros_like(f)
    for ch in range(3):
        p = f[..., ch]
        i = p if guide is None else guide
        mean_i, mean_p = _box_mean(i, r), _box_mean(p, r)
        cov = _box_mean(i * p, r) - mean_i * mean_p
        var = _box_mean(i * i, r) - mean_i * mean_i
        a = cov / (var + eps)
        b = mean_p - a * mean_i
        out[..., ch] = _box_mean(a, r) * i + _box_mean(b, r)
    return out


def _to8(x: np.ndarray) -> np.ndarray:
    return np.clip(np.floor(np.clip(x, 0, 1) * 255 + 0.5), 0, 255).astype(np.uint8)


def gen_denoise(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    noisy = _noisy(48, 40, 0xD3A015E)
    large = _noisy(96, 80, 0x1A26E)
    guide = np.clip(np.round(_clean(48, 40)), 0, 255).astype(np.uint8)
    for name, img in (("noisy", noisy), ("noisy_large", large), ("guide", guide)):
        Image.fromarray(img).save(os.path.join(out_dir, f"{name}.png"))

    cases = {
        "bilateral_r3.png": ("noisy.png", {"kind": "bilateral", "radius": 3, "sigma_spatial": 2.0, "sigma_range": 0.15}),
        "bilateral_r5.png": ("noisy.png", {"kind": "bilateral", "radius": 5, "sigma_spatial": 3.0, "sigma_range": 0.3}),
        "bilateral_large_r12.png": ("noisy_large.png", {"kind": "bilateral", "radius": 12, "sigma_spatial": 6.0, "sigma_range": 0.12}),
        "guided_r2.png": ("noisy.png", {"kind": "guided", "radius": 2, "epsilon": 0.01}),
        "guided_r4.png": ("noisy.png", {"kind": "guided", "radius": 4, "epsilon": 0.04}),
        "guided_joint_r3.png": ("noisy.png", {"kind": "guided_joint", "radius": 3, "epsilon": 0.005, "guide": "guide.png"}),
    }
    sources = {"noisy.png": noisy, "noisy_large.png": large}
    guide_luma = (0.299 * guide[..., 0] + 0.587 * guide[..., 1] + 0.114 * guide[..., 2]) / 255.0
    entries = []
    for out_name, (source, params) in cases.items():
        src = sources[source]
        if params["kind"] == "bilateral":
            result = bilateral(src, params["radius"], params["sigma_spatial"], params["sigma_range"])
        elif params["kind"] == "guided":
            result = guided(src, params["radius"], params["epsilon"])
        else:
            result = guided(src, params["radius"], params["epsilon"], guide_luma)
        Image.fromarray(_to8(result)).save(os.path.join(out_dir, out_name))
        entries.append({"expected": out_name, "source": source, **params})

    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"cases": entries}, f, indent=2)
        f.write("\n")
    print(f"denoise: {len(entries)} references")


if __name__ == "__main__":
    gen_denoise(os.path.join(os.path.dirname(os.path.abspath(__file__)), "denoise"))

"""Shared helpers for the int8 export and evaluation scripts.

Everything here is deterministic for a given seed: synthetic document pages are rendered with Pillow's
bundled default font (no system fonts), natural-image samples come from a directory of freely licensed
photos that the caller supplies, and every metric is computed with plain numpy.
"""
from __future__ import annotations

import hashlib
import json
import math
import os
from dataclasses import dataclass
from typing import Iterable, Iterator, Sequence

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

IMAGENET_MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
IMAGENET_STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)

WORDS = (
    "the of and to in is that for it as with was on be by this are from at or an have not which but "
    "page document scanner image text line paragraph section figure table invoice total amount date "
    "account reference number customer address payment summary report quarterly results revenue "
    "margin growth market product service quality delivery schedule project contract agreement "
    "clause party shall herein whereas notice period term renewal liability warranty"
).split()


def sha256_file(path: str) -> str:
    """Upper-case hex SHA-256 of a file (the registry format)."""
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def write_json(path: str, value) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(value, handle, indent=2, sort_keys=False)
        handle.write("\n")


# ----- Synthetic document pages -----

def render_page(seed: int, width: int = 850, height: int = 1100) -> Image.Image:
    """An upright RGB text page: a title, justified-ish paragraphs, an occasional rule or table block."""
    rng = np.random.default_rng(seed)
    paper = tuple(int(v) for v in rng.integers(225, 256, size=3))
    page = Image.new("RGB", (width, height), paper)
    draw = ImageDraw.Draw(page)
    ink = tuple(int(v) for v in rng.integers(0, 70, size=3))
    margin = int(rng.integers(40, 110))
    y = margin
    title = ImageFont.load_default(size=int(rng.integers(28, 44)))
    words = " ".join(rng.choice(WORDS, size=int(rng.integers(2, 6)))).title()
    draw.text((margin, y), words, fill=ink, font=title)
    y += int(title.size * 1.8)
    body_size = int(rng.integers(13, 24))
    body = ImageFont.load_default(size=body_size)
    line_height = int(body_size * float(rng.uniform(1.25, 1.7)))
    while y < height - margin - line_height:
        if rng.random() < 0.06:
            draw.line((margin, y + line_height // 2, width - margin, y + line_height // 2), fill=ink, width=2)
            y += line_height
            continue
        if rng.random() < 0.12:
            y += line_height  # paragraph break
            continue
        x = margin + (int(rng.integers(0, 40)) if rng.random() < 0.15 else 0)
        right = width - margin - int(rng.integers(0, 200) if rng.random() < 0.2 else 0)
        line = []
        while True:
            candidate = " ".join(line + [str(rng.choice(WORDS))])
            if draw.textlength(candidate, font=body) > right - x:
                break
            line = candidate.split(" ")
        draw.text((x, y), " ".join(line), fill=ink, font=body)
        y += line_height
    return page


def degrade_page(page: Image.Image, seed: int) -> Image.Image:
    """Scanner-like degradation: slight blur, uneven illumination and sensor noise."""
    rng = np.random.default_rng(seed)
    if rng.random() < 0.5:
        page = page.filter(ImageFilter.GaussianBlur(radius=float(rng.uniform(0.3, 1.2))))
    array = np.asarray(page, dtype=np.float32)
    h, w = array.shape[:2]
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    shade = 1.0 - float(rng.uniform(0.0, 0.35)) * ((xx / w - float(rng.uniform(0, 1))) ** 2 + (yy / h - float(rng.uniform(0, 1))) ** 2)
    array = array * shade[..., None] + rng.normal(0, float(rng.uniform(1, 8)), array.shape)
    return Image.fromarray(np.clip(array, 0, 255).astype(np.uint8))


def warp_page(page: Image.Image, seed: int) -> Image.Image:
    """A curled page photo: a smooth sinusoidal displacement on a darker background."""
    rng = np.random.default_rng(seed)
    array = np.asarray(page, dtype=np.float32)
    h, w = array.shape[:2]
    pad = int(0.08 * max(h, w))
    canvas = np.full((h + 2 * pad, w + 2 * pad, 3), rng.integers(30, 120, size=3), dtype=np.float32)
    canvas[pad:pad + h, pad:pad + w] = array
    hh, ww = canvas.shape[:2]
    yy, xx = np.mgrid[0:hh, 0:ww].astype(np.float32)
    amp = float(rng.uniform(5, 25))
    freq = float(rng.uniform(0.5, 1.5))
    sx = xx + amp * np.sin(yy / hh * math.pi * freq)
    sy = yy + amp * 0.6 * np.sin(xx / ww * math.pi * freq + 1.0)
    sx = np.clip(sx, 0, ww - 1).astype(np.int32)
    sy = np.clip(sy, 0, hh - 1).astype(np.int32)
    return Image.fromarray(canvas[sy, sx].astype(np.uint8))


# ----- Natural images -----

def load_images(directory: str) -> list[tuple[str, Image.Image]]:
    """Every PNG/JPEG in a directory (sorted by name), converted to RGB."""
    items = []
    for name in sorted(os.listdir(directory)):
        if name.lower().endswith((".png", ".jpg", ".jpeg")):
            with Image.open(os.path.join(directory, name)) as image:
                items.append((name, image.convert("RGB")))
    return items


def random_patches(images: Sequence[Image.Image], count: int, size: int, seed: int) -> list[Image.Image]:
    """Random square crops (optionally pre-scaled) from the given images."""
    rng = np.random.default_rng(seed)
    patches = []
    for i in range(count):
        image = images[i % len(images)]
        scale = float(rng.uniform(0.35, 1.0))
        scaled = image.resize((max(size, int(image.width * scale)), max(size, int(image.height * scale))), Image.BILINEAR)
        x = int(rng.integers(0, scaled.width - size + 1))
        y = int(rng.integers(0, scaled.height - size + 1))
        patches.append(scaled.crop((x, y, x + size, y + size)))
    return patches


def split(items: Sequence, holdout_every: int = 3) -> tuple[list, list]:
    """Deterministic calibration / evaluation split: every n-th item is held out for evaluation."""
    calibration = [item for i, item in enumerate(items) if i % holdout_every != holdout_every - 1]
    evaluation = [item for i, item in enumerate(items) if i % holdout_every == holdout_every - 1]
    return calibration, evaluation


# ----- Tensor conversion (mirrors the C# operations) -----

def to_chw(image: Image.Image, mean=0.0, std=1.0, bgr: bool = False) -> np.ndarray:
    array = np.asarray(image.convert("RGB"), dtype=np.float32) / 255.0
    if bgr:
        array = array[..., ::-1]
    array = (array - mean) / std
    return np.ascontiguousarray(array.transpose(2, 0, 1)[None]).astype(np.float32)


def to_gray(image: Image.Image) -> np.ndarray:
    """BT.709 luma rounded to 8 bits, in 0-1, as [1,1,H,W] (what the core ToGrayscaleTensor bridge feeds)."""
    array = np.asarray(image.convert("RGB"), dtype=np.float32)
    luma = np.floor(0.2126 * array[..., 0] + 0.7152 * array[..., 1] + 0.0722 * array[..., 2] + 0.5) / 255.0
    return luma[None, None].astype(np.float32)


def pad_to_multiple(tensor: np.ndarray, multiple: int) -> np.ndarray:
    """Edge-replicate an NCHW tensor so H and W are multiples of `multiple`."""
    _, _, h, w = tensor.shape
    ph = (-h) % multiple
    pw = (-w) % multiple
    if ph == 0 and pw == 0:
        return tensor
    return np.pad(tensor, ((0, 0), (0, 0), (0, ph), (0, pw)), mode="edge")


# ----- Metrics -----

def psnr(a: np.ndarray, b: np.ndarray, peak: float = 1.0) -> float:
    mse = float(np.mean((a.astype(np.float64) - b.astype(np.float64)) ** 2))
    return float("inf") if mse == 0 else 10.0 * math.log10(peak * peak / mse)


def _box_filter(x: np.ndarray, radius: int) -> np.ndarray:
    """Mean over a (2r+1)^2 window with an integral image, 'valid' region only."""
    k = 2 * radius + 1
    c = np.cumsum(np.cumsum(np.pad(x, ((1, 0), (1, 0))), axis=0), axis=1)
    return (c[k:, k:] - c[:-k, k:] - c[k:, :-k] + c[:-k, :-k]) / (k * k)


def ssim(a: np.ndarray, b: np.ndarray, peak: float = 1.0) -> float:
    """Mean SSIM over 2-D planes (channels averaged), 7x7 uniform window, standard constants."""
    a = np.asarray(a, dtype=np.float64)
    b = np.asarray(b, dtype=np.float64)
    if a.ndim == 4:
        a, b = a[0], b[0]
    if a.ndim == 2:
        a, b = a[None], b[None]
    c1 = (0.01 * peak) ** 2
    c2 = (0.03 * peak) ** 2
    scores = []
    for pa, pb in zip(a, b):
        mu_a = _box_filter(pa, 3)
        mu_b = _box_filter(pb, 3)
        var_a = _box_filter(pa * pa, 3) - mu_a ** 2
        var_b = _box_filter(pb * pb, 3) - mu_b ** 2
        cov = _box_filter(pa * pb, 3) - mu_a * mu_b
        s = ((2 * mu_a * mu_b + c1) * (2 * cov + c2)) / ((mu_a ** 2 + mu_b ** 2 + c1) * (var_a + var_b + c2))
        scores.append(float(np.mean(s)))
    return float(np.mean(scores))


def iou(mask_a: np.ndarray, mask_b: np.ndarray) -> float:
    a = mask_a.astype(bool)
    b = mask_b.astype(bool)
    union = np.logical_or(a, b).sum()
    return 1.0 if union == 0 else float(np.logical_and(a, b).sum() / union)


@dataclass
class Summary:
    mean: float
    minimum: float
    maximum: float

    @staticmethod
    def of(values: Iterable[float]) -> "Summary":
        values = [float(v) for v in values]
        return Summary(float(np.mean(values)), float(np.min(values)), float(np.max(values)))

    def as_dict(self, digits: int = 4) -> dict:
        return {"mean": round(self.mean, digits), "min": round(self.minimum, digits), "max": round(self.maximum, digits)}


def batched(items: Sequence, size: int) -> Iterator[Sequence]:
    for start in range(0, len(items), size):
        yield items[start:start + size]

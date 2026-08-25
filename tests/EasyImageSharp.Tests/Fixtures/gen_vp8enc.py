#!/usr/bin/env python
"""Fixture generator for the VP8 lossy encoder tests.

Discovered by generate.py, which calls ``gen_vp8enc(<Fixtures>/vp8enc)``.

The encoder tests need source images, not encoded files: the library encodes them and the test decodes
the result with the library's own bit-exact VP8 decoder. Two sources are provided:

  photo.png   a photographic stand-in: several octaves of smooth value noise per channel plus a few
              soft shapes and a vignette, so it has the mix of flat areas, gradients and texture that
              drives a lossy encoder's mode decision the way a real photograph does
  sharp.png   hard-edged synthetic geometry, which is what breaks a bad intra mode decision

Everything is derived from fixed seeds, so re-running the script produces byte-identical output.
"""
from __future__ import annotations

import os

import numpy as np
from PIL import Image


def _value_noise(shape: tuple[int, int], cells: int, rng: np.random.Generator) -> np.ndarray:
    """One octave of smoothly interpolated value noise in [0, 1]."""
    height, width = shape
    grid = rng.random((cells + 1, cells + 1))
    ys = np.linspace(0.0, cells, height, endpoint=False)
    xs = np.linspace(0.0, cells, width, endpoint=False)
    y0 = np.floor(ys).astype(int)
    x0 = np.floor(xs).astype(int)
    fy = (ys - y0)[:, None]
    fx = (xs - x0)[None, :]

    # Smoothstep keeps the octave free of the visible grid seams a linear blend would leave.
    fy = fy * fy * (3.0 - 2.0 * fy)
    fx = fx * fx * (3.0 - 2.0 * fx)

    top = grid[np.ix_(y0, x0)] * (1.0 - fx) + grid[np.ix_(y0, x0 + 1)] * fx
    bottom = grid[np.ix_(y0 + 1, x0)] * (1.0 - fx) + grid[np.ix_(y0 + 1, x0 + 1)] * fx
    return top * (1.0 - fy) + bottom * fy


def _photo(width: int, height: int) -> Image.Image:
    rng = np.random.default_rng(20260823)
    channels = []
    for base in (0.46, 0.42, 0.38):
        field = np.zeros((height, width), dtype=np.float64)
        amplitude = 0.30
        for cells in (3, 6, 12, 24, 48):
            field += amplitude * _value_noise((height, width), cells, rng)
            amplitude *= 0.55
        field = base + field - field.mean()
        channels.append(field)

    ys = np.linspace(-1.0, 1.0, height)[:, None]
    xs = np.linspace(-1.0, 1.0, width)[None, :]

    # A soft bright disc and a dark band give the frame some real structure to predict.
    disc = np.exp(-6.0 * (((xs - 0.30) ** 2) + ((ys + 0.20) ** 2)))
    band = np.exp(-40.0 * ((ys - (0.35 * xs) - 0.45) ** 2))
    vignette = 1.0 - (0.35 * ((xs ** 2) + (ys ** 2)))

    channels[0] = (channels[0] + (0.34 * disc) - (0.18 * band)) * vignette
    channels[1] = (channels[1] + (0.26 * disc) - (0.14 * band)) * vignette
    channels[2] = (channels[2] + (0.10 * disc) - (0.06 * band)) * vignette

    rgb = np.stack(channels, axis=-1)
    rgb = np.clip(rgb, 0.0, 1.0)
    return Image.fromarray((rgb * 255.0 + 0.5).astype(np.uint8), "RGB")


def _sharp(width: int, height: int) -> Image.Image:
    data = np.zeros((height, width, 3), dtype=np.uint8)
    data[:, :] = (24, 28, 40)
    ys = np.arange(height)[:, None]
    xs = np.arange(width)[None, :]

    data[((xs // 8) + (ys // 8)) % 2 == 0] = (240, 240, 235)
    data[np.abs((2 * ys) - xs - 10) < 3] = (220, 30, 30)
    data[np.abs(ys + xs - 70) < 4] = (30, 200, 90)
    data[(ys > height // 3) & (ys < (2 * height) // 3) & (xs > width // 4) & (xs < width // 2)] = (20, 60, 220)
    data[np.broadcast_to((ys % 16) == 0, (height, width))] = (255, 255, 0)
    return Image.fromarray(data, "RGB")


def gen_vp8enc(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    _photo(224, 160).save(os.path.join(out_dir, "photo.png"), optimize=True)
    _sharp(112, 80).save(os.path.join(out_dir, "sharp.png"), optimize=True)

    with open(os.path.join(out_dir, "EXPECTED.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(
            "# vp8enc fixtures\n\n"
            "Source images for the VP8 lossy encoder tests. These are inputs, not encoded fixtures: the\n"
            "tests encode them with the library's VP8 encoder and decode the result with the library's\n"
            "bit-exact VP8 decoder, which is itself verified against libwebp elsewhere in the suite.\n\n"
            "| file | size | content |\n"
            "| --- | --- | --- |\n"
            "| photo.png | 224x160 | photographic stand-in: five octaves of smooth value noise per\n"
            "channel plus a soft disc, a dark band and a vignette |\n"
            "| sharp.png | 112x80 | hard-edged synthetic geometry: checkerboard, thin diagonals and a\n"
            "solid rectangle, which stresses the intra mode decision |\n\n"
            "Regenerate with `python tests/EasyImageSharp.Tests/Fixtures/generate.py`.\n"
        )

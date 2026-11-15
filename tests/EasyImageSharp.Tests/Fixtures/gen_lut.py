"""Colour lookup table fixtures (Fixtures/lut/).

The LUT contents come from a fixed analytic grade (channel mixing, per-channel gamma and an S-curve); the
expected images come from Pillow (ImageFilter.Color3DLUT, trilinear) or from numpy implementations of the
published interpolation formulas, never from the library under test.

  src.png                       64x48 RGB source (gradients, a grey ramp and saturated patches)
  grade17.cube                  17^3 lattice with TITLE, comments and explicit default DOMAIN_MIN/MAX
  grade17_trilinear_pillow.png  Pillow Color3DLUT(17, table).filter(src)            -> within +-2 (Pillow
                                interpolates in 16-bit fixed point)
  grade17_trilinear.png         numpy trilinear, float64, rounded                    -> within +-1
  grade17_tetrahedral.png       numpy tetrahedral (Kasson, Nin, Plouffe, Hafner 1995: the six-tetrahedron
                                split ordered by the fractional coordinates), float64 -> within +-1
  domain9.cube                  9^3 lattice over DOMAIN_MIN -0.25 -0.25 -0.25 .. DOMAIN_MAX 1.25 1.5 1.25
  domain9_tetrahedral.png       numpy tetrahedral with that domain                   -> within +-1
  shaper32.cube                 LUT_1D_SIZE 32 per-channel curves only
  shaper32.png                  numpy per-channel linear interpolation               -> within +-1
  shaper3d.cube                 Resolve-style 1D shaper (16, LUT_1D_INPUT_RANGE 0 1) + 9^3 lattice
                                (LUT_3D_INPUT_RANGE 0 1)
  shaper3d_tetrahedral.png      numpy: shaper then tetrahedral                       -> within +-1
  hald4.png                     level-4 Hald CLUT (64x64 pixels, 16^3 lattice) of the grade, 8-bit
  hald4_tetrahedral.png         numpy tetrahedral over the Hald's 8-bit values / 255 -> within +-1
  manifest.json                 the files above and their parameters
"""
from __future__ import annotations

import json
import os

import numpy as np
from PIL import Image, ImageFilter

W, H = 64, 48


def _src() -> np.ndarray:
    ys, xs = np.mgrid[0:H, 0:W]
    a = np.zeros((H, W, 3), dtype=np.int64)
    a[..., 0] = xs * 255 // (W - 1)
    a[..., 1] = ys * 255 // (H - 1)
    a[..., 2] = (xs * 5 + ys * 3) % 256
    grey = ys >= H - 6
    a[grey] = np.repeat((xs[grey] * 255 // (W - 1))[:, None], 3, axis=1)
    a[0:6, 0:8] = (255, 0, 0)
    a[0:6, 8:16] = (0, 255, 0)
    a[0:6, 16:24] = (0, 0, 255)
    a[0:6, 24:32] = (255, 255, 0)
    return a.astype(np.uint8)


def _grade(rgb: np.ndarray) -> np.ndarray:
    """A smooth cross-channel grade on values in 0-1 (inputs outside are clamped first)."""
    c = np.clip(rgb, 0.0, 1.0)
    r, g, b = c[..., 0], c[..., 1], c[..., 2]
    r2 = np.clip(0.85 * r + 0.15 * g, 0, 1) ** 0.9
    g2 = np.clip(0.1 * r + 0.8 * g + 0.1 * b, 0, 1) ** 1.1
    b2 = np.clip(0.2 * g + 0.75 * b + 0.05 * np.sin(6 * r), 0, 1) ** 0.8
    out = np.stack([r2, g2, b2], axis=-1)
    s_curve = 0.5 - 0.5 * np.cos(np.pi * out)
    return 0.7 * out + 0.3 * s_curve


def _lattice(size: int, lo: np.ndarray, hi: np.ndarray) -> np.ndarray:
    """(size^3, 3) table with red varying fastest, sampled over [lo, hi], rounded to the 6 decimals written."""
    idx = np.arange(size) / (size - 1)
    b, g, r = np.meshgrid(idx, idx, idx, indexing="ij")
    coords = np.stack([r, g, b], axis=-1).reshape(-1, 3)
    coords = lo + coords * (hi - lo)
    return np.round(_grade(coords), 6)


def _write_cube(path: str, header: list[str], tables: list[np.ndarray]) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        for line in header:
            f.write(line + "\n")
        for table in tables:
            for row in table:
                f.write(f"{row[0]:.6f} {row[1]:.6f} {row[2]:.6f}\n")


def _sample(table: np.ndarray, size: int, rgb: np.ndarray, lo: np.ndarray, hi: np.ndarray, tetra: bool) -> np.ndarray:
    lat = table.reshape(size, size, size, 3)  # [b, g, r]
    p = np.clip((rgb - lo) / (hi - lo) * (size - 1), 0, size - 1)
    i0 = np.minimum(np.floor(p).astype(np.int64), size - 2)
    f = p - i0
    r0, g0, b0 = i0[..., 0], i0[..., 1], i0[..., 2]
    fr, fg, fb = f[..., 0:1], f[..., 1:2], f[..., 2:3]

    def c(dr, dg, db):
        return lat[b0 + db, g0 + dg, r0 + dr]

    if not tetra:
        c00 = c(0, 0, 0) * (1 - fr) + c(1, 0, 0) * fr
        c10 = c(0, 1, 0) * (1 - fr) + c(1, 1, 0) * fr
        c01 = c(0, 0, 1) * (1 - fr) + c(1, 0, 1) * fr
        c11 = c(0, 1, 1) * (1 - fr) + c(1, 1, 1) * fr
        return (c00 * (1 - fg) + c10 * fg) * (1 - fb) + (c01 * (1 - fg) + c11 * fg) * fb

    v000, v111 = c(0, 0, 0), c(1, 1, 1)
    cases = [
        ((fr > fg) & (fg > fb), (1 - fr) * v000 + (fr - fg) * c(1, 0, 0) + (fg - fb) * c(1, 1, 0) + fb * v111),
        ((fr > fg) & (fg <= fb) & (fr > fb), (1 - fr) * v000 + (fr - fb) * c(1, 0, 0) + (fb - fg) * c(1, 0, 1) + fg * v111),
        ((fr > fg) & (fg <= fb) & (fr <= fb), (1 - fb) * v000 + (fb - fr) * c(0, 0, 1) + (fr - fg) * c(1, 0, 1) + fg * v111),
        ((fr <= fg) & (fb > fg), (1 - fb) * v000 + (fb - fg) * c(0, 0, 1) + (fg - fr) * c(0, 1, 1) + fr * v111),
        ((fr <= fg) & (fb <= fg) & (fb > fr), (1 - fg) * v000 + (fg - fb) * c(0, 1, 0) + (fb - fr) * c(0, 1, 1) + fr * v111),
        ((fr <= fg) & (fb <= fg) & (fb <= fr), (1 - fg) * v000 + (fg - fr) * c(0, 1, 0) + (fr - fb) * c(1, 1, 0) + fb * v111),
    ]
    out = np.zeros(rgb.shape, dtype=np.float64)
    for mask, value in cases:
        out = np.where(mask, value, out)
    return out


def _shape(table: np.ndarray, size: int, rgb: np.ndarray, lo: float, hi: float) -> np.ndarray:
    p = np.clip((rgb - lo) / (hi - lo) * (size - 1), 0, size - 1)
    i0 = np.minimum(np.floor(p).astype(np.int64), size - 2)
    t = p - i0
    out = np.zeros(rgb.shape, dtype=np.float64)
    for ch in range(3):
        a = table[i0[..., ch], ch]
        b = table[i0[..., ch] + 1, ch]
        out[..., ch] = a + (b - a) * t[..., ch]
    return out


def _to8(x: np.ndarray) -> np.ndarray:
    return np.clip(np.floor(np.clip(x, 0, 1) * 255 + 0.5), 0, 255).astype(np.uint8)


def gen_lut(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    src = _src()
    Image.fromarray(src).save(os.path.join(out_dir, "src.png"))
    rgb = src.astype(np.float64) / 255.0
    zero, one = np.zeros(3), np.ones(3)

    grade17 = _lattice(17, zero, one)
    _write_cube(os.path.join(out_dir, "grade17.cube"), [
        "# Analytic test grade written by gen_lut.py",
        'TITLE "EasyImageSharp test grade"',
        "",
        "LUT_3D_SIZE 17",
        "DOMAIN_MIN 0.0 0.0 0.0",
        "DOMAIN_MAX 1.0 1.0 1.0",
        "# data follows, red varying fastest",
    ], [grade17])
    pillow_lut = ImageFilter.Color3DLUT(17, [tuple(v) for v in grade17.tolist()])
    Image.fromarray(src).filter(pillow_lut).save(os.path.join(out_dir, "grade17_trilinear_pillow.png"))
    Image.fromarray(_to8(_sample(grade17, 17, rgb, zero, one, False))).save(os.path.join(out_dir, "grade17_trilinear.png"))
    Image.fromarray(_to8(_sample(grade17, 17, rgb, zero, one, True))).save(os.path.join(out_dir, "grade17_tetrahedral.png"))

    lo, hi = np.array([-0.25, -0.25, -0.25]), np.array([1.25, 1.5, 1.25])
    domain9 = _lattice(9, lo, hi)
    _write_cube(os.path.join(out_dir, "domain9.cube"), [
        "LUT_3D_SIZE 9", "DOMAIN_MIN -0.25 -0.25 -0.25", "DOMAIN_MAX 1.25 1.5 1.25",
    ], [domain9])
    Image.fromarray(_to8(_sample(domain9, 9, rgb, lo, hi, True))).save(os.path.join(out_dir, "domain9_tetrahedral.png"))

    x = np.arange(32) / 31.0
    shaper32 = np.round(np.stack([x ** 0.6, x ** 1.4, 1 - (1 - x) ** 2], axis=-1), 6)
    _write_cube(os.path.join(out_dir, "shaper32.cube"), ["TITLE \"curves\"", "LUT_1D_SIZE 32"], [shaper32])
    Image.fromarray(_to8(_shape(shaper32, 32, rgb, 0.0, 1.0))).save(os.path.join(out_dir, "shaper32.png"))

    x16 = np.arange(16) / 15.0
    shaper16 = np.round(np.stack([x16 ** 0.8, x16 ** 0.8, x16 ** 0.8], axis=-1), 6)
    lattice9 = _lattice(9, zero, one)
    _write_cube(os.path.join(out_dir, "shaper3d.cube"), [
        "LUT_1D_SIZE 16", "LUT_1D_INPUT_RANGE 0.0 1.0", "LUT_3D_SIZE 9", "LUT_3D_INPUT_RANGE 0.0 1.0",
    ], [shaper16, lattice9])
    shaped = _shape(shaper16, 16, rgb, 0.0, 1.0)
    Image.fromarray(_to8(_sample(lattice9, 9, shaped, zero, one, True))).save(os.path.join(out_dir, "shaper3d_tetrahedral.png"))

    hald_table = _to8(_lattice(16, zero, one))  # 4096 entries -> a 64x64 image, red fastest, row by row
    Image.fromarray(hald_table.reshape(64, 64, 3)).save(os.path.join(out_dir, "hald4.png"))
    hald_values = hald_table.astype(np.float64) / 255.0
    Image.fromarray(_to8(_sample(hald_values, 16, rgb, zero, one, True))).save(os.path.join(out_dir, "hald4_tetrahedral.png"))

    manifest = {
        "source": "src.png",
        "cases": [
            {"lut": "grade17.cube", "interpolation": "trilinear", "expected": "grade17_trilinear_pillow.png", "tolerance": 2},
            {"lut": "grade17.cube", "interpolation": "trilinear", "expected": "grade17_trilinear.png", "tolerance": 1},
            {"lut": "grade17.cube", "interpolation": "tetrahedral", "expected": "grade17_tetrahedral.png", "tolerance": 1},
            {"lut": "domain9.cube", "interpolation": "tetrahedral", "expected": "domain9_tetrahedral.png", "tolerance": 1},
            {"lut": "shaper32.cube", "interpolation": "tetrahedral", "expected": "shaper32.png", "tolerance": 1},
            {"lut": "shaper3d.cube", "interpolation": "tetrahedral", "expected": "shaper3d_tetrahedral.png", "tolerance": 1},
            {"lut": "hald4.png", "interpolation": "tetrahedral", "expected": "hald4_tetrahedral.png", "tolerance": 1},
        ],
    }
    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(f"lut: {len(manifest['cases'])} cases")


if __name__ == "__main__":
    gen_lut(os.path.join(os.path.dirname(os.path.abspath(__file__)), "lut"))

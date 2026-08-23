"""Reference outputs for the geometry pipeline (resize kernels, affine and perspective warps).

Called by generate.py as gen_geometry(Fixtures/geometry). Everything is derived from a deterministic
synthetic 96x64 RGB image, so re-running produces byte-identical files.

Layout:
  source.rgba                       the smooth RGB source (alpha 255), width*height*4 bytes
  <name>.rgba                       Pillow's output for that entry, RGBA, row-major, top-left origin
  manifest.json                     entries with the parameters the C# tests need to reproduce the operation:
      kind = "resize":       filter, width, height                       -> Image.resize
      kind = "affine":       filter, width, height, coeffs[6], fill      -> Image.transform(AFFINE)
      kind = "perspective":  filter, width, height, coeffs[8], fill      -> Image.transform(PERSPECTIVE)

Pillow's transform coefficients map OUTPUT pixel centres to INPUT coordinates (the inverse mapping):
    affine:      xin = a*x + b*y + c,                  yin = d*x + e*y + f
    perspective: xin = (a*x + b*y + c)/(g*x + h*y + 1), yin = (d*x + e*y + f)/(g*x + h*y + 1)
with x, y being the output pixel centre (column + 0.5, row + 0.5) and pixel centres of the input likewise at
half-integers. The library uses the same conventions, so the C# tests rebuild the inverse matrix from these
numbers directly.

Kernel formulas match between the two implementations (Pillow: box/bilinear/bicubic a=-0.5/lanczos3), but the
arithmetic differs (Pillow filters in fixed point with an 8-bit intermediate, this library in float32), so the
comparisons are PSNR-based except for the nearest-neighbour entries whose coefficients are exact in float32.
"""
from __future__ import annotations

import json
import math
import os

import numpy as np
from PIL import Image

WIDTH, HEIGHT = 96, 64


def _source() -> Image.Image:
    ys, xs = np.mgrid[0:HEIGHT, 0:WIDTH].astype(np.float64)
    r = 128 + 100 * np.sin(xs / 9.0) * np.cos(ys / 7.0)
    g = 40 + 170 * (xs / (WIDTH - 1)) * (0.5 + 0.5 * np.cos(ys / 5.0))
    b = 255 * np.exp(-(((xs - 48) ** 2) / 900.0 + ((ys - 30) ** 2) / 500.0))
    rgb = np.stack([r, g, b], axis=-1)
    return Image.fromarray(np.clip(np.round(rgb), 0, 255).astype(np.uint8), "RGB")


def _write_rgba(path: str, im: Image.Image) -> None:
    with open(path, "wb") as f:
        f.write(im.convert("RGBA").tobytes())


def _rotation_inverse(width: int, height: int, degrees: float) -> tuple[list[float], int, int]:
    """Inverse (output->input) affine coefficients for a clockwise rotation about the centre with an expanded canvas."""
    rad = math.radians(degrees)
    c, s = math.cos(rad), math.sin(rad)
    out_w = int(math.ceil(abs(width * c) + abs(height * s)))
    out_h = int(math.ceil(abs(width * s) + abs(height * c)))
    # Forward (clockwise on screen): x' = c*x - s*y, y' = s*x + c*y about the centre; inverse rotates by -angle.
    cx, cy = width / 2.0, height / 2.0
    ox, oy = out_w / 2.0, out_h / 2.0
    # xin = c*(x-ox) + s*(y-oy) + cx ; yin = -s*(x-ox) + c*(y-oy) + cy
    a, b, cc = c, s, cx - c * ox - s * oy
    d, e, f = -s, c, cy + s * ox - c * oy
    return [a, b, cc, d, e, f], out_w, out_h


def gen_geometry(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    src = _source()
    _write_rgba(os.path.join(out_dir, "source.rgba"), src)
    manifest: list[dict] = []

    filters = {
        "box": Image.BOX,
        "bilinear": Image.BILINEAR,
        "bicubic": Image.BICUBIC,
        "lanczos": Image.LANCZOS,
    }

    # ----- Resize -----
    for name, flt in filters.items():
        sizes = ((40, 30),) if name == "box" else ((40, 30), (112, 72))
        for (w, h) in sizes:
            entry_name = f"resize_{name}_{w}x{h}"
            out = src.resize((w, h), resample=flt)
            _write_rgba(os.path.join(out_dir, entry_name + ".rgba"), out)
            manifest.append({"name": entry_name, "kind": "resize", "filter": name, "width": w, "height": h,
                             "notes": f"Image.resize({w}x{h}, {name.upper()})"})

    # ----- Affine: nearest with float32-exact coefficients (must match pixel for pixel) -----
    coeffs = [0.75, 0.5, 0.25, -0.25, 1.25, 0.125]
    out = src.transform((WIDTH, HEIGHT), Image.AFFINE, coeffs, resample=Image.NEAREST, fillcolor=(0, 0, 0))
    _write_rgba(os.path.join(out_dir, "affine_nearest_exact.rgba"), out)
    manifest.append({"name": "affine_nearest_exact", "kind": "affine", "filter": "nearest", "width": WIDTH,
                     "height": HEIGHT, "coeffs": coeffs, "fill": [0, 0, 0, 255],
                     "notes": "scale/shear/translate with dyadic coefficients; NEAREST; exact match expected"})

    # ----- Affine: rotation by 30 degrees, expanded canvas, several kernels -----
    coeffs, out_w, out_h = _rotation_inverse(WIDTH, HEIGHT, 30.0)
    for name, flt in (("bilinear", Image.BILINEAR), ("bicubic", Image.BICUBIC), ("nearest", Image.NEAREST)):
        entry_name = f"affine_rot30_{name}"
        out = src.transform((out_w, out_h), Image.AFFINE, coeffs, resample=flt, fillcolor=(0, 0, 0))
        _write_rgba(os.path.join(out_dir, entry_name + ".rgba"), out)
        manifest.append({"name": entry_name, "kind": "affine", "filter": name, "width": out_w, "height": out_h,
                         "coeffs": coeffs, "fill": [0, 0, 0, 255],
                         "notes": f"clockwise 30 degree rotation about the centre, {name.upper()}"})

    # ----- Perspective: explicit inverse coefficients -----
    coeffs = [1.1, 0.15, -4.0, -0.05, 1.2, 2.0, 0.002, 0.001]
    for name, flt in (("bilinear", Image.BILINEAR), ("bicubic", Image.BICUBIC), ("nearest", Image.NEAREST)):
        entry_name = f"perspective_{name}"
        out = src.transform((WIDTH, HEIGHT), Image.PERSPECTIVE, coeffs, resample=flt, fillcolor=(0, 0, 0))
        _write_rgba(os.path.join(out_dir, entry_name + ".rgba"), out)
        manifest.append({"name": entry_name, "kind": "perspective", "filter": name, "width": WIDTH, "height": HEIGHT,
                         "coeffs": coeffs, "fill": [0, 0, 0, 255],
                         "notes": f"mild perspective (g=0.002, h=0.001), {name.upper()}"})

    with open(os.path.join(out_dir, "manifest.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=1)
        f.write("\n")

    with open(os.path.join(out_dir, "EXPECTED.md"), "w", newline="\n") as f:
        f.write("# geometry fixtures\n\n")
        f.write("Reference outputs written by Pillow for the resize kernels and the affine/perspective warps.\n")
        f.write("`source.rgba` is the 96x64 synthetic RGB input; every other `.rgba` is Pillow's result for the\n")
        f.write("operation described in `manifest.json` (see the module docstring of `gen_geometry.py` for the\n")
        f.write("coefficient conventions). Tests compare by PSNR, except the nearest-neighbour entries which must\n")
        f.write("match exactly (affine) or on at least 99.5% of pixels (perspective, where float/double division\n")
        f.write("can flip a pixel that lands within 1e-6 of a boundary).\n")

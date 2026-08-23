"""Reference images for the colour / convolution / compositing / histogram tests (Fixtures/effects/).

Every file is derived from fixed formulas (no randomness) so re-running is byte-identical. All sources are
small synthetic images; the expected outputs come from Pillow or numpy, never from the library under test.

  src_rgb.png                64x48 RGB source (gradient + checker + diagonal stripe)
  src_rgba.png               64x48 RGBA source (same colours, alpha ramp; fully transparent column band)
  overlay_rgba.png           32x24 RGBA overlay with hard and soft alpha regions
  eq_exact_src.png           256x256 L image with exactly ONE pixel at its maximum level, so Pillow's
                             integer equalization step (pixels - count(max level)) // 255 == 257 is exact
  eq_exact_expected.png      Pillow ImageOps.equalize(eq_exact_src)   -> must match exactly
  eq_general_src.png         256x256 L low-contrast image
  eq_general_expected.png    Pillow ImageOps.equalize(eq_general_src) -> must match within +-1
  boxblur2_expected.png      Pillow ImageFilter.BoxBlur(2) of src_rgb  -> within +-1
  kernel3_expected.png       Pillow ImageFilter.Kernel 3x3 (sharpen-ish) of src_rgb -> interior within +-1
                             (Pillow leaves the 1-pixel border untouched; the library replicates edges)
  sobel_expected.png         numpy Sobel magnitude of the BT.709 grayscale of src_rgb with edge
                             replication, clamped to 0..255 -> within +-1
  conv5_expected.png         numpy 5x5 convolution (asymmetric kernel) with edge replication -> within +-1
  chops_*.png                Pillow ImageChops multiply/screen/add/subtract/difference/darker/lighter of
                             src_rgb and the overlay stretched to 64x48 -> within +-1
  alpha_composite_expected.png  Pillow Image.alpha_composite(src_rgba, overlay placed at (8,6)) -> within +-1
  manifest.json              the kernels and parameters used above
"""
from __future__ import annotations

import json
import os

import numpy as np
from PIL import Image, ImageChops, ImageFilter, ImageOps

W, H = 64, 48


def _src_rgb() -> np.ndarray:
    a = np.zeros((H, W, 3), dtype=np.uint8)
    for y in range(H):
        for x in range(W):
            r = x * 255 // (W - 1)
            g = y * 255 // (H - 1)
            b = (x * 7 + y * 13) % 256
            if ((x // 8) + (y // 8)) % 2 == 0:
                r = 255 - r
            if abs(x - y) < 2:
                r, g, b = 250, 250, 250
            a[y, x] = (r, g, b)
    return a


def _src_rgba() -> np.ndarray:
    rgb = _src_rgb()
    a = np.zeros((H, W, 4), dtype=np.uint8)
    a[..., :3] = rgb
    for y in range(H):
        for x in range(W):
            alpha = 255 if x < W // 2 else 128 + ((x * 3) % 100)
            if 20 <= x < 24:
                alpha = 0
            a[y, x, 3] = alpha
    return a


def _overlay_rgba() -> np.ndarray:
    ow, oh = 32, 24
    a = np.zeros((oh, ow, 4), dtype=np.uint8)
    for y in range(oh):
        for x in range(ow):
            r = 200 - (x * 3)
            g = 30 + (y * 8)
            b = (x * y) % 256
            alpha = 255 if x < 10 else (0 if x < 14 else min(255, (x - 14) * 14 + 5))
            a[y, x] = (r, g, b, alpha)
    return a


def _eq_exact_src() -> np.ndarray:
    ys, xs = np.mgrid[0:256, 0:256]
    v = 90 + 50 * np.sin(xs / 19.0) + 35 * np.cos(ys / 23.0) + ((xs // 16 + ys // 16) % 3) * 6
    v = np.clip(np.round(v), 0, 200).astype(np.uint8)
    v[0, 0] = 255  # the single maximum-level pixel: (65536 - 1) // 255 == 257 exactly
    return v


def _eq_general_src() -> np.ndarray:
    ys, xs = np.mgrid[0:256, 0:256]
    v = 110 + 30 * np.sin(xs / 31.0) * np.cos(ys / 17.0) + 12 * np.sin((xs + ys) / 9.0) + ((xs // 4 + ys // 4) % 5)
    return np.clip(np.round(v), 0, 255).astype(np.uint8)


def gen_effects(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)

    def save(name: str, arr: np.ndarray, mode: str) -> None:
        im = Image.fromarray(arr)
        assert im.mode == mode, (name, im.mode, mode)
        im.save(os.path.join(out_dir, name), optimize=True)

    src_rgb = _src_rgb()
    src_rgba = _src_rgba()
    overlay = _overlay_rgba()
    save("src_rgb.png", src_rgb, "RGB")
    save("src_rgba.png", src_rgba, "RGBA")
    save("overlay_rgba.png", overlay, "RGBA")

    # ---- Histogram equalization (Pillow) ----
    eq_exact = _eq_exact_src()
    save("eq_exact_src.png", eq_exact, "L")
    save("eq_exact_expected.png", np.array(ImageOps.equalize(Image.fromarray(eq_exact))), "L")
    eq_general = _eq_general_src()
    save("eq_general_src.png", eq_general, "L")
    save("eq_general_expected.png", np.array(ImageOps.equalize(Image.fromarray(eq_general))), "L")

    # ---- Box blur (Pillow) ----
    im_rgb = Image.fromarray(src_rgb)
    save("boxblur2_expected.png", np.array(im_rgb.filter(ImageFilter.BoxBlur(2))), "RGB")

    # ---- 3x3 kernel (Pillow; border pixels untouched by Pillow) ----
    kernel3 = [0, -1, 0, -1, 6, -1, 0, -1, 0]
    save("kernel3_expected.png", np.array(im_rgb.filter(ImageFilter.Kernel((3, 3), kernel3, scale=2, offset=0))), "RGB")

    # ---- numpy references with edge replication ----
    def conv2d_replicate(plane: np.ndarray, kernel: np.ndarray) -> np.ndarray:
        kh, kw = kernel.shape
        ay, ax = (kh - 1) // 2, (kw - 1) // 2
        padded = np.pad(plane.astype(np.float64), ((ay, kh - 1 - ay), (ax, kw - 1 - ax)), mode="edge")
        out = np.zeros(plane.shape, dtype=np.float64)
        for ky in range(kh):
            for kx in range(kw):
                out += kernel[ky, kx] * padded[ky:ky + plane.shape[0], kx:kx + plane.shape[1]]
        return out

    gray = np.floor(src_rgb[..., 0] * 0.2126 + src_rgb[..., 1] * 0.7152 + src_rgb[..., 2] * 0.0722 + 0.5)
    gray = np.clip(gray, 0, 255)
    sobel_x = np.array([[-1, 0, 1], [-2, 0, 2], [-1, 0, 1]], dtype=np.float64)
    sobel_y = np.array([[-1, -2, -1], [0, 0, 0], [1, 2, 1]], dtype=np.float64)
    gx = conv2d_replicate(gray, sobel_x)
    gy = conv2d_replicate(gray, sobel_y)
    mag = np.clip(np.floor(np.sqrt(gx * gx + gy * gy) + 0.5), 0, 255).astype(np.uint8)
    save("sobel_expected.png", mag, "L")

    conv5 = np.array(
        [
            [0.00, 0.01, 0.02, 0.01, 0.00],
            [0.01, 0.05, 0.10, 0.05, 0.01],
            [0.02, 0.10, 0.28, 0.10, 0.02],
            [0.01, 0.05, 0.10, 0.05, 0.01],
            [0.00, 0.01, 0.02, 0.01, 0.00],
        ]
    ) * 0.9 + np.array(
        [
            [0.0, 0.0, 0.0, 0.0, 0.0],
            [0.0, 0.0, 0.0, 0.0, 0.0],
            [0.0, 0.0, 0.0, 0.0, 0.0],
            [0.0, 0.0, 0.0, 0.0, 0.0],
            [0.0, 0.0, 0.0, 0.0, 0.1],
        ]
    )
    conv5_out = np.stack([conv2d_replicate(src_rgb[..., c], conv5) for c in range(3)], axis=-1)
    save("conv5_expected.png", np.clip(np.floor(conv5_out + 0.5), 0, 255).astype(np.uint8), "RGB")

    # ---- Blend modes (Pillow ImageChops on opaque images) ----
    overlay_big = Image.fromarray(overlay).resize((W, H), Image.NEAREST).convert("RGB")
    save("chops_overlay_rgb.png", np.array(overlay_big), "RGB")
    save("chops_multiply.png", np.array(ImageChops.multiply(im_rgb, overlay_big)), "RGB")
    save("chops_screen.png", np.array(ImageChops.screen(im_rgb, overlay_big)), "RGB")
    save("chops_add.png", np.array(ImageChops.add(im_rgb, overlay_big)), "RGB")
    save("chops_subtract.png", np.array(ImageChops.subtract(im_rgb, overlay_big)), "RGB")
    save("chops_difference.png", np.array(ImageChops.difference(im_rgb, overlay_big)), "RGB")
    save("chops_darker.png", np.array(ImageChops.darker(im_rgb, overlay_big)), "RGB")
    save("chops_lighter.png", np.array(ImageChops.lighter(im_rgb, overlay_big)), "RGB")

    # ---- Alpha composite (Pillow, premultiplied source-over) ----
    base = Image.fromarray(src_rgba)
    layer = Image.new("RGBA", base.size, (0, 0, 0, 0))
    layer.paste(Image.fromarray(overlay), (8, 6))
    save("alpha_composite_expected.png", np.array(Image.alpha_composite(base, layer)), "RGBA")

    manifest = {
        "size": [W, H],
        "overlay_size": [32, 24],
        "overlay_location": [8, 6],
        "kernel3": {"values": kernel3, "scale": 2, "offset": 0, "note": "Pillow applies sum(k*p)/scale + offset; border pixels are copied unchanged"},
        "conv5": [[round(float(v), 6) for v in row] for row in conv5],
        "boxblur_radius": 2,
        "eq_exact": {"size": [256, 256], "max_level_pixels": 1, "step": 257},
        "eq_general": {"size": [256, 256], "tolerance": 1},
    }
    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2)
        fh.write("\n")


if __name__ == "__main__":
    gen_effects(os.path.join(os.path.dirname(os.path.abspath(__file__)), "effects"))

"""Document-imaging fixtures: synthetic text pages for skew/orientation/page detection, line and
hole-punch removal, despeckling, illumination correction and layout segmentation.

Discovered by generate.py and called as gen_document(Fixtures/document). Deterministic (fixed seeds),
every file is a small 8-bit grayscale PNG. See manifest.json / EXPECTED.md for the ground truth.

Text pages are built from rectangles laid out like Latin text: each line is a row of "words" made of
"letters" (narrow rectangles) sitting on a baseline; some letters have ascenders (taller, above the
x-height band) and fewer have descenders (below the baseline). Lines are left-aligned with a ragged
right margin. That reproduces the two asymmetries the orientation heuristic relies on.
"""
from __future__ import annotations

import json
import os

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

WHITE = 255
BLACK = 0


def _text_layout(rng: np.random.RandomState, width: int, height: int, *, margin_left: int, margin_top: int,
                 margin_right: int, margin_bottom: int, pitch: int, x_height: int, ascender: int, descender: int):
    """Returns (lines, words) where lines = [(x0, y0, x1, y1, [word boxes])]; boxes are inclusive-exclusive."""
    lines = []
    text_right = width - margin_right
    baseline = margin_top + ascender + x_height
    while baseline + descender <= height - margin_bottom:
        if rng.rand() < 0.08:  # paragraph break
            baseline += pitch
            continue
        line_len = int((text_right - margin_left) * rng.uniform(0.55, 1.0))
        x = margin_left
        words = []
        line_letters = []
        while True:
            letter_count = rng.randint(2, 8)
            word_letters = []
            wx = x
            for _ in range(letter_count):
                lw = rng.randint(4, 8)
                top = baseline - x_height
                bottom = baseline
                r = rng.rand()
                if r < 0.30:
                    top = baseline - x_height - ascender
                elif r < 0.40:
                    bottom = baseline + descender
                word_letters.append((wx, top, wx + lw, bottom))
                wx += lw + 2
            wx -= 2
            if wx > margin_left + line_len:
                break
            words.append((x, min(b[1] for b in word_letters), wx, max(b[3] for b in word_letters)))
            line_letters.extend(word_letters)
            x = wx + 9
        if words:
            lines.append((
                min(w[0] for w in words), min(w[1] for w in words), max(w[2] for w in words), max(w[3] for w in words),
                words, line_letters,
            ))
        baseline += pitch
    return lines


def _render_page(width: int, height: int, seed: int, **layout_kwargs):
    rng = np.random.RandomState(seed)
    lines = _text_layout(rng, width, height, **layout_kwargs)
    im = Image.new("L", (width, height), WHITE)
    draw = ImageDraw.Draw(im)
    for line in lines:
        for (x0, y0, x1, y1) in line[5]:
            draw.rectangle((x0, y0, x1 - 1, y1 - 1), fill=BLACK)
    return im, lines


def _lines_manifest(lines):
    return [
        {
            "bounds": [int(l[0]), int(l[1]), int(l[2] - l[0]), int(l[3] - l[1])],
            "words": [[int(w[0]), int(w[1]), int(w[2] - w[0]), int(w[3] - w[1])] for w in l[4]],
        }
        for l in lines
    ]


def _save(im: Image.Image, out_dir: str, name: str) -> None:
    im.save(os.path.join(out_dir, name), optimize=True)


def _perspective_coeffs(src_quad, dst_quad):
    """Coefficients for Image.transform(PERSPECTIVE): maps output points (src_quad) to input points (dst_quad)."""
    matrix = []
    for (x, y), (u, v) in zip(src_quad, dst_quad):
        matrix.append([x, y, 1, 0, 0, 0, -u * x, -u * y])
        matrix.append([0, 0, 0, x, y, 1, -v * x, -v * y])
    a = np.array(matrix, dtype=np.float64)
    b = np.array([c for pt in dst_quad for c in pt], dtype=np.float64)
    return np.linalg.solve(a, b)


def _luminance8(rgb: np.ndarray) -> np.ndarray:
    """BT.709 luminance with the library's exact float32 arithmetic and truncating round."""
    r = rgb[..., 0].astype(np.float32)
    g = rgb[..., 1].astype(np.float32)
    b = rgb[..., 2].astype(np.float32)
    l = r * np.float32(0.2126) + g * np.float32(0.7152) + b * np.float32(0.0722)
    return np.clip((l + np.float32(0.5)).astype(np.int32), 0, 255).astype(np.uint8)


def _window_stats(plane: np.ndarray, half: int):
    """Mean, population variance and area of the window of half-size `half` centred at each pixel,
    clamped to the image -- exactly what the library's integral image computes (int64 sums)."""
    a = plane.astype(np.int64)
    h, w = a.shape
    s = np.zeros((h + 1, w + 1), dtype=np.int64)
    sq = np.zeros((h + 1, w + 1), dtype=np.int64)
    s[1:, 1:] = np.cumsum(np.cumsum(a, axis=0), axis=1)
    sq[1:, 1:] = np.cumsum(np.cumsum(a * a, axis=0), axis=1)
    y1 = np.maximum(0, np.arange(h) - half)
    y2 = np.minimum(h - 1, np.arange(h) + half)
    x1 = np.maximum(0, np.arange(w) - half)
    x2 = np.minimum(w - 1, np.arange(w) + half)
    Y1, X1 = np.meshgrid(y1, x1, indexing="ij")
    Y2, X2 = np.meshgrid(y2, x2, indexing="ij")

    def rect(t):
        return t[Y2 + 1, X2 + 1] - t[Y1, X2 + 1] - t[Y2 + 1, X1] + t[Y1, X1]

    area = (Y2 - Y1 + 1) * (X2 - X1 + 1)
    mean = rect(s) / area
    variance = np.maximum(0.0, rect(sq) / area - mean * mean)
    return mean, variance, area


def _local_threshold_map(plane: np.ndarray, kind: str, window: int, k: float,
                         p: float = 2.0, q: float = 10.0) -> np.ndarray:
    """Reference threshold surface for the four local methods. `k` is passed through float32 first
    because the library takes it as a C# `float`."""
    k = float(np.float32(k))
    half = window // 2
    mean, variance, area = _window_stats(plane, half)
    std = np.sqrt(variance)
    if kind == "niblack":
        return mean + k * std
    if kind == "wolf":
        m_min = float(plane.min())
        r = float(np.sqrt(variance.max()))
        if r <= 0:
            r = 1.0
        one_minus_k = float(np.float32(1.0) - np.float32(k))
        return one_minus_k * mean + k * m_min + k * (std / r) * (mean - m_min)
    if kind == "phansalkar":
        m = mean / 255.0
        s = std / 255.0
        return m * (1 + p * np.exp(-q * m) + k * ((s / 0.5) - 1)) * 255.0
    if kind == "nick":
        inner = variance + mean * mean - (mean * mean) / area
        return mean + k * np.sqrt(np.maximum(0.0, inner))
    raise ValueError(kind)


def _percentile(histogram: np.ndarray, total: int, percentile: float) -> int:
    target = total * min(max(percentile, 0.0), 100.0) / 100.0
    cumulative = 0
    for i in range(256):
        cumulative += int(histogram[i])
        if cumulative >= target:
            return i
    return 255


def _stretch_lut(low: int, high: int) -> np.ndarray:
    if high <= low:
        return np.arange(256, dtype=np.uint8)
    scale = 255.0 / (high - low)
    values = np.round((np.arange(256, dtype=np.float64) - low) * scale)
    return np.clip(values.astype(np.int64), 0, 255).astype(np.uint8)


def _gamma_lut(gamma: float) -> np.ndarray:
    exponent = 1.0 / gamma
    values = np.round(255.0 * np.power(np.arange(256, dtype=np.float64) / 255.0, exponent))
    return np.clip(values.astype(np.int64), 0, 255).astype(np.uint8)


def _apply_lut(rgb: np.ndarray, lut: np.ndarray) -> np.ndarray:
    return lut[rgb]


def gen_document(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    manifest: dict = {"entries": {}}

    # ---- 1. text page (portrait), skewed copies and quarter-turn copies -------------------------------
    W, H = 500, 700
    layout = dict(margin_left=40, margin_top=48, margin_right=36, margin_bottom=44, pitch=22, x_height=8,
                  ascender=5, descender=4)
    page, lines = _render_page(W, H, seed=1234, **layout)
    _save(page, out_dir, "text_page.png")
    manifest["entries"]["text_page"] = {
        "file": "text_page.png", "width": W, "height": H, "skew_clockwise": 0.0, "content_rotation_cw": 0,
        "fix_rotation_cw": 0, "lines": _lines_manifest(lines),
    }

    skews = []
    for pillow_angle in (-12.0, -8.0, -4.0, -1.5, 1.0, 3.0, 6.0, 10.0):
        rotated = page.rotate(pillow_angle, resample=Image.BICUBIC, expand=True, fillcolor=WHITE)
        if pillow_angle != 3.0:  # keep one full-gray copy; posterize the rest to 4 levels to stay small
            rotated = rotated.point(lambda v: min(255, (v // 64) * 85))
        # Pillow rotates counter-clockwise for positive angles: content skew (clockwise) is the negation.
        name = f"text_page_skew_{pillow_angle:+.1f}".replace("+", "p").replace("-", "m").replace(".", "_")
        _save(rotated, out_dir, name + ".png")
        manifest["entries"][name] = {
            "file": name + ".png", "width": rotated.width, "height": rotated.height,
            "skew_clockwise": -pillow_angle, "content_rotation_cw": 0, "fix_rotation_cw": 0,
        }
        skews.append(name)
    manifest["skew_entries"] = skews

    orient = []
    for content_cw, transpose in ((90, Image.ROTATE_270), (180, Image.ROTATE_180), (270, Image.ROTATE_90)):
        rotated = page.transpose(transpose)
        name = f"text_page_rot{content_cw}"
        _save(rotated, out_dir, name + ".png")
        manifest["entries"][name] = {
            "file": name + ".png", "width": rotated.width, "height": rotated.height, "skew_clockwise": 0.0,
            "content_rotation_cw": content_cw, "fix_rotation_cw": (360 - content_cw) % 360,
        }
        orient.append(name)
    manifest["orientation_entries"] = ["text_page"] + orient

    # ---- 2. rules page: text + long horizontal / vertical rules, plus masks -------------------------
    text_mask = np.array(page) < 128
    rules = np.zeros_like(text_mask)
    for y in (200, 420, 553):  # 553 cuts through a text line
        rules[y:y + 3, 30:W - 40] = True
    for x in (22, 262):  # x=262 cuts through text
        rules[60:H - 60, x:x + 3] = True
    combined = np.where(text_mask | rules, BLACK, WHITE).astype(np.uint8)
    _save(Image.fromarray(combined), out_dir, "rules_page.png")
    _save(Image.fromarray(np.where(text_mask, BLACK, WHITE).astype(np.uint8)), out_dir, "rules_page_text_mask.png")
    _save(Image.fromarray(np.where(rules, BLACK, WHITE).astype(np.uint8)), out_dir, "rules_page_rules_mask.png")
    manifest["entries"]["rules_page"] = {
        "file": "rules_page.png", "width": W, "height": H, "text_mask": "rules_page_text_mask.png",
        "rules_mask": "rules_page_rules_mask.png", "rule_thickness": 3,
        "horizontal_rule_length": W - 70, "vertical_rule_length": H - 120,
    }

    # ---- 3. hole-punch page ------------------------------------------------------------------------
    holes = np.zeros_like(text_mask)
    yy, xx = np.mgrid[0:H, 0:W]
    hole_centres = [(21, H // 6), (21, H // 2), (21, 5 * H // 6)]
    for cx, cy in hole_centres:
        holes |= (xx - cx) ** 2 + (yy - cy) ** 2 <= 13 ** 2
    combined = np.where(text_mask | holes, BLACK, WHITE).astype(np.uint8)
    _save(Image.fromarray(combined), out_dir, "holes_page.png")
    _save(Image.fromarray(np.where(holes, BLACK, WHITE).astype(np.uint8)), out_dir, "holes_page_holes_mask.png")
    manifest["entries"]["holes_page"] = {
        "file": "holes_page.png", "width": W, "height": H, "holes_mask": "holes_page_holes_mask.png",
        "hole_radius": 13, "hole_centres": hole_centres,
    }

    # ---- 4. speckle page ---------------------------------------------------------------------------
    rng = np.random.RandomState(99)
    specks = np.zeros_like(text_mask)
    count = 0
    while count < 400:
        x = rng.randint(2, W - 3)
        y = rng.randint(2, H - 3)
        size = rng.randint(1, 3)
        # Keep specks clear of the text so removal can be measured exactly.
        if text_mask[max(0, y - 3):y + size + 3, max(0, x - 3):x + size + 3].any():
            continue
        specks[y:y + size, x:x + size] = True
        count += 1
    combined = np.where(text_mask | specks, BLACK, WHITE).astype(np.uint8)
    _save(Image.fromarray(combined), out_dir, "speckle_page.png")
    _save(Image.fromarray(np.where(specks, BLACK, WHITE).astype(np.uint8)), out_dir, "speckle_page_specks_mask.png")
    manifest["entries"]["speckle_page"] = {
        "file": "speckle_page.png", "width": W, "height": H, "specks_mask": "speckle_page_specks_mask.png",
        "speck_count": count, "max_speck_area": 4,
    }

    # ---- 5. noisy page: illumination gradient + specks + rules + holes (+ a skewed variant) ---------
    def scan_noise(clean: np.ndarray, seed: int) -> np.ndarray:
        """Multiplicative illumination gradient + low-frequency paper texture (both piecewise constant on
        4x4 blocks so the PNG stays small) + sparse impulse noise."""
        h, w = clean.shape
        r = np.random.RandomState(seed)
        bw, bh = (w + 3) // 4, (h + 3) // 4
        gx = np.linspace(1.0, 0.55, bw)[None, :]
        gy = np.linspace(1.0, 0.7, bh)[:, None]
        cloud = np.array(Image.fromarray(r.uniform(-14, 14, size=(14, 10)).astype(np.float32), mode="F")
                         .resize((bw, bh), Image.BILINEAR))
        illum = np.repeat(np.repeat(gx * gy, 4, axis=0), 4, axis=1)[:h, :w]
        cloud = np.repeat(np.repeat(cloud, 4, axis=0), 4, axis=1)[:h, :w]
        sparse = np.where(r.rand(h, w) < 0.012, r.uniform(-30, 30, size=(h, w)), 0.0)
        return np.clip(clean * illum + cloud + sparse, 0, 255).astype(np.uint8)

    clean = np.where(text_mask | rules | holes | specks, 25.0, 235.0)
    noisy = scan_noise(clean, seed=7)
    _save(Image.fromarray(noisy), out_dir, "noisy_page.png")
    manifest["entries"]["noisy_page"] = {
        "file": "noisy_page.png", "width": W, "height": H, "text_mask": "rules_page_text_mask.png",
        "illumination": "multiplicative gradient 1.0 -> 0.55 left-to-right and 1.0 -> 0.7 top-to-bottom",
        "noise": "low-frequency +-14 cloud plus +-30 impulses on 1.2% of pixels (piecewise constant on 4x4 blocks)",
    }
    clean_img = Image.fromarray(np.where(text_mask | rules | holes | specks, BLACK, WHITE).astype(np.uint8))
    skewed_clean = clean_img.rotate(-2.0, resample=Image.BICUBIC, expand=True, fillcolor=WHITE)
    skewed_clean = np.where(np.array(skewed_clean) < 128, 25.0, 235.0)
    skewed = Image.fromarray(scan_noise(skewed_clean, seed=8))
    _save(skewed, out_dir, "noisy_page_skewed.png")
    manifest["entries"]["noisy_page_skewed"] = {
        "file": "noisy_page_skewed.png", "width": skewed.width, "height": skewed.height, "skew_clockwise": 2.0,
    }

    # ---- 6. perspective: landscape page photographed on a dark background --------------------------
    PW, PH = 560, 400
    flat, _ = _render_page(PW, PH, seed=4321, margin_left=36, margin_top=40, margin_right=30, margin_bottom=36,
                           pitch=20, x_height=8, ascender=5, descender=4)
    flat = flat.filter(ImageFilter.GaussianBlur(0.7))
    _save(flat, out_dir, "perspective_page_flat.png")

    CW, CH = 800, 600
    quad = [(150.0, 70.0), (700.0, 95.0), (665.0, 540.0), (105.0, 505.0)]  # TL, TR, BR, BL in canvas coordinates
    rect = [(0.0, 0.0), (PW, 0.0), (PW, PH), (0.0, PH)]
    coeffs = _perspective_coeffs(quad, rect)
    warped = flat.transform((CW, CH), Image.PERSPECTIVE, tuple(coeffs), resample=Image.BILINEAR, fillcolor=0)
    cover = Image.new("L", (PW, PH), 255).transform((CW, CH), Image.PERSPECTIVE, tuple(coeffs),
                                                    resample=Image.BILINEAR, fillcolor=0)
    bx = np.linspace(28, 62, CW // 4)[None, :]
    by = np.linspace(0, 18, CH // 4)[:, None]
    background = np.repeat(np.repeat(np.round(bx + by), 4, axis=0), 4, axis=1).astype(np.float64)
    alpha = np.array(cover, dtype=np.float64) / 255.0
    canvas = np.array(warped, dtype=np.float64) * alpha + background * (1.0 - alpha)
    _save(Image.fromarray(np.clip(canvas, 0, 255).astype(np.uint8)), out_dir, "perspective_page.png")
    manifest["entries"]["perspective_page"] = {
        "file": "perspective_page.png", "width": CW, "height": CH, "flat": "perspective_page_flat.png",
        "page_width": PW, "page_height": PH, "quad": [[x, y] for x, y in quad],
        "quad_order": "top-left, top-right, bottom-right, bottom-left",
    }

    # ---- 7. local-threshold page + numpy reference binarisations -----------------------------------
    TW, TH = 160, 120
    tpage, _ = _render_page(TW, TH, seed=777, margin_left=12, margin_top=16, margin_right=10,
                            margin_bottom=12, pitch=14, x_height=5, ascender=3, descender=2)
    tink = np.array(tpage) < 128
    ty, tx = np.mgrid[0:TH, 0:TW]
    # Paper darkens towards the right and bottom while the ink runs from barely-there on the left to solid
    # on the right, and a little grain gives blank paper a small but non-zero local variance. That mix is
    # what makes the four formulas disagree (hundreds of pixels between every pair); on an easy
    # high-contrast page Wolf-Jolion, Phansalkar and NICK all return exactly the same mask.
    paper = 235.0 - 85.0 * (tx / (TW - 1)) - 40.0 * (ty / (TH - 1))
    strength = 0.92 - 0.47 * (tx / (TW - 1))
    grain = np.random.RandomState(2024).randint(-7, 8, size=(TH, TW))
    threshold_page = np.clip(np.round(np.where(tink, paper * strength, paper) + grain), 0, 255).astype(np.uint8)
    _save(Image.fromarray(threshold_page), out_dir, "threshold_page.png")

    expected_thresholds = {}
    for label, kind, window, k in (("niblack", "niblack", 25, -0.2), ("wolf", "wolf", 25, 0.5),
                                   ("phansalkar", "phansalkar", 25, 0.25), ("nick", "nick", 25, -0.1)):
        surface = _local_threshold_map(threshold_page, kind, window, k)
        binary = np.where(threshold_page.astype(np.float64) >= surface, 255, 0).astype(np.uint8)
        name = f"threshold_{label}.png"
        _save(Image.fromarray(binary), out_dir, name)
        expected_thresholds[label] = {"file": name, "window": window, "k": k}
    manifest["entries"]["threshold_page"] = {
        "file": "threshold_page.png", "width": TW, "height": TH, "expected": expected_thresholds,
        "rule": "white where luminance >= T, black otherwise; windows are clamped to the image and the "
                "variance is the population variance of the clamped window",
    }

    # ---- 8. tone page + numpy reference illumination / tone outputs --------------------------------
    NW, NH = 80, 60
    ny, nx = np.mgrid[0:NH, 0:NW]
    tone = np.stack([
        40.0 + 120.0 * (nx / (NW - 1)),
        55.0 + 90.0 * (ny / (NH - 1)),
        70.0 + 60.0 * ((nx + ny) / (NW + NH - 2)),
    ], axis=-1)
    tone[8:16, 6:20] = 12.0     # near-black patch so the 0.5th percentile has something to clip
    tone[40:50, 55:74] = 243.0  # near-white patch for the 99.5th percentile
    tone = np.clip(np.round(tone), 0, 255).astype(np.uint8)
    _save(Image.fromarray(tone, mode="RGB"), out_dir, "tone_page.png")

    luminance = _luminance8(tone)
    lum_histogram = np.bincount(luminance.reshape(-1), minlength=256)
    pixels = int(luminance.size)
    low = _percentile(lum_histogram, pixels, 0.5)
    high = _percentile(lum_histogram, pixels, 99.5)
    _save(Image.fromarray(_apply_lut(tone, _stretch_lut(low, high)), mode="RGB"), out_dir, "tone_contrast.png")

    channel_luts = [
        _stretch_lut(_percentile(np.bincount(tone[..., c].reshape(-1), minlength=256), pixels, 0.5),
                     _percentile(np.bincount(tone[..., c].reshape(-1), minlength=256), pixels, 99.5))
        for c in range(3)
    ]
    auto_levels = np.stack([channel_luts[c][tone[..., c]] for c in range(3)], axis=-1)
    _save(Image.fromarray(auto_levels, mode="RGB"), out_dir, "tone_autolevels.png")

    _save(Image.fromarray(_apply_lut(tone, _stretch_lut(int(luminance.min()), int(luminance.max()))), mode="RGB"),
          out_dir, "tone_normalize.png")
    _save(Image.fromarray(_apply_lut(tone, _gamma_lut(1.8)), mode="RGB"), out_dir, "tone_gamma_1_8.png")

    manifest["entries"]["tone_page"] = {
        "file": "tone_page.png", "width": NW, "height": NH,
        "contrast_stretch": {"file": "tone_contrast.png", "low_percentile": 0.5, "high_percentile": 99.5},
        "auto_levels": {"file": "tone_autolevels.png", "percentiles": [0.5, 99.5]},
        "normalize": {"file": "tone_normalize.png"},
        "gamma": {"file": "tone_gamma_1_8.png", "gamma": 1.8},
    }

    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=1, sort_keys=True)
        f.write("\n")

    with open(os.path.join(out_dir, "EXPECTED.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(EXPECTED_MD)


EXPECTED_MD = """# document/ fixtures

Generated by `gen_document.py` (deterministic). All files are 8-bit grayscale PNGs. `manifest.json`
holds the ground truth referenced below.

| file | what | ground truth |
| --- | --- | --- |
| text_page.png | 500x700 upright synthetic text page (rectangles laid out as words/letters with ascenders and descenders, ragged right margin) | `lines[].bounds`, `lines[].words` (x, y, w, h) |
| text_page_skew_*.png | text_page rotated with Pillow (bicubic, expanded, white fill) | `skew_clockwise` = clockwise skew of the content in degrees (Pillow's angle negated) |
| text_page_rot90/180/270.png | text_page rotated clockwise by a quarter turn (lossless transpose) | `content_rotation_cw`, `fix_rotation_cw` (clockwise rotation that makes it upright) |
| rules_page.png | text_page + three horizontal and two vertical 3 px rules (some cut through text) | `rules_page_text_mask.png`, `rules_page_rules_mask.png` (black = member) |
| holes_page.png | text_page + three hole-punch discs (r = 13) on the left margin | `holes_page_holes_mask.png`, `hole_centres` |
| speckle_page.png | text_page + 400 black specks of 1x1 .. 2x2 px, none touching text | `speckle_page_specks_mask.png` |
| noisy_page.png | text + rules + holes + specks under a multiplicative illumination gradient with Gaussian noise | text mask = rules_page_text_mask.png |
| noisy_page_skewed.png | noisy_page rotated 2 degrees clockwise | `skew_clockwise` = 2.0 |
| perspective_page.png | 800x600 dark canvas with the 560x400 page perspective_page_flat.png warped onto a known quadrilateral | `quad` (TL, TR, BR, BL), `flat` |
| threshold_page.png | 160x120 grey page: mid-grey ink on paper that darkens to the right and bottom (no global threshold separates them) | `expected.<method>.file` = the numpy reference binarisation for Niblack / Wolf-Jolion / Phansalkar / NICK with `window` and `k` |
| tone_page.png | 80x60 RGB gradient with a near-black and a near-white patch | `contrast_stretch`, `auto_levels`, `normalize`, `gamma` -- numpy reference outputs of the matching tone operations |
"""

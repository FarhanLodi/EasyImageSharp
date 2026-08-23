"""Reference renderings for the annotation drawing tests (Fixtures/drawing/).

Every fixture is a small RGB PNG drawn with Pillow's ImageDraw on a white 40x30 canvas, plus a
manifest.json describing the equivalent EasyImageSharp call. Only axis-aligned rectangles at integer
coordinates are used because those are exactly reproducible without anti-aliasing: Pillow's
``rectangle([x0, y0, x1, y1])`` covers pixels x0..x1 / y0..y1 inclusive and draws outlines of ``width``
pixels inward, which maps to ``FillRectangle`` / ``DrawRectangle`` on ``RectangleF(x0, y0, x1 - x0 + 1,
y1 - y0 + 1)`` with ``Antialias = false``.

Deterministic: rerunning produces byte-identical files.
"""
from __future__ import annotations

import json
import os

from PIL import Image, ImageDraw

WIDTH, HEIGHT = 40, 30
WHITE = (255, 255, 255)


def _canvas() -> tuple[Image.Image, ImageDraw.ImageDraw]:
    im = Image.new("RGB", (WIDTH, HEIGHT), WHITE)
    return im, ImageDraw.Draw(im)


def _save(im: Image.Image, out_dir: str, name: str) -> None:
    # Fixed metadata-free PNG so the bytes are stable across runs.
    im.save(os.path.join(out_dir, name), format="PNG", optimize=False, compress_level=6)


def gen_drawing(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    entries: list[dict] = []

    # 1. Filled rectangle: pixels 5..20 x 4..15.
    im, d = _canvas()
    d.rectangle([5, 4, 20, 15], fill=(255, 0, 0))
    _save(im, out_dir, "rect_fill.png")
    entries.append({
        "name": "rect_fill", "file": "rect_fill.png", "width": WIDTH, "height": HEIGHT,
        "op": "FillRectangle", "color": [255, 0, 0], "rect": [5, 4, 16, 12], "thickness": 0,
        "notes": "ImageDraw.rectangle([5,4,20,15], fill=red)",
    })

    # 2. One-pixel outline: border pixels of the same box.
    im, d = _canvas()
    d.rectangle([5, 4, 20, 15], outline=(0, 0, 0), width=1)
    _save(im, out_dir, "rect_outline_w1.png")
    entries.append({
        "name": "rect_outline_w1", "file": "rect_outline_w1.png", "width": WIDTH, "height": HEIGHT,
        "op": "DrawRectangle", "color": [0, 0, 0], "rect": [5, 4, 16, 12], "thickness": 1,
        "notes": "ImageDraw.rectangle([5,4,20,15], outline=black, width=1)",
    })

    # 3. Three-pixel outline drawn inward.
    im, d = _canvas()
    d.rectangle([5, 4, 20, 15], outline=(0, 0, 255), width=3)
    _save(im, out_dir, "rect_outline_w3.png")
    entries.append({
        "name": "rect_outline_w3", "file": "rect_outline_w3.png", "width": WIDTH, "height": HEIGHT,
        "op": "DrawRectangle", "color": [0, 0, 255], "rect": [5, 4, 16, 12], "thickness": 3,
        "notes": "ImageDraw.rectangle([5,4,20,15], outline=blue, width=3)",
    })

    # 4. Outline partly outside the canvas (clipping) with a 2 px width.
    im, d = _canvas()
    d.rectangle([-4, 20, 12, 40], outline=(0, 128, 0), width=2)
    _save(im, out_dir, "rect_outline_clipped.png")
    entries.append({
        "name": "rect_outline_clipped", "file": "rect_outline_clipped.png", "width": WIDTH, "height": HEIGHT,
        "op": "DrawRectangle", "color": [0, 128, 0], "rect": [-4, 20, 17, 21], "thickness": 2,
        "notes": "ImageDraw.rectangle([-4,20,12,40], outline=(0,128,0), width=2); box crosses the left and bottom edges",
    })

    # 5. Fill touching the right/bottom edge exactly.
    im, d = _canvas()
    d.rectangle([30, 22, 39, 29], fill=(10, 20, 30))
    _save(im, out_dir, "rect_fill_corner.png")
    entries.append({
        "name": "rect_fill_corner", "file": "rect_fill_corner.png", "width": WIDTH, "height": HEIGHT,
        "op": "FillRectangle", "color": [10, 20, 30], "rect": [30, 22, 10, 8], "thickness": 0,
        "notes": "ImageDraw.rectangle([30,22,39,29], fill=(10,20,30)); flush with the bottom-right corner",
    })

    with open(os.path.join(out_dir, "manifest.json"), "w", newline="\n") as f:
        json.dump({"canvas": [WIDTH, HEIGHT], "background": list(WHITE), "entries": entries}, f, indent=2)
        f.write("\n")


if __name__ == "__main__":
    gen_drawing(os.path.join(os.path.dirname(os.path.abspath(__file__)), "drawing"))

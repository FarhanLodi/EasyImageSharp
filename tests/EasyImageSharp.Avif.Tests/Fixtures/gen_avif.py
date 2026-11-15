#!/usr/bin/env python
"""Writes the AVIF fixtures that EasyImageSharp.Avif is checked against.

Run from this directory: ``python -I gen_avif.py``. Needs Pillow 11.2 or newer with its bundled libavif.

Every file is encoded by Pillow's own libavif build and decoded back by Pillow; that decode is stored as a raw
RGBA ``.rgba`` dump (row-major, 4 bytes per pixel, every frame in order) next to the file. The library under test
uses a separately built libavif (scripts/build-libavif.ps1), so agreement is evidence from an independent
encoder and decoder build, not a round trip through the same code. AV1 decoding is bit-exact by specification,
but chroma upsampling and the YUV to RGB rounding are not: 4:4:4 files are held to a two-level bound and
subsampled files to a PSNR floor, as JPEG is.

The pixels are synthetic (generated below) and released with the repository under its licence.
"""
import json
import os

from PIL import Image, features

HERE = os.path.dirname(os.path.abspath(__file__))


def gradient(width, height, alpha):
    im = Image.new("RGBA" if alpha else "RGB", (width, height))
    px = im.load()
    for y in range(height):
        for x in range(width):
            r = (x * 255) // max(1, width - 1)
            g = (y * 255) // max(1, height - 1)
            b = ((x + y) * 7) % 256
            px[x, y] = (r, g, b, 255 - ((x * 3 + y * 5) % 200)) if alpha else (r, g, b)
    return im


def dump(path, frames):
    with open(path, "wb") as f:
        for frame in frames:
            f.write(frame.convert("RGBA").tobytes())


def frames_of(path):
    im = Image.open(path)
    out, durations = [], []
    for i in range(getattr(im, "n_frames", 1)):
        im.seek(i)
        out.append(im.convert("RGBA").copy())
        durations.append(im.info.get("duration"))
    return im, out, durations


def main():
    if not features.check("avif"):
        raise SystemExit("This Pillow has no AVIF support.")
    oracle = f"Pillow {Image.__version__} (libavif {features.version('avif')})"
    manifest = []

    def add(name, image, notes, subsampling, **save):
        path = os.path.join(HERE, name + ".avif")
        image.save(path, format="AVIF", subsampling=subsampling, **save)
        decoded, frames, durations = frames_of(path)
        dump(os.path.join(HERE, name + ".rgba"), frames)
        manifest.append({
            "name": name, "file": name + ".avif", "reference": name + ".rgba",
            "width": decoded.width, "height": decoded.height, "frames": len(frames),
            "durations_ms": durations if len(frames) > 1 else None,
            "has_alpha": decoded.mode in ("RGBA", "LA") or "A" in decoded.getbands(),
            "subsampling": subsampling, "oracle": oracle, "notes": notes,
        })

    add("pillow_rgb8_q90_444", gradient(48, 32, False), "8-bit RGB still, quality 90, 4:4:4", "4:4:4", quality=90)
    add("pillow_rgb8_q60_420", gradient(47, 33, False), "8-bit RGB still, quality 60, 4:2:0, odd dimensions", "4:2:0", quality=60)
    add("pillow_rgba8_q90", gradient(40, 24, True), "8-bit RGBA still with a varying alpha plane, quality 90", "4:4:4", quality=90)

    frames = [Image.new("RGB", (24, 16), c) for c in ((200, 30, 30), (30, 200, 30), (30, 30, 200))]
    path = os.path.join(HERE, "pillow_anim3.avif")
    frames[0].save(path, format="AVIF", save_all=True, append_images=frames[1:], duration=[100, 250, 40], loop=3, quality=90,
                   subsampling="4:2:0")
    decoded, out, durations = frames_of(path)
    dump(os.path.join(HERE, "pillow_anim3.rgba"), out)
    manifest.append({
        "name": "pillow_anim3", "file": "pillow_anim3.avif", "reference": "pillow_anim3.rgba",
        "width": decoded.width, "height": decoded.height, "frames": len(out), "durations_ms": durations,
        "loop": decoded.info.get("loop"), "has_alpha": False, "subsampling": "4:2:0", "oracle": oracle,
        "notes": "three-frame image sequence, durations 100/250/40 ms, loop 3",
    })

    with open(os.path.join(HERE, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    for entry in manifest:
        print(entry["name"], entry["width"], entry["height"], entry["frames"], entry.get("durations_ms"), entry.get("loop"))


if __name__ == "__main__":
    main()

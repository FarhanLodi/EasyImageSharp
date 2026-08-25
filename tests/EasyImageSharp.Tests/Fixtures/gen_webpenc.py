#!/usr/bin/env python
"""Fixtures for the WebP *encoder* tests.

Unlike the decoder fixtures, which are third-party-encoded WebP files, these are *source* images: the test
loads each PNG, encodes it as WebP with the library's own encoder and checks the result. ``manifest.json``
records, per image, the size libwebp (through Pillow) produces for the very same pixels at the same effort,
which is what the test measures the library's compression against.

Two ways to run this file:

    python generate.py                       # regenerates Fixtures/webpenc/ along with every other format
    python gen_webpenc.py --verify <dir>     # decodes the encoder's own output with libwebp and compares

The verify mode is the important one: ``WebpEncoderTests`` writes every file it encodes into
``<test output>/webpenc-output/``, and pointing this script at that directory proves the bitstreams are valid
for a decoder that has never seen this codebase, not merely self-consistent.
"""
from __future__ import annotations

import io
import json
import os
import sys

import numpy as np
from PIL import Image

METHODS = (0, 4, 6)


def _hash(x: np.ndarray, y: np.ndarray, seed: int) -> np.ndarray:
    """A small deterministic integer hash, so the fixtures never depend on a random number generator."""
    v = (x.astype(np.uint64) * np.uint64(374761393)) + (y.astype(np.uint64) * np.uint64(668265263))
    v = (v + np.uint64(seed) * np.uint64(2654435761)) & np.uint64(0xFFFFFFFF)
    v = ((v ^ (v >> np.uint64(13))) * np.uint64(1274126177)) & np.uint64(0xFFFFFFFF)
    return (v ^ (v >> np.uint64(16))) & np.uint64(0xFFFFFFFF)


def _grid(w: int, h: int) -> tuple[np.ndarray, np.ndarray]:
    y, x = np.mgrid[0:h, 0:w]
    return x, y


def _rgba(r, g, b, a) -> np.ndarray:
    return np.stack([np.asarray(c).astype(np.uint8) for c in (r, g, b, a)], axis=-1)


def _sources() -> dict[str, np.ndarray]:
    images: dict[str, np.ndarray] = {}

    x, y = _grid(32, 24)
    images["flat"] = _rgba(np.full_like(x, 40), np.full_like(x, 170), np.full_like(x, 90), np.full_like(x, 255))

    x, y = _grid(64, 48)
    band = ((x // 7) + (y // 5)) % 4
    palette = np.array([[0, 0, 0, 255], [255, 255, 255, 255], [220, 30, 40, 255], [10, 90, 200, 96]], dtype=np.uint8)
    images["bars"] = palette[band]

    x, y = _grid(96, 64)
    images["gradient"] = _rgba((x * 2) % 256, (y * 3) % 256, (x + y) % 256, np.full_like(x, 255))

    x, y = _grid(80, 60)
    ramp = np.clip(128 + 100 * np.sin(x * 0.1) * np.cos(y * 0.08), 0, 255).astype(np.uint8)
    images["gray_ramp"] = _rgba(ramp, ramp, ramp, np.full_like(x, 255))

    x, y = _grid(48, 36)
    n = _hash(x, y, 1)
    images["noise"] = _rgba(n & 0xFF, (n >> np.uint64(8)) & 0xFF, (n >> np.uint64(16)) & 0xFF, np.full_like(x, 255))

    n = _hash(x, y, 2)
    images["noise_alpha"] = _rgba(
        n & 0xFF, (n >> np.uint64(8)) & 0xFF, (n >> np.uint64(16)) & 0xFF, (n >> np.uint64(24)) & 0xFF)

    x, y = _grid(96, 72)
    n = _hash(x, y, 3)
    r = 128 + 100 * np.sin(x * 0.07) * np.cos(y * 0.05) + (n & 0x7) - 3
    g = 128 + 90 * np.sin((x + y) * 0.04) + ((n >> np.uint64(3)) & 0x7) - 3
    b = 128 + 80 * np.cos(x * 0.03 + y * 0.06) + ((n >> np.uint64(6)) & 0x7) - 3
    images["photo"] = _rgba(np.clip(r, 0, 255), np.clip(g, 0, 255), np.clip(b, 0, 255), np.full_like(x, 255))

    x, y = _grid(64, 48)
    index = (x * 40 + y) % 256
    images["palette256"] = _rgba(index, 255 - index, index // 2, np.full_like(x, 255))

    x, y = _grid(37, 23)
    images["odd"] = _rgba((x * 6) % 256, (y * 11) % 256, ((x * y) % 251), (x * 7) % 256)

    x, y = _grid(40, 40)
    inside = ((x - 20) ** 2 + (y - 20) ** 2) < 150
    # A shape on a fully transparent background whose hidden pixels carry leftover colour.
    images["sprite"] = _rgba(
        np.where(inside, 250, (x * 3) % 256),
        np.where(inside, 120, (y * 5) % 256),
        np.where(inside, 30, 199),
        np.where(inside, 255, 0))

    x, y = _grid(24, 18)
    images["two_color"] = _rgba(
        np.where((x + y) % 2 == 0, 255, 0), np.zeros_like(x), np.where((x + y) % 2 == 0, 0, 255), np.full_like(x, 255))

    return images


def gen_webpenc(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    entries = []
    for name, pixels in sorted(_sources().items()):
        h, w = pixels.shape[:2]
        image = Image.fromarray(pixels, "RGBA")
        image.save(os.path.join(out_dir, f"{name}.png"), optimize=True)

        reference = {}
        for method in METHODS:
            buffer = io.BytesIO()
            image.save(buffer, format="WEBP", lossless=True, method=method, quality=100, exact=True)
            reference[str(method)] = buffer.getbuffer().nbytes

        entries.append({
            "name": name,
            "file": f"{name}.png",
            "width": int(w),
            "height": int(h),
            "hasAlpha": bool((pixels[..., 3] != 255).any()),
            "colors": int(len(np.unique(pixels.reshape(-1, 4).view(np.uint32)))),
            "libwebp": reference,
        })

    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as handle:
        json.dump({"methods": list(METHODS), "images": entries}, handle, indent=1, sort_keys=True)
        handle.write("\n")

    with open(os.path.join(out_dir, "EXPECTED.md"), "w", encoding="utf-8", newline="\n") as handle:
        handle.write(
            "# webpenc\n\n"
            "Source images for the WebP **encoder** tests (`WebpEncoderTests`). Each `<name>.png` is a lossless\n"
            "RGBA source; `manifest.json` lists its dimensions, whether it uses alpha, how many distinct colours\n"
            "it has, and the byte size libwebp produces for the same pixels with `lossless=True, exact=True` at\n"
            "encoder methods 0, 4 and 6. The test encodes each image with the library and requires the result to\n"
            "round-trip pixel-exactly and to stay within 15% (plus a small fixed slack for tiny files) of those\n"
            "libwebp sizes.\n\n"
            "Regenerate with `python generate.py`. Cross-check the library's own output against libwebp with\n"
            "`python gen_webpenc.py --verify <test output>/webpenc-output`.\n\n"
            "| image | size | alpha | colours | " + " | ".join(f"libwebp m{m}" for m in METHODS) + " |\n"
            "| --- | --- | --- | --- | " + " | ".join("---" for _ in METHODS) + " |\n")
        for entry in entries:
            sizes = " | ".join(str(entry["libwebp"][str(m)]) for m in METHODS)
            handle.write(
                f"| {entry['name']} | {entry['width']}x{entry['height']} | "
                f"{'yes' if entry['hasAlpha'] else 'no'} | {entry['colors']} | {sizes} |\n")

    print(f"webpenc: {len(entries)} source images")


def verify(directory: str) -> int:
    """Decodes every .webp the encoder wrote into `directory` with libwebp and compares it with its .rgba twin."""
    files = sorted(f for f in os.listdir(directory) if f.endswith(".webp"))
    if not files:
        print(f"no .webp files in {directory}")
        return 1

    bad = 0
    decoded_only = 0
    for name in files:
        path = os.path.join(directory, name)
        expected_path = path[: -len(".webp")] + ".rgba"
        if not os.path.exists(expected_path):
            # Near-lossless output has no exact reference; it only has to be a file libwebp can read.
            try:
                Image.open(path).convert("RGBA").load()
                decoded_only += 1
            except Exception as exc:  # noqa: BLE001
                print(f"{name}: libwebp cannot read it ({exc})")
                bad += 1
            continue

        with open(os.path.join(directory, os.path.basename(expected_path)[: -len(".rgba")] + ".dim")) as handle:
            parts = [int(v) for v in handle.read().split()]
        frames, w, h = (parts + [1])[:3] if len(parts) == 3 else (1, parts[0], parts[1])
        want = np.frombuffer(open(expected_path, "rb").read(), dtype=np.uint8).reshape(frames, h, w, 4)

        image = Image.open(path)
        count = getattr(image, "n_frames", 1)
        if count != frames:
            print(f"{name}: libwebp sees {count} frame(s), expected {frames}")
            bad += 1
            continue

        for index in range(frames):
            image.seek(index)
            got = np.array(image.convert("RGBA"))
            if got.shape != want[index].shape or not (got == want[index]).all():
                differing = int((got != want[index]).sum()) if got.shape == want[index].shape else -1
                print(f"{name}: frame {index} differs ({differing} bytes)")
                bad += 1
                break

    print(f"verified {len(files)} files ({len(files) - decoded_only} against exact pixels, "
          f"{decoded_only} decode-only), {bad} mismatch(es)")
    return 1 if bad else 0


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "--verify":
        sys.exit(verify(sys.argv[2]))
    gen_webpenc(os.path.join(os.path.dirname(os.path.abspath(__file__)), "webpenc"))

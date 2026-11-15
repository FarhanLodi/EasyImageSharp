#!/usr/bin/env python
"""Fixture generator for the Radiance HDR (RGBE) decoder.

Discovered by generate.py, which calls ``gen_hdr(<Fixtures>/hdr)``. Each fixture is assembled byte by byte
here from the Radiance picture format as Greg Ward documents it ("The RADIANCE Picture File Format" and
"Real Pixels", Graphics Gems II) - never by the library:

  <name>.hdr        the fixture
  <name>.f32        ground truth: width*height*4 little-endian float32 RGBA, row-major, top-left origin,
                    the upright image; alpha is always 1
  manifest.json     entries: name, file, width, height, notes, format facts (orientation, encoding,
                    exposure); "expect" names the exception type the decoder must throw

Pixels are generated as RGBE quads directly, so the ground truth needs no rounding: a quad (m, e) is
``m * 2**(e - 136)`` per channel, and zero when e is 0 - the conversion of the common RGBE reference code,
evaluated with numpy.ldexp, which is exact. XYZE fixtures add the documented XYZ-to-linear-sRGB matrix,
evaluated in float32 in the same order as the decoder; their test allows a few ulps.

Everything is integer arithmetic on fixed seeds, so re-running the script reproduces identical bytes.

``python gen_hdr.py --verify-opencv`` additionally decodes every fixture OpenCV can read (standard
orientation, uniform scanline encoding, RGBE) with ``cv2.imread`` and checks it against the ground truth -
an independent decoder. It is not part of generate.py because OpenCV is not a fixture requirement.
"""
from __future__ import annotations

import json
import os
import sys

import numpy as np

# Linear sRGB from CIE XYZ relative to an equal-energy white (Bradford E->D65, then IEC 61966-2-1),
# the matrix the decoder documents, as float32.
XYZ_TO_RGB = np.array([
    [3.146657654151, -1.666463754524, -0.480193899627],
    [-0.99552160578, 1.955756733808, 0.039764871972],
    [0.063594802939, -0.214563222225, 1.150968419286],
], dtype=np.float32)


# --------------------------------------------------------------------------------------------------
# Pixel generation and the reference decode
# --------------------------------------------------------------------------------------------------

def quads(w: int, h: int, seed: int, *, runs: bool, avoid_markers: bool) -> np.ndarray:
    """H x W x 4 uint8 RGBE quads with realistic structure: the largest mantissa of a pixel is 128-255, the
    exponent varies by region (spanning roughly 2^-8 to 2^8), some pixels are black (e = 0), and with
    ``runs`` the image holds long horizontal runs of identical pixels for the run-length coders."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w]
    exponent = 128 + ((x // 7 + y // 5 + seed) % 17) - 8
    peak = 128 + (x * 5 + y * 11 + seed) % 128
    m = np.stack([peak, (peak * ((x + 3) % 7)) // 7, (peak * ((y + 5) % 9)) // 9], axis=-1)
    order = rng.permutation(3)
    m = m[..., order]
    out = np.concatenate([m, exponent[..., None]], axis=-1).astype(np.uint8)
    out[(x * 3 + y * 7) % 23 == 0] = 0
    if runs:
        for row in range(h):
            start = int(rng.integers(0, max(1, w // 3)))
            length = int(rng.integers(w // 4, w))
            out[row, start:start + length] = out[row, start]
    if avoid_markers:
        # Flat scanlines must not contain a (1, 1, 1, n) pixel, which Radiance readers take as an old-style run,
        # and must not start with (2, 2, b < 128), which they take as a new-style run-length marker.
        # The second rule is applied to every pixel, so any scanline order (see the orientation fixtures) is safe.
        ones = (out[..., 0] == 1) & (out[..., 1] == 1) & (out[..., 2] == 1)
        out[ones, 0] = 3
        twos = (out[..., 0] == 2) & (out[..., 1] == 2) & (out[..., 2] < 128)
        out[twos, 0] = 5
    return out


def reference_rgbe(q: np.ndarray, xyze: bool = False) -> np.ndarray:
    """Decodes quads to float32 RGBA: m * 2^(e - 136), zero when e is 0, alpha 1."""
    m = q[..., :3].astype(np.float32)
    e = q[..., 3].astype(np.int32)
    v = np.ldexp(m, (e - 136)[..., None]).astype(np.float32)
    v[e == 0] = 0
    if xyze:
        a, b, c = v[..., 0], v[..., 1], v[..., 2]
        mt = XYZ_TO_RGB
        v = np.stack([
            (mt[0, 0] * a + mt[0, 1] * b) + mt[0, 2] * c,
            (mt[1, 0] * a + mt[1, 1] * b) + mt[1, 2] * c,
            (mt[2, 0] * a + mt[2, 1] * b) + mt[2, 2] * c,
        ], axis=-1).astype(np.float32)
    alpha = np.ones(v.shape[:2] + (1,), dtype=np.float32)
    return np.ascontiguousarray(np.concatenate([v, alpha], axis=-1), dtype=np.float32)


# --------------------------------------------------------------------------------------------------
# Scanline encoders, written from the format description
# --------------------------------------------------------------------------------------------------

def rle_plane(values: bytes, literal_limit: int = 128) -> bytes:
    """New-style RLE of one component plane: runs of two or more equal bytes as (128 + n, value) with
    n <= 127, everything else as literal stretches of at most ``literal_limit`` bytes."""
    out = bytearray()
    i, n = 0, len(values)
    literal = bytearray()

    def flush() -> None:
        nonlocal literal
        while literal:
            chunk = literal[:literal_limit]
            out.append(len(chunk))
            out.extend(chunk)
            literal = literal[len(chunk):]

    while i < n:
        run = 1
        while i + run < n and run < 127 and values[i + run] == values[i]:
            run += 1
        if run >= 2:
            flush()
            out.extend((128 + run, values[i]))
            i += run
        else:
            literal.append(values[i])
            i += 1
    flush()
    return bytes(out)


def rle_scanline(row: np.ndarray, literal_limit: int = 128) -> bytes:
    width = row.shape[0]
    out = bytearray((2, 2, width >> 8, width & 0xFF))
    for channel in range(4):
        out.extend(rle_plane(bytes(row[:, channel]), literal_limit))
    return bytes(out)


def flat_scanline(row: np.ndarray) -> bytes:
    return row.astype(np.uint8).tobytes()


def old_rle_scanline(row: np.ndarray) -> bytes:
    """Old-style RLE: a repeat of the previous pixel is the marker (1, 1, 1, count); a count above 255 is
    split into consecutive markers whose counts are shifted by 8 bits each."""
    out = bytearray()
    i, n = 0, row.shape[0]
    while i < n:
        out.extend(row[i].tobytes())
        run = 0
        while i + 1 + run < n and (row[i + 1 + run] == row[i]).all():
            run += 1
        if run >= 2:
            low, high = run & 0xFF, run >> 8
            if low:
                out.extend((1, 1, 1, low))
            if high:
                if not low:
                    # A zero low byte still has to be sent so that the next marker is shifted.
                    out.extend((1, 1, 1, 0))
                out.extend((1, 1, 1, high))
            i += 1 + run
        else:
            i += 1
    return bytes(out)


def picture(header_lines: list[str], resolution: str, scanlines: list[bytes], identifier: str = "RADIANCE") -> bytes:
    head = "#?" + identifier + "\n" + "".join(line + "\n" for line in header_lines) + "\n" + resolution + "\n"
    return head.encode("ascii") + b"".join(scanlines)


# --------------------------------------------------------------------------------------------------
# Orientation: the scanline order each resolution line implies for an upright image
# --------------------------------------------------------------------------------------------------

def stored_scanlines(upright: np.ndarray, first: str, second: str) -> list[np.ndarray]:
    """Returns the scanlines, in file order, that store ``upright`` (H x W x 4) under the resolution axes
    ``first second`` (for example "-Y", "+X"). Y decreasing is top to bottom; X increasing is left to right."""
    img = upright
    if first[1] == "Y":
        rows = [img[r] for r in range(img.shape[0])]
        if first[0] == "+":
            rows = rows[::-1]
        if second[0] == "-":
            rows = [r[::-1] for r in rows]
        return [np.ascontiguousarray(r) for r in rows]
    cols = [img[:, c] for c in range(img.shape[1])]
    if first[0] == "-":
        cols = cols[::-1]
    if second[0] == "+":
        # Increasing Y runs bottom to top.
        cols = [c[::-1] for c in cols]
    return [np.ascontiguousarray(c) for c in cols]


def resolution_line(first: str, second: str, w: int, h: int) -> str:
    size = {"Y": h, "X": w}
    return f"{first} {size[first[1]]} {second} {size[second[1]]}"


# --------------------------------------------------------------------------------------------------
# The corpus
# --------------------------------------------------------------------------------------------------

def gen_hdr(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    entries: list[dict] = []
    std = ["FORMAT=32-bit_rle_rgbe"]

    def emit(name: str, data: bytes, upright: np.ndarray | None, notes: str, *, xyze: bool = False,
             expect: str | None = None, opencv: bool = False, **facts) -> None:
        path = os.path.join(out_dir, name + ".hdr")
        with open(path, "wb") as fh:
            fh.write(data)
        entry = {"name": name, "file": name + ".hdr", "notes": notes}
        if expect is not None:
            entry["expect"] = expect
        else:
            h, w = upright.shape[:2]
            truth = reference_rgbe(upright, xyze)
            truth.astype("<f4").tofile(os.path.join(out_dir, name + ".f32"))
            entry.update({"width": w, "height": h, "f32": name + ".f32", "xyze": xyze, "opencv": opencv})
        entry.update(facts)
        entries.append(entry)

    # New-style RLE in the standard orientation, with long runs and literal stretches.
    q = quads(61, 23, 1, runs=True, avoid_markers=False)
    emit("rle_rgbe", picture(std + ["EXPOSURE=1.0"], "-Y 23 +X 61", [rle_scanline(r) for r in q]), q,
         "new-style RLE, #?RADIANCE, standard orientation", opencv=True, orientation="-Y +X", encoding="rle")

    # Runs longer than 127 have to be split; a 300-pixel scanline with long uniform stretches forces that.
    q = quads(300, 6, 2, runs=True, avoid_markers=False)
    q[:, 10:290] = q[:, 10:11]
    # One scanline with 160 pixels whose components never repeat, so literals hit the 128-byte limit.
    xs = np.arange(160)
    q[5, :160] = np.stack([xs % 256, (xs * 3 + 1) % 256, (xs * 5 + 2) % 256, 120 + xs % 16], axis=-1).astype(np.uint8)
    emit("rle_long_runs", picture(std, "-Y 6 +X 300", [rle_scanline(r) for r in q]), q,
         "new-style RLE with runs split at 127 and literal stretches of exactly 128", opencv=True,
         orientation="-Y +X", encoding="rle")

    # Literal stretches capped at a small size exercise many short literal records.
    q = quads(40, 9, 3, runs=False, avoid_markers=False)
    emit("rle_short_literals", picture(std, "-Y 9 +X 40", [rle_scanline(r, literal_limit=5) for r in q]), q,
         "new-style RLE with literal records of at most five bytes", opencv=True, orientation="-Y +X", encoding="rle")

    # Flat scanlines (no run-length encoding at all) where RLE would be allowed.
    q = quads(45, 11, 4, runs=False, avoid_markers=True)
    emit("flat_rgbe", picture(std, "-Y 11 +X 45", [flat_scanline(r) for r in q]), q,
         "flat RGBE quads, no RLE", opencv=True, orientation="-Y +X", encoding="flat")

    # Scanlines shorter than eight pixels can only be flat (or old-style).
    q = quads(5, 13, 5, runs=False, avoid_markers=True)
    emit("flat_narrow", picture(std, "-Y 13 +X 5", [flat_scanline(r) for r in q]), q,
         "5-pixel scanlines, which new-style RLE does not cover", opencv=True, orientation="-Y +X", encoding="flat")

    # Old-style run-length encoding, including a run longer than 255 (two markers, the second shifted by 8).
    q = quads(400, 5, 6, runs=False, avoid_markers=True)
    q[1, 3:350] = q[1, 3]
    q[2, 100:356] = q[2, 100]
    q[3, :] = q[3, 0]
    emit("old_rle", picture(std, "-Y 5 +X 400", [old_rle_scanline(r) for r in q]), q,
         "old-style RLE: (1, 1, 1, n) repeat markers, consecutive markers shifted by 8 bits", orientation="-Y +X",
         encoding="old-rle")

    # A run marker at the start of a scanline repeats the last pixel of the previous scanline.
    q = quads(12, 4, 7, runs=False, avoid_markers=True)
    q[2, :] = q[1, -1]
    lines = [flat_scanline(r) for r in q]
    lines[2] = bytes((1, 1, 1, 12))
    emit("old_rle_cross_scanline", picture(std, "-Y 4 +X 12", lines), q,
         "an old-style run that opens a scanline repeats the previous scanline's last pixel", orientation="-Y +X",
         encoding="old-rle")

    # Scanlines may switch between encodings within one file.
    q = quads(33, 6, 8, runs=True, avoid_markers=True)
    mixed = [rle_scanline(r) if i % 2 == 0 else flat_scanline(r) for i, r in enumerate(q)]
    emit("mixed_scanlines", picture(std, "-Y 6 +X 33", mixed), q,
         "alternating new-style RLE and flat scanlines", orientation="-Y +X", encoding="mixed")

    # XYZE.
    q = quads(29, 7, 9, runs=True, avoid_markers=False)
    emit("xyze", picture(["FORMAT=32-bit_rle_xyze"], "-Y 7 +X 29", [rle_scanline(r) for r in q]), q,
         "XYZE pixels, converted from equal-energy XYZ to linear sRGB", xyze=True, orientation="-Y +X",
         encoding="rle")

    # Header variants: the #?RGBE identifier, comments, repeated EXPOSURE, COLORCORR, PIXASPECT, PRIMARIES,
    # SOFTWARE and a VIEW line the decoder ignores.
    q = quads(16, 8, 10, runs=True, avoid_markers=False)
    header = ["# a comment line", "SOFTWARE=gen_hdr.py", "EXPOSURE=2.0", "EXPOSURE= 0.25", "COLORCORR=1 0.5 2",
              "PIXASPECT=1.5", "PRIMARIES=0.640 0.330 0.290 0.600 0.150 0.060 0.333 0.333",
              "VIEW= -vtv -vp 0 0 0", "FORMAT=32-bit_rle_rgbe"]
    emit("header_variants", picture(header, "-Y 8 +X 16", [rle_scanline(r) for r in q], identifier="RGBE"), q,
         "#?RGBE, comments, cumulative EXPOSURE (0.5), COLORCORR, PIXASPECT, PRIMARIES, SOFTWARE", opencv=True,
         orientation="-Y +X", encoding="rle", exposure=0.5, colorcorr=[1, 0.5, 2], pixaspect=1.5,
         software="gen_hdr.py", program="RGBE")

    # A header without a FORMAT line defaults to RGBE.
    q = quads(9, 3, 11, runs=False, avoid_markers=True)
    emit("no_format_line", picture([], "-Y 3 +X 9", [flat_scanline(r) for r in q]), q,
         "no FORMAT line: RGBE is assumed", orientation="-Y +X", encoding="flat")

    # All eight orientations of one upright image: half run-length encoded, half flat.
    upright = quads(19, 11, 12, runs=False, avoid_markers=True)
    for index, (first, second) in enumerate([("-Y", "+X"), ("-Y", "-X"), ("+Y", "-X"), ("+Y", "+X"),
                                             ("+X", "+Y"), ("-X", "+Y"), ("-X", "-Y"), ("+X", "-Y")]):
        lines = stored_scanlines(upright, first, second)
        use_rle = index % 2 == 0
        encoded = [rle_scanline(s) if use_rle else flat_scanline(s) for s in lines]
        tag = (first + second).replace("+", "p").replace("-", "m")
        emit(f"orient_{tag}", picture(std, resolution_line(first, second, 19, 11), encoded), upright,
             f"orientation {first} {second}, {'RLE' if use_rle else 'flat'} scanlines",
             opencv=(first, second) == ("-Y", "+X"), orientation=f"{first} {second}",
             encoding="rle" if use_rle else "flat")

    # Malformed and unsupported inputs.
    good = rle_scanline(quads(20, 1, 13, runs=True, avoid_markers=False)[0])
    emit("bad_truncated", picture(std, "-Y 3 +X 20", [good, good[:-7]]), None, "pixel data ends early",
         expect="InvalidImageContentException")
    wrong_length = bytearray(good)
    wrong_length[3] = 19
    emit("bad_rle_length", picture(std, "-Y 1 +X 20", [bytes(wrong_length)]), None,
         "run-length marker declares 19 pixels in a 20-pixel scanline", expect="InvalidImageContentException")
    overrun = bytes((2, 2, 0, 20, 128 + 25, 7)) + good[6:]
    emit("bad_rle_overrun", picture(std, "-Y 1 +X 20", [overrun]), None, "a run longer than the scanline",
         expect="InvalidImageContentException")
    emit("bad_no_resolution", b"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n", None, "header ends before the resolution line",
         expect="InvalidImageContentException")
    emit("bad_zero_width", picture(std, "-Y 4 +X 0", []), None, "zero width", expect="InvalidImageContentException")
    emit("bad_resolution_axes", picture(std, "-Y 4 +Y 4", [good] * 4), None, "two Y axes",
         expect="InvalidImageContentException")
    emit("bad_old_rle_first_pixel", picture(std, "-Y 1 +X 4", [bytes((1, 1, 1, 4))]), None,
         "an old-style run with no pixel before it", expect="InvalidImageContentException")
    emit("unsupported_format", picture(["FORMAT=ascii"], "-Y 1 +X 1", [b"\x80\x80\x80\x80"]), None,
         "a FORMAT the decoder does not implement", expect="NotSupportedException")

    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as fh:
        json.dump(entries, fh, indent=1)
        fh.write("\n")


def verify_opencv(out_dir: str) -> int:
    """Decodes each fixture marked ``opencv`` with OpenCV and compares it bit-exactly with the ground truth."""
    import cv2  # noqa: PLC0415 - optional verification dependency

    with open(os.path.join(out_dir, "manifest.json"), encoding="utf-8") as fh:
        manifest = json.load(fh)
    failures = 0
    checked = 0
    for entry in manifest:
        if not entry.get("opencv"):
            continue
        image = cv2.imread(os.path.join(out_dir, entry["file"]), cv2.IMREAD_UNCHANGED)
        truth = np.fromfile(os.path.join(out_dir, entry["f32"]), dtype="<f4").reshape(entry["height"], entry["width"], 4)
        rgb = image[..., ::-1]
        same = image.shape[:2] == truth.shape[:2] and np.array_equal(rgb.view(np.uint32), truth[..., :3].view(np.uint32))
        checked += 1
        if not same:
            failures += 1
        print(("OK  " if same else "BAD ") + entry["name"])
    print(f"OpenCV {cv2.__version__}: {checked} fixtures checked, {failures} mismatch(es)")
    return 1 if failures else 0


if __name__ == "__main__":
    here = os.path.join(os.path.dirname(os.path.abspath(__file__)), "hdr")
    if "--verify-opencv" in sys.argv:
        sys.exit(verify_opencv(here))
    gen_hdr(here)

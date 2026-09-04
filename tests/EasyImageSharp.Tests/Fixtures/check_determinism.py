#!/usr/bin/env python
"""Checks that the fixture corpus on disk still matches what generate.py produces.

Run it from this directory, with no arguments:

    python check_determinism.py

It copies generate.py and every gen_*.py into a scratch directory, regenerates the whole corpus there
from nothing, and compares the result against the corpus in this directory. Exit status is 0 when the
two agree, 1 when they differ, and 2 when the check itself could not run (Pillow missing, a generator
crashed, a bad command line).

WHY THIS DOES NOT COMPARE FILE BYTES
------------------------------------
An earlier version of this check compared files byte for byte and had to be deleted, because it was
measuring the compressor rather than the corpus. Pillow's wheels deflate through zlib-ng, whose output
depends on the zlib-ng version *and* on the SIMD path it selects at run time from the host CPU. The same
generator, fed the same pixels, therefore emits different bytes on a GitHub runner than on a
contributor's laptop; the old job flagged roughly 330 files that carried byte-for-byte identical pixels.
A `git status` that comes back dirty after `python generate.py` is that same effect and is *not* a
determinism signal.

So each file is compared by the strongest rule that is stable for its kind:

  decoded pixels  images Pillow can open: mode, size, palette, frame count, a curated set of frame
                  info keys (delay, dispose, blend, frame rect, loop, transparency, ...), and every
                  frame's pixel array compared exactly. Every frame, not just the first - half the
                  animated fixtures differ from their neighbours only after frame 0.
  exact bytes     .rgba ground-truth dumps, .bin/.xml blobs, and any image both sides refuse to decode
                  (the deliberately-broken fixtures). All of these are uncompressed or hand-assembled
                  byte by byte in Python, so they are byte-stable by construction.
  structural      manifest.json, compared as JSON with the keys in VOLATILE_MANIFEST_KEYS deleted at
                  every depth, because those hash or measure the *encoded* bytes.
  masked text     EXPECTED.md and friends, with hashes and byte counts masked for the same reason.

Because the comparison is pixel-based, the exact Pillow version matters far less than it did. The pins
in CI exist to reproduce the generators' *behaviour*, not their compression output. If this check fails
on a file nobody touched, suspect the toolchain (a Pillow major version that changed a decode) before
suspecting the corpus.

Requires Python 3.11+, Pillow 11 and NumPy 2 - the same versions generate.py needs:

    python -m pip install "pillow==11.3.0" "numpy>=2,<3"
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import warnings
from typing import Any

HERE = os.path.dirname(os.path.abspath(__file__))

# Files in this tree that no generator writes. Everything else under the fixture root is expected to be
# reproduced exactly by generate.py, and a file here that is not regenerated is a stale fixture.
HAND_WRITTEN = frozenset({
    "README.md",        # this folder's documentation
    ".gitattributes",   # end-of-line policy for the corpus
    "gif/EXPECTED.md",  # GIF ground truth is hand-written prose, transcribed into GifTests.cs
})

# Directories skipped wherever they appear.
EXCLUDED_DIRS = frozenset({"__pycache__", ".git"})

# Manifest keys that describe the ENCODED bytes rather than the content: a hash of the compressed
# stream, its length, or the encoder that produced it. zlib-ng and libwebp make exactly these
# non-reproducible across machines. Extending this set weakens the check, so add a key only after
# convincing yourself it cannot carry content - `size` here means "bytes on disk", and a generator that
# starts recording an image dimension under that name would slip past unnoticed.
VOLATILE_MANIFEST_KEYS = frozenset({"sha256", "size", "libwebp"})

# Extensions worth handing to Pillow. Anything else goes straight to a byte comparison.
DECODABLE_EXTS = frozenset({
    ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico", ".cur",
    ".tga", ".qoi", ".pbm", ".pgm", ".ppm", ".pam",
})

# Per-frame Pillow info keys that carry decoded content the pixel arrays cannot express. Timing and
# compositing live here: without them an APNG whose frame delays all changed would still compare equal.
# Keys outside this list (exif, icc_profile, jfif_*, sizes, ...) are deliberately not compared here -
# they are metadata, and the metadata suites in C# assert them against their own ground truth.
FRAME_INFO_KEYS = (
    "duration",       # APNG/GIF/WebP frame delay, milliseconds
    "disposal",       # APNG dispose_op / GIF disposal method
    "blend",          # APNG blend_op
    "bbox",           # APNG fcTL frame rectangle
    "loop",           # animation repeat count
    "default_image",  # APNG: the IDAT image sits outside the animation
    "transparency",   # palette index or colour key that no pixel array carries
    "interlace",      # PNG Adam7 / GIF interlacing
    "gamma",          # PNG gAMA
    "dpi",            # pHYs / TIFF resolution
)

# Applied to both sides of a text comparison before it runs, for the same reason the manifest keys are
# stripped. Neither pattern matches anything in the corpus today; they are a guard against a generator
# that starts writing a digest into its prose.
MASK_PATTERNS = (
    (re.compile(r"\b[0-9a-fA-F]{16,64}\b"), "<hash>"),
    (re.compile(r"\b\d+ bytes\b"), "<n> bytes"),
)

RULE_PIXELS = "decoded pixels"
RULE_BYTES = "exact bytes"
RULE_STRUCTURAL = "structural json"
RULE_TEXT = "masked text"


class SetupError(Exception):
    """The check could not be run at all. Reported as exit status 2, never as a corpus difference."""


# ---------------------------------------------------------------------------------------------------
# File-set collection
# ---------------------------------------------------------------------------------------------------

def is_excluded(relative_path: str) -> bool:
    """True for paths that are not part of the generated corpus."""
    if relative_path in HAND_WRITTEN or relative_path.endswith(".py"):
        return True
    return any(part in EXCLUDED_DIRS for part in relative_path.split("/"))


def walk_corpus(root: str) -> dict[str, str]:
    """Maps every corpus-relative path under ``root`` to its absolute path."""
    found: dict[str, str] = {}
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(d for d in dirnames if d not in EXCLUDED_DIRS)
        for name in sorted(filenames):
            absolute = os.path.join(dirpath, name)
            relative = os.path.relpath(absolute, root).replace(os.sep, "/")
            if not is_excluded(relative):
                found[relative] = absolute
    return found


def drop_git_ignored(root: str, paths: dict[str, str]) -> list[str]:
    """Removes git-ignored paths from ``paths`` in place, returning the ones dropped.

    A contributor's editor backup or a half-finished experiment that .gitignore already covers must not
    be reported as a stale fixture. Outside a git checkout this is a no-op.
    """
    if not paths:
        return []
    try:
        completed = subprocess.run(
            ["git", "-C", root, "check-ignore", "--stdin"],
            input="\n".join(sorted(paths)), capture_output=True, text=True, encoding="utf-8",
        )
    except OSError:
        return []
    if completed.returncode not in (0, 1):  # 128 means "not a git repository"
        return []
    dropped = []
    for line in completed.stdout.splitlines():
        relative = line.strip().replace(os.sep, "/")
        if relative in paths:
            del paths[relative]
            dropped.append(relative)
    return dropped


def list_tracked(root: str) -> list[str]:
    """Corpus-relative paths that git has under version control."""
    try:
        completed = subprocess.run(
            ["git", "-C", root, "ls-files", "-z", "--", "."], capture_output=True,
        )
    except OSError as exc:
        raise SetupError(f"--source git needs the git executable on PATH: {exc}") from exc
    if completed.returncode != 0:
        raise SetupError("--source git needs a git checkout: " + completed.stderr.decode("utf-8", "replace").strip())
    names = completed.stdout.decode("utf-8", "replace").split("\0")
    return [n.replace(os.sep, "/") for n in names if n and not is_excluded(n.replace(os.sep, "/"))]


# ---------------------------------------------------------------------------------------------------
# Regeneration
# ---------------------------------------------------------------------------------------------------

def regenerate(fixtures_dir: str, scratch: str) -> str:
    """Copies the generators into ``scratch`` and runs them there, returning generate.py's output.

    Only the scripts are copied, never the corpus: the generators have to build every file from nothing.
    generate.py needs no change to cooperate, because its ``main()`` derives ``HERE`` from ``__file__``
    and writes each format into ``HERE/<format>``, so moving the scripts moves the whole output tree.
    """
    scripts = [
        name for name in sorted(os.listdir(fixtures_dir))
        if name == "generate.py" or (name.startswith("gen_") and name.endswith(".py"))
    ]
    if "generate.py" not in scripts:
        raise SetupError(f"no generate.py in {fixtures_dir}")
    for name in scripts:
        shutil.copy2(os.path.join(fixtures_dir, name), os.path.join(scratch, name))

    environment = dict(os.environ, PYTHONDONTWRITEBYTECODE="1")
    completed = subprocess.run(
        [sys.executable, "generate.py"], cwd=scratch, capture_output=True,
        text=True, encoding="utf-8", errors="replace", env=environment,
    )
    if completed.returncode != 0:
        tail = (completed.stderr or completed.stdout or "").strip().splitlines()[-20:]
        raise SetupError("generate.py failed in the scratch directory:\n  " + "\n  ".join(tail))
    return completed.stdout


# ---------------------------------------------------------------------------------------------------
# Comparison rules
# ---------------------------------------------------------------------------------------------------

def read_bytes(path: str) -> bytes:
    with open(path, "rb") as handle:
        return handle.read()


def read_frames(path: str) -> tuple[list[dict[str, Any]] | None, str | None]:
    """Decodes every frame of ``path``, or reports why Pillow refused.

    Returns ``(frames, None)`` on success and ``(None, "TypeName: message")`` on failure. Failure is a
    legitimate outcome: the corpus deliberately contains truncated and malformed files, and formats
    Pillow cannot read at all.
    """
    from PIL import Image, ImageSequence
    import numpy as np

    try:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            with Image.open(path) as image:
                frames: list[dict[str, Any]] = []
                for frame in ImageSequence.Iterator(image):
                    palette = frame.getpalette()
                    frames.append({
                        "mode": frame.mode,
                        "size": tuple(frame.size),
                        "pixels": np.array(np.asarray(frame), copy=True),
                        "palette": None if palette is None else tuple(palette),
                        "info": {key: frame.info[key] for key in FRAME_INFO_KEYS if key in frame.info},
                    })
                return frames, None
    except Exception as exc:  # noqa: BLE001 - any Pillow failure is "undecodable", by design
        return None, f"{type(exc).__name__}: {exc}"


def values_equal(left: Any, right: Any) -> bool:
    """Equality that survives the odd Pillow info value (IFDRational, bytes, numpy scalar)."""
    try:
        return bool(left == right)
    except Exception:  # noqa: BLE001
        return repr(left) == repr(right)


def describe_pixel_difference(index: int, left: Any, right: Any) -> str:
    """Names the first differing pixel of a frame, with both values."""
    import numpy as np

    if left.shape != right.shape:
        return f"frame {index}: pixel array shape {left.shape} != {right.shape}"
    if left.dtype != right.dtype:
        return f"frame {index}: pixel array dtype {left.dtype} != {right.dtype}"
    mismatches = np.argwhere(left != right)
    total = len(mismatches)
    if total == 0:
        return f"frame {index}: pixel arrays differ"
    position = tuple(int(v) for v in mismatches[0])
    if left.ndim >= 2:
        y, x = position[0], position[1]
        here, there = left[y, x], right[y, x]
        where = f"pixel (x={x}, y={y})"
    else:
        y = position[0]
        here, there = left[y], right[y]
        where = f"index {y}"
    return (f"frame {index}: {where} is {np.asarray(here).tolist()} in the corpus and "
            f"{np.asarray(there).tolist()} regenerated ({total} pixel component(s) differ)")


def compare_images(corpus: str, regenerated: str) -> tuple[str, str | None, str | None]:
    """Compares two images by decoded content. Returns (rule, difference, note)."""
    left_frames, left_error = read_frames(corpus)
    right_frames, right_error = read_frames(regenerated)

    if left_error is not None and right_error is not None:
        # Both sides are undecodable, which is the whole point of the bad_* fixtures. They are
        # hand-assembled byte by byte and never pass through a compressor, so bytes are the right rule.
        note = f"undecodable on both sides ({left_error})"
        if read_bytes(corpus) != read_bytes(regenerated):
            return RULE_BYTES, "file contents differ (neither side decodes, so bytes were compared)", note
        return RULE_BYTES, None, note
    if left_error is not None:
        return RULE_PIXELS, f"the corpus copy no longer decodes ({left_error}) but the regenerated one does", None
    if right_error is not None:
        return RULE_PIXELS, f"the regenerated copy does not decode ({right_error}) but the corpus one does", None

    import numpy as np

    assert left_frames is not None and right_frames is not None
    if len(left_frames) != len(right_frames):
        return RULE_PIXELS, f"frame count {len(left_frames)} != {len(right_frames)}", None

    for index, (left, right) in enumerate(zip(left_frames, right_frames)):
        if left["mode"] != right["mode"]:
            return RULE_PIXELS, f"frame {index}: mode {left['mode']} != {right['mode']}", None
        if left["size"] != right["size"]:
            return RULE_PIXELS, f"frame {index}: size {left['size']} != {right['size']}", None
        if left["palette"] != right["palette"]:
            detail = (f"frame {index}: palette differs ({len(left['palette'] or ())} entries in the "
                      f"corpus, {len(right['palette'] or ())} regenerated)")
            return RULE_PIXELS, detail, None
        for key in FRAME_INFO_KEYS:
            here, there = left["info"].get(key), right["info"].get(key)
            if not values_equal(here, there):
                return RULE_PIXELS, f"frame {index}: {key} is {here!r} in the corpus and {there!r} regenerated", None
        if not np.array_equal(left["pixels"], right["pixels"]):
            return RULE_PIXELS, describe_pixel_difference(index, left["pixels"], right["pixels"]), None

    frames = len(left_frames)
    return RULE_PIXELS, None, f"{frames} frame(s)" if frames > 1 else None


def strip_volatile(node: Any) -> Any:
    """Returns ``node`` with every VOLATILE_MANIFEST_KEYS entry removed, at any depth."""
    if isinstance(node, dict):
        return {k: strip_volatile(v) for k, v in node.items() if k not in VOLATILE_MANIFEST_KEYS}
    if isinstance(node, list):
        return [strip_volatile(v) for v in node]
    return node


def first_json_difference(left: Any, right: Any, path: str = "$") -> str | None:
    """Depth-first search for the first place two JSON documents disagree."""
    if isinstance(left, bool) != isinstance(right, bool) or type(left) is not type(right):
        if not (isinstance(left, (int, float)) and isinstance(right, (int, float))):
            return f"{path}: {type(left).__name__} != {type(right).__name__}"
    if isinstance(left, dict):
        for key in sorted(set(left) | set(right)):
            if key not in left:
                return f"{path}.{key}: only in the regenerated manifest"
            if key not in right:
                return f"{path}.{key}: only in the corpus manifest"
            found = first_json_difference(left[key], right[key], f"{path}.{key}")
            if found is not None:
                return found
        return None
    if isinstance(left, list):
        if len(left) != len(right):
            return f"{path}: {len(left)} entries in the corpus, {len(right)} regenerated"
        for index, (a, b) in enumerate(zip(left, right)):
            found = first_json_difference(a, b, f"{path}[{index}]")
            if found is not None:
                return found
        return None
    return None if left == right else f"{path}: {left!r} != {right!r}"


def compare_manifests(corpus: str, regenerated: str) -> tuple[str, str | None, str | None]:
    try:
        with open(corpus, encoding="utf-8") as handle:
            left = json.load(handle)
        with open(regenerated, encoding="utf-8") as handle:
            right = json.load(handle)
    except (OSError, ValueError) as exc:
        return RULE_STRUCTURAL, f"could not be parsed as JSON: {exc}", None
    difference = first_json_difference(strip_volatile(left), strip_volatile(right))
    note = "volatile keys stripped: " + ", ".join(sorted(VOLATILE_MANIFEST_KEYS))
    return RULE_STRUCTURAL, difference, note


def mask_text(text: str) -> tuple[str, int]:
    masked, hits = text, 0
    for pattern, placeholder in MASK_PATTERNS:
        masked, count = pattern.subn(placeholder, masked)
        hits += count
    return masked, hits


def compare_text(corpus: str, regenerated: str) -> tuple[str, str | None, str | None]:
    left, left_hits = mask_text(read_bytes(corpus).decode("utf-8", "replace"))
    right, right_hits = mask_text(read_bytes(regenerated).decode("utf-8", "replace"))
    note = f"{left_hits + right_hits} token(s) masked" if left_hits or right_hits else None
    if left == right:
        return RULE_TEXT, None, note
    left_lines, right_lines = left.splitlines(), right.splitlines()
    for number, (a, b) in enumerate(zip(left_lines, right_lines), start=1):
        if a != b:
            return RULE_TEXT, f"line {number}: {a!r} != {b!r}", note
    return RULE_TEXT, f"{len(left_lines)} lines in the corpus, {len(right_lines)} regenerated", note


def compare_file(relative: str, corpus: str, regenerated: str) -> tuple[str, str | None, str | None]:
    """Applies the rule that suits ``relative``'s kind. Returns (rule, difference, note)."""
    name = os.path.basename(relative).lower()
    extension = os.path.splitext(name)[1]
    if name == "manifest.json":
        return compare_manifests(corpus, regenerated)
    if extension in (".md", ".txt"):
        return compare_text(corpus, regenerated)
    if extension in DECODABLE_EXTS:
        return compare_images(corpus, regenerated)
    # .rgba ground-truth dumps, .bin blobs, .xml packets: uncompressed, so byte-stable by construction.
    if read_bytes(corpus) != read_bytes(regenerated):
        return RULE_BYTES, "file contents differ", None
    return RULE_BYTES, None, None


# ---------------------------------------------------------------------------------------------------
# Driver
# ---------------------------------------------------------------------------------------------------

def parse_arguments(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Verifies that the fixture corpus matches a fresh run of generate.py, by decoded "
                    "pixels rather than by file bytes.",
    )
    parser.add_argument("--scratch", metavar="DIR",
                        help="regenerate into DIR and keep it afterwards (must be empty or absent); "
                             "the default is a temporary directory that is deleted on exit")
    parser.add_argument("--source", choices=("worktree", "git"), default="worktree",
                        help="which corpus to check: every file on disk (default), or only the files "
                             "git tracks")
    parser.add_argument("--json", metavar="REPORT", dest="report",
                        help="write the full result to REPORT as JSON, for CI artifact upload")
    parser.add_argument("--verbose", action="store_true",
                        help="print the rule applied to every file, and generate.py's own output")
    return parser.parse_args(argv)


def prepare_scratch(requested: str | None) -> tuple[str, bool]:
    """Returns (directory, remove_when_done)."""
    if requested is None:
        return tempfile.mkdtemp(prefix="eis-fixtures-"), True
    directory = os.path.abspath(requested)
    if os.path.isdir(directory) and os.listdir(directory):
        raise SetupError(f"--scratch {directory} is not empty; pass an empty or non-existent directory")
    os.makedirs(directory, exist_ok=True)
    return directory, False


def build_corpus_index(source: str) -> tuple[dict[str, str], list[str], list[str]]:
    """Returns (relative -> absolute, git-ignored paths dropped, tracked paths missing from disk)."""
    if source == "git":
        index: dict[str, str] = {}
        absent: list[str] = []
        for relative in list_tracked(HERE):
            absolute = os.path.join(HERE, relative.replace("/", os.sep))
            if os.path.isfile(absolute):
                index[relative] = absolute
            else:
                absent.append(relative)
        return index, [], absent
    index = walk_corpus(HERE)
    return index, drop_git_ignored(HERE, index), []


def main(argv: list[str] | None = None) -> int:
    arguments = parse_arguments(argv)
    started = time.perf_counter()

    try:
        import numpy
        from PIL import Image as _Image  # noqa: F401 - imported for the version banner and to fail early
        import PIL
    except ImportError as exc:
        print(f"setup error: {exc}\n  python -m pip install \"pillow==11.3.0\" \"numpy>=2,<3\"", file=sys.stderr)
        return 2

    scratch = None
    remove_scratch = False
    try:
        scratch, remove_scratch = prepare_scratch(arguments.scratch)
        regeneration_started = time.perf_counter()
        generator_output = regenerate(HERE, scratch)
        regeneration_seconds = time.perf_counter() - regeneration_started
        if arguments.verbose:
            print(generator_output.rstrip())
            print(f"regenerated into {scratch} in {regeneration_seconds:.1f}s\n")

        corpus, ignored, absent = build_corpus_index(arguments.source)
        produced = walk_corpus(scratch)

        differences: list[dict[str, str]] = []
        for relative in absent:
            differences.append({"path": relative, "rule": "file set",
                                "detail": "tracked by git but not present in the working tree"})
        for relative in sorted(set(produced) - set(corpus)):
            differences.append({"path": relative, "rule": "file set",
                                "detail": "MISSING: generate.py writes it, the corpus does not have it"})
        for relative in sorted(set(corpus) - set(produced)):
            differences.append({"path": relative, "rule": "file set",
                                "detail": "EXTRA: in the corpus, but no generator writes it"})

        counts: dict[str, int] = {RULE_PIXELS: 0, RULE_BYTES: 0, RULE_STRUCTURAL: 0, RULE_TEXT: 0}
        comparison_started = time.perf_counter()
        for relative in sorted(set(corpus) & set(produced)):
            rule, difference, note = compare_file(relative, corpus[relative], produced[relative])
            counts[rule] += 1
            if arguments.verbose:
                suffix = f"  [{note}]" if note else ""
                print(f"  {'FAIL' if difference else 'ok  '} {relative:<62} {rule}{suffix}")
            if difference is not None:
                entry = {"path": relative, "rule": rule, "detail": difference}
                if note:
                    entry["note"] = note
                differences.append(entry)
        comparison_seconds = time.perf_counter() - comparison_started

        checked = len(set(corpus) & set(produced))
        print(f"checked {checked} files: {counts[RULE_PIXELS]} decoded-pixel, {counts[RULE_BYTES]} "
              f"exact-byte, {counts[RULE_STRUCTURAL]} structural, {counts[RULE_TEXT]} masked-text; "
              f"{len(differences)} difference(s)")
        print(f"regenerated in {regeneration_seconds:.1f}s, compared in {comparison_seconds:.1f}s, "
              f"{time.perf_counter() - started:.1f}s total")
        if ignored and arguments.verbose:
            print(f"skipped {len(ignored)} git-ignored path(s)")

        for entry in differences:
            print(f"  {entry['path']}  ({entry['rule']})\n      {entry['detail']}")
        if differences:
            print("\nA difference here means the corpus and the generators disagree. Re-run "
                  "`python generate.py`, or fix the generator; a dirty `git status` on its own is "
                  "recompression noise, not a determinism failure.")

        if arguments.report:
            report = {
                "fixtures_dir": HERE,
                "scratch": scratch,
                "source": arguments.source,
                "python": sys.version.split()[0],
                "pillow": PIL.__version__,
                "numpy": numpy.__version__,
                "checked": checked,
                "counts": counts,
                "git_ignored": ignored,
                "seconds": {"regenerate": round(regeneration_seconds, 3),
                            "compare": round(comparison_seconds, 3),
                            "total": round(time.perf_counter() - started, 3)},
                "differences": differences,
            }
            with open(arguments.report, "w", encoding="utf-8", newline="\n") as handle:
                json.dump(report, handle, indent=1)
                handle.write("\n")

        return 1 if differences else 0
    except SetupError as exc:
        print(f"setup error: {exc}", file=sys.stderr)
        return 2
    finally:
        if scratch is not None and remove_scratch:
            shutil.rmtree(scratch, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())

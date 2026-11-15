#!/usr/bin/env python3
"""Builds per-target libFuzzer seed corpora and merged dictionaries from the test fixtures.

    python fuzz/scripts/build_corpus.py                 # writes fuzz/out/corpus/<target>/ and fuzz/out/dict/<target>.dict
    python fuzz/scripts/build_corpus.py --zip DIR       # also writes DIR/<prefix><target>_seed_corpus.zip (OSS-Fuzz layout)

Standard library only, so it runs unchanged on a developer machine, in the GitHub workflow and inside the OSS-Fuzz
builder image. Every seed is a file a test fixture generator (or a conformance suite) already wrote; nothing here
generates image data.

How files are assigned to targets:
  * by signature: PNG, JPEG, WebP (RIFF....WEBP), GIF, BMP, TIFF/BigTIFF, QOI, ICO/CUR, Netpbm P1-P7;
  * by extension for TGA, which has no signature;
  * every classified file also seeds the auto-detecting targets (identify, roundtrip), and the formats
    Image.IdentifyHeader understands seed identify-header.

Ground-truth dumps (*.rgba) and the Pillow renderings kept for eyeballing (*.expected.png, *.decoded.png) are skipped:
they are outputs, not decoder inputs, and the PNG renderings would flood the png corpus with near-identical files.
Identical files are stored once per target (by SHA-256), and files above --max-bytes are skipped because
libfuzzer-dotnet hands the harness at most 1 MiB per input.

TARGETS below must list the same targets and dictionaries as fuzz/EasyImageSharp.Fuzz/FuzzTargets.cs.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
FUZZ_DIR = os.path.dirname(HERE)
REPO = os.path.dirname(FUZZ_DIR)
DEFAULT_FIXTURES = os.path.join(REPO, "tests", "EasyImageSharp.Tests", "Fixtures")
DICT_DIR = os.path.join(FUZZ_DIR, "dictionaries")

ALL_FORMATS = ["png", "jpeg", "webp", "gif", "bmp", "tiff", "tga", "pnm", "qoi", "ico"]

# target -> (formats whose files seed it, dictionaries merged for it)
TARGETS: dict[str, tuple[list[str], list[str]]] = {
    "png": (["png"], ["png"]),
    "jpeg": (["jpeg"], ["jpeg"]),
    "webp": (["webp"], ["webp"]),
    "gif": (["gif"], ["gif"]),
    "bmp": (["bmp"], ["bmp"]),
    "tiff": (["tiff"], ["tiff"]),
    "tga": (["tga"], ["tga"]),
    "pnm": (["pnm"], ["pnm"]),
    "qoi": (["qoi"], ["qoi"]),
    "ico": (["ico"], ["ico", "png", "bmp"]),
    "identify": (ALL_FORMATS, ALL_FORMATS),
    "identify-header": (["png", "jpeg", "gif", "bmp", "qoi", "webp"], ALL_FORMATS),
    "roundtrip": (ALL_FORMATS, ALL_FORMATS),
}

SKIPPED_SUFFIXES = (".rgba", ".expected.png", ".decoded.png", ".json", ".md", ".py", ".txt", ".sha256", ".md5")
PNM_SIGNATURE = re.compile(rb"^P[1-7][ \t\r\n#]")
LIBFUZZER_DOTNET_MAX_INPUT = 1 << 20


def classify(path: str, head: bytes) -> str | None:
    """Returns the format a fixture is written in, or None for files that are not decoder inputs."""
    if head.startswith(b"\x89PNG\r\n\x1a\n"):
        return "png"
    if head.startswith(b"\xff\xd8\xff"):
        return "jpeg"
    if head[:4] == b"RIFF" and head[8:12] == b"WEBP":
        return "webp"
    if head[:6] in (b"GIF87a", b"GIF89a"):
        return "gif"
    if head[:4] in (b"II*\x00", b"MM\x00*", b"II+\x00", b"MM\x00+"):
        return "tiff"
    if head[:4] == b"qoif":
        return "qoi"
    extension = os.path.splitext(path)[1].lower()
    if head[:4] in (b"\x00\x00\x01\x00", b"\x00\x00\x02\x00") and extension in (".ico", ".cur"):
        return "ico"
    if head[:2] == b"BM":
        return "bmp"
    if PNM_SIGNATURE.match(head) and extension in (".pbm", ".pgm", ".ppm", ".pam", ".pnm"):
        return "pnm"
    if extension == ".tga":
        return "tga"
    # Deliberately malformed fixtures keep their format's extension even when their signature is the very thing
    # that is broken; they are exactly the seeds a fuzzer wants.
    by_extension = {
        ".png": "png", ".apng": "png", ".jpg": "jpeg", ".jpeg": "jpeg", ".webp": "webp", ".gif": "gif",
        ".bmp": "bmp", ".dib": "bmp", ".tif": "tiff", ".tiff": "tiff", ".qoi": "qoi", ".ico": "ico", ".cur": "ico",
        ".pbm": "pnm", ".pgm": "pnm", ".ppm": "pnm", ".pam": "pnm", ".pnm": "pnm",
    }
    return by_extension.get(extension)


def collect(roots: list[str], max_bytes: int) -> tuple[dict[str, dict[str, str]], int, int]:
    """Maps format -> {sha256: path}; returns it with the counts of oversized and unclassified files."""
    by_format: dict[str, dict[str, str]] = {f: {} for f in ALL_FORMATS}
    oversized = unclassified = 0
    for root in roots:
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = sorted(d for d in dirnames if d not in ("__pycache__", ".git"))
            for name in sorted(filenames):
                lowered = name.lower()
                if lowered.endswith(SKIPPED_SUFFIXES) or name.startswith("."):
                    continue
                path = os.path.join(dirpath, name)
                size = os.path.getsize(path)
                if size == 0:
                    continue
                with open(path, "rb") as fh:
                    data = fh.read()
                fmt = classify(path, data[:64])
                if fmt is None:
                    unclassified += 1
                    continue
                if size > max_bytes:
                    oversized += 1
                    continue
                digest = hashlib.sha256(data).hexdigest()
                by_format[fmt].setdefault(digest, path)
    return by_format, oversized, unclassified


def seed_name(path: str, roots: list[str], digest: str) -> str:
    """A readable, collision-free file name: the fixture's path relative to its root, plus a short hash."""
    for root in roots:
        try:
            relative = os.path.relpath(path, root)
        except ValueError:  # different drive on Windows
            continue
        if not relative.startswith(".."):
            break
    else:
        relative = os.path.basename(path)
    flat = re.sub(r"[^A-Za-z0-9._-]+", "_", relative.replace(os.sep, "_").replace("/", "_"))
    return f"{flat}-{digest[:8]}"


def merged_dictionary(names: list[str]) -> str:
    parts = []
    for name in names:
        path = os.path.join(DICT_DIR, name + ".dict")
        with open(path, "r", encoding="utf-8") as fh:
            parts.append(f"# ---- {name}.dict ----\n" + fh.read().rstrip("\n") + "\n")
    return "\n".join(parts)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--fixtures", action="append", help="fixture root to scan (repeatable); default: the test fixtures")
    parser.add_argument("--out", default=os.path.join(FUZZ_DIR, "out"), help="output root (default: fuzz/out)")
    parser.add_argument("--max-bytes", type=int, default=256 * 1024,
                        help="skip seeds larger than this (default 262144; libfuzzer-dotnet caps inputs at 1 MiB)")
    parser.add_argument("--zip", metavar="DIR", help="also write DIR/<prefix><target>_seed_corpus.zip for OSS-Fuzz")
    parser.add_argument("--zip-prefix", default="", help="prefix for the zip and dictionary names in --zip DIR")
    parser.add_argument("--targets", nargs="*", help="only these targets (default: all)")
    args = parser.parse_args(argv)

    if args.max_bytes > LIBFUZZER_DOTNET_MAX_INPUT:
        parser.error(f"--max-bytes cannot exceed {LIBFUZZER_DOTNET_MAX_INPUT}: libfuzzer-dotnet rejects larger inputs")

    roots = [os.path.abspath(r) for r in (args.fixtures or [DEFAULT_FIXTURES])]
    for root in roots:
        if not os.path.isdir(root):
            print(f"error: fixture root {root} does not exist", file=sys.stderr)
            return 2

    targets = args.targets or list(TARGETS)
    unknown = [t for t in targets if t not in TARGETS]
    if unknown:
        parser.error(f"unknown target(s): {', '.join(unknown)}; known: {', '.join(TARGETS)}")

    by_format, oversized, unclassified = collect(roots, args.max_bytes)
    corpus_root = os.path.join(args.out, "corpus")
    dict_root = os.path.join(args.out, "dict")
    os.makedirs(dict_root, exist_ok=True)
    if args.zip:
        os.makedirs(args.zip, exist_ok=True)

    for target in targets:
        formats, dictionaries = TARGETS[target]
        seeds: dict[str, str] = {}
        for fmt in formats:
            for digest, path in by_format[fmt].items():
                seeds.setdefault(digest, path)

        directory = os.path.join(corpus_root, target)
        if os.path.isdir(directory):
            shutil.rmtree(directory)
        os.makedirs(directory)
        names = []
        for digest, path in sorted(seeds.items(), key=lambda kv: kv[1]):
            name = seed_name(path, roots, digest)
            shutil.copyfile(path, os.path.join(directory, name))
            names.append((name, path))

        dictionary = merged_dictionary(dictionaries)
        with open(os.path.join(dict_root, target + ".dict"), "w", encoding="utf-8", newline="\n") as fh:
            fh.write(dictionary)

        if args.zip:
            zip_path = os.path.join(args.zip, f"{args.zip_prefix}{target}_seed_corpus.zip")
            with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
                for name, path in names:
                    archive.write(path, name)
            with open(os.path.join(args.zip, f"{args.zip_prefix}{target}.dict"), "w", encoding="utf-8", newline="\n") as fh:
                fh.write(dictionary)

        print(f"{target:<16} {len(names):5d} seed(s)  dict: {'+'.join(dictionaries)}")

    print(f"skipped {oversized} file(s) over {args.max_bytes} bytes and {unclassified} unclassified file(s)")
    print(f"corpus: {corpus_root}")
    print(f"dictionaries: {dict_root}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

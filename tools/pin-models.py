#!/usr/bin/env python3
"""Computes the SHA-256 of each model file and emits the ModelRegistry.Checksums entries for it.

The library verifies every downloaded model against a checksum compiled into ModelRegistry.cs, and the
check is fail-closed: a file that does not match is deleted and the load throws. So publishing a model
is two steps, not one — upload the file, then pin the hash of the exact bytes you uploaded.

Usage:

    python tools/pin-models.py path/to/models            # print the C# dictionary entries
    python tools/pin-models.py path/to/models --check    # re-verify against what is already pinned

Run it against the directory you are about to upload, paste the output into the Checksums dictionary in
src/EasyImageSharp.AI/Models/ModelRegistry.cs, and rebuild. Verify after uploading too: download the
file back from Hugging Face and re-run this script on it, so the hash is of what the CDN actually serves
rather than of what you meant to send.
"""
from __future__ import annotations

import hashlib
import os
import re
import sys

# Every file the registry knows how to fetch, in registry order. int8 variants are optional.
KNOWN = [
    "PP-LCNet_x1_0_doc_ori.onnx",
    "UVDoc.onnx",
    "realesrgan_general_x4v3.onnx",
    "realesrgan_general_x4v3.int8.onnx",
    "dncnn_gray_blind.onnx",
    "dncnn_gray_blind.int8.onnx",
    "u2netp.onnx",
    "u2netp.int8.onnx",
    "sauvolanet.onnx",
    "sauvolanet.int8.onnx",
]

REGISTRY = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "src", "EasyImageSharp.AI", "Models", "ModelRegistry.cs")


def sha256(path: str) -> str:
    """Upper-case hex SHA-256, matching the format ModelHub compares against."""
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def pinned() -> dict[str, str]:
    """The checksums currently compiled into the registry."""
    if not os.path.exists(REGISTRY):
        return {}
    source = open(REGISTRY, encoding="utf-8").read()
    return dict(re.findall(r'\["([^"]+\.onnx)"\]\s*=\s*"([0-9A-Fa-f]{64})"', source))


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    directory = sys.argv[1]
    check = "--check" in sys.argv
    if not os.path.isdir(directory):
        print(f"error: {directory} is not a directory")
        return 2

    present = [name for name in KNOWN if os.path.exists(os.path.join(directory, name))]
    unknown = sorted(
        name for name in os.listdir(directory)
        if name.endswith(".onnx") and name not in KNOWN)

    if not present:
        print(f"error: none of the known model files are in {directory}")
        print("       expected one or more of: " + ", ".join(KNOWN))
        return 1

    already = pinned()
    failures = 0
    print()

    for name in present:
        path = os.path.join(directory, name)
        digest = sha256(path)
        size = os.path.getsize(path)
        state = ""
        if name in already:
            if already[name] == digest:
                state = "  (matches the pinned value)"
            else:
                state = "  *** DIFFERS from the pinned value ***"
                failures += 1
        print(f'        ["{name}"] = "{digest}",   // {size / 1_000_000:.1f} MB{state}')

    if unknown:
        print()
        print("Not recognised by the registry, so not pinned (check the file name):")
        for name in unknown:
            print(f"  {name}")

    print()
    missing = [name for name in KNOWN if name not in present]
    if missing:
        print("Not in this directory (fine if you are not publishing them yet):")
        for name in missing:
            print(f"  {name}")
        print()

    if check:
        if failures:
            print(f"{failures} file(s) do not match the pinned checksum.")
            return 1
        print("Every file present matches the pinned checksum.")
        return 0

    print("Paste the lines above into the Checksums dictionary in:")
    print(f"  {os.path.relpath(REGISTRY)}")
    print("then rebuild. A file with no pinned checksum cannot be downloaded by the library.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

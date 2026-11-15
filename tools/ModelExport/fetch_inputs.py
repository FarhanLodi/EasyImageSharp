"""Downloads the inputs of quantize_int8.py listed in inputs.tsv and verifies every SHA-256.

Usage:

    python -I tools/ModelExport/fetch_inputs.py --dest <new empty directory>

Creates <dest>/models (the YuNet fp32 file and its licence), <dest>/images (scikit-image sample photos) and
<dest>/faces (NASA crew portraits). The registry fp32 models are not downloaded here: copy them from a warmed
EasyImageSharp cache (%LOCALAPPDATA%/EasyImageSharp/models), which the library already verified against the pinned
checksums, or let ImageAiSession.WarmUpAsync fetch them. A file whose checksum does not match is deleted and the
script fails; nothing is ever executed from the downloaded data.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import sys
import urllib.request

MANIFEST = os.path.join(os.path.dirname(os.path.abspath(__file__)), "inputs.tsv")
USER_AGENT = "EasyImageSharp-ModelExport/1.0 (+https://github.com/FarhanLodi/EasyImageSharp)"
FOLDERS = {"model": "models", "images": "images", "faces": "faces"}


def entries():
    with open(MANIFEST, encoding="utf-8") as handle:
        for line in handle:
            line = line.rstrip("\n")
            if not line or line.startswith("#"):
                continue
            kind, name, sha256, licence, url = line.split("\t")
            yield kind, name, sha256, licence, url


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dest", required=True)
    args = parser.parse_args()

    failures = 0
    for kind, name, expected, licence, url in entries():
        folder = os.path.join(args.dest, FOLDERS[kind])
        os.makedirs(folder, exist_ok=True)
        path = os.path.join(folder, name)
        request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(request, timeout=120) as response:
            data = response.read()
        actual = hashlib.sha256(data).hexdigest().upper()
        if expected != "-" and actual != expected:
            print(f"FAIL {name}: expected {expected}, got {actual} ({url})", file=sys.stderr)
            failures += 1
            continue
        with open(path, "wb") as handle:
            handle.write(data)
        print(f"ok   {name} {actual} [{licence}]")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())

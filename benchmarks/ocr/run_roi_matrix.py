"""Run the resumable ROI quality matrix sequentially: never two OCR engine processes at once."""
import argparse
import subprocess
import sys
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_executable", type=Path)
    parser.add_argument("release_executable", type=Path)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("results", type=Path)
    parser.add_argument("logs", type=Path)
    args = parser.parse_args()
    args.logs.mkdir(parents=True, exist_ok=True)
    scripts = Path(__file__).resolve().parent
    reports = []
    runs = [("paddleocr", "source", None), ("easyocr", "source", None),
            ("paddleocr", "release", None), ("easyocr", "release", None),
            ("paddleocr", "source", "ScannedDocument"), ("paddleocr", "source", "PhoneDocument")]
    for engine, dependency, profile in runs:
        name = f"mdp-block-{engine}-{dependency}" + (f"-{profile}" if profile else "")
        report = args.results / (name + ".json")
        command = [sys.executable, "-u", str(scripts / "run_export_batches.py"),
                   str(args.source_executable if dependency == "source" else args.release_executable),
                   str(args.manifest), engine, str(report)]
        if dependency == "release" or profile:
            command.append("--offline")
        if profile:
            command.extend(["--profile", profile])
        print("Starting/resuming " + name, flush=True)
        with (args.logs / (name + ".log")).open("w", encoding="utf-8") as log:
            subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True)
        reports.append(report)
        print("Completed " + name, flush=True)
    subprocess.run([sys.executable, str(scripts / "summarize_exports.py"), str(args.manifest),
                    *map(str, reports), "--output", str(args.results / "mdp-block-quality-summary.json")], check=True)
    for engine in ("paddleocr", "easyocr"):
        subprocess.run([sys.executable, str(scripts / "compare_exports.py"),
                        str(args.results / f"mdp-block-{engine}-source.json"),
                        str(args.results / f"mdp-block-{engine}-release.json"),
                        str(args.results / f"mdp-block-{engine}-parity.json")], check=True)


if __name__ == "__main__":
    main()

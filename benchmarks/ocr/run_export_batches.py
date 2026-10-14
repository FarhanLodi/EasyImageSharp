"""Run each language in a fresh process, retaining resumable per-language reports."""
import argparse
import hashlib
import json
import subprocess
import os
import time
from pathlib import Path


def working_set(process):
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes
        class Counters(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
                (name, ctypes.c_size_t) for name in ("PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
                "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage")]
        counters = Counters()
        counters.cb = ctypes.sizeof(counters)
        query = ctypes.windll.psapi.GetProcessMemoryInfo
        query.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD]
        query.restype = wintypes.BOOL
        if not query(wintypes.HANDLE(int(process._handle)), ctypes.byref(counters), counters.cb):
            raise ctypes.WinError()
        return counters.WorkingSetSize
    if not Path("/proc").exists():
        raise RuntimeError("Resident-memory sampling requires Windows or Linux /proc")
    status = Path(f"/proc/{process.pid}/status")
    if status.exists():
        for line in status.read_text().splitlines():
            if line.startswith("VmRSS:"):
                return int(line.split()[1]) * 1024
    return 0


def run_bounded(command, limit_mib):
    started = time.monotonic()
    peak = 0
    with subprocess.Popen(command) as process:
        try:
            while process.poll() is None:
                try:
                    peak = max(peak, working_set(process))
                except OSError:
                    if process.poll() is None:
                        raise
                if peak > limit_mib * 1024 * 1024:
                    raise RuntimeError(f"OCR process exceeded {limit_mib} MiB working-set guard; observed {peak} bytes")
                if time.monotonic() - started > 1800:
                    raise TimeoutError("OCR language batch exceeded 1800 seconds")
                time.sleep(.5)
            if process.returncode:
                raise subprocess.CalledProcessError(process.returncode, command)
        except BaseException:
            if process.poll() is None:
                process.kill()
                process.wait()
            raise
    return peak


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("engine", choices=["easyocr", "paddleocr"])
    parser.add_argument("output", type=Path)
    parser.add_argument("--offline", action="store_true")
    parser.add_argument("--profile", choices=["NeuralOcr", "ScannedDocument", "PhoneDocument", "Screenshot"])
    parser.add_argument("--max-working-set-mib", type=int, default=4096,
                        help="Sampled resident-memory guard, not a hard process allocation limit")
    args = parser.parse_args()
    if args.max_working_set_mib < 1:
        parser.error("--max-working-set-mib must be positive")
    manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
    root = args.manifest.resolve().parent
    destination = args.output.resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    pieces = []
    for language in sorted({row["language"] for row in manifest["images"]}):
        batch = dict(manifest, images=[row for row in manifest["images"] if row["language"] == language],
                     allowModelDownload=not args.offline, preprocessingProfile=args.profile)
        name = destination.stem + "-" + language
        batch_manifest = root / (name + ".json")
        batch_manifest.write_text(json.dumps(batch, ensure_ascii=False, indent=2), encoding="utf-8")
        batch_output = destination.with_name(name + ".json")
        expected = hashlib.sha256(batch_manifest.read_bytes()).hexdigest().upper()
        if batch_output.exists():
            saved = json.loads(batch_output.read_text(encoding="utf-8-sig"))
            if saved["ManifestSha256"] != expected:
                raise ValueError("Existing report settings differ: " + str(batch_output))
            for assembly in saved["Assemblies"]:
                binary = args.executable.resolve().parent / (assembly["Name"] + ".dll")
                if hashlib.sha256(binary.read_bytes()).hexdigest().upper() != assembly["Sha256"].upper():
                    raise ValueError("Existing report assembly differs from executable directory: " + assembly["Name"])
        else:
            peak = run_bounded([str(args.executable.resolve()), str(batch_manifest), args.engine,
                                str(batch_output)], args.max_working_set_mib)
            saved = json.loads(batch_output.read_text(encoding="utf-8-sig"))
            saved["SampledPeakWorkingSetBytes"] = peak
            saved["WorkingSetSamplingSeconds"] = .5
            saved["WorkingSetLimitMiB"] = args.max_working_set_mib
            batch_output.write_text(json.dumps(saved, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            print(f"Completed {language}; sampled peak working set {peak / 1024**2:.1f} MiB", flush=True)
        pieces.append(json.loads(batch_output.read_text(encoding="utf-8-sig")))
    result = dict(pieces[0])
    result["ManifestSha256"] = hashlib.sha256(args.manifest.read_bytes()).hexdigest().upper()
    result["Rows"] = [row for piece in pieces for row in piece["Rows"]]
    result["FailedImages"] = sum(piece["FailedImages"] for piece in pieces)
    result["ExecutionNote"] = "Fresh process per language; per-language reports retain complete cache/assembly fingerprints. Cold timings are not throughput."
    result["BatchReports"] = [destination.stem + "-" + language + ".json" for language in sorted({row["language"] for row in manifest["images"]})]
    result["ModelCacheSnapshotsByBatch"] = [piece["ModelCacheSnapshot"] for piece in pieces]
    result["ModelsScope"] = "Union of cached assets across language batches; includes unused models"
    peaks = [piece["SampledPeakWorkingSetBytes"] for piece in pieces if "SampledPeakWorkingSetBytes" in piece]
    result["SampledPeakWorkingSetBytes"] = max(peaks) if peaks else None
    result["MemoryMeasuredBatches"] = len(peaks)
    result["TotalBatches"] = len(pieces)
    for piece in pieces:
        if piece["Assemblies"] != pieces[0]["Assemblies"]:
            raise ValueError("Assembly fingerprints differ between language batches")
    models = {}
    for piece in pieces:
        for model in piece["ModelCacheSnapshot"]:
            if model["Name"] in models and models[model["Name"]] != model["Sha256"]:
                raise ValueError("Model asset changed between batches: " + model["Name"])
            models[model["Name"]] = model["Sha256"]
    result["ModelCacheSnapshot"] = [{"Name": name, "Sha256": checksum} for name, checksum in sorted(models.items())]
    destination.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
